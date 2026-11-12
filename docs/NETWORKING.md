# NETWORKING · 命令流与同步协议

> 规范来源：GDD §9.1（三种模式一套模拟）、§9.2（权威主机 + 命令流）、§9.3（校验与容错）、
> §9.4（AI 托管）、ADR-009。实现：`src/SimCore/Sync.cs`、`Commands.cs`、`StateHash.cs`、
> `Simulation.cs`（提交与广播哈希的两处调用点）。
>
> **M1 的状态：协议骨架已经跑通，网络实现还没有。** 本文标 ✅ 的是 M1 已有行为，
> 标 ⛪ 的是 M10 要补的口子——把「单机跑通」与「联机返工」解耦，正是 §14 风险表里那条预案。

---

## 1. 三种模式，一套模拟（§9.1）

| `SyncMode` | 语义 | M1 实现 |
|---|---|---|
| `Solo`（默认） | 1 玩家 + N 个 AI 国家 | ✅ `LoopbackTransport`，命令在进程内转一圈再进总线 |
| `Hotseat` | 多人轮流在本机提交，统一结算 | ✅ 同上，只有 `Issuer` 会换人；私有信息隐藏是壳层的事 |
| `Online` | 2–8 人，房主权威 | ⛪ `SyncMode.Online` 是一个枚举值，没有任何实现读它 |

关键设计是**「单机也走传输层」**：`Simulation.Submit` 从不直接碰 `CommandBus`，
它调 `Transport.Submit(cmd)`（`Simulation.cs:308`），`AdvanceTurn` 开头再从
`TryDequeueReceived` 取回（`:339`）。因此「单机跑通」这件事对联机**有意义**：
换的只是 `ITransport` 的实现，模拟代码零改动（§10.2 职责 9）。

---

## 2. 只同步四样东西（§9.2）

```csharp
public interface ITransport {
    SyncMode Mode { get; }
    void Submit(Command command);                        // ① 命令
    bool TryDequeueReceived(out Command command);        // ① 同上，主机侧取入
    void BroadcastHash(int turn, StateHash hash);        // ② 回合号 + ③ 分块状态哈希
    IReadOnlyList<string> Diagnostics { get; }           // ④ 诊断文本（面板/日志）
}
```

**不同步画面对象**：选中格、相机、图层开关、面板状态、动画——这些一律不进 Core，
所以它们自然不在网络上（§10.1 壳层不含规则的对称面）。低频快照（§9.2 提到的第五样）
⛪ M10 再加，形状就是 `docs/SAVE_FORMAT.md` 的 `SaveGame`：断线重连直接复用存档格式。

`LoopbackTransport.BroadcastHash` 做的事是把哈希存进 `LastHash` 并写一行诊断
（`T003 哈希 <total>（<n> 分块 / <m> 字段）`）；`sim hash` 与测试读的就是这个语义的实体。

---

## 3. 命令流与定序

```csharp
class Command { int Turn; int Seq; string Issuer; string Type;
                IReadOnlyDictionary<string, SimValue> Args;  // SortedDictionary，ordinal
                string Reason; int AcceptedTurn;             // -1 = 尚未被主机接受
                string Canonical(); string Text; }
```

- **定序不靠到达顺序**：`DrainPending` 按 `(Turn, Seq, Issuer ordinal, Type ordinal)` 全序入日志
  （`Commands.cs:138`）。联机时各玩家提交到达顺序不确定，这条就是 §8.3 规则 2 的落点。
- `Seq` 由总线按 `(issuer@turn)` 自增；`Simulation` 另有一个 `_intentCounter` 做提交时的初值，
  读档后它续在命令流之后（`Simulation.cs:478`），避免读档后新命令撞 `seq`。
- `StrictSeq = true`（默认）：拒绝重复与越序提交，**主机侧应当开着**。
- `Issuer` 的写法约定：`nation.0000` / `hero.0042` / `"host"` / `"system"`（AI 托管与内核自动行为）。
- `AcceptedTurn` 在 `DrainPending` 时被主机盖章。

`Canonical()`（`Turn|Seq|Issuer|Type|k=v…` 按 key 排序）是**三处共用的同一串字节**：
状态哈希的 `LogHash`、存档的 `commands`、联机对账时报「哪一条命令被驳了」。
字符串参数走 `SubmitTarget`，所以 UI/脚本不需要自己拼 `SimValue`。

---

## 4. 主机校验链（§9.2「客户端不能指定金钱/人口/战斗结果」）

`CommandBus.Submit` 只做格式与定序，业务合法性一律在第 1 步按当时的世界状态判：

```
① 格式：类型有注册执行器？参数齐不齐（不齐 → Reject 并留原因）
② 身份：Issuer 是不是本回合的合法提交者                  ⛪ M10（M1 只有「没有插件注册命令 X」）
③ 回合：命令是否过期/越序（StrictSeq）                    ⛪ 同上
④ 规则：OnActionValidate 钩子回写 result = "reject:<原因>"   ✅ core.basic 未使用，机制已通
⑤ 资源与 AP：执行器里判，打折而不是禁止（§2.5/§7.5）        ✅ TownOps 走这条
```

**客户端无法伪造金钱/人口/战斗结果**，靠的是三条纪律而不是一条校验：
① 只有命令能进 Core，命令的参数是扁平标量；② 状态写入必须带 `reason` 且只有插件的执行器与
hook 能写；③ 结算全在主机的一份 sim 里。所以「反作弊靠确定性 + 权威 sim，不靠客户端可信」（§9.3）。

---

## 5. 分块哈希与定位流程（§9.3）

```
每回合末：StateHash.Compute(World, Commands, Rng) → Transport.BroadcastHash
        ↓ 本机 A 的 Total != 本机 B 的 Total
① 比 Chunks（kind → 哈希）：哪个 kind 分块不同（FirstDifference）
   · 全部分块相同、总分不同 → 差异在 world 元数据 / meta.turn / LogHash / RNG 状态
② 客户端上传命令流（CommandStream.Text()/Hash()）
③ 服务端用同一种子 + 同命令流回放，按回合二分：第 K 回合两边哈希首次不同
④ 在该回合内按 15 步二分（sim run --steps 的分步哈希 + --explain 的因果链）
```

`FirstDifference` 的两种输出（`StateHash.cs:56`）：

```
分块 'town' 不一致：本机 9f2c… vs 对方 1a7d…
总分不一致但分块相同:511b… vs 3c72…（很可能是 world 元数据或 RNG 状态）
```

- 分块**按 kind** 而不是按国家：跨主机时 kind 数量少，先定位子系统比定位玩家有用
  （`hex` 不同通常是地图生成器，`town` 不同通常是结算规则）。
- 位级相同的前提：定点数 + ordinal 遍历 + 分层 RNG（`docs/CORE_API.md` §9）。
- 本地对账工具：`sim hash --seed 99 --turns 50`（同种子跑两遍逐回合比）、
  `sim replay`（快照 vs 续跑）、`scripts/godot-smoke.txt`（CLI 与 Godot 两个主机末态哈希必须相同）。

---

## 6. 断线重连与 AI 托管（§9.3 / §9.4）

| 需求 | M1 有什么 | 还缺什么 |
|---|---|---|
| 拉快照 + 增量命令 | ✅ `SaveGame` 同时带 `entities`（快照）与 `commands`（全量日志），`ApplySave` 可复用 | ⛪ 「增量」：M1 重放的是**整条**命令日志，没有「快照之后只传增量」的裁剪 |
| 离线玩家由 AI 托管 | ✅ 机制同一条路：任何人（玩家/AI/托管）都只用 `Submit` | ⛪ `DecisionAgent`（§9.4 的五层决策）一个都没写 |
| 回来无缝接回 | ✅ 读档不依赖「谁提交的」：`Issuer` 只是字符串 | ⛪ 托管期间的命令要不要保留原 `Issuer`（§9.4「AI 不给额外资源」） |
| 私有信息隐藏（热座） | ⛪ Core 不区分公开/私有字段 | 需要一个「谁可见」的查询面，归 M10 或 M7（届时走 `CORE_API.md` 升版本） |

§9.4 的复用承诺在 M1 结构上成立：AI 与玩家**共用同一套命令接口**（`Submit`/`SubmitTarget`），
没有「AI 专用」的写状态通道——`Issuer` 是自由字符串，Core 不解释它是谁
（`host` / `nation.0000` / `system` 的语义留给 M10 与 `ai.nations`）。
GDD 说的「托管提交把 `Reason` 写成 `auto`」目前**只是约定**：全仓库只有
`Siting.Auto = "auto"` 是**参数值**（`town.found` 的 `hex=auto`，让规则包自己选址），
执行器里没有一处读 `cmd.Reason` 做特例。这条差距记进 `docs/CHANGELOG-DESIGN.md`。

---

## 7. M10 落地前必须先裁决的两件事

1. **在线传输放哪一层。** GDD §10.3 把 `multiplayer` 列为插件，但 SimCore 与插件的红线是
   「只允许 `System.*`」（ADR-002）。两条路：
   - **A**：插件用 `System.Net.Sockets` 自己实现 `ITransport`（红线不破，但要自己管打洞/中继/加密）；
   - **B**：Core 留接口，**壳层**（Godot）注入实现，用 Godot 的 MultiplayerAPI/Steam（靠引擎的传输能力，
     模拟仍然零依赖）。
   倾向 B（渲染与平台层本来就在壳层），**但这是架构变更**，要写进 `CHANGELOG-DESIGN.md` 并走
   `CORE_API.md` 版本条目。
2. **`Simulation` 现在没法注入 transport**：`Transport` 是只读属性，构造函数里写死
   `new LoopbackTransport(config.Mode)`（`Simulation.cs:197`）。上面 A/B 任一条都要先补这个口子
   （`SimConfig` 加一个工厂委托，或 `Simulation` 加一个 `AttachTransport`），
   这是第一个「要改 Core 才能实现」的已知案例——按 §10.2 补 Core，不在插件里开后门。

其余待补：主机/客户端角色区分、回合倒计时与「未提交者托管」的时钟、命令的 squash 与限流、
版本握要（`CoreInfo.ApiVersion` + `PluginVersions` 已经在存档里，网络侧还没用）。

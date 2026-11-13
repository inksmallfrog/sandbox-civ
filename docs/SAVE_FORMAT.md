# SAVE_FORMAT · 存档结构、迁移与回放

> 规范来源：GDD §10.6（存档、迁移、回放）、§11.3（破存档流程）、ADR-010。
> 实现：`src/SimCore/Save.cs`（`SaveGame` / `SaveStore` / `ISaveMigrator` / `MigratorChain`）、
> `src/SimCore/Simulation.cs`（`CaptureSave` / `Save` / `Restore` / `ApplySave`）、
> `src/SimCore/StateHash.cs`。
> 对账命令：`sim replay --seed 5 --turns 20 --at 12` 与 `sim run --save/--load`。

---

## 1. 文件

| 项 | 值 |
|---|---|
| 扩展名 | `.hcsave`（`SaveStore.Extension`，HexaCiv Save） |
| 编码 | **无 BOM 的 UTF-8**（`Utf8NoBom`）。用 `Encoding.UTF8` 会先吐 `EF BB BF`，JSON 解析器把它当值的开头 → 刚写出来的档自己读不回来。读侧容忍带 BOM 的明文档（手写的测试夹具、Windows 编辑器另存） |
| 压缩 | **GZip**（`System.IO.Compression.GZipStream`，`CompressionLevel.Optimal`）；读侧按魔数 `1F 8B` 自动识别，明文 JSON 也能读 |
| 覆盖策略 | 先写 `<path>.tmp` → 读回自检 → 旧档复制成 `<path>.bak` → 删旧档 → `File.Move` 原子替换 |
| 附注 | `<path>.note.txt` 侧车文件（只有传 `note` 时才写） |
| 失败语义 | 自检不过 → 删 tmp 并抛 `SimSaveException`，**正式档一个字节都没动**（GDD §10.6 的硬要求，测试 `坏档不得覆盖最后一个有效存档`） |

> ⚠ 与 GDD 的偏差：§10.6 写的是 `snapshot(zstd)`。实现用 GZip，因为「只允许 System.*」
> 这条纪律（ADR-002）里没有 zstd 的内置实现，而为一个压缩格式引第三方包要破 §10.1 的红线。
> 已记入 `docs/CHANGELOG-DESIGN.md` 等裁决；换回 zstd 只影响写盘格式，不影响状态哈希。

---

## 2. 顶层 JSON 键（`SaveStore.ToJson`）

```jsonc
{
  "schemaVersion": 1,          // CoreInfo.SchemaVersion；比本程序新 → 拒绝打开
  "gameVersion": "0.1.0",      // 诊断用
  "mapGenVersion": 6,          // GDD §3.7「生成器版本号写入存档」
  "worldSeed": "7",            // 注意：字符串（ulong 超过 JS 安全整数）
  "turn": 12,
  "yearBase": 1,
  "dataHash": "78bad421c6439b0d",     // 数据包摘要：档与数据不同源时报警
  "snapshotHash": "96522e125fca1059", // 写档时的 StateHash.Total
  "savedUtc": "…",                      // 🟡 M1 永远缺席：两个壳层都不填，Core 禁止读时钟
  "pluginVersions": { "core.basic": "0.1.0" },
  "kindCounters":  { "hex": 640, "town": 1, "nation": 1 },  // 缺了它重开后会撞 id
  "rng": { "global": "1234…", "streams": [ { "ns": "town.0000@harvest#12", "state": "…" } ] },
  "entities": [ { "id": "hex.0000", "kind": "hex", "fields": { "hex.q": "i0", … } } ],
  "commands": [ { "turn": 3, "seq": 1, "issuer": "nation.0000", "type": "building.build",
                 "reason": "玩家：囤粮", "args": { "building": "sbuilding.farm", "town": "stown.0000" } } ]
}
```

三条序列化纪律（都是确定性的地基）：

1. **值一律是带类型标签的字符串**：`SimValue.Canonical`——`i` 整数、`f` 定点 Raw（1/100）、
   `s` 字符串、`b` 布尔、`n` 空。类型进标签是为了让 `1` 与 `"1"` 不撞哈希。
   读回走 `SimValue.ParseCanonical`，与 diff 展示共用一份实现。
2. **顺序全在写侧定死**：实体按 id ordinal（`ApplySave` 再排一次）、命令按
   `(turn, seq, issuer)`、RNG 流按 `ns`、参数按 key、`pluginVersions`/`kindCounters` 是
   `SortedDictionary(Ordinal)`。读侧不依赖文件里的顺序。
3. **数组字段与嵌套对象**（数据包里的 `Root`）不进存档：存档只存世界状态，
   不存数据包本体——数据包由 `dataHash` 把关。

> M2 切片四新增的 `hex.shape` / `hex.resource` / `hex.yield.mat` 都是普通标量字段，
> 走同一套 `SimValue.Canonical`；旧档缺这三个字段时按空串 / 0 读回，不需要迁移器。
> 地图本身由 `mapGenVersion=6` 标识，旧版生成器的档不保证能重放出同一张图。

---

## 3. `SaveGame` 内存形态（`Save.cs:21`）

| 属性 | 来自 |
|---|---|
| `SchemaVersion` / `GameVersion` / `MapGenVersion` | `CoreInfo` 三个常量 |
| `WorldSeed` / `Turn` / `YearBase` | `Rng.WorldSeed` / `Time.Turn` / `Time.YearBase` |
| `PluginVersions` | `sim.Plugins.Loaded` 的 `id → manifest.version` |
| `Entities`（`SaveEntity(Id, Kind, Fields)`） | `World.All()` × `ent.Fields`（已按 ordinal 排序） |
| `Commands`（`SaveCommand(Turn, Seq, Issuer, Type, Reason, Args)`） | `Commands.Log` |
| `DataHash` | `sim.Data.DataHash` |
| `RngGlobalState` / `RngStreams` | `Simulation.RngCapture()`（`RngFactory.CaptureState`） |
| `KindCounters` | `Simulation.WorldKindCounters()` |
| `SnapshotHash` | 写档当下重算的 `StateHash.Total` |
| `SavedUtcText` | 调用方（M1 无人填） |

`SaveStore.Capture(sim)` 是纯函数（只读 sim）；`Simulation.Save(path)` 在捕获**之前**
广播 `OnBeforeSave`，所以插件有机会往 `world.*` 里塞快照衍生量。

---

## 4. 读档语义（`Simulation.ApplySave`，`Simulation.cs:443`）

```
新建 World → 按 id ordinal 重建实体（WriteRaw 灌字段，不走 reason 纪律）
→ RestoreKindCounters → RngFactory(worldSeed) + RestoreState(global, streams)
→ Time.Turn = save.turn, Season = Winter → Commands.Restore(turn, log, 空 pending)   // LogHash 重放
→ _intentCounter = max(seq) 续在命令流之后（避免读档后新命令撞 seq）
→ 广播 OnLoad（args 带 schemaVersion）
→ 重算 StateHash 与 snapshotHash 对账，不等 → SimSaveException
```

对账失败时**必须**并排给出四样输入（`Simulation.cs:490`）：

```
读档后状态哈希不一致：档内 511b… vs 现算 3c72…
  实体 63 / 字段 档内 412 vs 现算 411；命令日志 <logHash>；全局 RNG <state:x16>；回合 12。
  可能原因：迁移器漏字段、插件数据与写档时不同、或档损坏。请对比同名 .bak。
```

少这一步时，等价性自检失败只能靠猜——这条经验写在 `CHANGELOG-DESIGN.md`。

`Restore` **不吃命令重放**：GDD §10.6 的 `replay = snapshot + commands` 里，
快照本身已经是权威状态，命令日志只是为了后续提交新命令时能定 seq 与算 LogHash。
（真正的「重放定位」在 M10 联机里用：上传命令流 → 服务端逐回合二分对比 `StateHash.FirstDifference`。）

---

## 5. 迁移链

```csharp
public interface ISaveMigrator { int From { get; } int To { get; } string PluginId { get; }
                                 void Migrate(JsonNode root); }   // 就地改；抛异常即迁移失败
public static class MigratorChain { Register(m); Registered; Clear();
                                      Run(node, pathForError, out owned); }
```

规则（`Save.cs:360`）：

- 按 `(From 升序, PluginId ordinal)` 排序执行，一档一档推到当前 `SchemaVersion`。
- **缺档就报错**，不放行「大概能读」：`存档 schema 3，缺少 3→4 的迁移器（GDD §11.3：无迁移器不许合入）`。
  静默的旧档兼容是后期最难查的 bug，所以这里的态度是当场崩。
- 循环超过 64 次 → `迁移链疑似成环`。
- 迁移器改的是 `schemaVersion` 之外的 JSON 形状；链跑完后 `Parse` 才读值。
- `MigratorChain.Registered` 目前是**空的**：`schemaVersion = 1`，没有旧档要迁。
  第一次破存档的结构改动必须同时补上（1）迁移器（2）旧档回归测试（3）`CoreInfo.SchemaVersion + 1`，
  提交打 `[破存档→已附迁移器]`（GDD §11.2/§11.3）。

---

## 6. 等价性自检（`sim replay`）

```
第 12 回合快照往返　　：✓ 96522e125fca1059
一口气跑满 20 回合　　：c887c475cd95fd1c
存第 12 回合后续跑 8 回合：c887c475cd95fd1c
✓ 存档续跑等价（§10.6）
```

两条路径必须**位级相同**：`straight.Save()` 后 `resumed.Restore()` 再跑剩下的回合，
哈希等于一口气跑满。`--at` 省略时取回合数的一半。测试侧的对应物：
`第K回合的档加后面的命令等于一口气跑满`、`读回来的档能改能跑且kind计数器续得上`、
`坏档不得覆盖最后一个有效存档`、`存档负载不带BOM且带BOM的明文档也读得动`
（`tests/SimCore.Tests/AcceptanceTests.cs`）。

---

## 7. 哪些东西**不在**存档里（以及为什么）

| 不存 | 理由 |
|---|---|
| 数据包本体 | 由 `dataHash` 把关；数据改了行为就改了，塞进档里会掩盖这件事 |
| `TurnReport` / diff / 因果链 | 回合日志是**派生量**，重跑就能得到；存档是状态，不是历史 |
| 渲染状态（选中格、图层开关、相机） | 壳层的私有财产，Core 不该知道（§10.1） |
| RNG 的**历史** | 只存每条流的当前 `State`（SplitMix64 一个 ulong 就够续） |
| `meta.*` 之外的 Core 元数据 | 它们本身就在 `world.0000` 的字段里，跟着实体走 |
| 插件实例 / hook 注册表 | 读档时由 `PluginHost` 重新装载，注册表不进档（否则顺序敏感） |

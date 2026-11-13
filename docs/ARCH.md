# ARCH · 模块边界与分层

> 谁可依赖谁、谁不许知道什么、加内容时动哪一层。
> 规范来源：GDD §10.1（壳层不含规则）、§10.2（Core 九项职责）、§10.3（插件清单）、
> §10.9（仓库结构）、ADR-001…012。本文件只写**实现后的**边界与偏差。

---

## 1. 四层与依赖方向

```
        ┌──────────────────────────────────────────────┐
        │  壳层  src/HexaCiv.Cli（无头）  src/GodotShell（窗口）│  ← 渲染、输入、参数、报告打印
        └───────────────┬──────────────────────────────┘
                        │ 只调 Simulation 的公开方法 + 读 TurnReport
        ┌───────────────▼──────────────────────────────┐
        │  规则包  src/Plugins/core.basic               │  ← 玩法知识全在这里 + data/
        └───────────────┬──────────────────────────────┘
                        │ 只见 IPlugin / IPluginApi / IStepContext / DataRegistry
        ┌───────────────▼──────────────────────────────┐
        │  内核  src/SimCore                            │  ← 九项职责，零玩法词汇
        └───────────────┬──────────────────────────────┘
                        │ 文件 IO + System.*
        ┌───────────────▼──────────────────────────────┐
        │  数据  data/<pack>/*.json                     │  ← 数值、名称、门槛标签
        └──────────────────────────────────────────────┘
```

允许的方向是**从上往下**，反向依赖一律算违规：

- `SimCore` 不许 `using` 任何非 `System.*` 的包，不许引用 Godot，不许出现
  「城镇 / 农民 / 粮 / 宗教」这些词（`kind`、字段路径、数据包 id 都是字符串）。
  自检（M1 实况，两条都过）：
  `grep -rin "town\|peasant\|food" src/SimCore/*.cs` 去掉注释行后**为空**；
  `grep -rn "Godot" src/SimCore/*.cs` 只命中说明文字——
  `PluginHost.cs:44`、`PluginHost.cs:311`（讲加载上下文）、`SimCore.csproj:4`（红线本身），
  没有一处是依赖。
- `core.basic` 只能 `using SimCore;`。它拿到 `IPluginApi`，接口上没有 `World.WriteRaw`
  （internal），所以绕不过 reason 纪律。
- 壳层不许出现任何判断规则的代码。`src/GodotShell/Main.cs` 里可以写「点格 → 提交
  `town.found` 命令」，不可以写「粮不够就别建」（那是 `core.basic` 或 `econ.*` 的事）。
- 插件之间**禁止直接调用**（GDD §10.4）：只能经 hook。M1 只有一个插件，
  但 `PluginHost` 的装载顺序 + 主题分派已经按多插件设计。

---

## 2. 目录（实际，GDD §10.9 的落地）

```
sandbox-civ/
  Readme.md              怎么跑（人类入口）
  AGENTS.md              后续 AI 的最高优先级工作说明（§11.1）
  docs/DEV_ENVIRONMENTS.md 三台开发宿主的探测和快速启动说明
  docs/UI_DESIGN.md      GDD 对齐的 UI 风格、组件与交互方案
  Directory.Build.props  net8.0 / LangVersion 12 / Nullable / TreatWarningsAsErrors / RepoRoot
  HexaCiv.sln            SimCore + core.basic + sim + tests（Godot 壳层刻意不在里面，见 §6）
  docs/                  GDD.md（唯一规则来源，AI 不得自行修改）+ §11.1 要求的另外 12 份 + REQUIREMENTS.md + history/ + screenshots/
  src/
    SimCore/             内核（20 个 .cs，见 §3）
    Plugins/core.basic/  参考规则包（BasicPlugin/F/MapGen/Features/Sculpt/Nations/SeasonRules/Siting/TownOps + manifest.json）
    HexaCiv.Cli/         sim 无头壳层（Program.cs 含 Options / Runner.cs / Script.cs / AsciiMap.cs）
    GodotShell/          窗口壳层（Main.cs / HexView.cs / Main.tscn / project.godot）
  data/
    _schema.json         每种数据包类型的字段与类型
    core.basic/          building.json / class.json / good.json / landform.json / resource.json / shape.json / water.json
  tests/SimCore.Tests/   xUnit，100 条（单测 + §12 里程碑验收）
  scripts/               demo.txt（命令脚本）、godot-smoke.txt（与窗口冒烟同流）
  analysis/out/          基线 CSV 报告（每个里程碑提交附一份）
  saves/                 回归样本档（.gitkeep，产物被 ignore）
  build/plugins/         core.basic 的产物落点（csproj 复制，非版本控制）
```

---

## 3. SimCore 的文件 ↔ 九项职责（GDD §10.2）

| # | 职责 | 主要文件 | 关键类型 |
|---|---|---|---|
| 1 | 世界存储 | `World.cs`、`EntityId.cs`、`SimValue.cs` | `World` / `Entity` / `EntityId` / `SimValue`(`None/Int/Fix/Str/Bool`) |
| 2 | 阶段调度器 | `Time.cs`、`Simulation.cs` | `SimOrder.Steps`（15 步）/ `SimStep` / `Season` / `SimTime` / `AdvanceTurn` |
| 3 | 确定性随机 | `Rng.cs` | `StableHash`(SplitMix64/FNV) / `RandomStream` / `RngFactory`（命名空间流 + 每回合重置） |
| 4 | 数据注册表 | `DataRegistry.cs` | `DataRegistry`(`Load/OfType/Get/Resolve/Exists/DataHash`) / `DataEntry` / `TypeSchema` |
| 5 | 命令总线 | `Commands.cs` | `Command` / `CommandBus`(Pending/Log/LogHash/DrainPending) / `CommandResult`(执行率) |
| 6 | 不变量守卫 | `Invariants.cs`、`Mutation.cs`、`StepContext.cs` | `InvariantGuard` / `InvariantRule` / `Diff` / `Explanation` / `TurnJournal` / `IStepContext` |
| 7 | 存档 / 回放 / 迁移 | `Save.cs`、`StateHash.cs` | `SaveGame` / `SaveStore`(GZip+原子写) / `ISaveMigrator` / `MigratorChain` / `StateHash` |
| 8 | 插件宿主 | `PluginHost.cs`、`Plugins.cs`、`PluginApi.cs` | `IPlugin` / `PluginManifest` / `PluginHost` / `PluginLoadContext` / `IPluginApi` / `CoreInfo` |
| 9 | 同步层 | `Sync.cs` | `SyncMode` / `ITransport` / `LoopbackTransport` / `CommandStream` |
| — | 定点数（§10.7） | `Fix.cs` | `Fix`（×100，`UInt128` 中间量，向零截断） |
| — | 异常（说人话） | `Exceptions.cs` | `SimException` + `SimData/State/Plugin/Invariant/Save/CommandReject` |
| — | 装配 | `Simulation.cs` | `SimConfig` / `Simulation` / `TurnReport` / `StepRecord` |

「九项职责」之外 Core 什么都不做：没有地形规则、没有阶级、没有经济公式、没有科技、
没有军事、没有名人生成、没有外交、没有 AI 决策（§10.2 末段的排除清单同样生效）。

---

## 4. 一次回合的调用链（跨层）

```
壳层                Simulation              HookRegistry         core.basic            World/Data
 │ AdvanceTurn() ──► Turn++ / meta.turn
 │                 DrainPending()          （命令按全序入日志）
 │                 Dispatch(01.pre)  ─────► handlers…
 │                 ExecuteCommand()  ─────► OnActionValidate ──► 校验
 │                                     └──► TownOps.Found ─────► ctx.CreateEntity / Mutate(reason)
 │                 Dispatch(02.post) ─────► TownOps.Facilities ─► 折 Facility / 仓容 / 损耗
 │                 Dispatch(03.pre)  ─────► FanOut ─► DispatchSubjects(OnProduction, town)
 │                                                      SeasonRules.Produce(每镇)
 │                 … 步 4–15 同构
 │                 Invariants.RunAfter(每步)
 │                 StateHash.Compute ◄───────────────── 实体 + meta.turn + LogHash + RNG
 │ ◄── TurnReport（年报 / CSV / 因果链）
```

图里没有任何一步是壳层参与计算的：壳层只调 `AdvanceTurn` 和渲染返回值。

---

## 5. 边界上刻意留下的「接缝」

| 接缝 | 现状 | 谁将来用它 |
|---|---|---|
| `IPluginApi.RegisterLayer` | `core.basic` 注册了 terrain / owner / agriculture / resource / population 五个图层，Core 只存元数据 | M2 `map.view`（图层是纯查询，不改状态） |
| `IStepContext.DispatchSubjects` | 扇出点由插件调用，Core 不认识 `town` | 每个新玩法插件（`econ.local` 扇 `town`、`town.classes` 扇 `class`） |
| `HookArgs` 具名槽位 | 只有 `step/stepId/slot/layer/command/issuer/arg.*/result` 被 Core 使用 | 插件自定义参数从这里长出来 |
| `ISaveMigrator` / `MigratorChain` | 注册表为空（`schemaVersion=1`） | 第一次破存档的结构改动必须补上迁移器 + 旧档回归 |
| `ITransport` | 只有 `LoopbackTransport` | M10 联机：换实现，不改 sim |
| `SimConfig.YearLimit` | 字段在、**没人执行** | M6/M7 终局结算（已记入 `CHANGELOG-DESIGN.md`） |
| `PluginManifest.DataPacks` | 解析并打印，但数据包目前仍按目录整体加载 | M2「插件自带数据包」时接上 |

---

## 6. 两条实现层的架构决定（GDD 没细说，写在这里防丢）

1. **`src/GodotShell` 不进 `HexaCiv.sln`。** 它需要与项目 SDK 版本匹配的 Godot .NET 编辑器才能构建
   （`GodotSharp` 从编辑器目录解析）。放进 solution 会让 `dotnet build` / CI 在没有
   Godot SDK 的机器上直接红，而 CI 的职责恰恰是「没装引擎也能验内核」。
   构建方式：`dotnet build src/GodotShell` 或直接用匹配 SDK 版本的 Godot .NET 编辑器打开该目录。
2. **插件装在专用 `AssemblyLoadContext`。** `Assembly.LoadFrom` 会把插件塞进默认上下文，
   而 Godot 的脚本在自己的上下文里 → 插件会绑上第二份 `SimCore` → `IPlugin` 转型失败，
   表现成「程序集里没有 IPlugin 实现」。`PluginHost.PluginLoadContext.Load` 先从
   **宿主上下文**解析同名程序集，再退回 `.deps.json` 解析器（`src/SimCore/PluginHost.cs:319`）。

---

## 7. 加内容的标准动作（GDD §0.3 铁律 5 的落地版）

| 想做的事 | 动哪里 | 不该动哪里 |
|---|---|---|
| 改数值（产量、门槛、价格） | `data/core.basic/*.json` | 不改 C# |
| 加一种建筑 / 阶级 / 物资 | `data/` 加记录 + 若要行为则在插件挂 hook | 不改 Core |
| 加一条规则 | `src/Plugins/<新插件或 core.basic>` | 不在壳层写 `if` |
| 加一个 UI 面板 | `src/GodotShell/`，只读 `report.*` / `ctx.Get*` | 不在面板里算数 |
| 需要 Core 提供新能力 | 先写 `docs/CORE_API.md` 版本条目，再改 Core | **不许在插件里开后门** |

发现「要改 Core 才能实现」= Core 接口缺了一块，按 GDD §10.2 补 Core 并升 `CoreInfo.ApiVersion`。
这是本项目唯一的「加接口」通道，也是 AI 之间交接时最需要守的规矩。

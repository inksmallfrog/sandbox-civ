# CORE_API · Core 对外接口

> **接口版本：`CoreInfo.ApiVersion = "1.1"`**（`src/SimCore/Plugins.cs:10`）
> 插件 manifest 的 `api` 字段必须与它**同主版本**（`ApiMajor = 1`），否则 `PluginHost` 拒载
> 并报「Core API 主版本不符」。GDD §11.4：Core 的接口变更必须走这份文档的版本号。

版本规则（本项目自己定的，够用就行）：

| 变更 | 升哪一位 | 例子 |
|---|---|---|
| 加方法 / 加 hook 点 / 加只读属性 | minor `1.x` | `IStepContext.DispatchSubjects` 加可选 `argsFactory` |
| 改已有签名 / 改语义 / 删成员 | major `2.0` + 迁移说明 | 把 `Mutate` 的 `reason` 变成可选 |
| 改存档结构 | 与本版本无关，走 `CoreInfo.SchemaVersion` | 见 `docs/SAVE_FORMAT.md` |
| 改地图生成管线 | 递增 `CoreInfo.MapGenVersion` | 当前 6；见 `docs/ROADMAP.md` M2 |

下面每个成员都标了 M1 的实际状态：`✅` 已实现并在用；`🟡` 已实现但暂无消费者；
`⛪ 保留`：接口位已留，功能在后续里程碑。

---

## 1. 生命周期（壳层唯一需要知道的顺序）

```csharp
var sim = new Simulation(new SimConfig { Seed = 7 });   // 1. 构造：只装时钟/RNG/总线
sim.Bootstrap(loadData: true, loadPlugins: true);       // 2. 数据 → 内核实体 → 插件 → 不变量 → bootstrap
sim.SubmitTarget("nation.0001", "town.found", "选址", ("hex", "auto"));   // 3. 意图段
TurnReport r = sim.AdvanceTurn();                       // 4. 一整年（15 步）
sim.Save("saves/a.hcsave");                             // 5. 快照
var t = new Simulation(cfg); t.Bootstrap(loadData: true, loadPlugins: true); t.Restore("saves/a.hcsave");
```

- `Bootstrap` 只能调一次，重复调用抛 `SimStateException`（要重来就 new 一个实例）。
- `AdvanceTurn` 前必须已 `Bootstrap` 或 `Restore`。
- `Restore/ApplySave` 会**替换**内部 `World` 与 `Rng`，因此必须在 `Bootstrap` 之后调用
  （插件的 hook 注册在 `Simulation` 上，不在 `World` 上）。
- `SimConfig.Clone(b => b.Seed = 9)` 用来做「同配置换种子」的横扫。

---

## 2. 装配根：`Simulation`（`src/SimCore/Simulation.cs`）

### 2.1 `SimConfig`（✅）

| 成员 | 默认 | 说明 |
|---|---|---|
| `Seed: ulong` | 1 | 世界种子；分层 RNG 的唯一输入 |
| `DataDir` | `"data"` | 相对路径按 `ResolveDir` 解析 |
| `PluginDir` | `"build/plugins"` | 每个子目录一份 `manifest.json` |
| `Mode: SyncMode` | `Solo` | 决定 `ITransport` 实现 |
| `YearBase` | 1 | `Year = YearBase + Turn - 1` |
| `FatalInvariants` | true | 假 = 不变量只记不抛（`sim --lenient` 抢救坏档） |
| `YearLimit` | 200 | 🟡 **声明了但没人执行**（终局在 M6/M7；已记 CHANGELOG-DESIGN） |
| `StrictData` | true | 假 = 数据校验只警告 |
| `NoPlugins` | false | true = 证明「Core 无玩法也能跑」 |

`static string ResolveDir(string)`（✅）：相对路径依次尝试「`AppContext.BaseDirectory` 向上 9 级 + `bin/` 变体」
再试当前目录。CLI、测试、Godot 编辑器三种工作目录因此都能找到 `data/` 与 `build/plugins/`。

### 2.2 属性（读诊断与注册表，✅）

`Config` `World` `Time` `Rng` `Data` `Commands` `Hooks` `Invariants` `Plugins` `Transport`
`CurrentJournal` `StartupLog: IReadOnlyList<string>` `DataWarnings` `CommandCatalog: <type, 参数说明>`
`Layers: IReadOnlyList<LayerInfo>`（按 `Order` 再按 id 排好）。

`CommandCatalog` 是 UI「政令面板」的数据源：插件 `RegisterCommand` 时给的参数说明串会原样出现在这里。

### 2.3 方法

| 签名 | 状态 | 说明 |
|---|---|---|
| `Simulation Bootstrap(bool loadData = true, bool loadPlugins = true)` | ✅ | 见 §1 的顺序 |
| `Command Submit(string issuer, string type, string reason, params (string, SimValue)[] args)` | ✅ | 进 `Transport`，不直接进总线——单机也走同一条路（§10.2 职责 9） |
| `Command SubmitTarget(string issuer, string type, string reason, params (string, string)[] args)` | ✅ | 字符串参数的便利版，UI/CLI 主力 |
| `TurnReport AdvanceTurn()` | ✅ | 一整年；顺序见 `docs/SIM_ORDER.md` 第 5 节 |
| `TurnReport RunTurns(int turns)` | ✅ | 只返回最后一回合（无头标定/压力测试用） |
| `string CausalChain(EntityId, string path)` | ✅ | §8.4 的因果链文本 |
| `SaveGame CaptureSave()` / `void Save(string path, string? note = null)` | ✅ | `Save` 前广播 `OnBeforeSave` |
| `Simulation Restore(string path)` / `Simulation ApplySave(SaveGame)` | ✅ | 读档后**校验哈希**，不一致抛 `SimSaveException`（带档内/现算/字段数/LogHash/RNG 对照） |
| `StateHash Hash()` | ✅ | 当前状态摘要 |

### 2.4 `TurnReport`（✅，壳层的全部输入）

`Turn` `Year` `Commands: IReadOnlyList<CommandResult>` `Steps: IReadOnlyList<StepRecord>`
`Notes` `InvariantViolations` `Diffs` `Explanations` `Hash: StateHash`
`Headline: IReadOnlyDictionary<string,string>` `TotalMicros`。

- `AnnualReport()`：人读年报（国势 → 政令执行 → 记事 → 算式 → 不变量 → 耗时/哈希）。
- `CsvRow(columns, world)`：`--out` 用。列名规则：`turn|year|hash|ms|commands|diffs` 是内置列；
  `kind:字段路径`（如 `town:stock.food`）= 对该 kind 全部实体求和（`Fix` 与 `Int` 都按 1/100 归一后累加）。
- **`Headline` 的键是完整字段路径**，如 `report.towns`、`report.pop`、`report.minSat`。
  年报正文会剥掉 `report.` 前缀显示，但字典本身不剥——这是壳层曾经踩过并写进注释的坑
  （`src/GodotShell/Main.cs` 的 `Get(k) => r.Headline.GetValueOrDefault("report." + k, "—")`）。
  键由 `core.basic` 的 `F.Report*` 常量定义（`src/Plugins/core.basic/F.cs`）：
  `report.towns / pop / food.out / food.eat / food.rot / food.stock / minSat / unrest`。

`StepRecord(Index, Id, Title, DiffCount, ElapsedMicros)` ✅——`sim run --steps` 打的就是它。

---

## 3. 插件能看到的三张脸

Core 把同一个 `Simulation` 切成三个受限接口，**能力递增、范围递减**：

| 接口 | 交给谁 | 何时 | 能改世界吗 |
|---|---|---|---|
| `IPluginApi`（`src/SimCore/Plugins.cs:156`） | `IPlugin.OnLoad` | 注册期 | ❌（`StepContext` 在 `Step == -1` 时拒绝一切写入） |
| `IStepContext`（`src/SimCore/Mutation.cs:114`） | 每个 hook 处理器 / 命令执行器 / 不变量 | 结算期、bootstrap | ✅ 但必须带 `reason` |
| `IPluginRegistrar`（`src/SimCore/PluginHost.cs:356`） | Core 自己（`Simulation` 实现） | 装载期 | — 失败回滚用 |

签名详见 `docs/PLUGIN_API.md`；这里保留一份「接口面清单」，方便判断某件事有没有公开入口。

### 3.1 `IStepContext` 全签名（✅）

```csharp
World World { get; }            SimTime Time { get; }          RngFactory Rng { get; }
DataRegistry Data { get; }      TurnJournal Journal { get; }   CommandBus Commands { get; }
int Step { get; }                                        // 1–15；0 = 结算外；-1 = 注册期

SimValue Get(EntityId id, string path);
Fix      GetFix(EntityId id, string path, Fix fallback = default);
long     GetInt(EntityId id, string path, long fallback = 0);
string   GetStr(EntityId id, string path, string fallback = "");
bool     Has(EntityId id);
IReadOnlyList<Entity> EntitiesOfKind(string kind);        // 按 id ordinal 升序

void Mutate(EntityId id, string path, SimValue/Fix/long/string value, string reason);
void Add(EntityId id, string path, SimValue delta, string reason);
void AddFix(EntityId id, string path, Fix delta, string reason);
void AddInt(EntityId id, string path, long delta, string reason);
void RemoveField(EntityId id, string path, string reason);
Entity CreateEntity(string kind, string reason, EntityId? explicitId = null);
void DestroyEntity(EntityId id, string reason);

void Explain(EntityId subject, string title, string formula, IReadOnlyList<string> terms, string result);
void Note(string message);

void Require(HookPoint point, int priority, HookHandler handler);              // 仅 OnLoad
void RequireFor(HookPoint point, string entityKind, int priority, SubjectHandler handler);
void DispatchSubjects(HookPoint point, string entityKind, IReadOnlyList<EntityId> subjects,
                      Func<EntityId, HookArgs>? argsFactory = null);
```

三条硬纪律（都有测试或不变量兜底）：

1. **写入必须带 `reason`**，否则第 15 步的 `core.has_reason` 崩给你看。
2. **每笔写入都进 `Journal.Diffs`**（含 `@created` / `@destroyed` 两条结构性 diff），
   所以 UI 的因果链和 `sim --explain` 是同一份数据。
3. **`World.WriteRaw` 是 `internal`**：插件拿不到，因此不存在「悄悄改状态」这条路。

---

## 4. 数据模型（`World.cs` / `EntityId.cs` / `SimValue.cs` / `Fix.cs`）

### 4.1 `EntityId`（✅，`readonly struct`）

`new EntityId("town.0003")` · `IsEmpty` · `Prefix`（前 4 字符 = kind 前缀）· `Parts`
· `static New(kind, index)` → `kind.NNNN`（零填充 4 位）· `Field(path)`
· 比较一律 `StringComparison.Ordinal`（`CompareTo`/`Equals`/`GetHashCode`）——
**id 的排序就是全局遍历顺序**（§10.7）。

### 4.2 `World`（✅）

```csharp
static EntityId GlobalId = new("world.0000");  const string GlobalKind = "world";
long Revision { get; }                          Entity Global { get; }
int EntityCount { get; }
Entity Create(string kind, EntityId? explicitId = null);   // 自动 id 走 per-kind 计数器
bool Remove(EntityId) / bool Has(EntityId) / Entity? Get / Entity GetRequired
IReadOnlyList<Entity> All() / OfKind(string) / Kinds()
SimValue GetField(EntityId, string) / GetFix / GetInt / GetStr / GetBool（带 fallback 的重载）
IEnumerable<(EntityId, string, SimValue)> Flatten()
```

- `_entities` 与每个 kind 分组都是 `SortedDictionary`（ordinal）：遍历顺序与插入顺序无关。
- `WriteRaw` / `MergeInto` / `KindCountersSnapshot` / `RestoreKindCounters` 是 `internal`。
- 全局标量寄居在 `world.0000` 上：Core 写 `meta.seed / meta.schema / meta.gameVersion /
  meta.mapGenVersion / meta.turn / meta.yearBase`；插件写 `report.*` 与 `config.*`。

### 4.3 `Entity`（✅）

`Get(path)` → `SimValue` · `Has` · `GetFix/GetInt/GetStr/GetBool`（含 fallback 重载）
· `Prefix("stock.")` · `FieldPaths(prefix)` · `Count(prefix)`。字段表 `SortedDictionary` 是 `internal`
（读得到枚举，写不进去）。

### 4.4 `SimValue`（✅，`readonly struct`：`Kind + Number + Text`）

| `ValueKind` | 存法 | 典型用途 |
|---|---|---|
| `None` | — | 「没有这个字段」；`Mutate(... SimValue.Null)` 等于删除，但**优先用 `RemoveField`**（`core.no_empty_field` 会盯 None 残留） |
| `Int` | `Number` | 人口、等级、回合号、满意度 0–100 |
| `Fix` | `Number` = Raw（1/100） | 一切量：粮、价格、倍率、百分比 |
| `Str` | `Text` | 名称、枚举标签、引用 id |
| `Bool` | `Number` 0/1 | `nation.player`、`hex.buildable` |

`Canonical`（`"i5"` / `"f500"` / `"s魏"` / `"b1"` / `"n"`）是**哈希、存档、diff 三处共用的序列化形式**；
`ParseCanonical` 是其逆运算。带类型标签是为了让 `1` 与 `"1"` 不撞哈希。
`Plus(other)`：同类型相加；`Fix+Int` 统一到 `Fix`；`Str` 相加抛 `SimStateException`。

### 4.5 `Fix`（✅，×100 定点）

```csharp
const long Scale = 100;
FromRaw / FromInt / FromPercent(int) / FromDecimalText(string) / Zero One Half MinusOne MaxValue MinValue
+ - 取负  Mul(a,b) Div(a,b) Mul(a,long) Div(a,long)  Min Max Clamp
IsZero IsNegative IsPositive IsZeroOrLess IsOneOrMore Abs
< > <= >= == != CompareTo Equals
Text（两位小数，负号正确）  ToDouble()（只许显示）  RoundToInt() FloorToInt()
```

- 乘除用 `UInt128` 中间量（net8.0 无 `Int126`），溢出**抛异常**而不是回绕。
- 除法**向零截断**（不是银行家舍入）→ 全平台一致。`Div(a, Zero)` 返回 `Zero`，
  但内部 `MulDiv` 的分母为 0 会抛：调用方应先判分母（GDD §5.3 的 `max(S, 0.5)` 就是这个意思）。
- `ToDouble()` 与 `float` **禁止参与结算比较**（§10.7）。

---

## 5. 数据包：`DataRegistry`（✅，`src/SimCore/DataRegistry.cs`）

```csharp
static DataRegistry Load(string rootDir, bool stopOnFirstError = true);
int Count { get; }  string DataHash { get; }  IReadOnlyList<string> LoadedPacks { get; }
IReadOnlyList<string> Types { get; }  IReadOnlyList<string> Warnings { get; }
IReadOnlyDictionary<string, TypeSchema> Schemas { get; }
IReadOnlyList<DataEntry> OfType(string type);
bool TryGet(string id, out DataEntry) / DataEntry Get(string id) / bool Exists(string defRef);
DataEntry Resolve(string defRef);          // "def:building.farm" → entry
DataRegistry Clone();
const string DefPrefix = "def:";  static string SchemaFileName = "_schema.json";
```

`DataEntry`：`Id` `Type` `Source`（文件路径）`Pack` `Root: JsonElement`（任意结构，插件自解释）
`Scalars: <路径, SimValue>` + `Get/GetFix/GetInt/GetStr/Has/GetArray/GetObject`。
`TypeSchema`：`Type` `IdPrefix` `Required` `ScalarTypes` `Refs`——校验用的表型定义。

细节（`_schema.json` 的写法、校验规则、`dataHash` 算法）在 `docs/DATA_SCHEMA.md`。

---

## 6. 命令（✅，`src/SimCore/Commands.cs`）

```csharp
class Command { int Turn; int Seq; string Issuer; string Type;
                IReadOnlyDictionary<string, SimValue> Args;   // SortedDictionary(ordinal)
                string Reason; int AcceptedTurn;              // -1 = 尚未接受
                SimValue Arg(key) / string ArgStr / Fix ArgFix / long ArgInt
                string Canonical() / string Text; static Command Build(...) }

record CommandResult(bool Accepted, string CommandText, string CanonicalText,
                     string Reason, Fix ExecutionRate, IReadOnlyList<string> ExplainTerms);
static CommandResult Accept(Command, why, Fix rate, terms?)  /  Reject(Command, why)

class CommandBus { IReadOnlyList<Command> Pending / Log; int Turn; bool StrictSeq = true;
                   string LogHash; Command Submit(...); Command Submit(Command);
                   IReadOnlyList<Command> DrainPending(); }
```

要点：

- **业务合法性在第 1 步判**，不在 `Submit` 判：意图段的世界状态和结算时的不是一回事。
- `DrainPending` 按 `(Turn, Seq, Issuer, Type)` 全序入日志——联机时提交到达顺序不确定，
  定序不能靠到达序（§8.3 规则 2）。
- `LogHash` 初值必须是 `StableHash.FnvOffset`，且 `Restore` 用同一初值重放——
  否则「一口气跑 N 回合」与「读第 N 回合档再跑」的哈希天生不同（`sim replay` 会红）。
- `ExecutionRate` 是 §7.5「上令不一定下达」的落点；`Reject` 时为 0。

---

## 7. Hook（✅，`src/SimCore/Hooks.cs`）

`enum HookPoint`（0–20，共 21 个 + `Count`）：见 `docs/PLUGIN_API.md` 第 2 节的完整表。
`WhereHookLands(point)` 给人读的落点，`DescribeHookMap()` 给文档。

```csharp
class HookArgs { EntityId Subject; bool HasSubject;
                 HookArgs With(key, SimValue|Fix|long|string|EntityId);
                 SimValue Get(key) / bool TryGet / GetFix / GetInt / GetStr / GetEntity / TryGetEntity;
                 SimValue Result { get; set; }          // "result" 槽位：OnActionValidate 写回
                 IReadOnlyDictionary<string,SimValue> Values / <string,EntityId> Entities;
                 static HookArgs Empty; }

delegate void HookHandler(HookArgs args, IStepContext ctx);
delegate void SubjectHandler(EntityId subject, HookArgs args, IStepContext ctx);

class HookRegistry { IReadOnlyList<HookPoint> RegisteredPoints;
                     IReadOnlyList<string> RegisteredSubjectLines;
                     void Register(point, pluginId, priority 0..1000, handler);
                     void RegisterSubject(point, kind, pluginId, priority, handler);
                     void Dispatch(point, args, ctx);
                     void DispatchSubjects(point, kind, orderedSubjects, ctx, argsFactory?);
                     int Count(point); }
```

- 三级排序 `(priority, pluginId ordinal, 注册序号)`；`priority` 越界（<0 或 >1000）在注册时就抛。
- `Dispatch` 前先拷贝快照：允许 handler 在运行时注册新 handler（事件解锁用），但本次不生效。
- handler 抛异常**直接上抛**，Core 不吞（§10.5「不允许半加载/静默失败」的对称要求）。
- 主题分派为每个 subject 新建 `HookArgs`，插件之间不共享参数袋。

---

## 8. 不变量与因果（✅，`Invariants.cs` / `Mutation.cs` / `StepContext.cs`）

```csharp
delegate InvariantResult InvariantCheck(IStepContext ctx);
record InvariantResult(IReadOnlyList<string> Violations) { static Ok; bool Passed; static Fail(params string[]) }
record InvariantRule(string Name, string Owner, IReadOnlyList<int> Steps, InvariantCheck Check, bool Fatal = true)
{ bool AppliesTo(int step); }        // Steps 为空 = 每步末都跑
class  InvariantGuard { void Register(name, owner, steps, check, fatal); void Remove(name);
                        IReadOnlyList<InvariantRule> Rules; int CountForStep(step);
                        internal IReadOnlyList<string> RunAfter(step, ctx, throwOnFatal);
                        void RegisterCoreChecks(); }

enum MutationOp { Set, Add, Remove }
record Diff(int Turn, int Step, EntityId Id, string Path, string Before, string After, MutationOp Op, string Reason)
{ string DeltaText; }
record Explanation(int Turn, int Step, EntityId Subject, string Title, string Formula,
                   IReadOnlyList<string> Terms, string Result);
class  TurnJournal { Diffs / Explanations / Notes; ForEntity(id); ForField(id, path); RenderChain(world, id, path); }
```

Core 自带三条（都在第 15 步末）：`core.has_reason`、`core.no_empty_field`、`core.hash_stable`。
`RunAfter` 按规则名 ordinal 排序执行；检查器自己抛异常会被记成违规（「检查器坏了」不算通过）。
守恒类断言属于玩法插件（`core.basic` 的 `basic.ranges` 是示范，见 `docs/TEST_PLAN.md`）。

---

## 9. 随机与哈希（✅，`Rng.cs` / `StateHash.cs`）

```csharp
static class StableHash { const ulong FnvOffset = 14695981039346656037; 
                          ulong Of(string?|long); ulong Mix(a,b); string Hex(v) /* x16 */ }
class RandomStream { RandomStream(ulong seed); ulong State {get;set;}
    ulong NextUInt64(); int NextInt(min,maxExcl); long NextLong(...); bool Chance(Fix p);
    int PickWeighted(IReadOnlyList<long>); Fix NextGaussian(Fix mean, Fix sd);
    T Pick<T>(IReadOnlyList<T> sortedItems); List<int> SampleIndices(count,k); int D10();
    ulong NextBits53(); double NextUnitDouble(); }   // 两个 double 方法只许用于统计显示
class RngFactory   { RngFactory(ulong worldSeed); ulong WorldSeed; RandomStream Global;
                     RandomStream Stream(string ns); Stream(EntityId scope, string ns); Stream(scope, ns, subKey);
                     RandomStream OneShot(string ns); }
class StateHash    { IReadOnlyDictionary<string,string> Chunks; string Total; int FieldCount;
                     static Compute(world, commands, rng); static string? FirstDifference(a, b); }
```

- SplitMix64（`Gamma = 0x9E3779B97F4A7C15`）；`State` 可读写，这就是存档里的 RNG 快照。
- 命名空间必须是 `"{scope}@{ns}"` 形式并**含回合号**（由调用方保证，例：`town.0003@harvest#12`）；
  `AdvanceTurn` 末尾 `ResetNamespaceStreams()`，跨回合复用同名流属于违规（§8.3 纪律 1）。
- `Chunks` 按 kind 分块：联机发现分歧时先定位 kind，再二分回合（§9.3）。
- `Total` 混入：全部 kind 分块 + `meta.turn` + `Commands.LogHash` + `Rng.Global.State`。

---

## 10. 存档与同步（✅，细节见 `SAVE_FORMAT.md` / `NETWORKING.md`）

```csharp
class SaveGame { SchemaVersion, GameVersion, MapGenVersion, WorldSeed, Turn, YearBase,
                 PluginVersions, Entities, Commands, DataHash, RngGlobalState, RngStreams,
                 KindCounters, SnapshotHash, SavedUtcText }
record SaveEntity(string Id, string Kind, IReadOnlyList<(string Path, string Value)> Fields)
record SaveCommand(int Turn, int Seq, string Issuer, string Type, string Reason, IReadOnlyList<(string,string)> Args)
static class SaveStore { const string Extension = ".hcsave"; Capture(sim); ToJson(save);
                         Write(path, save, note?); Read(path) }
interface ISaveMigrator { int FromVersion; int ToVersion; string Description; void Migrate(JsonNode root, string path); }
static class MigratorChain { Register(m); Registered; Clear(); Run(node, path, out owned) }

enum SyncMode { Solo, Hotseat, Online }
interface ITransport { SyncMode Mode; void Submit(Command); bool TryDequeueReceived(out Command);
                       void BroadcastHash(int turn, StateHash); IReadOnlyList<string> Diagnostics; }
class LoopbackTransport : ITransport { StateHash? LastHash; }     // 单机/热座
class CommandStream { Record(c); All; OfTurn(t); Text(); Hash(); } // 🟡 回放/联机对账的原料，M1 仅测试使用
```

`Write` 的行为（先写 `.tmp` → 自检读回 → 旧档转 `.bak` → 原子 `File.Move`）与 JSON 键表在
`docs/SAVE_FORMAT.md`；M1 **没有** zstd（GDD §10.6 写了 zstd，实现用 `System.IO.Compression.GZipStream`），
这条偏差记在 `docs/CHANGELOG-DESIGN.md`。

---

## 11. 插件宿主（✅，`Plugins.cs` / `PluginHost.cs`）

```csharp
interface IPlugin { string Id { get; } void OnLoad(IPluginApi api); void OnUninstall() { } }
class PluginManifest { id version api dependencies load_priority dll enabled_by_default title summary data_packs
                       string EffectiveDll; static TryRead(path); string ToJson() }
record LoadedPlugin(PluginManifest Manifest, IPlugin Instance, Assembly Assembly, string Directory) { Id; Title; }
record DiscoveredPlugin(PluginManifest Manifest, string Directory)
class PluginHost { IReadOnlyList<LoadedPlugin> Loaded; IReadOnlyList<string> LoadLog; ISet<string> Disabled;
                   LoadedPlugin? Find(id); IReadOnlyList<string> Ids();
                   IReadOnlyList<DiscoveredPlugin> Plan(pluginsRoot);           // 只校验清单，不加载程序集
                   void LoadFromDirectory(root, IPluginRegistrar);
                   void LoadFromAssemblies(assemblies, IPluginRegistrar);
                   IReadOnlyList<string> DescribeLoaded(); }
static class CoreInfo { ApiVersion "1.1"; ApiMajor 1; ApiMinor 1; SchemaVersion 1; GameVersion "0.1.0";
                        MapGenVersion 6; string Describe }
```

- `PluginManifest.TryRead` 会**拒绝未知键**（`@` 开头的除外）。这是防「驼峰键静默落回默认值」，
  M1 真实踩过（记 CHANGELOG-DESIGN）。
- 装载检查顺序：文件存在 → api 主版本 → 依赖齐全 → 无环 → 注册成功。任一失败 → `SimPluginException`
  且回滚该插件的全部注册（`IPluginRegistrar.AbandonPlugin`），**不允许半加载**。
- 插件程序集装在 `PluginLoadContext`（`AssemblyLoadContext` 子类），同名依赖优先从**宿主上下文**解析，
  这样插件绑定的 `IPlugin` 与宿主是同一个 `Type`。

---

## 12. 异常契约（`Exceptions.cs`，✅）

| 类型 | 何时 | 壳层应当 |
|---|---|---|
| `SimDataException` | 数据包/schema 校验失败 | 显示 + 指明文件 |
| `SimStateException` | 调用顺序/参数非法（未 Bootstrap、步骤号越界、id 重复、定点溢出、对字符串求和） | 这是 bug，报错并附 `SIM_STACK=1` 的堆栈 |
| `SimPluginException` | 插件拒载（含 `PluginId`） | 提示禁用该插件 |
| `SimInvariantException` | 不变量违反（`Violations` 是全部条目） | 「崩在当回合」，逐条显示 |
| `SimSaveException` | 存档读写/迁移/哈希校验失败 | 提示同名 `.bak` |
| `SimCommandRejectException` | 命令执行器内部要求中止 | 少见；一般用 `CommandResult.Reject` |

CLI 的退出码：`0` 正常 · `2` 模拟异常 · `3` 非预期异常 · `64` 参数/脚本错误
（`src/HexaCiv.Cli/Program.cs`）。这些数字进了 CI 判定，改动要同步 `docs/TEST_PLAN.md`。

---

## 13. 想在 Core 里加东西？

1. 先确认这不是玩法知识（GDD §10.2 的排除清单）。
2. 写进本文件：新增成员 + 版本条目（minor 或 major）+ 影响面（存档？哈希？插件兼容？）。
3. 实现 + 测试（新公开面必须有测试覆盖，否则「接口存在但没人用过」会在 M2 反噬）。
4. 若破坏插件兼容：`CoreInfo.ApiVersion` 升主版本，并在 `docs/CHANGELOG-DESIGN.md` 写迁移动作。
5. 提交信息带 `[数值零变化]` / `[破存档→已附迁移器]` / `[改手感]`。

Core 里**不该有**：地形规则、阶级、经济公式、科技、宗教、军事、名人生成、外交、AI 决策、
任何 `float` 参与结算、任何 `Dictionary` 枚举顺序依赖、任何 `DateTime.Now`。

# PLUGIN_API · 插件规范与 hook 契约

> 面向「要写规则的人」。规范来源：GDD §10.3（插件清单）、§10.4（hook 契约）、
> §10.5（数据格式）、§10.7（确定性）。实现来源：`src/SimCore/Hooks.cs`、`PluginHost.cs`、
> `Plugins.cs`、`Mutation.cs`、`StepContext.cs`。
> 本文第 3 节的落点表由 `SimOrder.DescribeHookMap()` 生成，对账命令 `sim check`。

---

## 1. 一个插件是什么

一个目录 = 一份 `manifest.json` + 一个程序集（+ 可选的数据包目录）。
`Simulation.Bootstrap` 按 `PluginHost.Plan()` 的顺序装载它们，插件之间**禁止直接调用**：
唯一的通信手段是 hook 与共享的世界状态（GDD §10.4）。

装载检查顺序（任一失败 → `SimPluginException`，并回滚该插件已注册的一切，**不允许半加载**）：

```
目录名 == manifest.id  →  manifest.json 能读  →  键名都是合法键
→  api 主版本 == CoreInfo.ApiMajor(1)  →  dependencies 都在且未被禁用
→  无循环依赖  →  程序集能找到  →  里面有一个 Id 匹配的 IPlugin 实现  →  OnLoad 跑完
```

GDD §10.3 的规划（20 个插件）到 M6 才会逐个出现；M1 只有 `core.basic` 一个，
但上面的排序、依赖、禁用、回滚逻辑已经全部生效并有测试。

---

## 2. `manifest.json`

```jsonc
{
  "@comment": "以 @ 开头的键是给人看的注释字段，Core 允许",
  "id": "core.basic",              // 必填，必须等于目录名
  "version": "0.1.0",              // 进存档 pluginVersions，只做诊断
  "api": "1.0",                    // 必填，主版本不符直接拒载
  "dependencies": [],              // 其他插件的 id
  "load_priority": 10,             // 同层依赖时小的先加载（默认 100）
  "dll": "core.basic.dll",         // 省略则取 <id>.dll
  "enabled_by_default": true,      // 诊断：false 时 PluginHost.Disabled 会收它
  "title": "基础规则包",
  "summary": "……",
  "data_packs": ["core.basic"]     // 🟡 M1 只解析与打印，数据包仍按目录整体加载
}
```

三条硬规则（都是 M1 真踩过的坑，见 `docs/CHANGELOG-DESIGN.md`）：

1. **键名是 snake_case**，与 `PluginManifest` 上的 `[JsonPropertyName]` 一一对应
   （GDD §10.5 的示例就是 `"load_priority": 30`）。
2. **不认识的键会被拒绝**而不是静默忽略（`PluginManifest.TryRead`）。写错一个字母不会
   得到一个「悄悄落回默认值」的插件——`PropertyNameCaseInsensitive = false`，所以驼峰也算写错。
3. **目录名必须等于 `id`**。插件目录里躺着别的东西（例如没有 manifest 的文件夹）会被跳过，不报错。

---

## 3. hook 点全表与落位

`enum HookPoint` 共 21 个（`Count` 只是数组长度，不是点）。下面是 `sim check` 打出来的落位表
（`pre` = 该步首，`post` = 该步末，`prepost` = 首尾同一个点、广播两次）：

```
OnTurnStart → 01pre, 02pre
OnActionValidate → 01post
OnProduction → 02post, 03prepost, 04prepost, 05prepost
OnConsumption → 06post, 10prepost
OnPriceForm → 06pre, 07pre, 08pre
OnAllocate → 09prepost, 11prepost
OnSatisfaction → 12pre
OnClassTransition → 12post, 13pre
OnMigration → 07post, 13post
OnLogisticsCheck → 14pre
OnBattleResolve → 14post
OnEventDraw → 15pre
OnEventApply → 不落在任何步骤（意图段 / 存档期 / 命令执行器内）
OnHeroRoll → 不落在任何步骤（意图段 / 存档期 / 命令执行器内）
OnAppointment → 不落在任何步骤（意图段 / 存档期 / 命令执行器内）
OnDemand → 不落在任何步骤（意图段 / 存档期 / 命令执行器内）
OnDiplomaticAction → 08post
OnTurnEnd → 15post
OnBeforeSave → 不落在任何步骤（意图段 / 存档期 / 命令执行器内）
OnLoad → 不落在任何步骤（意图段 / 存档期 / 命令执行器内）
OnMigrate → 不落在任何步骤（意图段 / 存档期 / 命令执行器内）
```

`OnEventApply / OnHeroRoll / OnAppointment / OnDemand` 这四个点与 `OnMigrate`
**不在结算表里**，不是「没用的点」：

- `OnEventApply` 归 `events.core`——事件抽到之后由它自己 `ctx.Require`（M5 落地）；
- `OnHeroRoll / OnAppointment / OnDemand` 归名人与 AI 系统，在**意图段的命令执行器内部**广播；
- `OnBeforeSave`（`Simulation.cs:436`）与 `OnLoad`（`:480`）已经由 Core 广播，
  参数里带 `schemaVersion`；`OnMigrate` 目前由 `MigratorChain` 直接调迁移器，**不走 hook**
  （第一条真接线在第一次破存档的迁移器上，见 `docs/SAVE_FORMAT.md`）。

M1 没有任何插件调用这五个点，所以 `sim check` 的表里它们是空的。

广播时 `HookArgs` 里固定有（`Simulation.StepArgs`，`src/SimCore/Simulation.cs:399`）：

| 槽位 | 类型 | 值 |
|---|---|---|
| `Subject` | `EntityId` | 全局扇出时是 `world.0000`；主题分派时是主题实体 |
| `step` | `int` | 1–15；结算外（bootstrap / OnTurnEnd / OnLoad）为 0 |
| `stepId` | `str` | 如 `agriculture` |
| `slot` | `str` | `pre` / `post`（常量 `SimOrder.SlotPre/SlotPost`） |
| `layer` | `str` | 步骤声明的层（`agriculture`/`local`/`domestic`/`foreign`…），空则不带 |

命令路径另外带：`command`（命令类型）、`issuer`、`arg.<名>`，以及**回写槽** `result`
（值为 `"reject:<原因>"` 时命令被驳回，见 §7）。

---

## 4. 全局级 vs 主题级：扇出点模式

Core 广播的是**全局步骤事件**，而 §10.4 想让插件写 `OnProduction(town, ctx)`。
两者的桥就是「扇出点」——`core.basic` 的写法（`src/Plugins/core.basic/BasicPlugin.cs`）：

```csharp
private static HookHandler FanOut(HookPoint point, string kind, string stepId, string slot) =>
    (args, ctx) =>
    {
        if (!SimOrder.At(args, stepId, slot)) return;                 // 位置门闸：一年只跑一遍
        ctx.DispatchSubjects(point, kind, ctx.EntitiesOfKind(kind).Select(e => e.Id).ToList());
    };

// 注册：先挂扇出点（全局），再挂主题处理器（逐城）
api.Require(HookPoint.OnProduction, 100, FanOut(HookPoint.OnProduction, F.KindTown, "agriculture", SimOrder.SlotPre));
api.RequireFor(HookPoint.OnProduction, F.KindTown, 100, SeasonRules.Produce);
```

为什么要这么绕两层：

- `DispatchSubjects` 由 Core 保证**按 id ordinal 升序**遍历（`StepContext.DispatchSubjects`），
  这就是 §10.7 的「遍历顺序无关」；
- 每个主题**新建一份 `HookArgs`**（`HookRegistry.DispatchSubjects`），插件之间不会互相污染参数袋；
- 「哪些实体算这个主题」是玩法知识（城镇归 `town.core`，地块归 `map.gen`），
  所以只有拥有该概念的插件能决定，Core 只认 kind 字符串。

同一个 hook 点会落在多个步骤上（`OnProduction` 落在 2–5 步），所以门闸必须是
`(stepId, slot)` 这一对。只判 `stepId` 或只判 `slot` 都会漏或重复。

---

## 5. 注册顺序与 `priority`

`HookRegistry` 的三级排序：**priority 升序 → pluginId ordinal 升序 → 注册序号升序**。
0–1000，越界在注册时抛异常。`priority` 只决定**同一列表内**的先后：

| 段 | 用途 | M1 的实例 |
|---|---|---|
| 0–49 | 前置修正（把派生量算好给别人读） | — |
| 50–99 | 折算与预算 | `TownOps.Facilities` = 90（第 2 步末） |
| 100 | 正常规则（默认值） | core.basic 的扇出点、四个主题处理器 |
| 101–800 | 修正与覆盖（后到的改前到的） | 未来 `econ.market` 覆盖价格时用 |
| 801–1000 | 收尾、汇总、写报告 | — |

同 priority 时按插件 id 定序 ⇒ **两个插件抢同一个数就是抢 id**，写进 manifest 之前先在
`docs/CHANGELOG-DESIGN.md` 里说好谁前谁后。

---

## 6. `IPluginApi`（OnLoad 唯一能拿到的东西）

```csharp
string PluginId { get; }  PluginManifest Manifest { get; }  DataRegistry Data { get; }
IReadOnlyList<LayerInfo> Layers { get; }

void Require(HookPoint point, int priority, HookHandler handler);
void RequireFor(HookPoint point, string entityKind, int priority, SubjectHandler handler);
void RegisterCommand(string type, CommandExecutor executor, string argDocs = "");
void RegisterBootstrap(BootstrapInitializer init);
void RegisterInvariant(string name, IReadOnlyList<int> steps, InvariantCheck check, bool fatal = true);
void RegisterLayer(string id, string title, int order = 100);
void Log(string message);
```

`OnLoad` 阶段的世界是**只读**的：`StepContext` 在 `Step == -1` 时拒绝一切写入
（「注册期不得写状态：OnLoad 只负责注册，不负责改世界」）。想改世界就注册
`RegisterBootstrap`，它在开局以 `step = 0` 跑，那时允许 `CreateEntity/Mutate`。

`Log` 进 `Simulation.StartupLog`，`sim check` / Godot 输出面板都能看到；
`RegisterLayer` 只登记元数据（Core 不解释图层内容）。

---

## 7. 命令执行器

```csharp
public delegate CommandResult CommandExecutor(Command cmd, IStepContext ctx);
```

```csharp
api.RegisterCommand("town.found", TownOps.Found,
    "hex=<地块> name=<可省> nation=<可省：不填则成独立城镇>");
```

- **参数一律扁平标量**（`SimValue`），键有序；`cmd.ArgStr("hex")` / `ArgInt` / `ArgFix` 读。
  `def:` 前缀两种写法都允许（`F.BareId`）。
- 合法性判定发生在这里（不是 `Submit`）：那时才看得到当年的世界状态。
- **`reason` 必填**：`ctx.Mutate/Add/CreateEntity/Explain` 全部要带理由，
  第 15 步的 `core.has_reason` 会检查每一笔 diff。
- 驳回用 `CommandResult.Reject(cmd, "为什么")`；打折用 `CommandResult.Accept(cmd, 理由, Fix < 1.00, terms)`
  ——`§7.5 上令不一定下达` 靠这个而不是靠禁止（GDD §2.5）。
- 执行器必须**幂等**：同一条命令重放（联机、回放对账）不能二次扣粮。资源检查写在前面，
  写在 `ctx` 上而不是静态字段里。
- 未注册的类型在第 1 步被驳回并留原因「没有插件注册命令 X」，不会抛异常。

---

## 8. 不变量：写规则时必须同时交的合同

```csharp
api.RegisterInvariant("basic.ranges", new[] { 2, 3, 10, 12, 13 }, Sanity);
```

- `steps` 传空表 = 每步末都跑（默认建议指定步骤，别拖慢整年）。
- 返回 `InvariantResult.Ok` 或 `InvariantResult.Fail(逐条描述)`；**一条检查可以报多条**，
  「哪一项、差多少」是 GDD §10.6 的硬要求。
- `fatal: true`（默认）且 `SimConfig.FatalInvariants` 为真时 → `SimInvariantException`，回合崩掉。
  非致命条目会标 `(非致命)` 并只进年报。
- Core 不知道「粮不能为负」——它只提供守卫和三条结构检查
  （`core.has_reason` / `core.no_empty_field` / `core.hash_stable`）。
  **量纲类断言一律属于插件**：`core.basic` 的 `Sanity` 是示范（人口非负、库存非负、
  `Facility ∈ [1.00,1.45]`、`sat ∈ [0,100]`、城址双向引用一致）。

写错规则的最坏结果不是「报错」而是「误差扩散」，所以新规则的默认态度是**宁可崩在当回合**。

---

## 9. 字段路径与数据引用命名（GDD 附录 D 的代码化）

规范集中在 `src/Plugins/core.basic/F.cs`（**新插件请另开一个静态类，别改这个**）：

- 路径形如 `实体种类.组件.字段`，全小写点分；
- **引用数据包 id 时去掉类型前缀**：农民人口是 `town.pop.peasant`，不是 `town.pop.class.peasant`；
  代价是短名必须全局唯一，`class.peasant` 与 `good.peasant` 撞名要在数据校验那关就炸掉；
- 建筑在城上写 `town.building.<短名>`，在地上写 `hex.building.<短名>`；
- 数据包引用一律 `def:building.farm` 形式（`DataRegistry.DefPrefix`），`Exists/Resolve` 做校验；
- 全局标量寄居 `world.0000`：`config.mapRadius`、`report.towns`、`meta.*`（Core 只写 `meta.*`）。

年度报告的键是 `report.*`（`TurnReport.Headline` 的字典键**是完整路径**，UI 取时要带上前缀），
由玩法插件写，Core 每回合开头先清掉上一年的。

---

## 10. 最小新插件骨架

`src/Plugins/mill.units/manifest.json`：

```json
{
  "id": "mill.units",
  "version": "0.1.0",
  "api": "1.0",
  "dependencies": ["core.basic"],
  "load_priority": 40,
  "data_packs": ["mill.units"],
  "title": "兵种模板",
  "summary": "招募、编制、补给需求"
}
```

`src/Plugins/mill.units/mill.units.csproj`（照 `core.basic.csproj` 抄，三处改名）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>MillUnits</RootNamespace>
    <AssemblyName>mill.units</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- Private=false：SimCore 由宿主进程先加载，插件目录里不留第二份 -->
    <ProjectReference Include="..\..\SimCore\SimCore.csproj" Private="false" />
    <None Include="manifest.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <Target Name="PublishToPluginDir" AfterTargets="PostBuildEvent">
    <PropertyGroup><PluginOut>$(RepoRoot)build\plugins\mill.units\</PluginOut></PropertyGroup>
    <MakeDir Directories="$(PluginOut)" />
    <Copy SourceFiles="$(TargetPath);manifest.json" DestinationFolder="$(PluginOut)" SkipUnchangedFiles="true" />
  </Target>
</Project>
```

入口：

```csharp
using SimCore;

namespace MillUnits;

public sealed class UnitsPlugin : IPlugin
{
    public string Id => "mill.units";            // 必须等于 manifest.id 与目录名

    public void OnLoad(IPluginApi api)
    {
        api.Require(HookPoint.OnLogisticsCheck, 100, (args, ctx) =>
        {
            if (!SimOrder.At(args, "military", SimOrder.SlotPre)) return;
            // 新插件请自己开一个 F 风格的静态常量类，别改 core.basic 的 F（§9）
            const string Supply = "unit.supply";
            foreach (var unit in ctx.EntitiesOfKind("unit"))
                ctx.AddInt(unit.Id, Supply, -1, $"第 {ctx.Step} 步：行军消耗");
        });
        api.RegisterCommand("unit.recruit", Recruit, "town=<城镇> type=def:unit.light");
        api.RegisterInvariant("units.nonneg", new[] { 14 }, ctx => /* … */ InvariantResult.Ok);
        api.Log("mill.units 就绪");
    }

    private static CommandResult Recruit(Command cmd, IStepContext ctx)
    {
        EntityId town = F.Id(cmd.Arg("town"));                      // 解析参数：见 §7
        if (F.IsEmpty(town) || !ctx.Has(town))
            return CommandResult.Reject(cmd, "参数 town 缺失或实体不存在");
        ctx.AddInt(town, "unit.queue", +1, $"{cmd.Type}：排队征兵");  // 写入必须带 reason
        return CommandResult.Accept(cmd, "已入征兵队列", Fix.One,
            new[] { $"队列 = {ctx.GetInt(town, "unit.queue")}" });
    }
}
```

注意 `data_packs` 目前只影响 `sim check` 的显示（§2 的 🟡），新插件的数据包还是得落在
`data/<pack>/` 下才会被 `DataRegistry.Load` 读到。

---

## 11. 调试与拒载之后

| 症状 | 先看哪里 |
|---|---|---|
| `插件 X 加载失败：…` | `SimPluginException.Message` 会说清是哪一关：清单/键名/api 版本/依赖/环/程序集/`IPlugin` 类型/Id 不符/OnLoad 抛异常 |
| 「程序集里没有 Id=X 的 IPlugin 实现」 | 大概率是**绑了两份 SimCore**。修法是 `PluginLoadContext` 优先宿主上下文（`ARCH.md` §6）；临时验证可设 `SIM_STACK=1` |
| `sim check` 只看清单不加载程序集 | 「清单与依赖校验通过（未实例化）」是刻意的：CI 里先看依赖对不对，再看能不能跑 |
| 想单独禁掉一个插件 | `Simulation` 构造后 `Plugins.Disabled.Add("mill.units")`；CLI 用 `--no-plugins` 全关 |
| 规则跑了两遍 | 门闸没看 `slot`（§4） |
| 哈希飘了 | 检查字典枚举顺序 / `float` / `DateTime.Now` / 命名空间流没带回合号（`docs/CORE_API.md` §9） |

对账命令：`sim check` · `sim hash --seed 99 --turns 50` · `sim replay --seed 5 --turns 20 --at 12` ·
`dotnet test`（含 `M1 非法插件被拒载`、`M1 依赖排序` 等验收用例）。

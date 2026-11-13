# SIM_ORDER · 固定结算顺序的详细版

> GDD §2.3 是这张表的**规范**（改它要先改 GDD，见 §11.3）。本文件是它的**实现说明**：
> 每一步在代码里长什么样、hook 落在哪、谁挂在上面、以及为什么这个顺序不能动。
>
> 生成源是代码，不是这份文档：
> - `src/SimCore/Time.cs` 的 `SimOrder.Steps`（15 行，逐条对应 GDD §2.3 的 15 步）
> - `src/SimCore/Time.cs` 的 `SimOrder.Describe()` / `DescribeHookMap()`
>
> 对账命令：`sim check`。它把上面两个函数原样打印出来；本文件第 2、3 节的代码块就是它的输出。
> 如果有人改了 `SimOrder` 而没改本文件，`sim check` 与本文件第 2 节就会不一致——
> 这是设计好的：文档允许落后，但**不许领先**（以代码为准）。

---

## 1. 一句话规则

**顺序不可调换，插件不可插队。** 插件只能在既定的 22 个 hook 点上挂处理器，
点的落位由 Core 决定；同一个点在同一步的首尾各广播一次，靠 `slot` 参数区分。

为什么这么硬：结算是一个**有向无环的因果链**。生产 → 分配 → 消费 → 满意度 → 人口 →
下一年的生产。顺序一乱，就会出现「今年歉收 → 今年粮价跌 → 今年又算出丰年」这种
**同年自噬**（GDD §2.3 的脚注）。所以顺序属于「物理定律」，调它等于改物理学，
必须走 `docs/CHANGELOG-DESIGN.md` 裁决 + 回归测试（见第 6 节）。

---

## 2. 十五步（`sim check` 的 `SimOrder.Describe()` 输出）

```
01 应用已批准的命令与政策（扣 AP） | Intent | pre=OnTurnStart | post=OnIntentEnd | layer=—
02 建筑与设施生效/完工 | Spring | pre=OnTurnStart | post=OnProduction | layer=—
03 农业产出（春播秋收的净效果） | Spring | pre=OnProduction | post=OnProduction | layer=agriculture
04 工业生产（含原料约束） | Spring | pre=OnProduction | post=OnProduction | layer=industry
05 服务与非物质产出 | Spring | pre=OnProduction | post=OnProduction | layer=services
06 城镇市场出清（公式二·本地层） | Summer | pre=OnPriceForm | post=OnConsumption | layer=local
07 国内贸易（运输成本加权） | Summer | pre=OnPriceForm | post=OnMigration | layer=domestic
08 国际贸易（关税、关系、封锁） | Summer | pre=OnPriceForm | post=OnDiplomaticAction | layer=foreign
09 工资、利润、俸禄、军饷发放 | Autumn | pre=OnAllocate | post=OnAllocate | layer=—
10 阶级消费与需求满足率 | Autumn | pre=OnConsumption | post=OnConsumption | layer=—
11 税收与财政结算（含腐败漏损） | Autumn | pre=OnAllocate | post=OnAllocate | layer=—
12 满意度更新与阶级状态迁移（公式三） | Winter | pre=OnSatisfaction | post=OnClassTransition | layer=—
13 人口增长、阶级转化、城镇间迁移 | Winter | pre=OnClassTransition | post=OnMigration | layer=—
14 军事结算（补给、战斗、占领） | Winter | pre=OnLogisticsCheck | post=OnBattleResolve | layer=—
15 外交、名人诉求、事件抽取、年度报告、快照 | Winter | pre=OnEventDraw | post=OnTurnEnd | layer=—
```

四段结构与步数（`SimOrder.OfSeason`）：

| 段 | 步 | 数量 | 这一段在做什么 |
|---|---|---|---|
| `Intent` | 1 | 1（含意图段本身） | 收命令、扣 AP、上令下达 |
| `Spring` | 2–5 | 4 | 生产（公式一） |
| `Summer` | 6–8 | 3 | 市场与价格（公式二，三层循环） |
| `Autumn` | 9–11 | 3 | 分配、消费、财政 |
| `Winter` | 12–15 | 4 | 满意度（公式三）、人口、军事、年度收尾 |

---

## 3. hook 落点（`sim check` 的 `DescribeHookMap()` 输出）

```
OnTurnStart → 01pre, 02pre
OnActionValidate → 不落在任何步骤（命令执行器内，逐条命令）
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
OnIntentEnd → 01post
```

`prepost` = 这一步的 `Pre` 与 `Post` 是同一个点，广播两次。共六步会这样
（`agriculture / industry / services / payout / consumption / treasury`，
另加第 2 步的 `OnProduction` 尾）。测试
`OrderAndDataTests.首尾同名的步必须靠slot区分` 把这份名单钉死了。

**这是插件作者最容易踩的坑**：挂在 `OnProduction` 上的处理器如果不看 `slot`，
一年会被广播 **7 次**（第 2 步末 1 次 + 第 3/4/5 步首尾各 2 次）。
门闸写法见 `docs/PLUGIN_API.md` 第 4 节。

---

## 4. 广播参数里有什么

Core 每步广播两次（`Simulation.StepArgs`，`src/SimCore/Simulation.cs:399`）：

| 槽位 | 类型 | 含义 |
|---|---|---|
| `Subject` | `EntityId` | 恒为 `world.0000`（全局扇出点自己会换成主题实体） |
| `step` | int | 步骤号 1–15 |
| `stepId` | str | 步骤 id，如 `agriculture` |
| `slot` | str | `pre` / `post`（常量：`SimOrder.SlotPre` / `SlotPost`） |
| `layer` | str | 步骤声明的层（`agriculture`/`local`/`domestic`/`foreign`…），空则不带 |

三个用法正确的 API：

```csharp
SimOrder.At(args, "agriculture", SimOrder.SlotPre)   // 精确：某步某段（推荐）
SimOrder.InStep(args, "agriculture")                 // 某步首或尾（首尾同名时会跑两遍，慎用）
SimOrder.PointsOf(stepIndex)                         // (Pre, Post)：Core 内部与自检用
```

`OnTurnEnd` **只广播一次**：第 15 步的 `Post` 就是它（`Simulation.cs:356`）。
D-047 删掉了结算结束后那次 `step=0` 的补发——旧实现让挂在 `OnTurnEnd` 的处理器一年跑两遍。
`core.basic` 的 `Nations.AdoptCapital` 因此也只跑一次，不再依赖幂等。

---

## 5. 一步内部发生了什么（M1 实况）

`Simulation.AdvanceTurn()`（`src/SimCore/Simulation.cs:321`）的固定骨架：

```
回合开始： Time.Turn++ → Season=Intent → meta.turn 写入 → 清掉上一年的 report.*
命令交接： Transport.TryDequeueReceived → Commands.Submit → DrainPending()（按 Turn,Seq,Issuer,Type 全序）
for 每一步（1→15）:
    Season = step.Season
    ctx = new StepContext(step.Index)                 // Step=-1 才禁止写入，见第 7 节
    Hooks.Dispatch(step.Pre,  args(slot=pre))
    if step.Index == 1: 逐条 ExecuteCommand(cmd)      // OnActionValidate（逐条）→ 执行器 → CommandResult
    Hooks.Dispatch(step.Post, args(slot=post))
    Invariants.RunAfter(step.Index)                   // 违反且 FatalInvariants=true → 当场抛
结算后： StateHash.Compute → Transport.BroadcastHash
          → 从 world.0000 的 report.* 攒出 Headline → TurnReport
          → Rng.ResetNamespaceStreams()               // §8.3：命名空间流不跨回合
```

**每步末跑不变量**，不是每回合跑一次：误差在产生的那一步就崩掉，才不用回头猜是哪一步引入的
（GDD §10.2 职责 6「违反即报错，不是静默扩散」）。

命令的执行率打折、拒绝原因都发生在第 1 步，所以第 2 步以后看到的世界状态已经是
「政令落地后」的状态——这是 §7.5「上令不一定下达」的位置含义。

---

## 6. 谁挂在这张表上（M1 的 `core.basic`）

| 步骤 | 处理器 | 位置 | 注册方式 |
|---|---|---|---|
| 2 末 | `TownOps.Facilities` | `OnProduction` slot=post，priority 90 | `api.Require`（全局） |
| 3 首 | 扇出点 → `SeasonRules.Produce` | `OnProduction` slot=pre，priority 100 | `Require` + `RequireFor(×town)` |
| 10 首 | 扇出点 → `SeasonRules.Consume` | `OnConsumption` slot=pre | 同上 |
| 12 首 | 扇出点 → `SeasonRules.Satisfaction` | `OnSatisfaction` slot=pre | 同上 |
| 13 首 | 扇出点 → `SeasonRules.Demography` | `OnClassTransition` slot=pre | 同上 |
| 15 首 | `SeasonRules.Report` | `OnEventDraw` slot=pre | `Require`（写 `report.*`） |
| 15 末 | `Nations.AdoptCapital` | `OnTurnEnd` | `Require` |
| 2/3/10/12/13 末 | `basic.ranges` | 不变量 | `RegisterInvariant` |
| 15 末 | `core.has_reason` / `core.no_empty_field` / `core.hash_stable` | 不变量 | Core 自带 |

代码位置：`src/Plugins/core.basic/BasicPlugin.cs`（注册）与 `src/SimCore/Invariants.cs`（Core 三条）。

第 2 步末的 `Facilities` 必须早于第 3 步首的 `Produce`（priority 90 < 100 且在更早的一步），
否则「今年新建的田」会凭空多出今年的粮——这是 §2.3 顺序含义在插件层的直接体现，
写死在 `BasicPlugin.cs` 的注释里。

---

## 7. 不变量与只读步骤

- `StepContext` 的写入闸门是 `Step == -1`（注册期）：`OnLoad` 阶段只许注册，不许改世界。
- bootstrap 与读档路径由 Core 自己以 `step=0` 走，插件拿到的是同一个 `IStepContext`，
  所以开局初始化器可以合法写入（`CreateEntity` / `Mutate`）。
- 「只读步骤」在 M1 **还没实现**为独立机制：GDD §10.4 提到 hook 不得依赖隐式状态，
  而 M2/M3 会把 `layer` 参数与只读声明接上（见 `docs/ROADMAP.md`）。
  现在只有 `Core 的注释` 与 `sim check` 的 layer 列在提示哪些步属于哪一层。

---

## 8. 性能剖面

`AdvanceTurn` 逐步计时，`TurnReport.Steps` 里每步一个 `StepRecord(Index, Id, Title, DiffCount, ElapsedMicros)`。
命令：`sim run --seed 7 --turns 30 --steps`。
500 镇 × 200 回合的 < 60 秒目标（GDD §10.8）归 M3/M6 的标定，M1 只保证「剖面可得」。

---

## 9. 变更流程

1. 想调顺序 → 先改 `docs/GDD.md` §2.3（AI 不许自动改，须设计者裁决）。
2. 改 `src/SimCore/Time.cs` 的 `SimOrder.Steps`，同步本文件第 2、3、6 节。
3. 跑 `sim check` 对账 + `dotnet test`（`结算表就是GDD那张表`、`首尾同名的步必须靠slot区分` 会红）。
4. 重跑基线模拟并把哈希差异写进 `docs/CHANGELOG-DESIGN.md`，提交打 `[数值零变化]` 或 `[改手感]`。

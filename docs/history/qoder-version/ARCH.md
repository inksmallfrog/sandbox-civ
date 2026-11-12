# 技术架构与插件规范（ARCH v0.1）

给后续 AI 的开发基线。第一 AI 的引擎选择、模块边界、提交约定都写在这里，不允许绕过。

---

## 1. 引擎与技术栈（AI 决定项）

**选定：Godot 4.x + C# (.NET 8)。模拟核心是独立程序集 `SimCore.dll`，不引用任何 Godot/引擎类型。**

理由与代价：

| 候选 | 优点 | 代价 | 结论 |
|---|---|---|---|
| Godot 4 + C# | MIT 无分成、原生 Steam、hex/tilemap 与 UI 够用、C# 可 headless 跑模拟与专用服务器、单一 solution | AI 对 Godot C# API 细节不如 Web 生态稳；需自建 hex 数学库（约 300 行） | **选它** |
| TypeScript + Pixi/Phaser + Electron | 与作者现有 `war-peace` 栈一致、UI 迭代快、单测极轻 | Steam 包体大、120 回合 × 数百城镇的模拟性能吃紧、无原生专用服务器生态 | 备选（若首章性能验证失败可迁移，SimCore 与渲染无关正是为此留的退路） |

**纪律**：`SimCore` 只能依赖 `System.*`。渲染层通过事件订阅读取模拟状态，永远不在 UI 里写规则。这条纪律让"换引擎"和"跑无头模拟/机器人"都只花一次工。

---

## 2. Core 的最小职责（README：尽可能精简的核心系统）

Core **不含**地形、阶级、科技、战斗、宗教——这些全是插件。Core 只有 8 件事：

1. **世界存储**：`World { id → Entity }`，实体持有组件（纯数据 class）。不做完整 ECS，用"字典 + 组件 + 类型化访问器"足够，避免过度设计。
2. **阶段调度器**：回合的五阶段（见 `GDD.md` §1）与阶段内 hook 顺序。Core 不认识"宗教"，只认识 hook 点。
3. **确定性随机**：分层 RNG（见 §4）。
4. **数据注册表**：所有内容都是数据包（JSON/资源），运行时按 `id` 查；注册表提供 schema 校验与 `def:` 引用解析。
5. **不变量守卫**：物资守恒、人口守恒、数值区间断言。每阶段末校验，违反即报错而不是静默扩散。这是防"逻辑失控"的唯一有效机制，必须第一天就有。
6. **存档 / 回放**：快照 + 命令流（见 §5）。
7. **插件宿主**：加载、依赖排序、启用/禁用、schema 迁移链。
8. **同步层**：命令提交/广播/哈希校验（单机为本地环回，联机换传输，不改 sim）。

---

## 3. 插件划分与 hook 契约

| 插件 | 内容 |
|---|---|
| `map.tiles` / `map.gen` | 地表/水体/形胜/资源/人造五层；生成器（盆地、山谷、河网、隘口、绿洲链） |
| `town.core` | 城镇容器、地块绑定、建筑、库存 |
| `town.classes` | 八阶级模板与耦合表、满意式、反抗状态机 |
| `econ.local` | 镇内生产-分配-消费 |
| `econ.market` | 价格求解器（三循环共用，参数化开放度） |
| `econ.nation` | 财政、税、专卖、铸币、国债、平准 |
| `econ.foreign` | 外贸、银流、关税、禁运、走私 |
| `gov.admin` | AP、执行折扣、腐败、监察 |
| `culture.religion` / `culture.science` / `culture.art` | 信仰、研究点、文化值 |
| `mil.units` / `mil.ops` | 兵种模板、补给线、战斗解算、战争目标 |
| `politics.heroes` | 名人池、征辟（抽卡）、任命、诉求、叛逃、政变 |
| `politics.diplomacy` | 关系、条约、宣战理由、间谍、会议 |
| `events.core` | 事件抽取与选项结算 |
| `ui.*` | 每个面板一个插件，注册自己的视图与数据需求 |

Hook 契约（只有这些点位，禁止插件互相直接调用）：

```
OnTurnStart
OnActionValidate(action) -> AP 校验/拒绝
OnProduction(town, ctx)              // 公式一插入点
OnConsumption(town, ctx)
OnPriceForm(good, layer, ctx)        // 公式二插入点
OnAllocate(ctx)                      // 税/俸/饷/工资
OnSatisfaction(class, ctx)           // 公式三插入点
OnClassTransition(class, from, to)   // 服从/抱怨/反抗
OnMigration(townA, townB, ctx)
OnLogisticsCheck(unit, ctx)          // 补给线
OnBattleResolve(battle, ctx)
OnEventDraw / OnEventApply
OnHeroRoll / OnAppointment / OnDemand
OnDiplomaticAction
OnTurnEnd  OnBeforeSave  OnLoad  OnMigrate(save, fromVer, toVer)
```

插件通过 `ctx.Mutate(...)` 提交对状态的修改，core 记录 diff 供 UI 动画与"可解释链"展示使用。任何 hook 不得依赖执行顺序之外的隐式状态。

---

## 4. 确定性随机（随机快乐 + 可回放的前提）

```
seed(entity, namespace) = SplitMix64(worldSeed ⊕ hash(entityId) ⊕ hash(namespace))
```

- 每个随机点用**独立命名空间字符串**（`"hero.roll.town12.turn57"`），不共享流。
- 遍历顺序固定（按 id 升序，禁止用字典枚举顺序）。
- 效果：同种子 + 同命令流 ⇒ 位级相同状态。这是联机校验、回放、平衡模拟、bug 复现的共同地基。
- 抽卡保底与权重表因此可在单测里精确验证（例如"10 连必出紫"跑 10 万次统计）。

---

## 5. 存档与回放

- `save = schemaVersion + worldSeed + snapshot(压缩二进制) + commandLog`。
- `replay = snapshot + commands`；联机只传 commands 与低频全量校正。
- 存档版本迁移是插件自己的 `OnMigrate`，core 只负责按版本顺序驱动。**每次改动数据结构的提交必须附迁移器 + 旧存档回归测试**，否则不许合入。
- 加载时对每个城镇做守恒校验，失败即报告"哪一项、差多少"，与可解释链共用一套 diff。

---

## 6. 联机

- 权威主机（专用服务器或房主）跑 sim，客户端只提交命令与渲染。回合制因此**对延迟不敏感**。
- 同时回合 + 倒计时；未提交者由 `politics.heroes` 的决策函数托管（与 AI 国家、断线接管三合一复用）。
- 每回合末广播状态哈希（分块），不一致 → 客户端上传命令流 → 服务端回放定位差异回合。
- 反作弊依赖确定性 + 权威 sim；不依赖客户端可信。

---

## 7. 内容数据格式

```jsonc
// data/classes/merchant.json
{
  "id": "class.merchant",
  "unlock": { "req": ["building.market", "connected_towns>=1"], "ap": 4 },
  "labor": 0.9,
  "outputs": [ { "via": "circulation", "target": "good.silver_flow_efficiency" } ],
  "consumes": { "good.lux": 0.35, "svc": 0.2 },
  "satisfaction": { "expects": { "good.lux": 0.8 }, "volatility": 0.6, "baseline": 55 },
  "organization": { "base": 0.55, "sources": ["building.guildhall", "hero.patronsage"] },
  "conflicts": ["class.peasant@grain_price", "class.bureaucrat@monopoly"]
}
```

一个阶级的全部规则 = 一个文件。加第 9 个阶级、加时代卡包、加新形胜都是**加文件**，不改代码。这是 README"玩法尽量丰富但内部逻辑简洁"在工程上的落点。

---

## 8. 测试与标定（AI 开发的生命线）

- `sim run --seed X --turns N --headless`：无头跑图，输出 CSV（各阶级人口/满意度末态、价格轨迹、国库、战争结果）。
- 不变量测试：物资与人口守恒、价格区间、AP 不超容、状态机不可越级。
- 公式单测：三个公式各自独立可测，不启动渲染。
- 平衡脚本：固定种子跑 20 个 AI 国家 120 回合，检查是否有单一策略统治（"唯一最优解"是本设计最大的失败模式）。
- 存档往返测试：save → load → replay 同一命令流 → 状态哈希一致。
- **每个里程碑提交必须附一份基线模拟报告**，后续 AI 靠它判断自己是否改坏了经济结构。

---

## 9. 仓库结构与 git 约定

```
sandbox-civ/
  Readme.md
  docs/GDD.md  docs/ARCH.md  docs/ROADMAP.md  docs/CHANGELOG-DESIGN.md
  src/SimCore/            # 无引擎依赖
  src/Plugins/            # 一插件一目录
  src/GodotShell/         # 渲染、输入、UI、Steam
  data/                   # 数据包（地形/阶级/政策/科技/事件/名人）
  tests/  analysis/       # 单测 + 无头模拟与标定脚本
  saves/                  # 回归用存档样本
```

- 分支：`main`（可发布）+ 短生命周期 feature 分支，不长期分叉。
- **README 要求的强制提交点**：每个里程碑验收通过即 `git commit` + `git tag vX.Y` + 更新 `docs/CHANGELOG-DESIGN.md`（记录本次改了什么规则、为什么、基线模拟差异）。
- 提交信息带影响标签：`[数值零变化]` / `[破存档→已附迁移器]` / `[改手感]`，让后续 AI 一眼看出风险级别。
- `docs/CHANGELOG-DESIGN.md` 是跨 AI 交接的核心文件：任何规则改动若在其中没有条目，视为未完成。

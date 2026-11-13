# DATA_SCHEMA · 数据表规范

> 规范来源：GDD §10.5（加内容 = 加文件）、§3.2（地表）、§4.3（阶级）、§5.1（商品）、
> §5.7（建筑）、附录 D（命名）。实现：`src/SimCore/DataRegistry.cs` + `data/_schema.json`。
> 存档部分见 `docs/SAVE_FORMAT.md`（§11.1 把两份合在一份文档名里，这里只写数据包）。

---

## 0. 一分钟版

```
data/
  _schema.json          全仓库唯一的类型表（每种数据包有哪些必填字段、类型、引用）
  <pack>/<type>.json    数据包；一个文件可以装一条、也可以装 {"type","entries":[…]}
```

- **加新类型**：先在 `_schema.json` 的 `types` 里加一段，再往数据包里加条目。
- **加内容 = 加文件**：不改 C#，不改 Core。数值只住在数据里（`color` 这种渲染参数也一样）。
- 校验发生在 `DataRegistry.Load(rootDir, stopOnFirstError)`：严格模式一条错就抛
  （`sim check` / `Bootstrap` 走这条）；宽容模式只记 `Warnings`（`--lenient` 不是数据模式，
  是 `SimConfig.StrictData=false`，抢救坏数据包时用）。

---

## 1. 文件形态（`LoadFile`，`DataRegistry.cs:183`）

三种写法都合法，按 `id` 里的点前缀推 `type`：

```jsonc
// A) 一批：推荐（同类内容集中，diff 好读）
{ "type": "landform", "entries": [ { "id": "landform.plain", … }, … ] }

// B) 顶层数组：每条自带 type
[ { "type": "good", "id": "good.food", … } ]

// C) 单条对象
{ "type": "class", "id": "class.peasant", … }
```

- 文件按 ordinal 顺序枚举（`SearchOption.AllDirectories`）后加载，**与磁盘顺序无关**：
  每条记录插入 `_byType` 列表后立刻按 id 重排（`DataRegistry.cs:271`），因为 `OfType()`
  是插件遍历数据的主入口，顺序必须跨机器一致（§10.7）。
- `pack` = 相对路径的第一段（`data/core.basic/landform.json` → pack `core.basic`）；
  直接躺在 `data/` 下的文件 pack 名为 `base`。
- `_schema.json` 与 `manifest.json` 这两个文件名**永远不会**被当成数据包。
- JSON 允许 `//` 与 `/* */` 注释，也允许尾逗号（解析时 `CommentHandling.Skip` +
  `AllowTrailingCommas`，`DataRegistry.cs:189`），所以字段旁边可以直接写说明。
  另一条口子是以 `@` 开头的键：**永远不进标量表、不进 `dataHash`**，适合放整段
  `@comment`（各数据文件开头那段就是这么用的）。

---

## 2. `_schema.json` 的四种声明

```jsonc
{
  "types": {
    "building": {
      "idPrefix": "building.",              // id 必须以它开头（省略则默认 "<type>."）
      "required": ["title", "slot", "cost.food", "maxLevel"],   // 点分路径
      "scalars": { "cost.food": "fix", "maxLevel": "int", … },  // 期望类型
      "refs":    { "requiresBuilding": "building" }              // 字段 → 目标类型
    }
  }
}
```

| 段 | 检查什么 | 不检什么 |
|---|---|---|
| `idPrefix` | id 的前缀 | — |
| `required` | 该点分路径存在（标量表或可解析的嵌套路径都算） | 值本身对不对 |
| `scalars` | 已存在字段的名字类型是否匹配 | 没写的字段（**不禁止额外字段**） |
| `refs` | 值必须是 `"def:<id>"`，且目标存在、类型正确 | — |

类型只有四种：`fix` / `int` / `str` / `bool`。`fix` = ×100 定点（GDD §10.7）；
数据里写 `3.00` 落进 `Fix`，写 `3` 落进 `Int`。**整数写进 `fix` 字段是允许的**
（`want=="fix" && got=="int"` 被放过，`DataRegistry.cs:322`）——所以 `2` 与 `2.00` 同义，
但**同一份数据里请写一致**，否则 `dataHash` 会因写法不同而不同。带小数点的数字用原始文本判断
（`RawTextContainsDot`），不经过 `double`，避免跨平台解析差异。

数组与 `null` **不进标量表**：数组要插件用 `GetArray(path)` 自己遍历（`Root: JsonElement`
是留给结构化内容的口子，例如未来的科技卡、事件选项）。以 `@` 开头的键视为注释/元数据，
不进状态、不参与 `dataHash`。

---

## 3. `def:` 引用与短名规则

- 数据之间的引用一律写成 `"def:building.farm"`（常量 `DataRegistry.DefPrefix = "def:"`）。
  `Resolve(ref)` 取条目，`Exists(ref)` 做校验；引用格式不对或指向不存在的记录 → 校验警告。
- **世界字段里存的是「去掉类型前缀的短名」**：`town.pop.peasant`（不是 `town.pop.class.peasant`）、
  `hex.building.farm`、`town.stock.food`。规则与代价写在 `src/Plugins/core.basic/F.cs` 的头部注释：
  短一格换来 `Prefix("town.pop.")` 能一次取到全部阶级、UI 不必再拼前缀；
  代价是**短名必须全局唯一**（`class.peasant` 与 `good.peasant` 撞名要在数据校验那关就炸掉）。
- 命令参数允许两种写法：`F.BareId("def:building.farm")` → `building.farm`。

---

## 4. 当前实际数据（`sim check` 打的就是这份）

```
数据包 E:\project\sandbox-civ\data
  ✓ 50 条记录 / 1 个包（core.basic）
  dataHash = d3e94063673c797e
    building   9 条：building.academy, building.barracks, building.farm, building.granary, building.market,
                     building.temple, building.theater, building.towncenter, building.workshop
    class      8 条：class.artist, class.bureaucrat, class.merchant, class.peasant, class.priest,
                     class.scholar, class.soldier, class.worker
    good       5 条：good.food, good.goods, good.lux, good.mat, good.silver
    landform   9 条：landform.coast, landform.desert, landform.forest, landform.grassland, landform.hill,
                     landform.mountain, landform.plain, landform.tundra, landform.wetland
    resource   5 条：resource.fish, resource.horse, resource.iron, resource.salt, resource.spice
    shape     12 条：shape.basin, shape.delta, shape.estuary, shape.fan, shape.gorge, shape.headwater,
                     shape.island, shape.oasis, shape.pass, shape.saltlake, shape.valley, shape.volcano
    waterbody  2 条：water.lake, water.sea
```

（`dataHash` 是「id → 标量表规范化串」的 FNV 混合（`BuildDataHash`），
它进存档头部也进不变量：**同数据不同 hash 说明有人改了数据包**，插件的行为必须跟着改。
上面的数字是 M4 切片二之后的实况，改数据后必须回来更新这一段——`sim check` 是它的生成源。）

各表的字段意图（数值本身属于 GDD §3.2/§4.3/§5.1/§5.7，改数走 `CHANGELOG-DESIGN`）：

| 类型 | 当前用到的字段 | 谁读它 |
|---|---|---|
| `landform` | `potential.food/mat`、`moveCost`、`defense`、`buildable`、`color`、`class.priority/height/temp/rain/edge` | `MapGen` 用 `class.*` 分类并写进 `hex.*`；`color` 只给壳层 |
| `waterbody` | `potential.food`、`color` | `MapGen` 写进 `hex.water` 并覆盖该格 `hex.yield.food`（§3.2 水域行）；`color` 只给壳层 |
| `shape` | `yield.food/mat`（倍率）、`bonus.food/mat`（加性）、`moveFactor`、`defenseFactor`、`priority`、`glyph`、`color` | `MapGen` 按 §3.6 写 `hex.yield.*`/`hex.moveCost`/`hex.defense`，`hex.shape` 存标签；`glyph/color` 只给壳层；`priority` 决定同格冲突 |
| `resource` | `yield.food/mat`（倍率）、`bonus.food`（渔汛 +1）、`glyph`、`color` | `MapGen` 按 §3.6 写倍率、按 §3.5 写 `hex.resource`；`glyph/color` 只给壳层 |
| `class` | `labor`、`output.food/mat/goods/silver`、`outputCost.*`、`consumes.*`、`satisfaction.expects/baseline`、`status`、`taxWeight`、`unlockPop/Building/Towns/Resource/Surplus/Demand.*`、`shareTarget` | `SeasonRules` 的公式一（含 §4.3 原料约束）与公式三；`TownOps.Unlocks` 读 `unlock*`（§4.4）；`SeasonRules.Convert` 读 `shareTarget`（§4.7 占位，D-041）；`taxWeight` 目前只读不结算 |
| `good` | `unit`、`basePrice`、`stockCapPerPop`、`spoilage`、`essential` | `TownOps`（仓容/损耗）；`basePrice` 到 M3 才进求解器 |
| `building` | `slot`(`hex`/`town`)、`cost.food`、`upgradeCost.food`、`maxLevel`、`facility`、`alphaBonus`、`stockCap`、`spoilageCut`、`maintenance.food`、`requiresPop`、`requiresBuilding` | `TownOps.Found/Build/Facilities`；`alphaBonus` 被 `Pricing.Price` 读进本地价格弹性 α（§4.8 市场 +0.05/级）；`unlockBuilding` 反向被 class 引用 |

M2 **已经接上**：`potential.mat`（`hex.yield.mat`）、形胜与资源倍率、资源标签。
**还没接**的内容（`docs/ROADMAP.md` 按里程碑列）：铁/马/盐/香料的门槛（M7/M5/M3）、
`tech` 字段（M8）、`maintenance.*` 扣银（M5）、`basePrice` 参与价格（M3）、其余七个阶级（M4）。

---

## 5. 加一条数据的手顺

1. 在 `data/<pack>/<type>.json` 的 `entries` 里加一条，`id` 用短名全局唯一的那种形式
   （`building.farm` = `building.<短名>`）。
2. 若是新类型：先在 `_schema.json` 的 `types` 加一段（`idPrefix`/`required`/`scalars`/`refs`）。
3. `sim check` 必须绿：它会给「type 未声明」「id 前缀不符」「id 重复」「引用不合法」逐条报出来。
4. 跑基线：`sim run --seed 7 --turns 30 --headless --out analysis/out/<名字>.csv`，
   把差异记进 `docs/CHANGELOG-DESIGN.md`（§11.3：数值改动必须有这条日志，否则视为未完成）。
5. 提交带 `[数值零变化]` 或 `[改手感]`；**如果哈希变了就要写清为什么变**。

新数据若需要**新行为**，那是插件的活（`docs/PLUGIN_API.md` §7/§10），不是数据层的活；
数据表里的字段只有被插件读才有意义。

---

## 6. 报错都长什么样

| 消息 | 触发 | 级别 |
|---|---|---|
| `<rel> 不是合法 JSON：…` | 语法错 | 抛 |
| `<rel> 缺少 type 字段` | A 形态没写 type | 抛 |
| `<rel>：记录必须是对象` | 顶层给标量 | 抛 |
| `<src> <id>：缺少字符串 id` | 条目没 id | 抛 |
| `<src>：id 重复 <id>` | 两条同 id | 抛 |
| `<src> <id>：type='<x>' 未在 _schema.json 里声明` | 未声明类型 | 警告（严格模式=抛） |
| `<src> <id>：type=… 要求 id 前缀 '<x>'` | 前缀不符 | 警告 |
| `<src> <id>：缺少必填字段 '<path>'` | required 不满足 | 警告 |
| `<src> <id>：字段 '<p>' 期望 fix，实为 str` | 类型不符 | 警告 |
| `<src> <id>：引用 '<p>' 应写成 "def:<id>"` / 指向不存在 / 类型应为 X | refs | 警告 |
| `数据目录不存在：<path>` | 目录写错 | 抛（`Bootstrap` 前先判存在，正常开局不会走到这条） |

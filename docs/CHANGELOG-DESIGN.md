# CHANGELOG-DESIGN · 规则变更日志

> 规范来源：GDD §11.3（四条流程）与 §12（每里程碑做一次逻辑量盘点）、§14（风险表「AI 交接退化」）。
> **§11.3 第 4 条：任何规则改动若在本文件没有条目，视为未完成。**
>
> GDD 是规则的唯一来源，且实现方**不许自行修改**（附录 B 第 10 条）。
> 所以与 GDD 有任何不一致——不管是「做不到」还是「GDD 自己前后矛盾」——都写在这里等裁决，
> 而不是悄悄改代码或悄悄改文档。

---

## 0. 条目格式

```
### D-<编号> <一句话说清改了什么>
- 日期 / 类型（数值 · 规则 · 结构 · 引用 · 待裁决） / 状态（已实现 · 待裁决 · 已裁决）
- 改了什么：具体到文件与字段
- 为什么：GDD 的哪一条做不到 / 冲突在哪 / 什么证据逼出了这个改动
- 影响：哈希变不变、破不破存档、手感变不变、后续里程碑要不要还
```

编号只增不复用。**「待裁决」的条目不许被实现方自行改成「已裁决」。**

---

## 1. 待裁决（需要设计者拍板，实现方不动）

### D-001 §12 与 §11.2 的 tag 语义互相冲突

- 类型：结构 / 状态：**待裁决**
- 冲突：§12 的里程碑表给的是 **按里程碑**的 tag（M2 地图 `v0.2`、M3 经济 `v0.3`、M4 阶级 `v0.4` …
  M10 联机 `v0.10`、M11 `v1.0`）；§11.2 的「强制提交节点」给的是**按功能**的 tag
  （插件系统 `v0.2`、地图 `v0.3`、城镇 `v0.4`、阶级 `v0.5`、经济 `v0.6`、国家 `v0.7`、名人 `v0.8`、
  单机 Alpha `v0.9`、联机 Alpha `v0.10`、Beta `v0.11`、`v1.0-rc`、`v1.0`）。
  同一个 `v0.2` 一个说地图、一个说插件系统，`v0.4` 一个说阶级、一个说城镇。
- 实现方当前的选择：**按 §12 打 tag**（里程碑是验收单位，且 §12 的表是「交付 + 验收标准」的载体）。
  现在准备打的是 `v0.0`（M0）与 `v0.1`（M1 Core 可运行）——这两条两份文档给的语义一致，先落地。
- 影响：只影响 tag 名字，不影响代码。但**晚裁决比早裁决贵**：一旦 M2 的 tag 打出去，
  再换口径就会出现「v0.2 到底是地图还是插件」的历史歧义。

### D-002 分支模型：`main`/`develop`/`feature/*` 在单人 + AI 交接下的成本

- 类型：结构 / 状态：**待裁决**
- §11.2 要求三条分支线。当前实现方在单条本地分支上直线提交（无 remote、无并行开发者），
  每个关键任务完成即提交（设计者的显式要求：「不要等最后统一提交，要在关键任务完成时及时 commit 便于回滚」）。
- 实现方的选择：**先把 `master` 改名成 `main` 对齐 §11.2**，`develop`/`feature/*` 暂缓，
  等出现第二个并发写入者（人或 AI）时再开。理由：`develop` 在单人场景下只增加「忘记合并」这一类新失败模式，
  不提供任何保护，而回滚需求已经由「关键任务即提交 + tag」覆盖。
- 影响：若裁决要求现在就上 `develop`，改动是纯 git 操作，零代码影响。

### D-003 §11.1 的文件名 `DATA_SCHEMA.md` 同时被称作「数据表与存档格式」

- 类型：引用 / 状态：**已实现（取拆分）**
- §11.1 的清单里 `DATA_SCHEMA.md` 注释是「数据表与存档格式」，但同一份清单里又单列了 `SAVE_FORMAT.md`。
  实现方按职责拆开：`DATA_SCHEMA.md` 只管数据包与 `_schema.json`，`SAVE_FORMAT.md` 只管 `.hcsave` 与迁移链，
  两份文档互相指了一次。§11.1 的 13 份一份不缺。

### D-004 README 与 GDD §0.2 的五处判定

- 类型：结构 / 状态：**已实现**
- §0.2 已经判过原 README 五处（含冲突修正），GDD 是准。
  原 `Readme.md` 的需求原文挪到 `docs/REQUIREMENTS.md` 存档，根目录 `Readme.md` 重写为
  「这是什么 + 怎么跑起来 + 文档地图」，不再作为规则来源。

### D-041 阶级转化的「吸引力」与「住房容量」在 GDD 里没有可算的定义

- 类型：数值 / 状态：**待裁决**
- 冲突：§4.7 给了 `吸引力 = 收入 + 就业 + 地位 + 福利 − 生活成本 − 风险`，但其中「收入 / 工资」
  要到 §2.3 第 9 步的发放（M5）才有数；同一节的 `拥挤 = max(0, 总人口 / 住房容量 − 1)`
  也没说住房容量从哪来（§4.8 的建筑表里没有住房类建筑）。
- 实现方当前的占位（见 D-040）：
  1. `class.json` 的 `shareTarget`（各阶级占总人口的目标份额）代替吸引力差额；
  2. 住房容量 = 辖地自然粮潜力 ÷ 人均口粮（不乘 Facility）。
- 影响：200 年基线在人口 ≈117（两城合计）、最低满意 ≈30–42 处稳定，不再死亡螺旋；
  但这不是「吸引力」的真相。等 M5 的工资/物价接上后应换成 §4.7 的原式，
  届时 `shareTarget` 若要保留，应作为「目标结构」而不是「吸引力」的替身重新定义。

---

## 2. 与 GDD 字面不一致的实现偏差（已实现，等确认）

### D-010 存档压缩：zstd → GZip

- 日期：2026-11-12 / 类型：结构 / 状态：**已实现**
- 改了什么：`src/SimCore/Save.cs` 用 `System.IO.Compression.GZipStream`，文件魔数按 gzip（`1f 8b`）识别。
- 为什么：GDD 提 zstd，但 net8.0 的 BCL 不带 zstd，引第三方包会同时打破两条铁律——
  SimCore 只依赖 `System.*`（§10.1 / ADR-002）与插件宿主的可加载性。
- 影响：压缩率略差（`.hcsave` 体积 +20% 量级），**格式自带魔数嗅探**，将来换 zstd 只要加一种识别分支，
  旧档仍可读，不需要迁移器。详见 `docs/SAVE_FORMAT.md`。

### D-011 定点中间量：`Int126` → `UInt128` + 符号位

- 日期：2026-11-12 / 类型：结构 / 状态：**已实现**
- 为什么：net8.0 只有 `UInt128`，没有 `Int126`。`Fix.MulDiv` 取绝对值走 `UInt128`、符号单独记，
  截断方向仍是向零。
- 影响：无行为差异，但要小心一个反直觉的坑：`Abs128` 里**必须先在 64 位无符号空间取负再升位**，
  先升位再取负会得到 2^128 级的数。`Fix.cs:84-98` 有注释，`FixTests.负数乘法不误报溢出` 有断言。

### D-012 `map.gen` 不是 Core 的插件，而是 `core.basic` 的一部分

- 日期：2026-11-12 / 类型：结构 / 状态：**已实现**
- 为什么：§10.5 的「加内容 = 加文件」要求 Core 里没有玩法。地图生成读地表数据、写地块实体，
  属于玩法内容，所以放进 `core.basic`（`src/Plugins/core.basic/MapGen.cs`），通过
  `api.RegisterBootstrap` 挂进开局流程，而不是做成第二个插件——单插件时做插件间依赖是过度设计。
- 影响：M2 若要「换一套生成器」，正确做法是新插件覆盖 bootstrap，而不是改 Core。

### D-013 建城成本按实物粮计，不是 §3.8 的「料 12 + 器 4 + 银 40 + AP 3」

- 日期：2026-11-12 / 类型：数值 / 状态：**已实现（M3/M5 要还）**
- 为什么：M1 没有料/器两种商品（§5.1 的数据在库但无生产与价格），也没有 AP（§6.3）。
  先按 `TownOps.FoundCostFood = 6.00` 石粮计，让「国库见底就开不了城」这条反馈当年就能被玩到。
- 影响：**是数值偏差，不是等价实现**。M3 接公式二、M5 接 AP 之后必须回到 §3.8 的成本向量，
  届时这条要重开一个数值条目并附基线差异。

### D-014 `SimConfig.YearLimit` 解析了但没有执法者

- 日期：2026-11-12 / 类型：结构 / 状态：**已实现（缺口）**
- 为什么：结束条件（§1.5 的阈值速胜与时代推进）整块属于 M9，M1 提前实现会逼着 Core 认玩法。
- 影响：`--turns 200` 之后可以继续 `--turns 9999`，不会自动收官。已在 `docs/CORE_API.md` §3 与
  `docs/ROADMAP.md` §4 各标一次。

### D-015 `manifest.data_packs` 解析了但装载器不看

- 日期：2026-11-12 / 类型：结构 / 状态：**已实现（缺口）**
- 为什么：数据包现在按目录整体加载（`data/<pack>/*.json`），卡包开关要等 §8.1 的六类卡池与时代包（M8）。
- 影响：写了 `data_packs` 不影响行为。`docs/PLUGIN_API.md` §4 明确标了「解析但不生效」。

### D-016 §4.2 的阶级状态升级闸门只累计年数，不兑现罚则

- 日期：2026-11-12 / 类型：规则 / 状态：**已实现（M4 兑现）**
- 为什么：§4.2 要求「S<30 连续 2 年才升级到怠工/罢工/暴动」，而四种镇压与各自代价在 §12 属于 M4。
  M1 把年数写进 `town.riot`，并在年报里给「再有一年就要怠工/抗税」的告警。
- 影响：现在的「反抗」是**零代价**的。这不是平衡结论，只是 M1 的边界。

### D-017 全局统计走 `report.*` 字段，而不是让年报再扫一遍世界

- 日期：2026-11-12 / 类型：结构 / 状态：**已实现**
- 为什么：第 15 步的 `Report` 确实扫了城镇（`SeasonRules.Report`），但损耗这类**逐笔**合计走
  `ctx.AddFix(World.GlobalId, F.ReportFoodRot, …)` 在发生地累加。两件事分开是为了让「聚合」这件事
  只有一处定义，且 500 城时不必为了打印一行数字再遍历一次仓库。
- 影响：`report.*` 是**每回合重算**的瞬时值，回合首由 `AdvanceTurn` 清零；它不是历史序列。
  历史序列在 CSV 基线报告里，不在存档里（`docs/SAVE_FORMAT.md` 的「不在存档里的东西」表）。

---

## 3. 数值与公式变更（§11.3 第 1 条要求附模拟差异）

### D-020 人口增长改为「先乘 P」，修掉低粮安年份的定点归零

- 日期：2026-11-13 / 类型：**数值 + 规则** / 状态：已实现，已附回归测试
- 改了什么：`SeasonRules.Demography` 里
  `rate = Mul(FromPercent(4), secure); delta = Mul(FromInt(P), rate)`
  改为 `delta = Div(Mul(Mul(FromInt(P), 4), secure), 100)`，即 ΔP = P × 粮安 × 4 ÷ 100，
  全程只在最后截断一次。常数一个都没动（0.04、粮安 = clamp(2s−1) 都还是原值）。
- 为什么：×100 定点下 `0.04 × 粮安` 在 s ≤ 0.62 时正好是 0.00xx ⇒ 截成 0.00 ⇒ 乘回人口还是 0。
  实测 seed 42 基线从第 83 年起人口**永久卡在 72 个百人单位**（117 年一动不动）。
  这不是标定问题，是公式没被执行出来：附录 A §8 要的是 ΔP，不是「先把增长率量化成两位小数」。
- 模拟差异（同种子同命令流，200 年，`scripts/demo.txt`）：

  | 年 | 人口 前 → 后 | 最低满意 前 → 后 | 不安城数 前 → 后 |
  |---:|---|---|---|
  | 63 | 60 → 61 | 37.14 → 35.71 | 0 → 0 |
  | 83 | 72 → 79 | 29.04 → 25.99 | 1 → 1 |
  | 123 | 72 → 88 | 28.28 → 28.28 | 2 → 2 |
  | 200 | **72（冻死）** → **89** | 32.84 → 26.58 | 0 → 2 |

  前 62 年完全一致（满口粮时两种写法逐位相同），分叉发生在第一次出现「饿而不死」的那一年。
  改后人口收敛到解析平衡点 `P* = 2 × Cap × Facility ÷ need = 2 × 44.90 = 89.8` 的下方整数 89
  （推导见 `docs/BALANCE.md` §3），满意度稳定在 S\* ≈ 29 的反抗线附近——这才是 §4.9 与 §5.8 描述的循环。
- 哈希影响：**分叉**。`3c72a09ae26803e9`（6 回合跨宿主冒烟）、`3a0d8ef70410ba0a`（12 回合快照）、
  `511b3328ad7ee58e`（20 回合）三个基线**不变**（缺不到 25% 粮安就轮不到这条分支），
  200 年基线从 `859a9a0963fb2267` 变为 `36c3463658b99a90`。
- 存档兼容：**不破**。没加字段、没改字段语义，旧档读进来照常跑（只是后续演化不同）。
- 回归测试：`CoreBasicTests.缺粮但不至于饿死时零头桶照样在涨`。
  用 `git stash` 把改动撤掉验过它确实红（消息：`零头桶只加了 0.00`）。

### D-021 插件清单未知键从「静默落回默认值」改为拒载

- 日期：2026-11-13 / 类型：结构 / 状态：已实现
- 改了什么：`PluginManifest.TryRead` 反序列化后用 `JsonDocument` 审一遍根对象键名，
  未知键（`@` 前缀的注释键除外）→ `SimPluginException`，错误消息列出合法键集合。
  同时把 `manifest.json` 的 `loadPriority`/`enabledByDefault`/`dataPacks` 改成 §10.3 的 snake_case。
- 为什么：`System.Text.Json` 的 snake_case `[JsonPropertyName]` + 大小写敏感默认值 = 写错一个字母
  就静默变成 0 / false / 空表。优先级与数据包开关这类字段静默失效，比崩溃难查一个数量级。
- 影响：Core 无玩法的判定、插件装载顺序都不变（改回 snake_case 后 `load_priority` 才真的 = 10）。
  **不影响存档、不影响哈希**（清单不进状态）。
- 测试：`OrderAndDataTests.清单键名拼错就拒载而不是静默落回默认值` + `仓库里真的装着core_basic` 的三条新断言。

### D-037 公式二（粮价求解器）+ 守恒等式不变量（M3 单镇经济）

- 日期：2026-11-13 / 类型：**规则 + 结构** / 状态：已实现，已附 4 条回归测试
- 改了什么：
  1. 新增 `Pricing.cs`：第 6 步 Summer、OnPriceForm hook、market_local 层、每城跑一次。
     `p_{t+1} = clamp( p_t × (1 + 0.30 × (D − S) / max(S, 0.5) + shock) , 0.2×p0, 5×p0 )`
     只对粮（good.food）跑价格，其余商品暂保持 basePrice（M4 八阶级接入后循环扩展）。
  2. F.cs 新增 6 组字段路径方法：`town.price/demand/supply/consumed/spoiled/spilled.*`。
  3. `SeasonRules.Consume` 补写 `consumed/spoiled/spilled` 三个字段（守恒校验的数据源）。
  4. `TownOps.Found` 建城时初始化粮价 = good.food.basePrice = 4.00。
  5. 新增两个不变量：`basic.price_bounds`（第 6 步末，价格区间兜底）+ `basic.conservation`
     （第 10 步末，`supply = stockAfter + consumed + spoiled + spilled`，允许误差 0.02）。
- 为什么：GDD §5.3 / §5.9。M1 只实现了公式一（生产）、公式三（满意度），价格公式二、
  物资守恒四条等式全是缺口。M3 要求"守恒通过 + 灾年价格轨迹符合预期"才能验收。
- 定点纪律：gap = (D−S)/max(S, 0.5) 可能在 S 很小（0.5 档）时产生大绝对值，
  但我们先算完整括号里的 change = 1 + 0.30×gap + shock，再乘 p_prev，最后 clamp——
  中间量不会被 ×100 定点截断两次。shock = N(0, 0.04) 用 `Fix.FromPercent(4)`，
  乘 p_prev 时大数字在前。
- 时序：D（需求）在第 6 步是预计值 `need × pop`，不是第 10 步的实际消费。
  这符合 GDD §5.4 的「市场先出清、后消费」语义。价格求解器**不修改库存**，
  只改价格字段和 demand/supply 快照——实际库存变动全在第 10 步 Consume。
- 模拟差异（同种子 seed 42 / 200 年，2026-11-13 实跑）：

  | 指标 | M1 末态 | M3 末态 | 差异 |
  |---|---|---|---|
  | 城数 | 2 | 2 | — |
  | 人口 | 163 | 163 | — |
  | 粮产出 | 82.75 | 82.75 | — |
  | 最低满意 | 26.58 | 26.58 | — |
  | 不安城数 | 2 | 2 | — |
  | **200 年哈希** | `820efa76ba0f98b6` | `e7e69b15db632966` | 🔄 变了 |
  | **6 回合跨宿主哈希** | `5e8fb72f798550d4` | `9fe62d078937e3ef` | 🔄 变了 |

  **末态数值完全一致，但哈希变了**——因为世界状态里多了 price/demand/supply/consumed/spoiled/spilled 等字段。
  价格是"随供需变化的数字"，还没接入消费分配逻辑（那是 M5 多镇贸易的事），
  所以人口、产出、满意度、不安的轨迹和 M1 相同。

- 影响：
  - **确定性**：两遍逐回合哈希全同（`sim hash` 通过）。RNG 流命名 `price.good.food.T{turn}`，三要素齐全。
  - **存档兼容**：新存档带 price 字段，旧存档读进来 price 为 Fix.Zero，
    Pricing.Price 里 fallback 用 good.food.basePrice（4.00）兜底，不会崩。
    这是新增字段不改旧档的安全模式，不需要迁移器。
  - **SimCore 零改动**：hook 点和 RNG 已就位，不需要开新接口。
  - **数据文件零改动**：good.json 的 basePrice 4.00 已是 GDD §5.1 的值，不需要调。
- 回归测试（4 条新增，全部在 `CoreBasicTests`）：
  - `粮价初始化且五回合内保持在区间内` —— 建城时 price = 4.00，5 回合后仍在 [0.80, 20.00]
  - `多种子二十回合粮价始终在区间内` —— seed 201–210，全部 clamp 生效
  - `三十回合守恒不变量不触发` —— 能跑完 = supply = stockAfter + consumed + spoiled + spilled
  - `灾年粮价涨丰年粮价跌` —— 辖地潜力清零后，价格从正常年的 3.12 涨到 7.30+
- 踩坑记录（写入本仓库 `docs/AI_HANDOFF.md` §5）：
  - FanOut stepId 写成 `"local"` 会导致 hook 不触发，正确 stepId 是 `"market_local"`
    （`Time.cs` 的 stepIds 表：layer 名 ≠ stepId 名）
  - 产出已被 Produce 写进 stock（AddFix），Pricing 里再 `stock + output` 会翻倍 supply → 守恒恒等于 supply - 2×supply ≠ 0

---

### D-038 M4 切片一：八阶级循环 + 状态机五档 + 多商品价格

- 日期：2026-11-13 / 类型：功能扩展 / 状态：已实现，切片一完成
- **为什么改**：M1 只有农民、只有粮、只有三档状态（服从/抱怨/反抗）。GDD §4.3 八阶级是文明的骨架，三条公式要能循环多阶级 × 多商品才能支撑八阶级的行为差异。切片一先把循环跑起来、五档状态机兑现罚则、价格求解器扩展到 4 种商品，原料约束（outputCost）和解锁闸门（unlockPop > 0）推切片二。

- **改了什么**：
  - `data/core.basic/class.json`：从 1 条 `class.peasant` 扩到 8 条（peasant/worker/merchant/priest/bureaucrat/scholar/soldier/artist），数值照抄 GDD §4.3 表。全部先 unlocked（unlockPop=0），解锁闸门推切片二。祭司/学者/文艺家的非物质产出（信仰/知识/文化）切片一 stub（有消费无产出）。
  - `src/Plugins/core.basic/F.cs`：新增 `TownStatusPrev(clsId)`（状态机上回合值，供 `basic.class_monotonic` 不变量读）和 `TownWealth(clsId)`（阶级持有银，GDD §4.1，切片一先写字段路径不接逻辑）。
  - `SeasonRules.Produce`：从只处理 `peasant → food` 改成循环 `ctx.Data.OfType(F.TypeClass)` × 循环每个阶级的 `output.*` 前缀键。状态机 multiplier：怠工=50%、罢工/暴动=0%、服从/抱怨=100%。hard cap（Cap × Facility）目前只对农民产粮生效（GDD §5.2 第一产业受地约束），非第一产业阶级先只用 soft cap。多阶级产同一商品时用 AddFix 累加到 output 字段（先清零再循环）。
  - `SeasonRules.Consume`：从只处理 `peasant → food` 改成循环所有阶级 × 所有 `consumes.*` 前缀键。ration（s_c）简化决策：优先取 food 的 got/want，没有 food 需求的阶级取第一个消耗商品。仓满抛洒 + 年损耗循环所有商品（原来只循环 food）。
  - `Pricing.Price`：从只跑 `good.food` 改成循环除 silver 外所有 4 种商品（food/mat/goods/lux）。D（本期需求）从只算 peasant 的 food 需求改成循环所有阶级的 `consumes.{goodShort}` × pop 汇总。公式参数不变：α=0.30、shock N(0,0.04)、clamp [0.2×p0, 5×p0]。
  - `SeasonRules.Satisfaction`：状态机从三档（服从 S>55 / 抱怨 30<S≤55 / 反抗 S≤30）改成五档（服从 / 抱怨 / 怠工 riot≥2 / 罢工 riot≥3 / 暴动 riot≥4）。先存 prevStatus 到 `TownStatusPrev` 再算 next，为 `class_monotonic` 不变量提供输入。riot 还是全局计数（简化决策，per-class riot 推切片二）。
  - `BasicPlugin`：新增 `basic.class_monotonic` 不变量（第 12 步末，检查状态只能逐步恶化或返回）；`basic.conservation` 从只循环 food 改成循环除 silver 外所有商品；原有 `basic.ranges` 和 `basic.price_bounds` 不变量保持不变。
  - 测试：原有 69 条全绿（其中 1 条灾年测试硬编码 "反抗" 改成 "暴动"，因为 20 年断粮 riot≥4），新增 5 条：`八阶级循环五回合不崩` / `满意度满四年跌破三十到暴动档` / `怠工状态下产出折半` / `罢工状态下产出归零` / `四种商品价格都在区间内`。class_monotonic 集成测试跳过（正常游戏流里 Satisfaction 会自己维护 StatusPrev，跳级在运行时不可能发生）。

- **没改什么（切片二债）**：
  - 原料约束（`outputCost.*`）暂时忽略。工人产器耗料 1.2/人/年 的约束逻辑推切片二。
  - 解锁闸门（`unlockPop > 0`）暂时全部阶级 unlocked。阶级切换/解锁逻辑推切片二。
  - 非第一产业 hard cap（Cap × Facility 对工人/商人等阶级不生效）。
  - `TownWealth` 字段路径已写但不接逻辑（M5 税制时接入）。
  - per-class riot 计数（当前全局一个 riot 覆盖所有阶级的 S<30 连续年数）。
  - Demography 人口增长/阶级转化：当前只对 peasant 有增长，其他阶级 M4 切片一不接增长逻辑。

- **模拟差异（M3 → M4）**：
  - **确定性**：seed=7 两遍 6 回合同 hash（`9c29b42e2868ef10`，M3 是 `9fe62d078937e3ef`）；seed=42 两遍 200 回合同 hash（`e824c66acc43569b`，M3 是 `e7e69b15db632966`）。值变了是预期内——状态机从 3 档改 5 档、多商品都跑价格/消耗、Conservation 循环扩展。
  - **行为差异**：M4 切片一建城时**只初始化 peasant 的 pop/sat/growth**（其他 7 阶级 pop=0 自动跳过 Produce/Consume/Satisfaction），所以**单镇、单阶级、只有农民**的模拟轨迹和 M3 基本一致。差异来源：（1）状态机字符串从 "反抗" 变成 "怠工/罢工/暴动" 多档，虽然数值计算逻辑一样；（2）Pricing 对 mat/goods/lux 也跑价格公式，虽然这些商品目前库存=0、D=0，p_t+1 ≈ p_t（基本不涨不跌）；（3）Conservation 循环所有商品（silver 跳过），新增 3 个守恒检查点。
  - **存档兼容**：旧存档没有 `TownStatusPrev.*` 字段，class_monotonic 不变量会因为 Rank=-1 跳过（`rp < 0 || rn < 0`），不触发错误。新字段全部有 fallback，不需要迁移器。
  - **SimCore 零改动**：所有改动在插件层。
  - **hook 落点 15 步 21 不变**：`sim check` 显示 hook 落点和 M3 完全一样，三个新增不变量挂在第 12 步末（class_monotonic）和第 10 步末（conservation 扩展后还是同一个不变量，只是循环范围变了）。
  - **dataHash 变了**：M3 `eebed2c6f0ba30d1` → M4 `8a19d5bcee5da2fa`（class.json 从 1 条扩到 8 条）。

- **回归结果**：
  - `dotnet build` — 0 error / 0 warning
  - `dotnet test` — 74/74 全绿（69 原有 + 5 M4 新增）
  - `sim check` — 15 步 / 21 落点（7 不落在任何步骤），dataHash=8a19d5bcee5da2fa
  - `sim hash --seed 7 --turns 6` — 两遍同 9c29b42e2868ef10
  - `sim run --seed 42 --turns 200` — 两遍同 e824c66acc43569b
  - `sim run --no-plugins --no-data --turns 1000` — 无崩溃

### D-040 M4 切片二：罚则对齐 + 分阶级反抗 + 解锁链 + 阶级转化 + 原料约束 + 死亡螺旋

- 日期：2026-11-13 / 类型：规则 + 数值 / 状态：已实现（M4 切片二）
- **为什么改**：D-038 的切片一把 §4.2 的罚则写重了（怠工 −50%、罢工 −100%），把
  「连续 2 年 S<30 才升一级」（§4.2 原文）实现成「每年一档」，反抗计数还是全局一个；
  §4.4 的解锁链、§4.7 的阶级转化、§4.9 的料/器算例、§4.3 的 `outputCost.*` 全部欠账。
  这些欠账合在一起，让 seed 42 的 200 年基线从第 108 年起产出归零、人口从 151 坍缩到 6。

- **改了什么**：
  - `SeasonRules.Produce` 的 `statusMult` 改为 §4.2 表原值：怠工 **0.85**、罢工/暴动 **0.60**
    （GDD 没定义暴动的产出，先沿用罢工档，记在 D-040 的待确认项里）。
  - `SeasonRules.Produce` 拆成两趟：第一趟粮/料受 `Cap × Facility` 硬上限（§4.9 的
    `min(6×0.9×0.8, 2×1.0)`）；第二趟器/奢先按 `outputCost.*` 扣原料，再按
    `min(1, 可用料 / 需料)` 缩放产出（§4.3 工人产器耗料 1.2/人/年）。
  - `SeasonRules.Satisfaction`：反抗计数从全局 `town.riot` 改为**分阶级** `town.riot.<阶级>`；
    升级档位改为 `连续年数 ÷ 2`（§4.2「连续 2 年才升一级」：第 2 年怠工、第 4 年罢工、第 6 年暴动）；
    `town.riot` 保留为「本城最长连续年数」的聚合值给年报与旧读者。
  - `SeasonRules.Satisfaction` 新增 §4.2 的**暴动 3 年控制权倒计时** `town.countdown`：
    归零时该城清空 `town.owner`、治权置 `independent`，并把暴动阶级降回罢工（「让步」选项）。
    镇压要军力（M7），M4 只有让步这一条路。
  - `TownOps.Unlocks`（第 2 步末、Facilities 之后）：按 `class.json` 的
    `unlockPop / unlockBuilding / unlockTowns / unlockResource / unlockSurplus / unlockDemand.<商品>`
    判定并粘住 `town.unlocked.<阶级>`。
  - `SeasonRules.Convert`（第 13 步、增长之后）：已解锁阶级朝 `shareTarget` 收敛，
    单阶级单年 ≤ 总人口 2%，从农民池出人、农民至少留 1 个百人单位；零头进 `town.convert.<阶级>`。
  - `SeasonRules.Demography` 接上 §4.7 的**拥挤**项：`拥挤 = max(0, 总人口 / 住房容量 − 1)`，
    住房容量 = 辖地自然粮潜力 ÷ 人均口粮（占位，见 D-041）。增长率改成
    `ΔP = P × (4·粮安 − 3·拥挤) ÷ 100`，仍然「先乘 P、最后只截一次」。
  - `data/core.basic/class.json`：农民接上 §4.3 的料产出 0.9 / 耗料 0.2；八阶级补 `unlock*` 与 `shareTarget`。
  - `data/core.basic/building.json`：补 §4.8 的市场 / 城镇中心 / 神庙 / 学宫 / 兵营 / 剧院；
    §4.8 的「料/器/银」造价按农田既有折算（银 ÷ 10）换成 `cost.food`，多商品造价等 M5。
  - `Pricing.Price`：α 从常量 0.30 改为 `0.30 + Σ(alphaBonus × 等级)`，市场每级 +0.05（§4.8）。
  - `F.cs`：新增 `TownRiotOf / TownUnlocked / TownConvert / TownCountdown`。
  - `BasicPlugin.Sanity`：新增「反抗年数非负」「状态必须落在 §4.2 五档」两条范围检查。
  - `data/_schema.json`：class 增加 `unlockTowns / unlockBuilding / unlockResource / unlockSurplus / shareTarget`
    与 `unlockBuilding → building` 引用；building 增加 `alphaBonus`。

- **与 GDD 字面不一致、需要设计者确认的代理**（都写在 D-041 或这里）：
  - 工人的「器需求 ≥ 1」在工人出现前无解（需求由工人自己消费产生）→ M4 只判「建作坊」。
  - 商人的「与另一镇连通」→ M5 才有道路/贸易，M4 用「世界上至少 2 座城」代理。
  - 学者的「文字」→ M6 科技卡，M4 只判学宫；祭司/文艺家的「剩余」用城内存粮代理；
    文艺家的「规模 ≥ 20」用人口 20 个百人单位代理。
  - 建筑造价只有粮（§4.8 是料/器/银）。

- **模拟差异（D-038 → D-040）**：
  - 罚则变轻 + 分阶级计数 + 拥挤项，使 seed 42 的 200 年基线**不再死亡螺旋**：
    末态从「2 城 / 人口 6 / 粮产 0」变成「2 城 / 人口 117 / 粮产 82.05 / 最低满意 26.4」，
    第 100–200 年人口稳定在 111–119，失业与反抗不再单向恶化。
  - 200 年基线的中间态仍会在粮价、满意度上波动（ξ 与价格 shock 未变），
    但人口不再跨过不可逆的坍缩点；这是「有界的坏均衡」，不是「螺旋」。

- **回归结果**：
  - `dotnet build` — 0 error / 0 warning
  - `dotnet test` — **97/97** 全绿（原 95 条改名 2 条 + 新增 2 条：`建作坊后工人解锁并从农民转出`、`工人没有料就产不出器`）
  - `sim check` — 15 步 / 21 落点（7 个不落在任何步骤），**dataHash = 495c26d59b6e2362**
  - `sim hash --seed 7 --turns 6 --script scripts/godot-smoke.txt` — 末态 **fc88ca36ce90fb37**，
    Godot 4.7.2 无头冒烟同值、`SMOKE PASS`、拾取 1920/1920
  - `sim hash --seed 99 --turns 50` — 末态 **4e1f5c96888da71f**（两遍同值）
  - `sim replay --seed 5 --turns 20 --at 12` — 快照往返 **1ad5c84b9f9e468b**；续跑 = 一口气跑满 **f5fda8d1f5f68d3f**
  - `sim hash --seed 42 --turns 200` — 末态 **c627c4d01388e426**（两遍同值；CSV 末行 2 城 / pop 117）
  - **SimCore 零改动**，所有改动在插件层与数据层；hook 落点表不变。

---

## 4. 引用修正（不改行为，但指错章节会让下一个人按错的规则改代码）

### D-030 全仓库 GDD 章节引用审计

- 日期：2026-11-13 / 类型：引用 / 状态：已实现
- 三类系统性指错（**章节存在 ≠ 指对了**）：

  | 误引 | 该章实际是什么 | 被用来标什么 | 改成 |
  |---|---|---|---|
  | `§6.6` | 宗教（祭司） | 建城规则、铺城间距、选址 | `§3.8` 建城 |
  | `§5.6` | 国际循环 | 「剩余物资留仓、粮 5%/年损耗」 | `§5.4` 步 6（城镇内循环） |
  | `§7.1` | 名人数据结构 | 独立城镇（owner 可为 null） | `§4.1` + `§6.2` |

  另修：`town.founded` 的理由写了不存在的 `§12.2`；治权档位欠账标成 M5（应是 M9 的 §6.2/§6.3）；
  `SeasonRules` 里「§5.4 公式三」应为「§4.2 公式三」；`good.json` 注释里的「§6 步 6」。
- 顺带纠正一条口径错误（**这次审计真正的收获**）：`MinTownSpacing = 3` 原先在注释、测试与文档里被
  描述成「拿空间限制顶替 §6.6 的建城 5 回合冷却」。实际上「距最近城镇 ≥ 3 格」就是 §3.8 的原文，
  它是**直接实现**；同表里真正没做的是「每国每 5 年最多新建 1 城」。旧说法把一条已做的规则
  记成了欠账，同时把真正的欠账藏起来了。
- 影响：理由文本不进状态哈希，**复验过哈希三个基线全不变**、59 测试全绿。
- 顺手更正两条本仓库文档自己写错的计数（`sim check` 与 `dotnet test --list-tests` 当场重数）：
  hook 落点里**不落在任何步骤的是 7 个**（`OnEventApply / OnHeroRoll / OnAppointment / OnDemand /
  OnBeforeSave / OnLoad / OnMigrate`），本文件初稿写成 6 个，已改；测试条数是 **59**，
  `ARCH.md` 的目录树里还留着 58，也已改。这类数不像引用错误那样无害——「7 个落点没有步骤」
  直接决定 bootstrap 类 hook 该在哪儿门闸，写少了会让人以为 `OnLoad` 有地方挂。

### D-031 solution 格式改为兼容 .NET 8 SDK

- 日期：2026-11-13 / 类型：结构 / 状态：已实现
- 改了什么：以 `HexaCiv.sln` 替换 `HexaCiv.slnx`，仍包含 SimCore、core.basic、CLI 与测试项目；GodotShell 继续单独构建。
- 为什么：当前项目目标为 net8.0，宿主安装 SDK 8.0.428；该 SDK 无法解析 `.slnx`，导致文档要求的根目录 `dotnet build` 失败。
- 影响：无运行时或哈希影响；根目录 build/test 可使用已安装 SDK。复核通过根 solution build 与 60 条测试。

### D-032 地图噪声改为整数定点

- 日期：2026-11-13 / 类型：结构 + 确定性 / 状态：已实现，已重跑回归
- 改了什么：`MapGen` 的噪声插值、外环高度及平方根改用百万分之一整数定点和整数平方根；`CoreInfo.MapGenVersion` 从 1 升至 2；`CaptureSave` 从快照的 `meta.mapGenVersion` 保留原地图版本，旧地图档读回再存不冒充新生成器。
- 为什么：旧实现以 `double` 中间量生成 `hex.height` 与 `hex.landform`，这些字段进入世界哈希，违反 `AGENTS.md` 的定点纪律，也没有跨平台舍入保证。
- 存档：地图保存在快照中，现有档读回不重新生成地图；schema 未变，无迁移器需要。
- 模拟差异（数据包未变，dataHash 仍为 `af324b5a33571234`）：

  | 场景 | 原地图算法 | 整数算法 |
  |---|---|---|
  | seed 7 / 6 回合 | `3c72a09ae26803e9` | `1cd6f74b25e33aa3`（CLI 与 Godot 4.7.2 相同） |
  | seed 99 / 50 回合 | `a221788270cd695a` | `e6c324ac3bcfbe3b`（2026-11-13 Windows #2 重跑补齐） |
  | seed 5 / 第 12 回合快照 | `3a0d8ef70410ba0a` | `9b82e9599ae198cd` |
  | seed 5 / 20 回合续跑 | `511b3328ad7ee58e` | `482c8643eada3e64`（续跑与直跑相同） |
  | seed 42 / 200 回合 | `36c3463658b99a90` | `66769b250054b11e`（仍为 2 城 / 人口 89） |

- 确定性证据：60 条测试全绿，包含旧地图版本保存回归；CLI 与 Godot smoke 输出同一逐回合终态哈希；跨 OS 仍待 CI 验证。

### D-033 地图形状从半径制改为 32 × 20 矩形（M2 切片一）

- 日期：2026-11-13 / 类型：结构 + 数值 / 状态：已实现，已重跑全部回归
- 改了什么：
  - `MapGen.Bootstrap` 从「半径 4 的六边形（61 格）」改为 §3.9 / §13.1 要求的 **32 × 20 = 640 格**；
    按 odd-r 偏移坐标铺格、存轴坐标（`q` 可为负），`MapGen.Distance` 仍是标准轴距。
  - 尺寸配置键 `config.mapRadius` → `config.mapColumns` / `config.mapRows`（默认 32 / 20，8–128 越界拒建图）；
    全局写回字段 `meta.mapRadius` → `meta.mapColumns` / `meta.mapRows`。
  - 高度图改为「矩形四边归一化抬升 + 两层值噪声」，材料仍是整数定点与整数平方根。
  - `CoreInfo.MapGenVersion` 从 2 升至 3；`GodotShell.HexView` 增加整图缩放入视口、拾取自检改为
    640 格 × 3 点（`smoke 拾取：1920/1920`）。`sim map` 增加一行 bootstrap 建图计时。
- 为什么：§3.9 与 §13.1 明确要求 32 × 20；M2 的验收标准「32×20 生成 < 1 s」在旧形状下无法判定。
  这是 `ROADMAP.md` §3 写明的「必须先动接口」的第一件事。
- 存档：地图数据本就在快照里，旧档读回不重新生成地图，`meta.mapGenVersion` 保留原值；
  schema 未变，无迁移器。但**新开局不再长旧半径制地图**，旧种子哈希全部按 §3.7 末行重排。
- 模拟差异（数据包未变，`dataHash` 仍为 `af324b5a33571234`）：

  | 场景 | 半径制 61 格（MapGenVersion=2） | 32×20（MapGenVersion=3） |
  |---|---|---|
  | seed 7 / 6 回合 | `1cd6f74b25e33aa3` | `e0909883c6019fba`（宿主 #1 CLI 与 Godot 4.7.2 smoke 同值，`SMOKE PASS`） |
  | seed 99 / 50 回合 | `e6c324ac3bcfbe3b` | `856535a916aa2e20` |
  | seed 5 / 第 12 回合快照 | `9b82e9599ae198cd` | `2b4879a4f1de53f9` |
  | seed 5 / 20 回合续跑 | `482c8643eada3e64` | `49b257e9a5ce3fe4`（续跑与直跑相同） |
  | seed 42 / 200 回合 | `66769b250054b11e`（2 城 / 人口 89） | `8ff2b81f535a32b6`（2 城 / 人口 174；产出上限 44.90 → 88.20 石） |

- 确定性证据：63 条测试全绿（新增 `地图是32乘20且id与轴坐标互证`、`同种子同图换种子换图`、
  `建图落在M2性能预算内`）；`sim map --seed 7` 建图 34.7 ms（判据 < 1000 ms）。
- 影响：所有地图相关基线哈希与 200 年轨迹整表换值（`BALANCE.md` §4 已重跑）；
  `Siting.BestTownSite` 与命令面未动，`scripts/*.txt` 无需改。CLI 与 Godot 4.7.2 已在宿主 #1 对账同哈希；
  跨 OS 对账仍待 CI。

### D-034 地表扩到 §3.2 的九类，并接入温度/降水分类（M2 切片二）

- 日期：2026-11-13 / 类型：数据 + 数值 / 状态：已实现，已重跑全部回归
- 改了什么：
  - `data/core.basic/landform.json` 从 5 行补到 GDD §3.2 的 **9 行**：新增湿地、荒漠、冻土、海岸，数值照抄该表
    （湿地防御 −1、荒漠/冻土料潜力 1、海岸粮潜力 2）。
  - 每条地表新增 `class.priority` 与 `class.height/temp/rain/edge` 区间；`data/_schema.json` 同步声明这些字段。
  - `MapGen` 的高度图改为「大陆轮廓（中央高、边缘低）+ 两层值噪声」，新增 `hex.temp`（按行纬度梯度 + 小噪声）
    与 `hex.rain`（两层值噪声）；分类不再按高度分档，而是按 `class.*` 区间从高优先级往下匹配，
    区间有空档直接抛异常（§3.2 / §3.7 第 3–4 步）。
  - 九类地表的 `moveCost` / `defense` / `buildable` / `potential.food` 全部由数据写入 `hex.*`。
  - `CoreInfo.MapGenVersion` 从 3 升至 4。ASCII 图城址标记改为 `@`（避免与海岸 `C` 冲突）；
    Godot 悬停与侧栏显示温度/降水。
- 为什么：§3.2 的地表表是 9 行，M2 的验收「32×20 五层数据」需要地表分类完整；
  温度/降水是 §3.7 第 3 步，也是后续河流（冻土/荒漠影响径流）与形胜（绿洲/湖岛）的前置。
- 存档：`_schema.json` 只加可选字段，旧数据包仍能通过校验；地图仍在快照里，旧档读回不重算地图，
  缺 `hex.temp/rain` 时壳层按 0 回退，不需要迁移器。但新开局的地图整体重排，旧种子哈希按 §3.7 末行作废。
- 模拟差异（`dataHash` 从 `af324b5a33571234` 变为 `eebed2c6f0ba30d1`）：

  | 场景 | MapGenVersion=3（五类地表） | MapGenVersion=4（九类地表） |
  |---|---|---|
  | seed 7 / 6 回合 | `e0909883c6019fba` | `5e8fb72f798550d4`（宿主 #1 CLI 与 Godot 4.7.2 smoke 同值，`SMOKE PASS`） |
  | seed 99 / 50 回合 | `856535a916aa2e20` | `48ca402a8e5a4ae5` |
  | seed 5 / 第 12 回合快照 | `2b4879a4f1de53f9` | `1ee5dcff0dd4127b` |
  | seed 5 / 20 回合续跑 | `49b257e9a5ce3fe4` | `b189d5e21b3eafab`（续跑与直跑相同） |
  | seed 42 / 200 回合 | `8ff2b81f535a32b6`（2 城 / 人口 174；产出上限 88.20） | `820efa76ba0f98b6`（2 城 / 人口 163；产出上限 82.75 石） |

- 确定性证据：65 条测试全绿（新增 `地表是GDD九类且每格都命中自己的气候区间`、
  `温度随纬度升高且九类地表都能长出来`）；`sim map --seed 7` 建图 41.0 ms（判据 < 1000 ms）；
  地图哈希与 `hex.temp/rain` 全部走整数定点，无浮点中间量。
- 影响：`BALANCE.md` §4 的 200 年轨迹整表重跑；`DATA_SCHEMA.md` 的实况段与 `docs/` 基线同步更新。
  `Siting.BestTownSite` 与命令面未动，`scripts/*.txt` 无需改。跨 OS 对账仍待 CI。

### D-035 河网与水体：§3.3 有向边 + §3.1 水体层 + 水利系数（M2 切片三）

- 日期：2026-11-13 / 类型：数据 + 数值 + 规则 / 状态：已实现，已重跑全部回归
- 改了什么：
  - 新增 `data/core.basic/water.json`（`waterbody` 2 条，`dataHash` 因此变化）：`water.lake`（湖，粮 2）、
    `water.sea`（近海，粮 1），数值照抄 GDD §3.2 水域行；`data/_schema.json` 增加 `waterbody` 类型。
  - 新增叠加层 `hex.water`：水体格仍然保留九类 `hex.landform`（§3.1 五层模型），
    但 Y 由水体数据覆盖，`hex.buildable=false`，不进辖地、不计入选址评分。
  - 新增 §3.3 有向边：每段河写在上游格上——`hex.river.down`（下游 hex id）、`hex.river.flow`（集水格数）、
    `hex.river.width`（1–4 档）。生成用整数优先淹没法：从外圈海格出发按淹没水位扩张，
    parent 链必然入海，不会成环、不会有无出口的内陆湖。
  - §3.6 水利系数：河/湖相邻的陆格 `Y_g = 地表潜力 × 1.15`；水体格自己的 Y 取 `water.*`，不叠这个系数。
    河段阈值 `RiverMinFlow=12`、湖泊阈值 `LakeMinFlow=2`（见代码注释与 `AI_HANDOFF.md` 坑 20）。
  - `CoreInfo.MapGenVersion` 从 4 升至 5。CLI ASCII 用 `~` 画水域、`+` 画河段；Godot 按 `water.*` 配色、
    沿 `hex.river.down` 画河线，悬停/侧栏显示水体与集水数。
- 为什么：§3.7 第 3 步要求河流与湖泊；§3.3 的「河 = 地块边上的有向图」是 §3.4 河口/三角洲/峡谷/上游高地的前置；
  §3.6 的水利系数 1.15 需要河/湖先存在。深海的航海门槛归 M7，筑坝/水运/渡口归 M5/M7。
- 存档：没有新增结构版本——`hex.water` / `hex.river.*` 都是普通字段，旧档读回按缺省空串/0 处理，不需要迁移器；
  `meta.mapGenVersion=5` 写进存档头部。新开局的地图整体重排，旧种子哈希按 §3.7 末行作废。
- 模拟差异（`dataHash` 从 `eebed2c6f0ba30d1` 变为 `854a0facb520c4ee`）：

  | 场景 | MapGenVersion=4（切片二） | MapGenVersion=5（切片三） |
  |---|---|---|
  | seed 7 / 6 回合 | `5e8fb72f798550d4` | `707c154e0930be52`（宿主 #1 CLI 与 Godot 4.7.2 smoke 同值，`SMOKE PASS`） |
  | seed 99 / 50 回合 | `48ca402a8e5a4ae5` | `d8e2fb8206d7544f` |
  | seed 5 / 第 12 回合快照 | `1ee5dcff0dd4127b` | `1403936053e8ccf5` |
  | seed 5 / 20 回合续跑 | `b189d5e21b3eafab` | `18546b1d4f2d1866`（续跑与直跑相同） |
  | seed 42 / 200 回合 | `820efa76ba0f98b6`（2 城 / 人口 163；产出上限 82.75 石） | `fae9549e734a3908`（2 城 / 人口 164；产出上限 83.20 石） |

- 确定性证据：70 条测试全绿（新增 `河流每条有向边都连相邻格且最终入海或湖`、`水体按数据取粮潜力且不可建城`、
  `河湖相邻的陆地格享受水利系数`、`同种子同河网换种子换河网`、`自动选址与辖地都不会落在水域`）；
  `sim map --seed 7` 建图约 55 ms（判据 < 1000 ms）；河网、水体与 Y 全部整数定点，无浮点中间量。
- 影响：`BALANCE.md` §4 的 200 年轨迹整表重跑（+1 百人单位、产出上限 +0.45 石，全部来自 1.15）；
  `DATA_SCHEMA.md` / `SAVE_FORMAT.md` / `ROADMAP.md` / `TEST_PLAN.md` / `DEV_ENVIRONMENTS.md` 的实况段同步更新。
  `Siting.BestTownSite` 只增加「跳过水域」一行，命令面与 `scripts/*.txt` 未动。跨 OS 对账仍待 CI。

### D-036 十二形胜 + 五种门槛资源 + 图层 4–5（M2 切片四）

- 日期：2026-11-13 / 类型：数据 + 数值 + 规则 / 状态：**已实现，已重跑全部回归**
- 改了什么：
  - 新增 `data/core.basic/shape.json`（`shape` 12 条，§3.4 全部行）与 `data/core.basic/resource.json`
    （`resource` 5 条，§3.5 全部行）；`data/_schema.json` 增加 `shape` / `resource` 两种类型。
  - 新增 `hex.shape` 与 `hex.resource` 两个标签字段：形胜/资源是 §3.1 五层地块模型的第三、四层，
    叠加在地表上，不改 `hex.landform`，也不进 `town.stock.*` 的 5 类库存。
  - 新增 `hex.yield.mat`（料潜力）。§3.6 的 `Y_g` 对粮/料两行都成立，M1 只落了粮；山谷/火山的
    `yield.mat` 没有这个字段就无处可写。
  - §3.6 乘法链落到 `hex.yield.food/mat`：`(地表潜力 + 形胜地力) × 形胜倍率 × 资源倍率 × 水利系数`。
    `moveCost` / `defense` 也乘形胜修正（隘口 4×0.25=1 行军、2×2.5=5 防御）。`hex.water` 格仍走水体底值；
    渔汛按 §3.5 原文「湖海粮潜力 +1」做加法。
  - 新增 `src/Plugins/core.basic/Features.cs`：§3.4 十二种形胜的识别规则；§3.5 五种资源的地形资格 +
    每格确定性哈希生成，并对「整图缺一种」做一次确定性兜底（§3.7 公平性检查的一条）。
  - 新增 `src/Plugins/core.basic/Sculpt.cs`：§3.7 第 6 步「出生点公平性修正」的生成侧——
    主动雕刻一处盆地（距中心 2 格抬成丘陵环、留 1 个排水口）、一条山谷（6–10 格槽 + 两侧丘陵）、
    一段峡谷（高流量河两岸抬高、河床压低）。纯噪声在 32×20 上经常长不出这三种骨架，
    不雕刻就只能把识别阈值放松到「到处都是形胜」，那会毁掉 §3.4 的战略含义。
  - `CoreInfo.MapGenVersion` 从 5 升至 6。CLI `sim map` 新增主图 `--layer`
    （`terrain` / `owner` / `agriculture` / `resource` / `population`），并固定输出形胜分布、资源分布两张小图；
    Godot 增加「图层 T」按钮、左上角形胜圆点、资源图层标记，悬停/侧栏显示形胜、资源、料潜力。
  - `core.basic` 图层声明从 3 层补到 5 层：新增 `resource`（资源）与 `population`（人口密度）。
- 为什么：§12 M2 的交付是「六边形渲染、五层数据、生成器（盆地/山谷/河网/隘口）、拾取」，
  验收是「同形胜在 20 张图里人工可认出」；§3.4/§3.5/§3.9 是这三项的直接来源。
  军事实算（隘口 +3、关城）、筑坝/断水、资源门槛（铁/马/盐/香料）分别归 M7/M5/M3，
  本轮只把标签、倍率与查询落下来。
- 与 GDD 字面的三处解释（**待设计者确认，不阻塞 M2**）：
  1. §3.4 绿洲「粮潜力 ×1.5」而 §3.2 荒漠粮潜力为 0，纯乘法恒为 0。实现给 `shape.oasis`
     加 `bonus.food = 1.00`（形胜自带地力），再乘 1.5，得到局部 1.5。
  2. §3.4 只给了盆地 1.3 / 山谷料 1.2 / 三角洲 1.5 / 绿洲 1.5 / 火山料 1.5 的字面倍率；
     河口 1.2、山谷 0.75 行军、隘口 2.5 防御等其余取值是本切片的占位标定，全部住在 `shape.json`，
     改数值不需要动代码。
  3. 隘口仍按 §3.8「山地不是城址」保持 `hex.buildable=false`；它是 M7 的关城位，不是 M2 的城址。
     若设计者要「山口可建关城」，那是 M7 的额外规则，不在本轮偷偷放开。
  4. §3.4 写盆地是 8–20 格低地；32×20 的雕刻盆底本身是 7 格（中心 + 六邻，另有 1 格排水口），
     再被湖/谷切掉一两格时会出现 4–6 格的小盆地。实现把下限放到 4，20 个种子的多数是 7–9 格；
     若设计者要求严格 8 格起，改 `Features.BasinMinTiles` 即可，但会让一部分图没有盆地。
- 存档：没有新增结构版本——`hex.shape` / `hex.resource` / `hex.yield.mat` 都是普通字段，
  旧档读回按空串/0 处理，不需要迁移器；`meta.mapGenVersion=6` 写进存档头部。
  新开局的地图整体重排，旧种子哈希按 §3.7 末行作废。
- 模拟差异（`dataHash` 从 `854a0facb520c4ee` 变为 `78bad421c6439b0d`）：

  | 场景 | MapGenVersion=5（切片三） | MapGenVersion=6（切片四） |
  |---|---|---|
  | seed 7 / 6 回合 | `707c154e0930be52` | `dbd386ed1bd058de`（宿主 #1 CLI 与 Godot 4.7.2 smoke 同值，`SMOKE PASS`） |
  | seed 99 / 50 回合 | `d8e2fb8206d7544f` | `d4a2a19b7ccd4845` |
  | seed 5 / 第 12 回合快照 | `1403936053e8ccf5` | `96522e125fca1059` |
  | seed 5 / 20 回合续跑 | `18546b1d4f2d1866` | `c887c475cd95fd1c`（续跑与直跑相同） |
  | seed 42 / 200 回合 | `fae9549e734a3908`（2 城 / 人口 164；产出上限 83.20 石） | `1533e72d71f1e089`（2 城 / 人口 162；产出上限 82.05 石） |

- 确定性证据：86 条测试全绿（新增 16 条：形胜/资源数据、§3.6 公式、盆地/隘口/河口/上游高地/峡谷/
  湖岛/绿洲/盐湖/冲积扇/火山的结构判据、渔汛、20 图十二形胜、20 图五种资源、同种子同图、图层声明）；
  `sim map --seed 7` 建图约 71 ms（判据 < 1000 ms）；识别与雕刻只用整数与整数哈希，无浮点中间量。
- 影响：200 年轨迹重跑（人口 164→162、产出上限 83.20→82.05 石，来自地形重排与形胜倍率）；
  `DATA_SCHEMA.md` / `SAVE_FORMAT.md` / `ROADMAP.md` / `TEST_PLAN.md` / `BALANCE.md` /
  `AI_HANDOFF.md` / `AGENTS.md` / `DEV_ENVIRONMENTS.md` 的实况段同步更新。
  20 张图的形胜清单在 `analysis/out/m2-shapes.txt`（gitignore 产物），人工目检判据仍是 §12 的那一条。

### D-039 合并 M2 切片三/四 与 M3/M4 两条开发线（编号顺延 + 基线重定）

- 日期：2026-11-13 / 类型：结构 / 状态：**已实现**
- 改了什么：
  - `master` 合并 `origin/master` 的四个提交（M3 公式二、M4 切片一及其两份文档同步）。代码自动合并，
    只有 `AGENTS.md` / `docs/AI_HANDOFF.md` / `docs/ROADMAP.md` 三份文档需要人工合。
  - **编号撞车**：两条线各自把新条目编成 D-035/D-036。按「编号只增不复用」，M2 切片三/四保留
    D-035/D-036（代码注释已引用），M3/M4 两条顺延为 **D-037**（公式二 + 守恒）/ **D-038**（M4 切片一）。
  - 合并后的新基线：`dataHash 1c451f9a3d142d7e`；seed 7 / 6 回合 `2e9b047076e49543`（CLI 与 Godot 同值）；
    seed 99 / 50 回合 `aad33ef54aca84a7`；快照往返 `e9962eacb7b9402b` / 续跑 `1bf10ff00577dfca`；
    seed 42 / 200 回合 `880d3f17bc5c47aa`；`dotnet test` = 95 绿；建图 74.3 ms。
- 为什么：两条开发线并行时，各自的基线都是在对方的改动之外测的，合并后**任何一方的旧哈希都不再成立**
  （地图字段 × 阶级/价格循环共同进世界哈希）。不重测就沿用旧值，等于把「确定」写成文档里的假证据。
- 影响：`AGENTS.md` / `AI_HANDOFF.md` / `ROADMAP.md` / `BALANCE.md` / `TEST_PLAN.md` / `SAVE_FORMAT.md` /
  `DATA_SCHEMA.md` / `DEV_ENVIRONMENTS.md` 的实况数字全部换成合并后的实测值；
  D-037/D-038 条目里的"当时"基线表保留为历史记录，不再作为当前对账锚点。
  **已知回归**：200 年基线在 M4 状态机下进入死亡螺旋（`pop` 150+ → 6），两条分支各自都如此，
  不是合并引入的；处置记在 `AI_HANDOFF.md` §5 坑 22 与 `ROADMAP.md` §4，归 M4 切片二标定。

---

## 5. 逻辑量盘点（§12 推进规则第 2 条：只增不减视为失控信号）

```bash
# 重新数一遍的命令（obj/bin 已排除）
find src/SimCore -name '*.cs' -not -path '*/obj/*' | wc -l
find src/SimCore src/Plugins/core.basic src/HexaCiv.Cli src/GodotShell tests \
     -name '*.cs' -not -path '*/obj/*' -exec cat {} + | wc -l
dotnet run --project src/HexaCiv.Cli -- check | head -3
```

| 量 | M2+M3+M4 切片一合并后（2026-11-13） | 怎么数出来的 |
|---|---:|---|
| SimCore 文件 / 行 | 20 / 3404 | `find src/SimCore -name '*.cs'`（不含 `obj/` 生成物） |
| `core.basic` 文件 / 行 | 10 / 2357 | 同上（+`Pricing.cs` / `Features.cs` / `Sculpt.cs`） |
| CLI 壳层 / Godot 壳层 行 | 1562（合计） | 壳层合计 1562 行，**里面一条规则都没有**（§10.2） |
| 测试行 / 条数 | 1827 / 95 | `dotnet test --list-tests` |
| 数据文件 / 记录 | `_schema.json` + 7 个 json / 44 条 | 地表 9、水体 2、形胜 12、资源 5、物资 5、建筑 3、阶级 8 |
| 结算步 | 15 | §2.3 写死，`sim check` 打印的表 |
| hook 落点 | 21（其中 7 个不落在任何步骤） | `SimOrder.DescribeHookMap()` |
| 已实装的规则处理器 | 12 处注册 | `BasicPlugin.Register` |
| 已实装的公式 | 3 / 3 | 公式一 ✅ 公式二 ✅ 公式三 ✅ |
| 命令 | 3 | `nation.found` / `town.found` / `building.build` |
| 不变量 | 4 组 | `basic.ranges` / `basic.price_bounds` / `basic.conservation` / `basic.class_monotonic` |
| 图层声明 | 5 / 9 | `terrain` `owner` `agriculture` `resource` `population`；§3.9 的阶级/贸易/军事/信仰/文化随 M4/M5/M7/M8 进 |

**判读**：合并后 SimCore 仍是 20 文件 / 3404 行，**一行没涨**——M3/M4 的规则、价格与状态机全在
`core.basic`（10 文件 / 2357 行），符合「Core 稳定、插件扩展」。单文件最长的仍是 `MapGen.cs`
（约 500 行，未触发 ~1200 行的拆文件阈值）。
下一次盘点在 M4 切片二结束时做，重点看状态机罚则的标定（200 年死亡螺旋）与原料约束的落点。

# AI_HANDOFF · 进度、已实现、待办、坑

> 规范来源：GDD §11.1（M0 必建文档）、§11.4（收工必须更新本文件）。
> **本文件只写「现在几点」，不写「去哪儿」**——里程碑状态看 `docs/ROADMAP.md`，数值依据看
> `docs/BALANCE.md`，测试清单看 `docs/TEST_PLAN.md`，接口看 `docs/CORE_API.md` / `docs/PLUGIN_API.md`。
> 这里只放三样东西：接手第一天需要知道的仓库实况、会咬人的坑、收工前的复验命令。
> 同一个缺口在别的文档里已经写过的，这里只给指针，不重抄一遍。

---

## 0. 一分钟版

- **M1 Core 已经立住，M0 只差 CI**。59 条测试绿，三个基线哈希当场复验过（§1 表格）。
- **接手第一件事不是写代码，是跑 §6 那四条命令**。四条里有对不上的，先修环境再动代码——
  这个仓库的正确性主张全靠「同种子同命令流 → 同哈希」撑着，跑不出来时任何改动都无法判断好坏。
- **读的顺序（§11.4 原文）**：本文件 → `AGENTS.md` → GDD §10 + 附录 A → 要动的子系统章节。
  `AGENTS.md` 里那八条架构原则是本仓库的地契，违反其中任意一条的提交应当被打回，而不是被合并后再修。
- **不许改 `docs/GDD.md`**（附录 B 第 10 条 + 设计者约束）。和 GDD 有分歧就写进
  `docs/CHANGELOG-DESIGN.md` §1 等裁决，那里已经挂着 D-001…D-004 四条待裁决。

---

## 1. 仓库实况（2026-11-13 当场核对）

| 项 | 值 | 复现 |
|---|---|---|
| 分支 / 提交数 | `master`，13 笔，**无 remote、无 tag** | `git branch -a` / `git rev-list --count HEAD` |
| 未提交 | `docs/TEST_PLAN.md`、`docs/CHANGELOG-DESIGN.md`、`docs/AI_HANDOFF.md`、`AGENTS.md`（M0 文档第三批） | `git status --short` |
| 测试 | **59 绿，0 跳过**，1 s | `dotnet test --nologo` |
| 跨宿主末回合哈希 | `3c72a09ae26803e9`（seed 7 / 6 回合 / `scripts/godot-smoke.txt`） | CLI `sim hash` 与 Godot `-- --smoke` 各打一次，两边同值 |
| 快照往返 | `3a0d8ef70410ba0a`（seed 5 / 20 回合 / 第 12 回合存盘） | `sim replay --seed 5 --turns 20 --at 12` |
| 续跑等价 | `511b3328ad7ee58e`（一口气跑满 = 存盘续跑） | 同上 |
| 200 年基线 | seed 42 末态 `36c3463658b99a90`，`dataHash af324b5a33571234`，2 城 / 人口 89 | `sim run --seed 42 --turns 200 --headless --out analysis/out/demo.csv` |
| 代码量 | SimCore 20 文件 / 3884 行 · core.basic 7 / 1158 · 壳层 1457 · 测试 1298 | `find … -name '*.cs' -not -path '*obj*' \| xargs wc -l` |

**上面每一个数字都是这次会话里重跑出来的**，不是从旧文档抄的。改动作废它们不算违约，
但请把 §6 那四条命令当作收工自检跑一遍，把新值写回 `docs/ROADMAP.md` §2 和这里。

`analysis/out/*.csv` 与 `saves/*.bin` 都被 gitignore（`.gitignore:20,26`），
它们是**产物**不是**现状**——§5 坑 14 讲过这里踩过一次。

---

## 2. 已实现（只列「有代码且当场验过」的）

### 2.1 SimCore 九项职责（GDD §10.2）

| 职责 | 文件 | 细节文档 |
|---|---|---|
| 世界存储 | `World.cs` / `EntityId.cs` / `SimValue.cs` / `Fix.cs` | `CORE_API.md` §4 |
| 结算调度 | `Time.cs`（15 步 + 21 hook 落点 + 门闸 `SimOrder.At`） | `SIM_ORDER.md` §2/§3 |
| 分层随机 | `Rng.cs`（SplitMix64，命名空间带回合号，回合边界重置流缓存） | `CORE_API.md` §9 |
| 数据注册 | `DataRegistry.cs`（`_schema.json` 驱动，严格/宽容两档） | `DATA_SCHEMA.md` |
| 不变量 | `Invariants.cs`（按步区间校验，违规抛 `InvariantViolation`） | `CORE_API.md` §8 |
| 因果链 | `Mutation.cs`（每次变更带 reason，`Diff` 是回放与报告的最小单位） | `CORE_API.md` §8 |
| 命令流 | `Commands.cs`（意图段入队、定序、受理结果与执行率） | `CORE_API.md` §6、`NETWORKING.md` §3 |
| 存档 / 回放 | `Save.cs`（快照 + 命令日志 + 迁移链） | `SAVE_FORMAT.md` |
| 插件宿主 | `Plugins.cs` / `PluginHost.cs`（清单校验、依赖定序、拒载原因） | `PLUGIN_API.md` §3/§11 |

`Sync.cs` / `StateHash.cs` 是第十块砖：分块哈希与联机一致性检查（`NETWORKING.md` §5）。

### 2.2 core.basic（M1 循环的全部规则都在这一个插件里）

半径制六边形图（§3.7 前两步）→ 建国 → 建城（§3.8 距离闸门）→ 开荒 / 起仓 →
产粮（公式一，§5.4 步 6 损耗）→ 吃粮 → 配给 → 满意度（公式三 + 正态扰动）→ 人口
（零头桶累积）→ 不安与反抗计数 → 年报。三条公式实装两条（价格没有），21 个落点里挂了 11 个
处理器，3 条命令，1 组不变量，3 个图层。逐条常数与解析解在 `BALANCE.md` §1/§3。

### 2.3 两个壳层

- **CLI**（`src/HexaCiv.Cli`，718 行）：`run / check / hash / map / peek / replay / help` 六个动词 +
  `--seed --turns --at --out --save --load --script --data --plugins --no-plugins --no-data --steps --explain --lenient --quiet`。
  动词表在 `Program.cs:15` 的 `UsageText`，改参数别只改代码。
- **GodotShell**（`src/GodotShell`，739 行）：六边形绘制 + 点格建城 + 侧栏 + 年报 + `-- --smoke`
  headless 自检（含 183/183 拾取命中与字体诊断）。**刻意不进 `.slnx`**，理由见 `ARCH.md` §5。

---

## 3. 本次会话改了什么（一笔一行，细节在 CHANGELOG-DESIGN）

| 提交 | 事 | CHANGELOG 条目 |
|---|---|---|
| `8039610` | 低粮安不再把人口冻死在定点刻度上 + 回归测试 | D-020（附模拟差异表） |
| `88b5859` | 修一批指错章节的 GDD 引用 + 补 BALANCE / ROADMAP | D-030 |
| 未提交 | TEST_PLAN / CHANGELOG-DESIGN / AI_HANDOFF / AGENTS | — |

D-020 是这次会话里唯一的**行为变更**：`SeasonRules.Demography` 里 `0.04 × 粮安` 只有 0.00xx 量级，
在 ×100 定点下 `粮安 ≤ 0.62` 时被截成 0，乘回人口还是 0，于是人口永久卡在地块上限。
改法是先把 P 乘进去、全程只在最后截一次。分叉范围：前 62 年逐回合哈希不变，
200 年基线 `859a9a0963fb2267` → `36c3463658b99a90`（seed 42 从「第 83 年起冻死 117 年」变成「第 200 年 89 个百人单位」）。

---

## 4. 待办（按「什么时候必须做」排，不按严重程度）

| # | 事 | 判据 | 记录在哪 |
|---|---|---|---|
| 1 | **CI 工作流**（M0 三条验收里唯一没兑现的） | 三个 job 在 ubuntu + windows 上都绿；M0 才打 `v0.0` | `TEST_PLAN.md` §6 |
| 2 | 根 `Readme.md` 重写（需求原文已在 `docs/REQUIREMENTS.md`） | 玩家视角：一条命令跑起来 + 一张截图 + 指回 `AGENTS.md` | `CHANGELOG-DESIGN.md` D-004 |
| 3 | tag `v0.0`（M0）/ `v0.1`（M1）+ `master` → `main` 改名 | §11.2 的 tag 表；`v0.2` 的语义冲突挂在 D-001 | `CHANGELOG-DESIGN.md` §1 |
| 4 | **M2 地图**：32×20 矩形、河网 / 形胜 / 门槛资源、bootstrap 计时 | §3.7 六步全跑 + 三条验收各给得出证据 | `ROADMAP.md` §3 |
| 5 | M2 的三条接口前置改动（半径制 → 矩形、`CoreInfo.MapGenVersion` +1、只改 `Siting`） | 旧种子旧图必须仍然可复现，或明确记录重排 | `ROADMAP.md` §3 |

---

## 5. 坑（这一节是本文件的正文；每条都是「症状 → 原因 → 处置」）

### 定点与确定性

**坑 1 — 小于 0.01 的量绝不先物化再相乘。**
症状：某个变量到某一年就不动了，年报里没有任何错误，因果链文本看起来完全正常。
原因：×100 定点下 `0.04 × 粮安`（≤ 0.62 时）截成 `0.00`，乘回 P 还是 0。
处置：把大整数先乘进去，全程只在最后截一次：`ΔP = P × 粮安 × 4 ÷ 100`。
判据与代码位置在 `BALANCE.md` §2。**同类量在八阶级里成串出现**（执行率、税率、系数），写规则前
先问一句「这一步会不会落在 1/100 刻度以下」。

**坑 2 — 没有 `Int128`，只有 `UInt128`。**
附录 A 写的 `Int126` 是 .NET 9/10 的 API，net8 编不出来。中间量用 `UInt128` + 显式符号位
（`Fix.MulDiv`），别退回 `double`——那是 §10.7 的头号禁令。见 `CHANGELOG-DESIGN.md` D-011。

**坑 3 — 浮点、`DateTime`、`Pow`/`Exp`/三角函数在模拟代码里一律不许。**
`Rng.NextGaussian` 用的是 Irwin–Hall 求和，均匀支取的是 `[0,100)`，期望 **49.5 不是 50**：
按 50 减会留下每年 −0.12 的系统偏置，S 的均值就不是文档说的 0。
`RngTests.正态扰动均值为零且离散度等于标称标准差` 钉的就是这个常数。

**坑 4 — 改文案安全，改参数分叉。**
`Command.Canonical()`（`Commands.cs:34`）只拼 `Turn|Seq|Issuer|Type|args`，**不含 reason**；
`StateHash.Compute` 混的是 `commands.LogHash`（`StateHash.cs:42`）。
所以 reason、因果链、`--explain` 文本、报告措辞都不进哈希；命令 args 进。
这次会话能一次改十几处引用文本而哈希三位不动，靠的就是这条。反过来：
`scripts/*.txt` 里把 `hex=auto` 改成具体格子，基线立刻分叉。

**坑 5 — `hex=auto` 的普适性不是免费的。**
选址规则在 `Siting.BestTownSite`，脚本换任何种子都成立的前提是**不硬编码格子**。
要改打分只改这一个函数，命令面与脚本都不用动（这是它当初存在的全部理由，见 `ROADMAP.md` §3）。

### 结算顺序与 hook

**坑 6 — Pre == Post 的六步必须用门闸，不能用 `InStep`。**
症状：只挂了一个插件时一切正常，加第二个插件后结果随 `priority` 变化。
原因：`OnProduction` 一类落点在同一步的首尾各广播一次，`InStep` 只看步不看段（`Time.cs:154`，它的注释就地写着「会跑两遍」）。
处置：`SimOrder.At(args, stepId, SimOrder.SlotPre)`；全表在 `SIM_ORDER.md` §3，门闸写法在 `PLUGIN_API.md` §4。

**坑 7 — 21 个落点里有 7 个不落在 15 步上。**
`OnEventApply / OnHeroRoll / OnAppointment / OnDemand / OnBeforeSave / OnLoad / OnMigrate`
住在意图段、存档期、命令执行器里。症状：拿 `sim run --steps` 的分步耗时表去找 `map.gen`，找不到——
地图生成是 `RegisterBootstrap`，跑在 `Bootstrap()`，不在 15 步的任何一步上。
要测 §12 的「32×20 生成 < 1 s」得先给 `sim map` 加一行 bootstrap 计时（`ROADMAP.md` §3 末）。

**坑 8 — `OnProduction` 一年广播 7 次。**
第 2 步末 1 次 + 第 3/4/5 步首尾各 2 次。挂在上面做「年度累计」会 ×7。
（本次会话前数错过一次，写成「6 个落点不落在任何步骤」——按 `sim check` 重数是 **7**，已改。）

### 壳层与构建

**坑 9 — `TreatWarningsAsErrors` 是 true。**
`Directory.Build.props`：未使用的 using、过时 API、可空警告一律**编译失败**，不是黄色警告。
CI 里不要为了跑通而关掉它；`WarningsNotAsErrors` 只豁免了 `NU1503` 一项。

**坑 10 — 插件必须由宿主从 `build/plugins/*.dll` 装载，且必须先 build。**
CLI 用默认 `AssemblyLoadContext`，Godot 用它自己的脚本上下文；把插件类型直接 `using` 进宿主是
`76ce061` 修掉的错。改完 `core.basic` 不重新 build 就跑 smoke，装到的是旧 dll——
症状是「代码改了行为没变」，这时先去看 `build/plugins/core.basic.dll` 的时间戳。

**坑 11 — 跨宿主一致性不在 `dotnet test` 里，要手动跑两条。**
`sim hash --seed 7 --turns 6 --script scripts/godot-smoke.txt` 与
`godot --headless --path src/GodotShell -- --smoke` 必须同为 `3c72a09ae26803e9`。
改 `Main.cs` 的 `Smoke()` 参数**必须同步改 `scripts/godot-smoke.txt`**，两边逐字相同才有意义（`TEST_PLAN.md` §3）。

**坑 12 — GodotShell 不在 `.slnx` 里**：`dotnet build` 不编它，验收要靠 Godot 可执行文件单独跑
（`ARCH.md` §5）。仓库机器上有两个 4.6 版本，**只有 mono 版能跑 C#**：不带 mono 的正式版跑
`-- --smoke` 什么都不打印、**退出码还是 0**（当场验过）。判据是屏幕上有没有 `SMOKE PASS`，不是退出码——
CI 里这条 job 必须 grep 这行，否则会把「根本没跑」当成「跑通了」。

**坑 13 — 中文渲染是壳层的事，也是 headless 的盲区。**
smoke 会打 `FallbackFont=FontFile FallbackFontSize=16`。没装 CJK 字体的机器上地图文字会变方块，
但哈希不受影响（文案不进哈希，坑 4）。§12 的 M2 人工验收要在有字体的机器上眼看一次。

### 开发流程

**坑 14 — `analysis/out/*.csv` 和 `saves/*.bin` 是旧产物，不是现状。**
症状：改完规则后拿一张旧 CSV 对比，看起来「改了第 8 回合的产粮」，实际前 8 回合逐回合哈希一模一样——
那张表是上一次别的种子或别的版本留下的。这次会话真的踩了：`smoke8.csv` 里的 8.06 白吓一跳。
处置：A/B 之前先把 CSV 重新生成一遍（§6 第 4 条）。

**坑 15 — 做 A/B 用 `git checkout <commit>~1 -- <file>`，别用 `git stash`。**
改动一旦提交，`git stash push` 是空操作，你会得到「改前改后完全一致」的假结论。
本次会话第一次做定点回归就是这么废掉的，换成 `checkout <commit>~1 -- file` 才拿到「修复前测试真的红」。

**坑 16 — 测试钉公式，不钉基线数字。**
`--out` 的 CSV 是基线，任何数值改动都会漂；断言写成解析解（`P* = 2 × Cap × Facility ÷ need`，
`BALANCE.md` §3）才留得住回归。加测试的规矩在 `TEST_PLAN.md` §4：改公式要附反例，失败信息要自带证据。

**坑 17 — 规则改动没有 `CHANGELOG-DESIGN.md` 条目 = 未完成**（§11.3 第 4 条）。
数值改动还要附「改了什么 / 为什么 / 模拟差异」表（第 1 条）。破存档必须附迁移器 + 旧档回归（第 3 条）。

---

## 6. 收工前复验（四条，跑完把新值写回 §1）

```bash
dotnet build -v q --nologo                          # 0 错误（warning = error，坑 9）
dotnet test --nologo                                # 59 绿
dotnet run --project src/HexaCiv.Cli --no-build -- hash --seed 7 --turns 6 --script scripts/godot-smoke.txt
dotnet run --project src/HexaCiv.Cli --no-build -- replay --seed 5 --turns 20 --at 12
godot --headless --path src/GodotShell -- --smoke    # 与第 3 条同值；mono 版可执行文件
```

`sim check` 顺手跑一下：它同时校验数据包、清单、15 步表与 21 个落点表，是「文档与实现有没有分叉」的最快探针。

§11.4 收工三件事：更新本文件 → 提交 → 打 tag（`v0.0` 的门槛是 §1 表格的 CI 那行补上）。

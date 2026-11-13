# AGENTS.md · 后续 AI 的最高优先级工作说明

> 谁该读：任何要动这个仓库的 AI 或人，**在写第一行代码之前**。
> GDD §11.4 规定的开工顺序就是本节引用的顺序：本文件 → [`docs/AI_HANDOFF.md`](docs/AI_HANDOFF.md) §0 与 §5
> → [`docs/GDD.md`](docs/GDD.md) §10 + 附录 A → 要动的子系统章节。
> 开始前还要按 [`docs/DEV_ENVIRONMENTS.md`](docs/DEV_ENVIRONMENTS.md) 探测当前宿主；不要假定路径、操作系统或工具已配置。
> **开工第一步先从 `origin` 同步**：`git fetch origin` 并把当前分支快进到远端最新；本地有未提交改动先提交或另存，
> 不许覆盖。详见 §8 第一条。
> 本文件只写「不许做什么」和「怎么验」。**规则内容的唯一来源是 GDD**，这里不重抄。

---

## 1. 项目简介

《群邦纪元》（代号 **HexaCiv**）：六角格上的聚合型文明模拟。一年 = 一回合，内部按四季分 15 步结算；
人口按阶级聚合、物资分 5 类，个体只用于名人。玩法是「三条公式 + 耦合表」驱动的宏观演化，
玩家的操作是**下政令**，不是微操单位。要做什么、卖给谁、为什么长这样，看
[`docs/GDD.md`](docs/GDD.md) §1–§2 与 [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md)。

## 2. 当前阶段

**M1 Core 已完成并当场复验；M2 的地图骨架（32×20）、九类地表/气候分类与河网/水体已完成；M0 的 CI 工作流已补，只差首次远端双系统全绿。** 详见 [`docs/ROADMAP.md`](docs/ROADMAP.md) §1/§2。

- 能跑的东西：无头模拟器 `sim`（6 个动词）、Godot 窗口壳（32×20 地图 + 点格建城 + headless 冒烟）。
- 还不能跑的东西：价格（公式二）、八阶级（只有农民）、AP、名人、军事、外交、事件、联机传输。
- 演示版本：`dotnet run --project src/HexaCiv.Cli -- run --seed 7 --turns 12 --steps --explain`。

## 3. 引擎与语言版本

| 项 | 值 | 出处 |
|---|---|---|
| 引擎 | Godot **4.7.2 .NET**（具体安装路径因宿主而异） | ADR-001；宿主清单见 [`docs/DEV_ENVIRONMENTS.md`](docs/DEV_ENVIRONMENTS.md) |
| ⚠️ | Godot C# 验收必须使用与项目 SDK 匹配的 .NET 版引擎，且机器必须安装 .NET SDK；判据是输出 `SMOKE PASS`，不能只看退出码 | 各宿主探测与复验记录见 [`docs/DEV_ENVIRONMENTS.md`](docs/DEV_ENVIRONMENTS.md) |
| 运行时 | .NET **net8.0**（与 GodotSharp `lib/net8.0` 对齐） | ADR-002 |
| 语言 | C# **LangVersion 12**、`Nullable enable`、`ImplicitUsings enable` | [`Directory.Build.props`](Directory.Build.props) |
| 编译 | **`TreatWarningsAsErrors = true`**（只豁免 `NU1503`）——警告就是失败 | 同上 |
| 依赖 | SimCore 与插件**只允许 `System.*`，零 NuGet 包**（测试项目例外，用 xUnit） | GDD §10.1 |

## 4. 目录结构

```
Readme.md              人类入口：怎么跑起来
AGENTS.md              本文件
Directory.Build.props  net8.0 / C#12 / Nullable / 警告即错误 / RepoRoot
HexaCiv.sln            SimCore + core.basic + sim + tests（GodotShell 刻意不在里面）
.github/workflows/ci.yml 三 job × ubuntu + windows；godot-smoke 强制核对 SMOKE PASS 与哈希
docs/                  GDD.md（唯一规则来源）+ §11.1 要求的文档 + REQUIREMENTS.md + DEV_ENVIRONMENTS.md
src/SimCore/           内核：20 个 .cs，引擎无关，一条玩法规则都没有
src/Plugins/core.basic/ 参考规则包：7 个 .cs + manifest.json（M1 的全部规则都在这一层）
src/HexaCiv.Cli/       无头壳层（sim：Program / Runner / Script / AsciiMap）
src/GodotShell/        窗口壳层（Main.cs / HexView.cs / Main.tscn / project.godot）
data/                  _schema.json + core.basic/*.json（数值只住在这里）
tests/SimCore.Tests/   xUnit，70 条
scripts/               demo.txt（命令脚本）、godot-smoke.txt（跨宿主对账脚本）
analysis/out/          基线 CSV（gitignore，**是产物不是现状**）
saves/                 回归样本档（.bin 被 ignore，只留 .gitkeep）
build/plugins/         core.basic.dll 的落点，宿主从这里装（gitignore）
```

逐层职责与调用链见 [`docs/ARCH.md`](docs/ARCH.md) §2–§4。

## 5. 插件开发标准

一个插件 = 一个目录 = 一份 `manifest.json` + 一个程序集（可选数据包目录）。完整规范在
[`docs/PLUGIN_API.md`](docs/PLUGIN_API.md)，以下是硬约束：

1. **`manifest.id` 必须等于目录名**；`api` 主版本必须是 `1`；键名 **snake_case**，
   **不认识的键直接拒载**（不是静默忽略），`PropertyNameCaseInsensitive = false`，驼峰也算写错。
2. **插件之间禁止直接调用**（GDD §10.4）。唯一的通信手段是 hook 点与共享世界状态。
3. 只能挂在既定的 **21 个 hook 点**上；顺序不可调换、不可插队，`priority` 只定同一步内的顺序。
4. **Pre == Post 同名的 6 个步必须用门闸**：`SimOrder.At(args, stepId, SimOrder.SlotPre)`。
   只看 `SimOrder.InStep` 会在同一步跑两遍——这是加第二个插件后才暴露的坑（`PLUGIN_API.md` §4）。
5. 主题级 hook 收到的是全局广播，**扇出点模式**（`FanOut` / `RequireFor`）负责按城镇逐个调用；
   扇出层自己不再读别的城镇的字段。
6. **`RegisterCommand` 必须带 usage**；命令执行器返回 `CommandResult`，**被拒也要留下记录与原因**，
   不许静默。合法性判定在第 1 步（意图段），不在入队段。
7. **写世界状态必须带 reason**（`ctx.Mutate(..., reason)` / `ctx.CreateEntity(reason)`）。
   没有原因的写入在 `--explain` 里说不清「为什么变了」，等于不可交付（GDD §8.4）。
8. **规则改动要同时交不变量**：能范围校验的写 `RegisterInvariant`，别指望壳层发现。
9. 数值只走 `Fix`（×100 定点）与 `long`；**不许** `float`/`double`/`DateTime`/`Pow`/`Exp`/三角函数。
10. **`map.gen` 类 bootstrap 不落在 15 步上**（`RegisterBootstrap`）。别把年度累计写在那种落点上。

## 6. 代码规范

- **注释、实体名、测试名一律中文**；标识符用英文，`snake_case` 的键与 `附录 D` 一致
  （字段路径形如 `town.stock.good.food`、`hex.yield.food`，见 `PLUGIN_API.md` §9）。
- **每条规则旁边写它实现的 GDD 章节**。写之前用 `grep -n '^### \|^## ' docs/GDD.md` 核对标题——
  本仓库有过成批指错章节的提交（`CHANGELOG-DESIGN.md` D-030）。指错不是 cosmetic：下一个人会照错的章节改代码。
- **定点纪律**：小于 0.01 的中间量绝不先物化再相乘，把大整数先乘进去、最后截一次
  （`BALANCE.md` §2；这条规则来自一次「人口冻死 117 年」的真实故障）。
- **排序一律 `StringComparer.Ordinal`**，字典用 `SortedDictionary`；遍历世界必须按 id 序数，
  顺序换一次就会改哈希（GDD §10.7）。
- **壳层不含规则**：`src/HexaCiv.Cli` 与 `src/GodotShell` 只调 `AdvanceTurn`、只渲染。
  在壳层里写一条 if 玩法分支，就是破坏 §10.2。
- **解释文本不许重算公式**：年报/因果链里的展示数字必须复用上一步算好的值。两处公式必然分叉。
- **测试钉公式，不钉基线数字**（`TEST_PLAN.md` §4）：断言写成解析解或反例，失败信息自带证据。
- 公开 API 改动要过 [`docs/CORE_API.md`](docs/CORE_API.md)：SimCore 的接口版本走该文件的版本号，不走 PR 意见。

## 7. 测试命令

```bash
dotnet build -v q --nologo                  # 必须 0 错误（警告 = 错误）
dotnet test --nologo                        # 70 绿，0 跳过
dotnet run --project src/HexaCiv.Cli -- check                      # 数据包 + 清单 + 15 步 + 21 落点全表
dotnet run --project src/HexaCiv.Cli -- hash  --seed 99 --turns 50  # 确定性自检
dotnet run --project src/HexaCiv.Cli -- replay --seed 5 --turns 20 --at 12
dotnet run --project src/HexaCiv.Cli -- run --no-plugins --no-data --turns 1000 --quiet   # Core 空跑
godot --headless --path src/GodotShell -- --smoke # 看 SMOKE PASS；需 Godot .NET 4.7.2 和 .NET SDK，若不在 PATH 按 DEV_ENVIRONMENTS.md 调用
```

当场复验过的期望值（2026-11-13，改数值后这些会漂，漂了要连同文档一起改）：

| 命令 | 期望 |
|---|---|
| `… hash --seed 7 --turns 6 --script scripts/godot-smoke.txt` | 末态 `707c154e0930be52`，**且必须与 Godot 冒烟同值** |
| `… replay --seed 5 --turns 20 --at 12` | 快照往返 `1403936053e8ccf5`；续跑 = 一口气跑满 `18546b1d4f2d1866` |
| `… run --seed 42 --turns 200 --headless --out analysis/out/demo.csv` | 末行：2 城 / 人口 164 / hash `fae9549e734a3908` |
| `… check` | `dataHash 854a0facb520c4ee`，15 步、21 落点（**7 个不落在任何步骤**） |

`dotnet test` 不覆盖跨宿主一致性：改过 `src/GodotShell/Main.cs` 的 `Smoke()` 参数，
**必须同步改 `scripts/godot-smoke.txt`** 并跑上面两条对比（`TEST_PLAN.md` §3）。

## 8. Git 规范

- **开工前先同步远端**：写第一行代码之前执行 `git fetch origin`，并把当前分支快进到远端最新
  （`git pull --ff-only`；未设置上游时用 `git pull --ff-only origin <当前分支>`）。
  多宿主轮流开发，落后于远端的本地基线会让复验数字与文档对不上。同步前先看 `git status`：
  有未提交改动先提交或安全另存，**不许**用 `reset --hard` / 清理命令覆盖；若快进被拒，停下来向人确认，不要强改历史。
- **Conventional Commits + 中文正文**，例：`fix(core.basic): 低粮安不再把人口冻死在定点刻度上`。
  带影响标签：`[数值零变化]` / `[破存档→已附迁移器]` / `[改手感]`。
- **关键任务完成时就提交**，不许攒到最后统一提交（设计者的硬约束，回滚粒度是交付要求的一部分）。
- **提交前跑测试**；不许隐藏失败测试、不许删测试绕过错误；**一个提交只做一件事**；不覆盖他人未提交代码。
- **分支**：`main`（可发布）/ `develop`（集成）/ `feature/*`（短命）（§11.2）。
  当前检出 `master` 且跟踪 `origin/master`，remote 为 `origin`；D-002 的改名计划是在此状态下写的。
  改分支前先核对 remote 与工作树，避免照搬旧的「无 remote」记录。
- **tag**：§11.2 与 §12 对 `v0.2` 及以上给了**两套互斥的含义**（挂在 **D-001 待裁决**）。
  重叠区间只有 `v0.0`（M0 骨架）与 `v0.1`（Core 可运行），两边一致——**只在这两个区间打 tag**，
  裁决前不许打 `v0.2`。
- **规则改动流程**（§11.3）：改数值 → 改 `data/*.json` → 跑基线 → 在
  [`docs/CHANGELOG-DESIGN.md`](docs/CHANGELOG-DESIGN.md) 记「改了什么 / 为什么 / **模拟差异**」→ 提交。
  改规则 → 先改 GDD（**只有设计者能改**，见下）→ 再改代码 → 附回归测试。破存档 → 必须附迁移器 + 旧档回归。
  **任何规则改动若在 `CHANGELOG-DESIGN.md` 没有条目，视为未完成。**
- **禁止修改 [`docs/GDD.md`](docs/GDD.md)**（GDD 附录 B 第 10 条 + 设计者约束：
  「你可以改动除 docs/GDD.md 的任何文件」）。与 GDD 有分歧 → 写进 `CHANGELOG-DESIGN.md` §1 待裁决，
  等人类拍板；**不许**用代码默默改掉它，也不许在别的文档里改口径。
- **收工**：更新 [`docs/AI_HANDOFF.md`](docs/AI_HANDOFF.md)（做了什么 / 没做完什么 / 下一步 / 踩过的坑）+ 提交 + 打 tag（§11.4）。

## 9. 已完成模块

| 层 | 内容 | 证据 |
|---|---|---|
| SimCore | 九项职责全部实现（世界存储 / 15 步调度 / 分层 RNG / 数据注册 / 不变量 / 因果链 / 命令流 / 存档回放迁移 / 插件宿主）+ 同步层与分块哈希铺好接缝 | `AI_HANDOFF.md` §2.1、`ROADMAP.md` §2.2 |
| core.basic | 32×20 矩形六边形图（§3.7 前四步：高度/温度/降水/九类地表 + §3.1 水体 + §3.3 河网）+ 建国 → 建城 → 开荒/起仓 → 产粮（公式一）→ 消耗 → 配给 → 满意度（公式三）→ 人口 → 不安计数 → 年报；**2/3 条公式** | `BALANCE.md` §1/§3/§4 |
| 壳层 | CLI 6 动词 + 选项集；Godot 窗口（绘制 / 拾取 / 侧栏 / 年报 / 640 格全格拾取自检；河网与水体渲染） | `ROADMAP.md` §2 |
| 测试 | 70 条：定点 12 / RNG 10 / 顺序与数据 10 / 循环 24 / 验收 14 | `TEST_PLAN.md` §2 |
| 文档 | §11.1 要求的 13 份全部落地（GDD 之外 12 份 + REQUIREMENTS）+ 本文件 | `ROADMAP.md` §2.1 |

## 10. 已知问题（会咬人的都在这里，全表见 `AI_HANDOFF.md` §5 与 `ROADMAP.md` §4）

1. **CI 尚待远端首验**：`.github/workflows/ci.yml` 已有三个 job × ubuntu + windows，但本地复验不等于 CI 绿；
   首跑全绿前 M0 仍标 🟡，不能打 `v0.0`。命令与判据见 `TEST_PLAN.md` §6。
2. **两条接缝是「解析了但没人用」**：`SimConfig.YearLimit`（120/200 年不会自动结束）、
   `manifest.data_packs`（卡包开关还不生效）。**别误判成已生效**——前者归 M9，后者归 M8。
3. **守恒等式没实现**（§5.9 前三条）：M1 没有进口/出口，现在只有范围与符号不变量兜着。归 M3。
4. **建图计时已经有了**：`sim map` 会打印插件的建图耗时（32×20 实测约 55 ms，判据 < 1 s）。
   河网与水体（§3.3/§3.1）已在切片三落地；剩余地图工作（12 种形胜 / 门槛资源 / 图层 4–5）归 M2 后续切片。
5. **`analysis/out/*.csv` 与 `saves/*.bin` 是旧产物**（gitignore）。A/B 前必须重新生成——
   本仓库已经有一次结论被旧 CSV 带偏。
6. **D-001 未裁决**：`v0.2` 以上的 tag 语义两套。在此之前只打 `v0.0` / `v0.1`。
7. **分支仍叫 `master`，CI 尚待远端首验**：当前已有 `origin` remote，但双系统工作流实际全绿前，任何「CI 绿」的主张都没有证据基础。
8. **地图生成的跨 OS 结果仍待 CI 验证**：`MapGen.cs` 用整数定点与整数平方根，地图形状为 32×20 矩形，
   `CoreInfo.MapGenVersion=5`（河网/水体）；宿主 #1 上 CLI 与 Godot 4.7.2 smoke 同为 `707c154e0930be52`，
   CI 首验要在 ubuntu + windows 上复核同种子地图哈希。

## 11. 下一项任务（按顺序，不跳队）

1. **完成 CI 首验**：推送后核对 `.github/workflows/ci.yml` 的三 job 在 ubuntu + windows 全部实际通过，
   把首个 Linux 哈希写回 `BALANCE.md` / `CHANGELOG-DESIGN.md`；本地等价复验不能替代这一步。
2. **重写根 [`Readme.md`](Readme.md)**：人类入口，一条命令 + 一张截图 + 指回本文件（需求原文已在 `REQUIREMENTS.md`）。
3. **打 tag `v0.0` / `v0.1`，并把 `master` → `main`**（D-002 的三步；`develop` / `feature/*` 有理由地推迟）。
4. **继续 M2 地图**：形状（32×20）、建图计时、九类地表与温度/降水、河网与水体已在切片一/二/三完成；
   剩余 12 种形胜（§3.4）、门槛资源（§3.5）、图层 4–5（§3.9）在
   [`docs/ROADMAP.md`](docs/ROADMAP.md) §3。**M1 验收不过不许开 M2**（§12 推进规则）。

## 12. 不可违反的架构原则

违反其中任何一条的改动应当**打回**，而不是合并后再修。前四条就是 GDD §0.3 的五条铁律里最容易被顺手破坏的：

1. **聚合模拟**：人口按阶级聚合、物资按 5 类聚合，**不模拟个体**（个体只用于名人）。
2. **三条公式**：新玩法必须能写成「三条公式 + 耦合表的一行」，写不成就不进主线。
3. **因果可读**：任何数值变化都要能展开成「结果 ← 公式 ← 各项数值」——落到代码就是
   每次写入带 reason、`--explain` 能读出因果链。**没有因果文本的数值改动视为未完成**。
4. **同种子同命令流 → 位级相同结果**：确定性优先于便利。不许读时钟、不许随机全局散投、
   不许依赖字典序与遍历顺序、不许用浮点凑近似。分叉就是缺陷。
5. **Core 稳定、插件扩展、数据驱动**：加内容 = 加文件。**Core 里出现一条玩法分支就是越界**；
   数值住在 `data/*.json`，规则住在 `src/Plugins/*`，壳层只装配。
6. **插件之间禁止直接调用**（§10.4）：能用的只有 hook 点 + 世界状态 + 数据包。
7. **SimCore 与引擎无关**（ADR-002）：`src/SimCore` 里除了注释不许出现任何 Godot 类型。
   为了「让模拟跑起来」而往 Core 里塞引擎依赖，是整条架构最难逆转的一次破例。
8. **规则唯一来源是 [`docs/GDD.md`](docs/GDD.md)**：AI 不许自行修改；分歧走 `CHANGELOG-DESIGN.md` 待裁决。
   改规则必附 CHANGELOG 条目 + 回归测试（§11.3）。

---

**遇到本文件与 GDD 或 `docs/` 里任何一份文档冲突时**：以 GDD 为准，把冲突写进
`docs/CHANGELOG-DESIGN.md` §1 待裁决，然后停在那里问人——不要靠猜继续做。

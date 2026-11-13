# 开发宿主与快速启动

本项目可在三台宿主间同步开发。仓库只保存项目所需的工具版本与验证方法，不把某台机器的安装路径写进项目配置。每次开始工作，先在仓库根目录识别当前宿主，再运行对应命令；未知信息以实际探测结果为准。

## 项目工具基准

| 工具 | 项目要求 | 说明 |
|---|---|---|
| .NET SDK | 8.x（建议安装最新 8.0 SDK） | 项目目标为 `net8.0`，语言版本 C# 12；安装较新的 SDK 不等于改变目标运行时。 |
| Godot | **4.7.2 .NET** | Godot C# 项目使用 `Godot.NET.Sdk/4.7.2`；标准版/非 .NET 版不能构建或运行 C#。 |
| OS | Windows 或 macOS | 不同系统上的 Godot 与 .NET 必须架构匹配；地图结果应保持跨平台一致。 |

Godot 版本基准以 `src/GodotShell/HexaCiv.GodotShell.csproj` 中的 SDK 版本为准。不要为了迁就某台机器而改项目 SDK 或目标框架；若版本不符，应安装匹配的 Godot .NET 4.7.2。

## 三台宿主清单

| 宿主 | 已知信息 | 开工判断 |
|---|---|---|
| #1 原 Windows 宿主 | 2026-11-13 实测：.NET SDK `10.0.201`（`C:\Program Files\dotnet`，共享运行时含 8.0.25）；**Godot .NET 4.7.2 已装**：`E:\devenv\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`（`4.7.2.stable.mono.official.ed1daf0bf`）。 | `dotnet build/test/check/hash/replay` 与 Godot `-- --smoke` 均已通过；smoke 输出 `SMOKE PASS`、拾取 1920/1920、末态哈希 `e0909883c6019fba`（与 CLI 同值）。 |
| #2 现 Windows 宿主 | **已验证** Godot .NET 4.7.2：`D:\Applications\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`；.NET SDK 8.0.428：`C:\Program Files\dotnet\dotnet.exe`。2026-11-13 构建、测试与 Godot smoke 通过。 | 如果 `dotnet` 或 `godot` 不在 PATH，可在当前终端临时使用上述完整路径；不要把绝对路径写入提交的脚本或项目文件。 |
| #3 Mac 宿主 | 尚未探测；macOS 版本、CPU 架构、.NET SDK、Godot 安装路径/版本均待确认。 | 首次使用按 macOS 探测步骤记录结果；使用与本机 CPU 匹配的 Godot .NET 4.7.2 和 .NET SDK。 |

若机器角色与表格不符，以 OS、CPU 架构和工具版本的实测结果识别，不要仅凭宿主编号推断配置。补齐宿主 #1 或 #3 信息时，只更新本表的实测项和探测日期。

## 开工探测

先检查仓库状态，确认同步来的提交和本机未提交修改：

```sh
git status -sb
git branch -vv
git remote -v
```

不要用 `reset --hard`、清理命令或切换覆盖本机未提交的改动。随后按当前 OS 探测工具：

### Windows PowerShell

```powershell
$PSVersionTable.PSVersion
Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object Source
Get-Command godot -ErrorAction SilentlyContinue | Select-Object Source
dotnet --info
godot --version
```

如果 `Get-Command` 找不到程序，在资源管理器/开始菜单确认安装位置后，以完整路径运行 `dotnet.exe --info`、`Godot*.exe --version`。不要猜测路径。

### macOS Terminal

```sh
sw_vers
uname -m
command -v dotnet || true
command -v godot || true
dotnet --info
godot --version
```

`uname -m` 用于确认 `arm64` 或 `x86_64`。如果可执行程序不在 PATH，检查实际安装目录并用完整路径探测。Apple Silicon 上优先使用原生 arm64 版本；不要混用不同架构的 Godot 与 .NET。

### 如何判定

1. 仓库根目录应包含 `HexaCiv.sln` 和 `src/GodotShell/HexaCiv.GodotShell.csproj`。
2. `dotnet --info` 应显示可用的 .NET 8 SDK；`dotnet --list-sdks` 可进一步核对。
3. `godot --version` 应为 4.7.2，且安装包是 .NET 版。可检查 Godot 可执行文件/安装包名称，并确认该安装含 C#/.NET 支持；只看到版本号不足以区分标准版与 .NET 版。
4. 打开 `src/GodotShell/HexaCiv.GodotShell.csproj`，确认 SDK 为 `Godot.NET.Sdk/4.7.2`。版本不匹配先处理工具链，不要改项目文件来绕过。
5. 若系统刚安装工具但当前终端/Codex 找不到，重开终端或刷新 PATH，再继续。

## 快速启动与复核

从仓库根目录执行。首次构建后，先跑 CLI 演示：

```sh
dotnet run --project src/HexaCiv.Cli -- run --seed 7 --turns 12 --steps --explain
```

基础复核命令如下：

```sh
dotnet build -v q --nologo
dotnet test --nologo
dotnet run --project src/HexaCiv.Cli -- check
dotnet run --project src/HexaCiv.Cli -- hash --seed 7 --turns 6 --script scripts/godot-smoke.txt
dotnet build src/GodotShell/HexaCiv.GodotShell.csproj
godot --headless --path src/GodotShell -- --smoke
```

当前基线（2026-11-13，MapGenVersion=3 / 32×20）：63 项测试通过；CLI 与 Godot .NET 4.7.2 smoke
在宿主 #1 同为末态哈希 `e0909883c6019fba`（seed 7 / 6 回合 / `scripts/godot-smoke.txt`），
Godot 侧输出 `SMOKE PASS`、拾取 `1920/1920`（640 格 × 3 点）。Godot smoke 必须在输出中出现 `SMOKE PASS`，
单看退出码不算通过。首次在 Mac 验收时，记录 OS/CPU 架构、SDK 版本、Godot 版本和实际输出；
若哈希不同，先确认代码提交一致、插件 DLL 已由构建更新，再定位跨平台问题，不要直接改基线。

Godot 若未加入 PATH，可把最后一条命令中的 `godot` 替换为已探测到的完整可执行文件路径。路径仅供本机命令使用，不提交机器专属路径。

## 三台机器间同步

- 切换开发机前先检查 `git status -sb`，把需要保留的工作提交或另行安全保存；同步前检查分支、remote 和上游关系。
- 只同步 Git 管理的源代码与文档。`bin/`、`obj/`、`.godot/`、`build/plugins/`、`analysis/out/` 和 `saves/*.bin` 都是本机生成物或临时产物，不作为跨机状态依据；换机后按需重新构建/生成。
- 拉取或推送前核对目标分支与远端。远端当前配置可能随仓库状态变化，不要依据旧记录盲目推送；未经用户明确要求，不代替用户发布或推送。
- 不要在同步冲突时覆盖本机未提交内容。先检查 diff，逐项解决后再继续验证。

各项正式验收命令及架构要求仍以 [`../AGENTS.md`](../AGENTS.md)、[`AI_HANDOFF.md`](AI_HANDOFF.md) 和 [`TEST_PLAN.md`](TEST_PLAN.md) 为准。

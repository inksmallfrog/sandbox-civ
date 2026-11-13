using System.Diagnostics;

namespace SimCore;

/// <summary>一局模拟的配置。注意这里没有玩法参数——玩法全在 data/ 与插件里（§0.3 铁律 5）。</summary>
public sealed class SimConfig
{
    public ulong Seed { get; init; } = 1;
    /// <summary>数据包目录（相对仓库根或绝对路径）。</summary>
    public string DataDir { get; init; } = "data";
    /// <summary>插件目录，每个子目录一份 manifest.json。</summary>
    public string PluginDir { get; init; } = "build/plugins";
    public SyncMode Mode { get; init; } = SyncMode.Solo;
    public int YearBase { get; init; } = 1;
    /// <summary>真：不变量违反直接抛（默认，开发期）。假：只记不抛，用于抢救坏档。</summary>
    public bool FatalInvariants { get; init; } = true;
    /// <summary>年数上限（§1.4），到点自动终局结算。</summary>
    public int YearLimit { get; init; } = 200;
    /// <summary>数据校验是否严格（假 = 只记警告）。</summary>
    public bool StrictData { get; init; } = true;
    /// <summary>关掉所有插件（M0 验收「空世界跑 N 回合不崩」用）。</summary>
    public bool NoPlugins { get; init; } = false;

    public SimConfig Clone(Action<SimConfigBuilder>? mutate = null)
    {
        var b = new SimConfigBuilder
        {
            Seed = Seed, DataDir = DataDir, PluginDir = PluginDir, Mode = Mode, YearBase = YearBase,
            FatalInvariants = FatalInvariants, YearLimit = YearLimit, StrictData = StrictData, NoPlugins = NoPlugins
        };
        mutate?.Invoke(b);
        return b.Build();
    }
}

/// <summary>配置构建器：C# 的 init-only 记录在链式改写时很啰嗦，用一个可变 builder 收口。</summary>
public sealed class SimConfigBuilder
{
    public ulong Seed { get; set; } = 1;
    public string DataDir { get; set; } = "data";
    public string PluginDir { get; set; } = "build/plugins";
    public SyncMode Mode { get; set; } = SyncMode.Solo;
    public int YearBase { get; set; } = 1;
    public bool FatalInvariants { get; set; } = true;
    public int YearLimit { get; set; } = 200;
    public bool StrictData { get; set; } = true;
    public bool NoPlugins { get; set; } = false;

    public SimConfig Build() => new()
    {
        Seed = Seed, DataDir = DataDir, PluginDir = PluginDir, Mode = Mode, YearBase = YearBase,
        FatalInvariants = FatalInvariants, YearLimit = YearLimit, StrictData = StrictData, NoPlugins = NoPlugins
    };
}

/// <summary>一个结算步骤的运行时记录（无头性能剖面与年度报告都要它）。</summary>
public sealed record StepRecord(int Index, string Id, string Title, int DiffCount, long ElapsedMicros)
{
    public string Line => $"{Index:D2} {Title}：{DiffCount} 笔变更，{ElapsedMicros / 1000.0:0.###} ms";
}

/// <summary>
/// 一回合的产出（GDD §1.2「看懂因果」的载体）。
/// 壳层只做渲染：年度报告、分步耗时、不变量、哈希、可展开的因果链。
/// </summary>
public sealed class TurnReport
{
    public int Turn { get; init; }
    public int Year { get; init; }
    public IReadOnlyList<CommandResult> Commands { get; init; } = Array.Empty<CommandResult>();
    public IReadOnlyList<StepRecord> Steps { get; init; } = Array.Empty<StepRecord>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InvariantViolations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Diff> Diffs { get; init; } = Array.Empty<Diff>();
    public IReadOnlyList<Explanation> Explanations { get; init; } = Array.Empty<Explanation>();
    public StateHash Hash { get; init; } = new();
    /// <summary>回合末的世界摘要（插件写的 <c>report.*</c> 字段），供 UI 首页直接取用。</summary>
    public IReadOnlyDictionary<string, string> Headline { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    public long TotalMicros { get; init; }

    /// <summary>年度报告正文：结果 → 因为 → 代价。§1.3 核心循环的起点。</summary>
    public string AnnualReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ 第 {Turn} 回合（公元 {Year} 年）年报 ═══");
        if (Headline.Count > 0)
        {
            sb.AppendLine("— 国势 —");
            foreach (var kv in Headline) sb.AppendLine($"  {kv.Key["report.".Length..]}：{kv.Value}");
        }
        if (Commands.Count > 0)
        {
            sb.AppendLine("— 政令执行 —");
            foreach (var c in Commands)
            {
                string rate = c.Accepted ? $"执行率 {c.ExecutionRate.Text}" : "驳回";
                sb.AppendLine($"  {c.CommandText}　→ {rate}：{c.Reason}");
                foreach (var t in c.ExplainTerms) sb.AppendLine($"      · {t}");
            }
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine("— 记事 —");
            foreach (var n in Notes) sb.AppendLine("  " + n);
        }
        if (Explanations.Count > 0)
        {
            sb.AppendLine("— 算式 —");
            foreach (var e in Explanations.Take(14))
            {
                sb.AppendLine($"  {e.Subject} {e.Title} = {e.Result}");
                sb.AppendLine($"    公式：{e.Formula}");
                foreach (var t in e.Terms) sb.AppendLine($"      各项：{t}");
            }
        }
        if (InvariantViolations.Count > 0)
        {
            sb.AppendLine("— 不变量违反 —");
            foreach (var v in InvariantViolations) sb.AppendLine("  " + v);
        }
        sb.AppendLine($"— 结算 {TotalMicros / 1000.0:0.###} ms · 状态哈希 {Hash.Total}（{Hash.FieldCount} 字段）—");
        return sb.ToString();
    }

    /// <summary>CSV 一行（§10.8「sim run --out report.csv」）。</summary>
    public string CsvRow(IReadOnlyList<string> columns, World world)
    {
        var vals = new List<string>(columns.Count);
        foreach (string col in columns)
        {
            if (col == "turn") { vals.Add(Turn.ToString()); continue; }
            if (col == "year") { vals.Add(Year.ToString()); continue; }
            if (col == "hash") { vals.Add(Hash.Total); continue; }
            if (col == "ms") { vals.Add((TotalMicros / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); continue; }
            if (col == "commands") { vals.Add(Commands.Count.ToString()); continue; }
            if (col == "diffs") { vals.Add(Diffs.Count.ToString()); continue; }

            // 形如 town:stock.food 的列 = 对该 kind 的所有实体求和（物资/人口总量的横向对比）
            int sep = col.IndexOf(':');
            string kind = sep < 0 ? World.GlobalKind : col[..sep];
            string path = sep < 0 ? col : col[(sep + 1)..];
            long acc = 0;
            foreach (var ent in world.OfKind(kind))
            {
                var v = ent.Get(path);
                if (!v.IsNull) acc += v.Kind == ValueKind.Fix ? v.Number : v.Number * Fix.Scale;
            }
            vals.Add((acc / (double)Fix.Scale).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }
        return string.Join(',', vals);
    }
}

/// <summary>
/// 一局模拟（GDD §10.2 的九项职责在这里装配起来）。
///
/// 生命周期：<c>new Simulation(cfg) → Bootstrap() → [Submit(...)·, AdvanceTurn()]* → Save()/Restore()</c>
/// 壳层（Godot / CLI）只调这些方法；任何规则都不在壳层里（§10.1 纪律）。
/// </summary>
public sealed partial class Simulation
{
    public SimConfig Config { get; }
    public World World { get; private set; } = new();
    public SimTime Time { get; private set; }
    public RngFactory Rng { get; private set; }
    public DataRegistry Data { get; private set; }
    public CommandBus Commands { get; } = new();
    public HookRegistry Hooks { get; } = new();
    public InvariantGuard Invariants { get; } = new();
    public PluginHost Plugins { get; } = new();
    public ITransport Transport { get; }
    public TurnJournal CurrentJournal { get; private set; } = new();

    internal readonly Dictionary<string, CommandExecutor> Executors = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> CommandDocs = new(StringComparer.Ordinal);
    internal readonly List<(string Plugin, int Order, BootstrapInitializer Init)> Bootstraps = new();
    internal readonly List<LayerInfo> LayerList = new();
    internal readonly List<string> StartupLogLines = new();
    internal string? LoadingPluginId;
    internal readonly List<string> DataWarningLines = new();

    private bool _bootstrapped;
    private int _intentCounter;

    public IReadOnlyList<string> StartupLog => StartupLogLines;
    public IReadOnlyList<string> DataWarnings => DataWarningLines;
    public IReadOnlyDictionary<string, string> CommandCatalog => CommandDocs;
    public IReadOnlyList<LayerInfo> Layers => LayerList.OrderBy(l => l.Order).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();

    public Simulation(SimConfig config)
    {
        Config = config;
        Time = new SimTime { YearBase = config.YearBase };
        Rng = new RngFactory(config.Seed);
        Data = new DataRegistry();
        Transport = new LoopbackTransport(config.Mode);
    }

    // ================= 启动 =================

    /// <summary>
    /// 开局。顺序固定：数据 → 内核实体 → 插件 → 内核不变量 → bootstrap 初始化器。
    /// <paramref name="loadPlugins"/> 为假时连插件都不读，用来证明「Core 无玩法也能启动」（§0.2 判定 5）。
    /// </summary>
    public Simulation Bootstrap(bool loadData = true, bool loadPlugins = true)
    {
        if (_bootstrapped) throw new SimStateException("本 Simulation 已 Bootstrap 过；请新建实例或走 Restore");

        if (loadData)
        {
            string dataDir = ResolveDir(Config.DataDir);
            if (Directory.Exists(dataDir))
            {
                Data = DataRegistry.Load(dataDir, Config.StrictData);
                _bootstrappedDataDir = dataDir;
                StartupLogLines.Add($"数据：{Data.Count} 条 / {Data.LoadedPacks.Count} 包 / dataHash {Data.DataHash}");
            }
            else StartupLogLines.Add($"数据目录不存在（{dataDir}）：按空数据启动，Core 仍可运行");
        }
        else StartupLogLines.Add("跳过数据加载");

        Rng = new RngFactory(Config.Seed);
        World.Create(World.GlobalKind, World.GlobalId);
        World.WriteRaw(World.GlobalId, "meta.seed", SimValue.OfStr(Config.Seed.ToString()));
        World.WriteRaw(World.GlobalId, "meta.schema", SimValue.OfInt(CoreInfo.SchemaVersion));
        World.WriteRaw(World.GlobalId, "meta.gameVersion", SimValue.OfStr(CoreInfo.GameVersion));
        World.WriteRaw(World.GlobalId, "meta.mapGenVersion", SimValue.OfInt(CoreInfo.MapGenVersion));
        World.WriteRaw(World.GlobalId, "meta.turn", SimValue.OfInt(0));
        World.WriteRaw(World.GlobalId, "meta.yearBase", SimValue.OfInt(Config.YearBase));

        bool tryPlugins = loadPlugins && !Config.NoPlugins;
        if (tryPlugins)
        {
            string pluginDir = ResolveDir(Config.PluginDir);
            if (Directory.Exists(pluginDir) && Directory.EnumerateDirectories(pluginDir).Any())
            {
                Plugins.LoadFromDirectory(pluginDir, this);
            }
            else
            {
                // build/plugins 未生成时（只跑了 dotnet test）退回到「已加载的 core.* 程序集」发现
                var builtin = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name is { } n && n.StartsWith("core.", StringComparison.Ordinal))
                    .ToList();
                if (builtin.Count > 0) Plugins.LoadFromAssemblies(builtin, this);
                else StartupLogLines.Add($"插件目录不存在（{pluginDir}）：无插件启动，Core 独立运行");
            }
            if (Plugins.Loaded.Count > 0)
                StartupLogLines.Add("插件：" + string.Join(", ", Plugins.Ids()));
        }
        else StartupLogLines.Add(Config.NoPlugins ? "已禁用全部插件：Core 独立运行" : "跳过插件加载");

        Invariants.RegisterCoreChecks();
        StartupLogLines.Add($"hook：{Hooks.RegisteredPoints.Count} 个点被使用，主题分派 {Hooks.RegisteredSubjectLines.Count} 条");

        // 开局初始化按插件加载顺序执行，输入只有种子与数据
        foreach (var (plugin, _, init) in Bootstraps.OrderBy(b => IndexOfPlugin(b.Plugin)).ThenBy(b => b.Order))
        {
            var ctx = new StepContext(this, step: 0, allowRegister: false);
            init(ctx);
            StartupLogLines.Add($"开局初始化：{plugin}");
        }

        // 插件声明的 id 前缀与数据 id 冲突检测（§10.5「数据 id 无重复」）
        _bootstrapped = true;
        return this;
    }

    private string? _bootstrappedDataDir;
    private int IndexOfPlugin(string id)
    {
        var ids = Plugins.Ids();
        for (int i = 0; i < ids.Count; i++)
            if (string.Equals(ids[i], id, StringComparison.Ordinal)) return i;
        return 999;
    }

    /// <summary>存档捕获用的 RNG 内部状态（全局流 + 已开动的命名空间流）。</summary>
    internal RngState RngCapture() => Rng.CaptureState();

    /// <summary>存档捕获用的实体自增计数，缺了它重开后会重复分配 id。</summary>
    internal IReadOnlyDictionary<string, int> WorldKindCounters() => World.KindCountersSnapshot();

    /// <summary>相对路径按「程序目录向上找仓库根 → 当前目录」依次尝试，CLI 与编辑器两种工作目录都能跑。</summary>
    public static string ResolveDir(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;
        var tried = new List<string>();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 9 && dir is not null; i++, dir = dir.Parent)
        {
            tried.Add(Path.Combine(dir.FullName, configured));
            tried.Add(Path.Combine(dir.FullName, "bin", configured));
        }
        tried.Add(Path.Combine(Directory.GetCurrentDirectory(), configured));
        foreach (var c in tried)
            if (Directory.Exists(c)) return Path.GetFullPath(c);
        return Path.GetFullPath(configured, Directory.GetCurrentDirectory());
    }

    // ================= 命令 =================

    /// <summary>提交一条命令（意图段）。走传输层，与联机同一条路（§9.2）。</summary>
    public Command Submit(string issuer, string type, string reason = "", params (string Key, SimValue Value)[] args)
    {
        var cmd = Command.Build(Time.Turn, ++_intentCounter, issuer, type, reason, args);
        Transport.Submit(cmd);
        return cmd;
    }

    public Command SubmitTarget(string issuer, string type, string reason = "", params (string Key, string Value)[] args) =>
        Submit(issuer, type, reason, args.Select(a => (a.Key, SimValue.OfStr(a.Value))).ToArray());

    // ================= 回合 =================

    /// <summary>
    /// 跑一整年：意图段收命令 → §2.3 的 15 步 → 哈希 → 年报。
    /// 顺序写死在这里，插件只能通过 hook 参与（§2.3「不能插队」）。
    /// </summary>
    public TurnReport AdvanceTurn()
    {
        if (!_bootstrapped) throw new SimStateException("还没 Bootstrap：先决定要不要带数据与插件");

        CurrentJournal = new TurnJournal();
        Time.Turn++;
        Time.Season = Season.Intent;
        Commands.Turn = Time.Turn;
        World.WriteRaw(World.GlobalId, "meta.turn", SimValue.OfInt(Time.Turn));
        // 上一回合的 report.* 不清掉会一直挂在年报上；这里显式重置
        foreach (var stale in World.Get(World.GlobalId)!.FieldPaths("report.").ToList())
            World.WriteRaw(World.GlobalId, stale, SimValue.Null);

        var stepRecords = new List<StepRecord>(SimOrder.Steps.Count);
        var commandResults = new List<CommandResult>();
        long simStart = Stopwatch.GetTimestamp();

        // 传输层 → 总线：单机也走这一步，保证与联机同路径（§10.2 职责 9）
        while (Transport.TryDequeueReceived(out var incoming)) Commands.Submit(incoming);
        IReadOnlyList<Command> ordered = Commands.DrainPending();

        for (int i = 0; i < SimOrder.Steps.Count; i++)
        {
            var step = SimOrder.Steps[i];
            Time.Season = step.Season;
            var ctx = new StepContext(this, step.Index, allowRegister: false);
            long t0 = Stopwatch.GetTimestamp();
            int before = ctx.Journal.Diffs.Count;

            Hooks.Dispatch(step.Pre, StepArgs(step, SimOrder.SlotPre), ctx);

            if (step.Index == 1)
                foreach (var cmd in ordered)
                    commandResults.Add(ExecuteCommand(cmd, ctx));

            Hooks.Dispatch(step.Post, StepArgs(step, SimOrder.SlotPost), ctx);

            long us = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
            var violations = Invariants.RunAfter(step.Index, ctx, Config.FatalInvariants);
            stepRecords.Add(new StepRecord(step.Index, step.Id, step.Title, ctx.Journal.Diffs.Count - before, us));
            if (violations.Count > 0 && !Config.FatalInvariants)
                CurrentJournal.AddNote("不变量（非致命）：" + string.Join("; ", violations));
        }

        Time.Season = Season.Winter;
        // OnTurnEnd 只由第 15 步的 post 广播一次。这里不再补发——
        // 旧实现在结算循环后以 step=0 又广播一遍，任何挂在 OnTurnEnd 的处理器都会一年跑两次
        // （REVIEW-M0-M4 §7）。收尾逻辑要么幂等、要么挂到步骤表里，不能靠「多播一次」兜底。

        var hash = StateHash.Compute(World, Commands, Rng);
        Transport.BroadcastHash(Time.Turn, hash);

        var headline = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in World.Get(World.GlobalId)!.Prefix("report.")) headline[kv.Key] = kv.Value.Display;

        long totalUs = (Stopwatch.GetTimestamp() - simStart) * 1_000_000 / Stopwatch.Frequency;
        var report = new TurnReport
        {
            Turn = Time.Turn,
            Year = Time.Year,
            Commands = commandResults,
            Steps = stepRecords,
            Notes = CurrentJournal.Notes,
            InvariantViolations = CurrentJournal.Notes.Where(n => n.Contains("不变量", StringComparison.Ordinal)).ToList(),
            Diffs = CurrentJournal.Diffs,
            Explanations = CurrentJournal.Explanations,
            Hash = hash,
            Headline = headline,
            TotalMicros = totalUs,
        };
        // 命名空间流按回合重置：命名空间必须自带回合号，跨回合复用同名属于违规（§8.3 纪律 1）
        Rng.ResetNamespaceStreams();
        return report;
    }

    /// <summary>
    /// 步骤广播用的参数。
    /// <c>slot</c> 必须是 "pre" / "post"：同一个 hook 点可能同时挂在同一步的首尾
    /// （见 §2.3 第 3 步），处理器不认位置就会跑两遍，所以这个信息不能省。
    /// </summary>
    private HookArgs StepArgs(SimStep step, string slot)
    {
        var a = new HookArgs { Subject = World.GlobalId }
            .With("step", step.Index).With("stepId", step.Id).With("slot", slot);
        if (!string.IsNullOrEmpty(step.Layer)) a = a.With("layer", step.Layer);
        return a;
    }

    /// <summary>逐条命令：校验 hook → 执行器。拒绝与打折都留原因（§9.2 主机校验）。</summary>
    private CommandResult ExecuteCommand(Command cmd, IStepContext ctx)
    {
        var vArgs = new HookArgs { Subject = World.GlobalId }
            .With("command", cmd.Type)
            .With("issuer", cmd.Issuer);
        foreach (var a in cmd.Args) vArgs = vArgs.With("arg." + a.Key, a.Value);
        Hooks.Dispatch(HookPoint.OnActionValidate, vArgs, ctx);

        if (vArgs.TryGet("result", out var res) && res.Kind == ValueKind.Str)
        {
            string r = res.AsString();
            if (r.StartsWith("reject:", StringComparison.Ordinal))
                return CommandResult.Reject(cmd, r["reject:".Length..]);
        }
        if (!Executors.TryGetValue(cmd.Type, out var exec))
            return CommandResult.Reject(cmd, $"没有插件注册命令 {cmd.Type}");
        return exec(cmd, ctx);
    }

    /// <summary>取某字段的因果链（UI 展开用，§8.4）。</summary>
    public string CausalChain(EntityId id, string path) => CurrentJournal.RenderChain(World, id, path);

    // ================= 存档 / 回放 =================

    public SaveGame CaptureSave() => SaveStore.Capture(this);

    public void Save(string path, string? note = null)
    {
        Hooks.Dispatch(HookPoint.OnBeforeSave, new HookArgs { Subject = World.GlobalId }, new StepContext(this, 0, allowRegister: false));
        SaveStore.Write(path, CaptureSave(), note);
    }

    public Simulation Restore(string path) => ApplySave(SaveStore.Read(path));

    /// <summary>读档：吃快照，不重放命令（§10.6「replay = snapshot + commands」）。</summary>
    public Simulation ApplySave(SaveGame save)
    {
        var next = new World();
        foreach (var e in save.Entities.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var ent = next.Create(e.Kind, new EntityId(e.Id));
            foreach (var (path, val) in e.Fields)
                next.WriteRaw(ent.Id, path, SimValue.ParseCanonical(val));
        }
        World = next;
        World.RestoreKindCounters(save.KindCounters);

        Rng = new RngFactory(save.WorldSeed);
        Rng.RestoreState(new RngState(save.RngGlobalState,
            save.RngStreams.Select(s => new RngStreamState(s.Ns, s.State)).ToList()));

        Time = new SimTime { YearBase = save.YearBase };
        Time.Turn = save.Turn;
        Time.Season = Season.Winter;
        Commands.Turn = save.Turn;

        var log = new List<Command>();
        foreach (var c in save.Commands)
        {
            var args = new SortedDictionary<string, SimValue>(StringComparer.Ordinal);
            foreach (var (k, v) in c.Args) args[k] = SimValue.ParseCanonical(v);
            log.Add(new Command
            {
                Turn = c.Turn, Seq = c.Seq, Issuer = c.Issuer, Type = c.Type, Reason = c.Reason,
                Args = args, AcceptedTurn = c.Turn
            });
        }
        Commands.Restore(save.Turn, log, Array.Empty<Command>());
        _bootstrapped = true;
        // 读档后 intent 计数续在命令流之后，避免 seq 撞号
        _intentCounter = log.Count == 0 ? 0 : log.Max(c => c.Seq);

        Hooks.Dispatch(HookPoint.OnLoad, new HookArgs { Subject = World.GlobalId }.With("schemaVersion", save.SchemaVersion),
            new StepContext(this, 0, allowRegister: false));

        var recomputed = StateHash.Compute(World, Commands, Rng);
        if (save.SnapshotHash.Length > 0 && recomputed.Total != save.SnapshotHash)
        {
            // 把三样输入并排打出来：字段数（快照漏/多字段）、命令日志哈希（初值或顺序不幂等）、
            // RNG 状态（流没进档、或读档后多取了一个数）。少了这一步，等价性自检失败只能靠猜。
            int savedFields = 0;
            foreach (var e in save.Entities) savedFields += e.Fields.Count;
            throw new SimSaveException(
                $"读档后状态哈希不一致：档内 {save.SnapshotHash} vs 现算 {recomputed.Total}。\n" +
                $"  实体 {save.Entities.Count} / 字段 档内 {savedFields} vs 现算 {recomputed.FieldCount}；" +
                $"命令日志 {Commands.LogHash}；全局 RNG {Rng.Global.State:x16}；回合 {save.Turn}。\n" +
                "  可能原因：迁移器漏字段、插件数据与写档时不同、或档损坏。请对比同名 .bak。");
        }
        return this;
    }

    /// <summary>连续推进 N 回合，取最后一份报告（无头标定与压力测试用）。</summary>
    public TurnReport RunTurns(int turns)
    {
        TurnReport? last = null;
        for (int i = 0; i < turns; i++) last = AdvanceTurn();
        return last ?? new TurnReport { Turn = Time.Turn, Year = Time.Year };
    }

    public StateHash Hash() => StateHash.Compute(World, Commands, Rng);
}

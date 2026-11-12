using System.Globalization;
using System.Text;
using SimCore;

namespace HexaCiv.Cli;

/// <summary>
/// 各个动词的实现。这里唯一的「知识」是怎么把 <see cref="TurnReport"/> 打印成人看的表，
/// 以及怎么把脚本行变成命令——两样都不涉及任何数值规则。
/// </summary>
public static class Runner
{
    // ================= run =================

    public static int Run(Options o)
    {
        var sim = Open(o, out var script);
        var rows = new List<string>();
        string? csv = o.Out;
        if (csv is not null) rows.Add(Csv.Header);

        PrintStartup(sim, o);

        // 接档跑的时候 --turns 是「再跑几回合」，不是「跑到第几回合」：
        // 脚本回合号要从快照的下一回合开始投，否则会把历史回合的命令投进未来。
        int first = sim.Time.Turn + 1;
        for (int turn = first; turn < first + o.Turns; turn++)
        {
            int submitted = Script.SubmitFor(sim, script, turn);
            var r = sim.AdvanceTurn();

            if (!o.Headless && !o.Quiet) PrintTurn(sim, r, o, submitted);
            if (csv is not null) rows.Add(Csv.Row(sim, r));
        }

        if (csv is not null) Csv.Write(csv, rows);
        if (o.Save is { } path)
        {
            sim.Save(path);
            if (!o.Quiet) Console.WriteLine($"存档 → {Path.GetFullPath(path)}（第 {sim.Time.Turn} 回合，哈希 {sim.Hash().Total}）");
        }
        if (o.Headless && !o.Quiet) Console.WriteLine($"OK seed={o.Seed} turns={o.Turns} hash={sim.Hash().Total}");
        return 0;
    }

    // ================= check =================

    public static int Check(Options o)
    {
        string dataDir = Simulation.ResolveDir(o.DataDir);
        Console.WriteLine($"数据包 {dataDir}");
        if (Directory.Exists(dataDir))
        {
            var reg = DataRegistry.Load(dataDir, stopOnFirstError: true);
            Console.WriteLine($"  ✓ {reg.Count} 条记录 / {reg.LoadedPacks.Count} 个包（{string.Join(", ", reg.LoadedPacks)}）");
            Console.WriteLine($"  dataHash = {reg.DataHash}");
            foreach (var w in reg.Warnings) Console.WriteLine($"  ! {w}");
            foreach (var t in reg.Types.OrderBy(x => x, StringComparer.Ordinal))
                Console.WriteLine($"    {t,-10} {reg.OfType(t).Count} 条：{string.Join(", ", reg.OfType(t).Select(e => e.Id))}");
        }
        else Console.WriteLine($"  （目录不存在：Core 按空数据启动）");

        string pluginDir = Simulation.ResolveDir(o.PluginDir);
        Console.WriteLine($"插件 {pluginDir}");
        if (Directory.Exists(pluginDir))
        {
            var host = new PluginHost();
            foreach (var d in host.Plan(pluginDir))
            {
                var m = d.Manifest;
                Console.WriteLine($"  ✓ {m.Id} v{m.Version} api={m.Api} priority={m.LoadPriority} " +
                                  $"deps=[{string.Join(",", m.Dependencies)}] packs=[{string.Join(",", m.DataPacks)}]");
            }
            Console.WriteLine($"  清单与依赖校验通过（未实例化，所以不加载程序集）");
        }
        else Console.WriteLine($"  （目录不存在：跳过插件，等价于 --no-plugins）");

        Console.WriteLine("结算顺序（docs/SIM_ORDER.md 的生成源）");
        foreach (var line in SimOrder.Describe()) Console.WriteLine($"  {line}");
        return 0;
    }

    // ================= hash =================

    /// <summary>
    /// 确定性自检（§8.3 规则 4 / §10.8）：同种子 + 同命令流跑两遍，逐回合比哈希。
    /// 这里能抓住的是「遍历顺序不稳定」「混进了 float」「命名空间忘了带回合号」这一类
    /// 只在第二次运行时才暴露的问题——单跑一遍永远看不出。
    /// </summary>
    public static int HashCheck(Options o)
    {
        var a = RunOnceCollect(o);
        var b = RunOnceCollect(o);
        int bad = -1;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) { bad = i + 1; break; }

        if (bad < 0)
        {
            Console.WriteLine($"✓ 确定性通过：seed={o.Seed} 连续两遍 {o.Turns} 回合逐回合哈希全同，末态 {a[^1]}");
            return 0;
        }
        Console.WriteLine($"✗ 第 {bad} 回合起哈希不同：\n  A {a[bad - 1]}\n  B {b[bad - 1]}");
        return 4;
    }

    private static List<string> RunOnceCollect(Options o)
    {
        var sim = Open(o, out var script);
        var list = new List<string>(o.Turns);
        // 上界要在进循环前定死：sim.Time.Turn 每推进一回合就 +1，
        // 写成 turn <= sim.Time.Turn + o.Turns 会变成追不上去的兔子。
        int first = sim.Time.Turn + 1;
        for (int turn = first; turn < first + o.Turns; turn++)
        {
            Script.SubmitFor(sim, script, turn);
            list.Add(sim.AdvanceTurn().Hash.Total);
        }
        return list;
    }

    // ================= replay =================

    /// <summary>
    /// 存档续跑等价性（§10.6「replay = snapshot + commands」）：
    /// 一口气跑满 N 回合，与「跑到第 K 回合存盘、读档后再跑 N−K 回合」必须得到同一个哈希。
    /// 不相等就说明快照漏了东西（通常是 RNG 流状态或 kind 计数器）。
    ///
    /// 这个动词自带存盘，不需要 <c>--load</c>：要接着别人的档跑请用 <c>run --load</c>。
    /// </summary>
    public static int Replay(Options o)
    {
        if (o.Load is not null)
            throw new CommandLineException("replay 自己会存盘，不需要 --load；接着已有存档跑请用 sim run --load <path>");
        if (o.Turns < 1) throw new CommandLineException("replay 需要 --turns ≥ 1");
        if (o.At > o.Turns) throw new CommandLineException($"--at 不能超过 --turns（{o.At} > {o.Turns}）");

        int split = o.At >= 0 ? o.At : o.Turns / 2;
        string savePath = o.Save ?? "saves/replay.bin";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePath))!);

        var script = Script.Load(Script.PathFor(o));

        // 第一遍：一口气跑满 N 回合，中途在第 split 回合落一次盘并记下当时的哈希。
        // 存盘只读不写状态，所以这一遍既是「一口气」的基线，又顺手产出了第二遍要读的档。
        var straight = NewSim(o);
        string atSplit = straight.Hash().Total;
        if (split == 0) straight.Save(savePath);         // 开局即存：测的是「空档」能不能读回来
        int first = straight.Time.Turn + 1;
        for (int turn = first; turn < first + o.Turns; turn++)
        {
            Script.SubmitFor(straight, script, turn);
            straight.AdvanceTurn();
            if (turn != split) continue;
            atSplit = straight.Hash().Total;
            straight.Save(savePath);
        }
        string straightHash = straight.Hash().Total;

        // 第二遍：读第 split 回合的快照，只补后面的回合。
        // 读档后命令总线是空的：历史命令不回灌，只投「当前回合该做什么」——
        // 快照负责过去，命令负责未来，这就是 §10.6 那行公式的全部含义。
        var resumed = NewSim(o);
        resumed.Restore(savePath);

        Console.WriteLine($"存档 → {Path.GetFullPath(savePath)}（第 {split} 回合）");
        // 先单独比「快照往返无损」，再比「续跑等价」：两种病分开报，省得把
        // Save/Restore 字段不对称误诊成「规则依赖了快照里没有的过去」。
        bool lossless = resumed.Hash().Total == atSplit;
        for (int turn = split + 1; turn <= o.Turns; turn++)
        {
            Script.SubmitFor(resumed, script, turn);
            resumed.AdvanceTurn();
        }
        string viaSave = resumed.Hash().Total;

        Console.WriteLine($"第 {split} 回合快照往返　　：{(lossless ? "✓" : "✗")} {atSplit}");
        Console.WriteLine($"一口气跑满 {o.Turns} 回合　　：{straightHash}");
        Console.WriteLine($"存第 {split} 回合后续跑 {o.Turns - split} 回合：{viaSave}");
        bool same = straightHash == viaSave;
        if (!lossless)
            Console.WriteLine("✗ 读回来的状态和存进去时就不一样：Save/Restore 的字段表不对称，见 docs/SAVE_FORMAT.md");
        Console.WriteLine(same
            ? "✓ 存档续跑等价（§10.6）"
            : "✗ 不等价：快照漏了状态，见 docs/SAVE_FORMAT.md 的字段清单");
        return same && lossless ? 0 : 5;
    }

    // ================= map =================

    public static int PrintMap(Options o)
    {
        var sim = Open(o, out var script);
        sim.RunTurns(0);
        Console.WriteLine($"seed={o.Seed} 地图（§3.1 轴坐标，pointy-top）");
        Console.WriteLine(AsciiMap.Render(sim));
        if (o.Turns > 0)
        {
            Console.WriteLine($"跑 {o.Turns} 回合后的城镇：");
            foreach (var t in sim.World.OfKind("town"))
                Console.WriteLine($"  {t.Id} {t.GetStr("town.name")} 辖地 {t.GetInt("town.tiles", 0)} 格 " +
                                  $"人口 {t.GetInt("town.pop.peasant", 0)} 百 仓 {t.GetFix("town.stock.food", Fix.Zero).Text} 石 " +
                                  $"满意 {t.GetFix("town.sat.peasant", Fix.Zero).Text}");
        }
        return 0;
    }

    // ================= peek =================

    /// <summary>
    /// 看某个实体的字段（<c>sim peek --path town.growth.peasant</c>）。
    /// 「任何一个数都能点开」这条要求（§1.3 / §8.4）首先得开发者自己点得开，
    /// 所以无头壳层也要有一扇门，而不是只能靠 Godot。
    /// </summary>
    public static int Peek(Options o, string target, string? field)
    {
        var sim = Open(o, out var script);
        int first = sim.Time.Turn + 1;
        for (int turn = first; turn < first + o.Turns; turn++)
        {
            Script.SubmitFor(sim, script, turn);
            sim.AdvanceTurn();
        }

        var ids = new List<EntityId>();
        if (target == "*" || target.Length == 0)
        {
            foreach (var k in new[] { "world", "nation", "town", "hex" })
                foreach (var e in sim.World.OfKind(k)) ids.Add(e.Id);
        }
        else ids.Add(new EntityId(target));

        foreach (var id in ids)
        {
            var ent = sim.World.Get(id);
            if (ent is null) { Console.WriteLine($"{id}：不存在"); continue; }
            var paths = field is null
                ? ent.FieldPaths("").OrderBy(x => x, StringComparer.Ordinal).ToList()
                : new List<string> { field };
            foreach (var p in paths)
            {
                var v = ent.Get(p);
                if (v.IsNull && field is null) continue;
                Console.WriteLine($"{id}.{p,-24} = {(v.IsNull ? "—" : v.Display)}");
            }
        }
        return 0;
    }

    // ================= 公共 =================

    private static Simulation Open(Options o, out List<ScriptLine> script)
    {
        script = Script.Load(Script.PathFor(o));
        if (o.Load is null) return NewSim(o);
        var loaded = NewSim(o);
        loaded.Restore(o.Load);
        return loaded;
    }

    /// <summary>开局：装数据包 + 装插件，不碰存档。<c>Bootstrap</c> 的顺序（先数据后插件）由 Core 保证。</summary>
    private static Simulation NewSim(Options o)
    {
        var sim = new Simulation(o.ToConfig());
        sim.Bootstrap(loadData: !o.NoData, loadPlugins: !o.NoPlugins);
        return sim;
    }

    private static void PrintStartup(Simulation sim, Options o)
    {
        if (o.Quiet || o.Headless) return;
        Console.WriteLine($"群邦纪元 HexaCiv v{CoreInfo.GameVersion} · Core API {CoreInfo.ApiVersion} · schema v{CoreInfo.SchemaVersion}");
        foreach (var line in sim.StartupLog) Console.WriteLine($"  {line}");
        foreach (var w in sim.DataWarnings) Console.WriteLine($"  ! {w}");
        Console.WriteLine();
    }

    private static void PrintTurn(Simulation sim, TurnReport r, Options o, int submitted)
    {
        Console.WriteLine(r.AnnualReport());
        if (o.Steps)
        {
            Console.WriteLine("— 分步耗时（§10.8 无头性能剖面）—");
            foreach (var s in r.Steps) Console.WriteLine($"  {s.Line}");
        }
        if (o.Explain)
        {
            Console.WriteLine("— 因果链（§8.4）—");
            foreach (var e in r.Explanations)
            {
                Console.WriteLine($"  [{e.Step:D2}] {e.Subject} {e.Title}");
                Console.WriteLine($"      {e.Formula}");
                foreach (var t in e.Terms) Console.WriteLine($"        · {t}");
                Console.WriteLine($"        ⇒ {e.Result}");
            }
        }
    }
}

/// <summary>CSV 一行一回合：标定的原料（docs/BALANCE.md 要求任何结论都带上 seed 与 dataHash）。</summary>
public static class Csv
{
    public const string Header = "seed,dataHash,turn,year,hash,towns,pop,foodOut,foodEat,foodRot,stock,minSat,unrest,steps,ms,fields";

    public static string Row(Simulation sim, TurnReport r)
    {
        string[] cols =
        [
            sim.Config.Seed.ToString(CultureInfo.InvariantCulture),
            sim.Data.DataHash,
            r.Turn.ToString(CultureInfo.InvariantCulture),
            r.Year.ToString(CultureInfo.InvariantCulture),
            r.Hash.Total,
            Num(sim, "report.towns"),
            Num(sim, "report.pop"),
            Fix(sim, "report.food.out"),
            Fix(sim, "report.food.eat"),
            Fix(sim, "report.food.rot"),
            Fix(sim, "report.food.stock"),
            Fix(sim, "report.minSat"),
            Num(sim, "report.unrest"),
            r.Steps.Count.ToString(CultureInfo.InvariantCulture),
            (r.TotalMicros / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
            r.Hash.FieldCount.ToString(CultureInfo.InvariantCulture),
        ];
        return string.Join(',', cols);
    }

    public static void Write(string path, List<string> rows)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join('\n', rows) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"CSV → {full}（{rows.Count - 1} 回合）");
    }

    private static string Num(Simulation sim, string path)
    {
        var v = sim.World.GetField(World.GlobalId, path);
        return v.IsNull ? "0" : v.AsInt().ToString(CultureInfo.InvariantCulture);
    }

    private static string Fix(Simulation sim, string path)
    {
        var v = sim.World.GetField(World.GlobalId, path);
        return v.IsNull ? "0.00" : v.AsFix().Text;
    }
}

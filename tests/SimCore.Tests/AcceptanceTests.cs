using System.Globalization;
using SimCore;

namespace HexaCiv.Tests;

/// <summary>
/// 里程碑验收（§12）。这些测试的断言对象不是某个数值，而是「这台机器能不能被相信」：
///   M0 —— 空世界跑 1000 回合不崩；
///   M1 —— 同种子同命令流 → 哈希位级相同；非法插件被拒载（见 OrderAndDataTests）；
///   §10.6 —— replay = snapshot + commands：第 K 回合的档 + 后面的命令 == 一口气跑满。
/// 数值对不对属于标定（docs/BALANCE.md），不属于这里；这里只管「可复现」和「不撒谎」。
/// </summary>
public class AcceptanceTests
{
    // ================= M0 =================

    [Fact]
    public void 空世界跑一千回合不崩()
    {
        // §12 M0 验收标准原文。没有数据、没有插件：Core 的九项职责必须自己站住。
        var sim = new Simulation(new SimConfig { Seed = 2026, NoPlugins = true }).Bootstrap(loadData: false);
        for (int turn = 1; turn <= 1000; turn++)
        {
            var r = sim.AdvanceTurn();
            Assert.Equal(turn, r.Turn);
            Assert.Equal(15, r.Steps.Count);          // 十五步一步不少，哪怕全是空跑
        }
        Assert.Equal(1000, sim.Time.Turn);
        Assert.Equal(1000, sim.Time.Year);           // YearBase 默认 1，1 回合 = 1 年（§2.1）
        Assert.NotEmpty(sim.Hash().Total);
        // 空世界里除了 world.0000 不该凭空长出别的实体
        Assert.Equal(1, sim.World.EntityCount);

        // 换一年纪元：时钟要跟着挪（剧本包会用它把开局对齐到公元）
        var alt = new Simulation(new SimConfig { Seed = 2026, YearBase = 1400, NoPlugins = true }).Bootstrap(loadData: false);
        alt.AdvanceTurn();
        Assert.Equal(1400, alt.Time.Year);
    }

    // ================= M1：确定性 =================

    private static Simulation Open(ulong seed) =>
        new Simulation(new SimConfig { Seed = seed }).Bootstrap();

    private static List<string> RunAndCollectHashes(ulong seed)
    {
        var sim = Open(seed);
        var hashes = new List<string>();
        for (int turn = 1; turn <= 24; turn++)
        {
            SubmitDemo(sim, turn);
            hashes.Add(sim.AdvanceTurn().Hash.Total);
        }
        return hashes;
    }

    /// <summary>
    /// 一条最小但完整的命令流：建国 → 建城 → 四块田 → 粮仓 → 分城，之后放任演化 14 年。
    /// 参数一律写 auto：选址规则在 core.basic 里，所以同一段剧本换任何种子都成立
    /// ——这正是 §10.1「壳层不含规则」的副作用：测试也不用跟着种子改。
    /// </summary>
    private static void SubmitDemo(Simulation sim, int turn)
    {
        switch (turn)
        {
            case 1: sim.SubmitTarget("host", "nation.found", "开局", ("name", "魏")); break;
            case 2: sim.SubmitTarget("host", "town.found", "第一座城", ("hex", "auto"), ("nation", "auto")); break;
            case 3:
            case 4:
            case 6:
            case 8:
                sim.SubmitTarget("host", "building.build", "开荒", ("town", "auto"), ("building", "def:building.farm"), ("hex", "auto"));
                break;
            case 7: sim.SubmitTarget("host", "building.build", "起粮仓", ("town", "auto"), ("building", "def:building.granary")); break;
            case 10: sim.SubmitTarget("host", "town.found", "分城", ("hex", "auto"), ("nation", "auto")); break;
        }
    }

    [Fact]
    public void 同种子同命令流_逐回合哈希位级相同()
    {
        // §12 M1 验收标准原文。跑两遍、逐回合比——只在末态比一次会漏掉
        // 「中途岔开又碰巧汇合」，更会漏掉「第一遍侥幸对了」。
        var a = RunAndCollectHashes(99);
        var b = RunAndCollectHashes(99);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void 换种子必须换世界()
    {
        Assert.NotEqual(RunAndCollectHashes(99)[^1], RunAndCollectHashes(100)[^1]);
    }

    [Fact]
    public void 少一条命令就该分叉()
    {
        // 哈希若对命令流不敏感，联机校验与回放就是个摆设。
        var full = RunAndCollectHashes(99);
        var sim = Open(7);
        var partial = new List<string>();
        for (int turn = 1; turn <= 24; turn++)
        {
            switch (turn)
            {
                case 1: sim.SubmitTarget("host", "nation.found", "开局", ("name", "魏")); break;
                case 2: sim.SubmitTarget("host", "town.found", "第一座城", ("hex", "auto"), ("nation", "auto")); break;
                // 故意不建田
            }
            partial.Add(sim.AdvanceTurn().Hash.Total);
        }
        Assert.NotEqual(full[^1], partial[^1]);
    }

    [Fact]
    public void 命令被拒也要留下记录而不是静悄悄()
    {
        var sim = Open(5);
        // §4.1（owner 可为 null）+ §6.2（独立是归属表的一行）：不带 nation 的建城是合法的「独立城镇」，
        // 不是错误——先钉住这条，免得下一个人把「没有国家」当 bug 改成拒收，把独立城镇这条路堵死。
        sim.SubmitTarget("host", "town.found", "无主之城", ("hex", "auto"));
        var r = sim.AdvanceTurn();
        CommandResult solo = Assert.Single(r.Commands, c => c.Accepted);
        var town = Assert.Single(sim.World.OfKind("town"));
        Assert.Equal("independent", town.GetStr(F.TownSovereignty));
        Assert.Equal("", town.GetStr(F.TownOwner));
        Assert.False(string.IsNullOrWhiteSpace(solo.Reason));

        // 指一个不存在的国家：拒
        sim.SubmitTarget("host", "town.found", "投奔", ("hex", "auto"), ("nation", "nation.9999"));
        var r2 = sim.AdvanceTurn();
        CommandResult rej = Assert.Single(r2.Commands, c => !c.Accepted);
        Assert.Contains("国家不存在", rej.Reason, StringComparison.Ordinal);

        // 没注册的命令类型同样要给结论，不能当没听见
        sim.SubmitTarget("host", "warp.drive", "越权", ("to", "nowhere"));
        var r3 = sim.AdvanceTurn();
        Assert.Contains(r3.Commands, c => !c.Accepted && c.Reason.Contains("warp.drive", StringComparison.Ordinal));
    }

    // ================= §10.6 存档 / 回放 =================

    [Fact]
    public void 第K回合的档加后面的命令等于一口气跑满()
    {
        foreach (int split in new[] { 0, 1, 9, 17, 24 })
        {
            using var tmp = new TmpDir("replay");
            string path = Path.Combine(tmp.Path, "s.hcsave");

            // 基线：一口气跑满，顺手在第 split 回合落盘（split==0 是「开局即空档」）
            var straight = Open(31);
            string atSplit = straight.Hash().Total;
            if (split == 0) straight.Save(path);
            for (int turn = 1; turn <= 24; turn++)
            {
                SubmitDemo(straight, turn);
                straight.AdvanceTurn();
                if (turn == split) { atSplit = straight.Hash().Total; straight.Save(path); }
            }
            string fullHash = straight.Hash().Total;

            var resumed = Open(31);
            resumed.Restore(path);
            // 先验「快照往返无损」，再验「续跑等价」：字段表不对称与依赖过去，是两种病
            Assert.Equal(atSplit, resumed.Hash().Total);
            for (int turn = split + 1; turn <= 24; turn++)
            {
                SubmitDemo(resumed, turn);
                resumed.AdvanceTurn();
            }
            Assert.Equal(fullHash, resumed.Hash().Total);
        }
    }

    [Fact]
    public void 读回来的档能改能跑且kind计数器续得上()
    {
        using var tmp = new TmpDir("restore");
        string path = Path.Combine(tmp.Path, "s.hcsave");
        var a = Open(41);
        for (int turn = 1; turn <= 12; turn++) { SubmitDemo(a, turn); a.AdvanceTurn(); }
        a.Save(path);

        var b = Open(41);
        b.Restore(path);
        int townsBefore = b.World.OfKind("town").Count;
        b.SubmitTarget("host", "town.found", "读档后再建一座", ("hex", "auto"), ("nation", "auto"));
        b.AdvanceTurn();
        Assert.Equal(townsBefore + 1, b.World.OfKind("town").Count);
        // 计数器没续上就会撞已有的 hex/town id——那是「读档后世界变小了」的经典症状
        Assert.Equal(townsBefore + 1, b.World.OfKind("town").Select(e => e.Id.Value).Distinct().Count());
    }

    [Fact]
    public void 坏档不得覆盖最后一个有效存档()
    {
        using var tmp = new TmpDir("save-safety");
        string path = Path.Combine(tmp.Path, "s.hcsave");
        var sim = Open(43);
        for (int turn = 1; turn <= 6; turn++) { SubmitDemo(sim, turn); sim.AdvanceTurn(); }

        sim.Save(path);
        string good = File.ReadAllText(path);                       // GZip 二进制，只为拿长度
        Assert.True(good.Length > 0);
        string goodHash = sim.Hash().Total;

        // 再存一次要留下 .bak，且 .bak 读回来还是上一个状态
        for (int turn = 7; turn <= 12; turn++) sim.AdvanceTurn();
        sim.Save(path);
        Assert.True(File.Exists(path + ".bak"), "第二次写档必须把上一份留成 .bak");
        var probe = Open(43);
        probe.Restore(path + ".bak");
        Assert.Equal(goodHash, probe.Hash().Total);

        // 截断的档：给的是「改用 .bak」这句话，而不是半路崩在解析器里
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        var broken = Open(43);
        var e = Assert.ThrowsAny<SimException>(() => broken.Restore(path));
        Assert.Contains(".bak", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回归：<c>SaveStore.Write</c> 曾经用 <c>Encoding.UTF8</c> 写 GZip 负载，
    /// 于是 JSON 开头多了 EF BB BF，刚写出的档自己读不回来（解析器把 BOM 当值的开头）。
    /// </summary>
    [Fact]
    public void 存档负载不带BOM且带BOM的明文档也读得动()
    {
        using var tmp = new TmpDir("bom");
        string path = Path.Combine(tmp.Path, "s.hcsave");
        var sim = Open(47);
        sim.AdvanceTurn();
        sim.Save(path);

        byte[] plain = SaveStoreGz.Decompress(path);
        Assert.False(plain.Length >= 3 && plain[0] == 0xEF && plain[1] == 0xBB && plain[2] == 0xBF,
            "写侧必须用无 BOM 的 UTF-8，否则自检读不回来");
        Assert.Equal((byte)'{', plain[0]);

        // 读侧反过来要容忍：手写的测试夹具、Windows 编辑器另存的明文 JSON 都带 BOM
        string text = System.Text.Encoding.UTF8.GetString(plain);
        string withBom = Path.Combine(tmp.Path, "plain.hcsave");
        File.WriteAllText(withBom, text, new System.Text.UTF8Encoding(true));
        // 按字节验：File.ReadAllText 会自己吞掉 BOM，拿它断言「有 BOM」永远断不成立
        byte[] bomBytes = File.ReadAllBytes(withBom);
        Assert.Equal(0xEF, bomBytes[0]);
        Assert.Equal(0xBB, bomBytes[1]);
        Assert.Equal(0xBF, bomBytes[2]);
        var reader = Open(47);
        reader.Restore(withBom);                       // 不抛异常就是通过
        Assert.Equal(sim.Hash().Total, reader.Hash().Total);
    }

    // ================= 不变量守卫 =================

    [Fact]
    public void 非法状态在结算里被抓住而不是悄悄跑下去()
    {
        var sim = Open(51);
        for (int turn = 1; turn <= 4; turn++) { SubmitDemo(sim, turn); sim.AdvanceTurn(); }
        var town = Assert.Single(sim.World.OfKind("town"));
        // 直接从外部把人口拧成负数：这就是「插件写错 / 迁移器漏字段」的现场
        sim.World.WriteRaw(town.Id, F.TownPop("class.peasant"), SimValue.OfInt(-7));

        var e = Assert.Throws<SimInvariantException>(() => sim.AdvanceTurn());
        Assert.Contains("basic.ranges", e.Message, StringComparison.Ordinal);
        Assert.NotEmpty(e.Violations);
    }

    [Fact]
    public void 宽容模式下同样的坏只记录不抛()
    {
        var sim = new Simulation(new SimConfig { Seed = 51, FatalInvariants = false }).Bootstrap();
        for (int turn = 1; turn <= 4; turn++) { SubmitDemo(sim, turn); sim.AdvanceTurn(); }
        var town = Assert.Single(sim.World.OfKind("town"));
        sim.World.WriteRaw(town.Id, F.TownPop("class.peasant"), SimValue.OfInt(-7));

        var r = sim.AdvanceTurn();                     // 不抛
        Assert.NotEmpty(r.InvariantViolations);        // 但年报里看得见——坏档抢救要的就是这个
    }

    // ================= 解释性（§8.4）=================

    [Fact]
    public void 每一次数值变化都带着理由()
    {
        var sim = Open(53);
        for (int turn = 1; turn <= 6; turn++) { SubmitDemo(sim, turn); sim.AdvanceTurn(); }
        var report = sim.AdvanceTurn();

        Assert.NotEmpty(report.Diffs);
        // 没有 reason 的变更就是「凭什么是这个数」问不出来的那种，§14 把它列为 P0
        Assert.DoesNotContain(report.Diffs, d => string.IsNullOrWhiteSpace(d.Reason));
        Assert.NotEmpty(report.Explanations);
        Assert.All(report.Explanations, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Title));
            Assert.False(string.IsNullOrWhiteSpace(x.Formula));
            Assert.NotEmpty(x.Terms);
            Assert.False(string.IsNullOrWhiteSpace(x.Result));
        });
        // 公式一必须原样出现在产出解释里——这是「贴着附录 A 写代码」的证据
        Assert.Contains(report.Explanations, x =>
            x.Formula.Contains("P × λ × Tech × η(S)", StringComparison.Ordinal)
            && x.Formula.Contains("Cap × Facility", StringComparison.Ordinal));
    }

    [Fact]
    public void 年报里的数字能对上世界状态()
    {
        var sim = Open(57);
        for (int turn = 1; turn <= 14; turn++) { SubmitDemo(sim, turn); sim.AdvanceTurn(); }

        long pop = sim.World.OfKind("town").Sum(t => t.GetInt(F.TownPop("class.peasant"), 0));
        Assert.Equal(pop, sim.World.GetInt(World.GlobalId, F.ReportPop, 0));

        Fix stock = sim.World.OfKind("town")
            .Select(t => t.GetFix(F.TownStock("good.food"), Fix.Zero))
            .Aggregate(Fix.Zero, static (a, b) => a + b);
        Assert.Equal(stock.Raw, sim.World.GetFix(World.GlobalId, F.ReportStock, Fix.Zero).Raw);

        int towns = sim.World.OfKind("town").Count;
        Assert.Equal(towns, (int)sim.World.GetInt(World.GlobalId, F.ReportTowns, 0));
    }
}

/// <summary>GZip 解包只做测试取证用（写侧是否带 BOM）。</summary>
internal static class SaveStoreGz
{
    public static byte[] Decompress(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        using var ms = new MemoryStream(raw);
        using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
    }
}

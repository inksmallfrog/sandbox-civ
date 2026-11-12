using SimCore;

namespace HexaCiv.Tests;

/// <summary>
/// core.basic 的最小循环（§12 M1：地图 → 建城 → 产粮 → 回合）。
///
/// 这一批断言管的是「规则跑起来像不像 §4/§5 写的那样」，不管「数值好不好玩」——
/// 后者属于标定（docs/BALANCE.md）。特别地，<see cref="产出等于公式一的两个上限取小"/>
/// 在测试里把公式一<b>重新算了一遍</b>：实现与文档各写一次、结果必须撞在一起，
/// 这才叫「贴着附录 A 写代码」被机器看着。
/// </summary>
public class CoreBasicTests
{
    private static Simulation Open(ulong seed) =>
        new Simulation(new SimConfig { Seed = seed }).Bootstrap();

    /// <summary>开局一座城（不带 nation = 独立城镇，§7.1），再跑 turns 年（建城发生在第 1 年的意图段）。</summary>
    private static Simulation OneTown(ulong seed, int turns = 1)
    {
        var sim = Open(seed);
        sim.SubmitTarget("host", "town.found", "开城", ("hex", "auto"));
        Assert.Single(sim.AdvanceTurn().Commands, c => c.Accepted);
        for (int t = 1; t < turns; t++) sim.AdvanceTurn();
        Assert.Equal(1, sim.World.OfKind("town").Count);
        return sim;
    }

    private static Entity Town(Simulation sim) => Assert.Single(sim.World.OfKind("town"));

    /// <summary>地块可否建城（§3.2）。World 只有 <c>Get(id)</c>，所以布尔要这样取。</summary>
    private static bool Buildable(Simulation sim, EntityId hex) =>
        sim.World.Get(hex)!.Get(F.HexBuildable).AsBool();

    /// <summary>某城的辖地格（含城址）——与 TownOps.TilesOf 同一条判据，测试侧独立写一遍。</summary>
    private static List<Entity> Tiles(Simulation sim, EntityId town) =>
        sim.World.OfKind("hex").Where(h => F.IdOf(sim.World.GetStr(h.Id, F.HexOwner, "")) == town).ToList();

    // ================= 建城（§6.6 / §3.6 / §4.1）=================

    [Fact]
    public void 建城即有人有仓底有辖地()
    {
        var sim = Open(7);
        sim.SubmitTarget("host", "town.found", "开城", ("hex", "auto"));
        var r = sim.AdvanceTurn();
        var town = Town(sim);

        // §6.6 的落点要读建城那一年的日志：同一回合的第 10/12 步还会动这些字段，现值已经不是初值了
        SimValue Written(string path, string reasonToken) => SimValue.ParseCanonical(
            Assert.Single(r.Diffs, d => d.Path == path && d.Reason.Contains(reasonToken, StringComparison.Ordinal)).After);

        Assert.Equal(TownOps.FoundPop, Written(F.TownPop("class.peasant"), "首批人口").AsInt());
        Assert.Equal(Fix.FromInt(8), Written(F.TownStock("good.food"), "开城仓底").AsFix());
        Assert.Equal(Fix.FromInt(55), Written(F.TownSat("class.peasant"), "满意度初值").AsFix());
        Assert.Equal(Fix.Zero, Written(F.TownGrowth("class.peasant"), "零头累积").AsFix());

        // 一年以后：ΔP = 4 × 0.04 = 0.16，不到 100 人不进位，所以人口还站在首批上
        Assert.Equal(TownOps.FoundPop, town.GetInt(F.TownPop("class.peasant")));

        // §4.1 的治权档位：M1 只写 core / independent
        Assert.Equal("independent", town.GetStr(F.TownSovereignty));
        Assert.Equal("", sim.World.GetStr(town.Id, F.TownOwner, ""));
        Assert.Equal("village", town.GetStr(F.TownTier));

        // §3.6：辖地 = 半径 2 内至多 12 格，城址本身也占 1 格
        var tiles = Tiles(sim, town.Id);
        Assert.InRange(tiles.Count, 2, TownOps.ClaimMaxTiles);
        Assert.Equal(tiles.Count, town.GetInt(F.TownTiles));
        var site = F.IdOf(town.GetStr(F.TownSite));
        Assert.Equal(town.Id.Value, sim.World.GetStr(site, F.HexTown, ""));
        var (sq, sr) = MapGen.Coords(site);
        Assert.All(tiles, t =>
        {
            var (q, r2) = MapGen.Coords(t.Id);
            Assert.True(MapGen.Distance(q - sq, r2 - sr) <= TownOps.ClaimRadius, $"{t.Id} 跳出半径 {TownOps.ClaimRadius} 了");
        });
    }

    [Fact]
    public void 自动选址只会挑可建城的地块()
    {
        var sim = OneTown(9);
        var site = F.IdOf(Town(sim).GetStr(F.TownSite));
        Assert.True(Buildable(sim, site), "§3.2：山地不能当城址");
    }

    [Fact]
    public void 驳回理由要说人话而不是抛异常()
    {
        var sim = OneTown(11);
        var town = Town(sim);
        string site = town.GetStr(F.TownSite);

        // ① 城址格已有城
        sim.SubmitTarget("host", "town.found", "贴脸", ("hex", site));
        var r = sim.AdvanceTurn();
        var rej = Assert.Single(r.Commands, c => !c.Accepted);
        Assert.Contains("已经有城", rej.Reason, StringComparison.Ordinal);

        // ② 辖地格（有主但无城）
        var otherTile = Tiles(sim, town.Id).First(t => t.Id.Value != site);
        sim.SubmitTarget("host", "town.found", "抢地盘", ("hex", otherTile.Id.Value));
        r = sim.AdvanceTurn();
        rej = Assert.Single(r.Commands, c => !c.Accepted);
        Assert.Contains("辖地不重叠", rej.Reason, StringComparison.Ordinal);

        // ③ 不可建城的地表（这局地图上得有山地，没有就换个种子）
        var mountain = sim.World.OfKind("hex").FirstOrDefault(h => !Buildable(sim, h.Id));
        Assert.NotNull(mountain);
        sim.SubmitTarget("host", "town.found", "上山", ("hex", mountain!.Id.Value));
        r = sim.AdvanceTurn();
        rej = Assert.Single(r.Commands, c => !c.Accepted);
        Assert.Contains("不可建城", rej.Reason, StringComparison.Ordinal);

        // 被驳回的命令不会凭空造出城来
        Assert.Equal(1, sim.World.OfKind("town").Count);
    }

    [Fact]
    public void 两城之间至少隔3格且辖地不重叠()
    {
        var sim = Open(13);
        sim.SubmitTarget("host", "nation.found", "开局", ("name", "吴"));
        sim.SubmitTarget("host", "town.found", "一号", ("hex", "auto"), ("nation", "auto"));
        sim.AdvanceTurn();
        sim.SubmitTarget("host", "town.found", "二号", ("hex", "auto"), ("nation", "auto"));
        sim.AdvanceTurn();

        var towns = sim.World.OfKind("town");
        Assert.Equal(2, towns.Count);
        var (aq, ar) = MapGen.Coords(F.IdOf(towns[0].GetStr(F.TownSite)));
        var (bq, br) = MapGen.Coords(F.IdOf(towns[1].GetStr(F.TownSite)));
        // §6.6 的铺城限制在 M1 用空间顶替（见 CHANGELOG-DESIGN），判据写在 TownOps.MinTownSpacing
        Assert.True(MapGen.Distance(aq - bq, ar - br) >= TownOps.MinTownSpacing,
            $"两城城址只隔 {MapGen.Distance(aq - bq, ar - br)} 格，下限是 {TownOps.MinTownSpacing}");

        // 辖地不重叠：一格只能有一个主人，两城的辖地集合不许有交集
        var a = Tiles(sim, towns[0].Id).Select(t => t.Id.Value).ToHashSet(StringComparer.Ordinal);
        var b = Tiles(sim, towns[1].Id).Select(t => t.Id.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(a.Intersect(b));
        Assert.Equal(2, sim.World.GetInt(World.GlobalId, F.ReportTowns, 0));
    }

    // ================= 产出（§4.2 公式一 / §5.2）=================

    [Fact]
    public void 产出等于公式一的两个上限取小()
    {
        var sim = OneTown(17, 4);           // 跑几年，让设施与满意度都不是初值
        var town = Town(sim);

        // 第 3 步读的是「本回合进来时」的人口、满意度、设施——所以要在 AdvanceTurn 之前取快照
        long pop = town.GetInt(F.TownPop("class.peasant"));
        Fix sat = town.GetFix(F.TownSat("class.peasant"), Fix.FromInt(50));
        Fix facility = town.GetFix(F.TownFacility, Fix.One);
        Fix cap = Fix.Zero;
        foreach (var t in Tiles(sim, town.Id)) cap += sim.World.GetFix(t.Id, F.HexYieldFood, Fix.Zero);

        Fix lambda = sim.Data.Get("class.peasant").GetFix("output.food");           // §4.3：2.40
        Fix eta = Fix.FromPercent(60) + Fix.Mul(Fix.FromPercent(40), Fix.Div(sat, Fix.FromInt(100)));
        Fix soft = Fix.Mul(Fix.Mul(Fix.FromInt(pop), lambda), Fix.Mul(Fix.One, eta));
        Fix hard = Fix.Mul(cap, facility);
        Fix expected = Fix.Min(soft, hard);

        var r = sim.AdvanceTurn();
        Fix actual = Town(sim).GetFix(F.TownOutput("good.food"), Fix.Zero);
        Assert.Equal(expected, actual);
        Assert.True(r.Diffs.Any(d => d.Reason.Contains("产出", StringComparison.Ordinal)),
            "产出变更必须带着理由（§8.4）");
        // 紧约束是谁，取决于这局的地图；两个上限都得是正数，否则上面那条断言是在比 0==0
        Assert.True(soft > Fix.Zero && hard > Fix.Zero, $"soft={soft.Text} hard={hard.Text}");
    }

    [Fact]
    public void 没有人口绝不出粮()
    {
        // §5.2 点名的那个经典 bug：「没有人口也自动产出」。Cap × Facility 里没有人，一样不许出粮。
        var sim = OneTown(19, 3);
        var town = Town(sim);
        Assert.True(town.GetFix(F.TownOutput("good.food"), Fix.Zero) > Fix.Zero);

        sim.World.WriteRaw(town.Id, F.TownPop("class.peasant"), SimValue.OfInt(0));
        sim.AdvanceTurn();

        Assert.Equal(Fix.Zero, Town(sim).GetFix(F.TownOutput("good.food"), Fix.Zero));
    }

    [Fact]
    public void 农田折进设施系数且封顶1_45()
    {
        var sim = OneTown(23, 1);
        int levels = 0;
        Fix ceiling = Fix.One + Fix.FromPercent(45);

        for (int i = 0; i < 8; i++)
        {
            sim.SubmitTarget("host", "building.build", $"开荒{i + 1}",
                ("town", "auto"), ("building", "def:building.farm"), ("hex", "auto"));
            var r = sim.AdvanceTurn();
            if (r.Commands.Any(c => c.Accepted)) levels++;

            Fix facility = Town(sim).GetFix(F.TownFacility, Fix.One);
            Assert.True(facility <= ceiling, $"§4.2：Facility 上限 1.45，实际 {facility.Text}");
            Assert.True(facility == Fix.One + Fix.Min(Fix.Mul(Fix.FromPercent(15), levels), Fix.FromPercent(45)),
                $"第 2 步该把 {levels} 级田折成 Facility，实际 {facility.Text}");
        }

        Assert.True(levels >= 3, $"8 次营造至少该落地 3 级田（钱不够会被驳回，实际 {levels} 级）");
        Assert.Equal(ceiling, Town(sim).GetFix(F.TownFacility, Fix.One));   // 封顶生效
    }

    [Fact]
    public void 粮仓同时加仓容与降损耗()
    {
        var sim = OneTown(29);
        // 仓容按第 2 步「进来时」的人口算，所以人口要在推进之前取
        long pop = Town(sim).GetInt(F.TownPop("class.peasant"));
        sim.SubmitTarget("host", "building.build", "起仓", ("town", "auto"), ("building", "def:building.granary"));
        sim.AdvanceTurn();
        var town = Town(sim);

        Fix grain = sim.Data.Get("good.food").GetFix("stockCapPerPop");
        Assert.Equal(Fix.Mul(grain, Fix.FromInt(pop)) + Fix.FromInt(20),
            town.GetFix(F.TownStockCap("good.food"), Fix.Zero));             // §5.6：人口×2 + 粮仓×20
        Assert.Equal(Fix.FromPercent(5) - Fix.FromPercent(2),
            town.GetFix(F.TownSpoilage("good.food"), Fix.Zero));             // §5.7：5% − 每级 2%
    }

    [Fact]
    public void 存粮永远不超过仓容()
    {
        var sim = OneTown(31);
        for (int turn = 1; turn <= 12; turn++)
        {
            sim.AdvanceTurn();
            foreach (var town in sim.World.OfKind("town"))
            {
                Fix stock = sim.World.GetFix(town.Id, F.TownStock("good.food"), Fix.Zero);
                Fix cap = sim.World.GetFix(town.Id, F.TownStockCap("good.food"), Fix.MaxValue);
                Assert.True(stock <= cap, $"{town.Id} 存粮 {stock.Text} 越过仓容 {cap.Text}（§5.6 步 6：仓满即抛）");
                Assert.True(stock >= Fix.Zero, $"{town.Id} 存粮为负：{stock.Text}");
            }
        }
    }

    // ================= 人口（附录 A §8 / §4.2）=================

    [Fact]
    public void 丰年人口靠零头攒着往上涨()
    {
        var sim = OneTown(37);
        long start = Town(sim).GetInt(F.TownPop("class.peasant"));
        long prev = start;
        for (int turn = 1; turn <= 18; turn++)
        {
            sim.AdvanceTurn();
            var town = Town(sim);
            Assert.Equal(Fix.One, town.GetFix(F.TownRation("class.peasant"), Fix.Zero));   // 口粮年年够
            long pop = town.GetInt(F.TownPop("class.peasant"));
            // ΔP = P × 0.04，不到 100 人不进位 ⇒ 人口逐年「不动或 +n」，绝不许倒退
            Assert.True(pop >= prev, $"第 {turn} 年人口从 {prev} 掉到 {pop}，丰年不该减员");
            prev = pop;
            Fix carry = town.GetFix(F.TownGrowth("class.peasant"), Fix.Zero);
            Assert.InRange(carry.Raw, 0, Fix.Scale - 1);                                    // 零头桶必须留在 [0,1)
        }
        Assert.True(prev > start, $"18 年丰年人口没长（{start} → {prev}）");
    }

    /// <summary>
    /// 「饿而不死」那一档的定点回归。附录 A §8 的 0.04·粮安 在 s = 0.60 时只有 0.008，
    /// 先物化增长率再乘 P 会被 ×100 刻度截成 0，人口从此永久冻死在地块上限上
    /// （seed 42 的 200 年基线在第 83 年卡在 72 个百人单位，就是这个坑）。
    /// 判据用「第 13 步有没有往零头桶里写东西」，而不是只看年末人口——不进位的年份人口本来就不动。
    /// </summary>
    [Fact]
    public void 缺粮但不至于饿死时零头桶照样在涨()
    {
        var sim = OneTown(37);
        for (int turn = 1; turn <= 40; turn++) sim.AdvanceTurn();
        var town = Town(sim);
        long pop = town.GetInt(F.TownPop("class.peasant"));
        Assert.True(pop >= 12, $"40 年丰年只长到 {pop} 个百人单位，靶心没对上（要的是 P × 0.04·粮安 < 0.01 的量级）");

        // 辖地压成「只够六成口粮」，仓底清空 ⇒ 实得 = 本年产出 = 0.60 × 需求
        Fix want = Fix.Mul(sim.Data.Get("class.peasant").GetFix("consumes.food"), pop);
        Fix supply = Fix.FromRaw(want.Raw * 6 / 10);
        var tiles = Tiles(sim, town.Id);
        for (int i = 0; i < tiles.Count; i++)
            sim.World.WriteRaw(tiles[i].Id, F.HexYieldFood, SimValue.Of(i == 0 ? supply : Fix.Zero));
        sim.World.WriteRaw(town.Id, F.TownStock("good.food"), SimValue.Of(Fix.Zero));

        var r = sim.AdvanceTurn();
        Fix ration = town.GetFix(F.TownRation("class.peasant"), Fix.Zero);
        Assert.Equal(Fix.FromPercent(60), ration);                       // s = 0.60 ⇒ 粮安 = 0.20 > 0，该长人

        var bucket = r.Diffs.FirstOrDefault(d => d.Id == town.Id && d.Path == F.TownGrowth("class.peasant"));
        Assert.True(bucket != default,
            $"s = {ration.Text}（粮安 0.20）时第 13 步一笔变更都没写：0.04·粮安 = 0.008 被 1/100 刻度截成 0，" +
            "人口会永久冻死（附录 A §8 要的是 ΔP = P × 粮安 × 4 ÷ 100，先乘 P）");
        Fix grew = SimValue.ParseCanonical(bucket.After).AsFix() - SimValue.ParseCanonical(bucket.Before).AsFix();
        Assert.True(grew > Fix.Zero, $"零头桶只加了 {grew.Text}，s = {ration.Text} 时应为 0.04 × 粮安 × P > 0");
    }

    [Fact]
    public void 灾年先断粮再减员最后反抗()
    {
        var sim = OneTown(41);
        long start = Town(sim).GetInt(F.TownPop("class.peasant"));

        // 灾年的等价写法：辖地粮潜力清零。只动数据不动结算代码，所以它同样在 §10.7 的确定性里。
        foreach (var t in Tiles(sim, Town(sim).Id))
            sim.World.WriteRaw(t.Id, F.HexYieldFood, SimValue.Of(Fix.Zero));

        Fix lastRation = Fix.One;
        for (int turn = 1; turn <= 20; turn++)
        {
            sim.AdvanceTurn();
            lastRation = Town(sim).GetFix(F.TownRation("class.peasant"), Fix.Zero);
        }

        var town = Town(sim);
        Assert.Equal(Fix.Zero, lastRation);                                                // 仓见底了
        Assert.True(town.GetInt(F.TownPop("class.peasant")) < start,
            $"断粮 20 年人口还 {town.GetInt(F.TownPop("class.peasant"))}（起 {start}），ΔP 的负号没生效");
        Assert.Equal("反抗", sim.World.GetStr(town.Id, F.TownStatus("class.peasant"), ""));  // §4.2：<30
        Assert.True(town.GetInt(F.TownRiot) >= 1, "§4.2：连续 S<30 要留下年数，满 2 年才升级");
        Assert.Equal(1, sim.World.GetInt(World.GlobalId, F.ReportUnrest, 0));
    }

    [Fact]
    public void 满意度向S星收敛且只吃口粮一项()
    {
        var sim = OneTown(43);
        Fix e = sim.Data.Get("class.peasant").GetFix("satisfaction.expects");
        // 丰年 s=1，M1 只有 60(s−e) 一项参与，其余四项按 0 ⇒ S* = 50 + 60×(1−0.85) = 59
        Fix sStar = (Fix.FromInt(50) + Fix.Mul(Fix.FromInt(60), Fix.One - e)).Clamp(Fix.Zero, Fix.FromInt(100));

        var samples = new List<Fix>();
        for (int turn = 1; turn <= 30; turn++)
        {
            sim.AdvanceTurn();
            var town = Town(sim);
            Assert.Equal(Fix.One, town.GetFix(F.TownRation("class.peasant"), Fix.Zero));
            Fix sat = town.GetFix(F.TownSat("class.peasant"), Fix.Zero);
            Assert.InRange(sat.Raw, 0, Fix.FromInt(100).Raw);          // clamp 0–100
            // §4.2 的三档不是写死的字，而是跟着 S 走的：S* ≈ 59 时会在「服从/抱怨」之间来回抖
            string band = sat > Fix.FromInt(55) ? "服从" : sat >= Fix.FromInt(30) ? "抱怨" : "反抗";
            Assert.Equal(band, sim.World.GetStr(town.Id, F.TownStatus("class.peasant"), ""));
            samples.Add(sat);
        }

        // 0.35(S*−S) 收敛后只剩 ξ~N(0,2) 的抖动，所以比的是后 10 年的均值而不是单年值
        Fix avg = Fix.FromRaw(samples.Skip(20).Sum(s => s.Raw) / 10);
        Assert.True((avg - sStar).Abs() <= Fix.FromInt(3), $"S 的后 10 年均值 {avg.Text}，S* = {sStar.Text}");
    }

    // ================= 国势栏（§1.3 第一屏）=================

    [Fact]
    public void 国势栏是各城的合计而不是又扫一遍世界()
    {
        var sim = Open(47);
        sim.SubmitTarget("host", "nation.found", "开局", ("name", "蜀"));
        sim.SubmitTarget("host", "town.found", "一号", ("hex", "auto"), ("nation", "auto"));
        sim.AdvanceTurn();
        sim.SubmitTarget("host", "town.found", "二号", ("hex", "auto"), ("nation", "auto"));
        for (int turn = 1; turn <= 6; turn++) sim.AdvanceTurn();

        Fix produced = Fix.Zero, stock = Fix.Zero, eaten = Fix.Zero;
        long pop = 0, towns = 0;
        Fix minSat = Fix.FromInt(100);
        foreach (var town in sim.World.OfKind("town"))
        {
            towns++;
            // 与 TownOps.TotalPop 同一条判据：遍历 pop.* 前缀，阶级增减都不用改这里
            pop += town.Prefix("town.pop.").Sum(kv => kv.Value.AsInt());
            produced += sim.World.GetFix(town.Id, F.TownOutput("good.food"), Fix.Zero);
            stock += sim.World.GetFix(town.Id, F.TownStock("good.food"), Fix.Zero);
            eaten += Fix.Mul(sim.Data.Get("class.peasant").GetFix("consumes.food"),
                sim.World.GetInt(town.Id, F.TownPop("class.peasant"), 0));
            Fix sat = sim.World.GetFix(town.Id, F.TownSat("class.peasant"), Fix.FromInt(50));
            if (sat < minSat) minSat = sat;
        }

        var g = World.GlobalId;
        Assert.Equal(towns, sim.World.GetInt(g, F.ReportTowns, 0));
        Assert.Equal(pop, sim.World.GetInt(g, F.ReportPop, 0));
        Assert.Equal(produced, sim.World.GetFix(g, F.ReportFoodOut, Fix.Zero));
        Assert.Equal(eaten, sim.World.GetFix(g, F.ReportFoodEat, Fix.Zero));
        Assert.Equal(stock, sim.World.GetFix(g, F.ReportStock, Fix.Zero));
        Assert.Equal(minSat, sim.World.GetFix(g, F.ReportMinSat, Fix.Zero));
    }
}

using SimCore;

namespace CoreBasic;

/// <summary>
/// 一年里真正跑起来的部分（GDD §2.3 的 15 步中，M1 实装第 2/3/10/12/13/15 步）。
///
/// 每条规则都按 §8.4 的三段式落地：变更 → 公式 → 各项，
/// 于是 UI 能把「今年为什么只打了这么多粮」一路点开到单个乘数。
/// 数值一律不写在代码里：λ、需求篮子、期望、上限都从 <c>data/core.basic/*.json</c> 读。
///
/// 下面除 <see cref="Report"/> 外都是<b>主题级</b>处理器（一个城调一次），
/// 由 <see cref="BasicPlugin"/> 的扇出点按城镇 id 升序派发（§10.7 纪律 1）。
/// 扇出点必须判 (stepId, slot)：<c>OnProduction</c> 与 <c>OnConsumption</c> 各自落在校验表
/// 的两个步骤、四个槽位上，不判位置就会一年跑好几遍。
/// </summary>
public static class SeasonRules
{
    private static readonly Fix Hundred = Fix.FromInt(100);

    // ================= 第 3 步 agriculture：公式一（§4.2 / §5.2）=================

    /// <summary>
    /// <c>Out = min( P × λ × Tech × η(S) , Cap × Facility )</c>，其中 <c>η(S) = 0.6 + 0.4 × S/100</c>。
    /// 人是软上限、地是硬上限——§5.2 特别点名这一条挡住了「没有人口也自动产出」的经典 bug。
    /// M1 只有农民与粮：<c>Tech = 1.00</c>（科技卡 M6），缺料折扣不触发（还没有工/奢）。
    /// </summary>
    public static void Produce(EntityId town, HookArgs args, IStepContext ctx)
    {
        var peasant = ctx.Data.Get("class.peasant");
        Fix lambda = peasant.GetFix("output.food");      // §4.3：农民 2.40 粮/人/年
        Fix tech = Fix.One;                               // M6 换成科技卡倍率（1.0–2.5）

        long pop = ctx.GetInt(town, F.TownPop("class.peasant"));
        if (pop <= 0)
        {
            ctx.Mutate(town, F.TownOutput("good.food"), Fix.Zero, "P = 0 ⇒ 产出 0（§4.2：没有人不打粮）");
            return;
        }

        Fix sat = ctx.GetFix(town, F.TownSat("class.peasant"), Fix.FromInt(50));
        // η(S) = 0.6 + 0.4 × S/100：写成两个定点百分数的乘积，全程不出现浮点（§10.7 纪律 3）
        Fix eta = Fix.FromPercent(60) + Fix.Mul(Fix.FromPercent(40), Fix.Div(sat, Hundred));

        var tiles = TownOps.TilesOf(ctx, town);
        Fix cap = Fix.Zero;
        foreach (var t in tiles) cap += ctx.GetFix(t, F.HexYieldFood, Fix.Zero);
        Fix facility = ctx.GetFix(town, F.TownFacility, Fix.One);

        Fix soft = Fix.Mul(Fix.Mul(Fix.FromInt(pop), lambda), Fix.Mul(tech, eta));
        Fix hard = Fix.Mul(cap, facility);
        Fix gross = Fix.Min(soft, hard);
        Fix store = ctx.GetFix(town, F.TownStock("good.food"), Fix.Zero);

        ctx.Mutate(town, F.TownOutput("good.food"), gross, "本年净产出，年报与价格求解都读它（§4.1 的 output）");
        ctx.AddFix(town, F.TownStock("good.food"), gross, $"第 3 步农业产出 +{gross.Text} 石");

        ctx.Explain(town, "粮食产出（§5.2 公式一）",
            "Out = min( P × λ × Tech × η(S) , Cap × Facility )",
            [$"P（农民人口）= {pop} 个百人单位 = {pop * 100} 人",
             $"λ = {lambda.Text} 粮/人/年（data/core.basic/class.json）",
             $"Tech = {tech.Text}（科技卡 M6 接入，现在恒 1.00）",
             $"η(S) = 0.6 + 0.4 × {sat.Text}/100 = {eta.Text}（满意的人干活多）",
             $"⇒ 人的上限 soft = {soft.Text} 石",
             $"Cap（辖地 ΣY）= {cap.Text} 石，共 {tiles.Count} 格",
             $"Facility = {facility.Text}（第 2 步折算，§4.2 封顶 1.45）",
             $"⇒ 地的上限 hard = {hard.Text} 石"],
            $"Out = {(soft <= hard ? "人" : "地")}为紧约束 → {gross.Text} 石；仓底 {store.Text} → {(store + gross).Text} 石");
    }

    // ========== 第 10 步 consumption：口粮、满足率、仓满抛洒与损耗（§4.2 s_c、§5.6 步 6）==========

    public static void Consume(EntityId town, HookArgs args, IStepContext ctx)
    {
        var peasant = ctx.Data.Get("class.peasant");
        Fix need = peasant.GetFix("consumes.food");       // §4.3：1.00 粮/人/年

        long pop = ctx.GetInt(town, F.TownPop("class.peasant"));
        Fix want = Fix.Mul(need, pop);
        Fix have = ctx.GetFix(town, F.TownStock("good.food"), Fix.Zero);

        Fix got = Fix.Min(have, want);
        Fix ration = want.IsZero ? Fix.One : Fix.Div(got, want);
        ctx.AddFix(town, F.TownStock("good.food"), -got, $"第 10 步口粮 −{got.Text} 石（需求 {want.Text}）");
        ctx.Mutate(town, F.TownRation("class.peasant"), ration, "s_c = min(1, 实得/需求)（§4.2），公式三的输入");

        // 仓满抛洒：先按仓容截断，剩下的才对年损耗计息
        Fix left = have - got;
        Fix stockCap = ctx.GetFix(town, F.TownStockCap("good.food"), Fix.MaxValue);
        if (left > stockCap)
        {
            Fix spilled = left - stockCap;
            ctx.Mutate(town, F.TownStock("good.food"), stockCap,
                $"仓容 {stockCap.Text} 已满，抛洒 {spilled.Text} 石（§5.6 步 6：剩余物资留仓，仓满即抛）");
            left = stockCap;
        }

        Fix rate = ctx.GetFix(town, F.TownSpoilage("good.food"), Fix.Zero);
        Fix rotten = Fix.Mul(left, rate);
        if (!rotten.IsZero)
        {
            ctx.AddFix(town, F.TownStock("good.food"), -rotten,
                $"存粮损耗 {left.Text} × {rate.Text} = −{rotten.Text} 石（§5.7 的 5%/年，粮仓每级 −2%）");
            // 全国合计走 report.*：第 15 步的年报直接读，不必再遍历一次仓库
            ctx.AddFix(World.GlobalId, F.ReportFoodRot, rotten, "年报：本年损耗合计");
        }

        if (ration < Fix.One)
            ctx.Note($"{ctx.GetStr(town, F.TownName)}：口粮只够 {Percent(got, want)}%（需求 {want.Text}，实得 {got.Text}，缺 {(want - got).Text} 石）");
    }

    // ================= 第 12 步 satisfaction：公式三（§4.2 / 附录 A §3）=================

    /// <summary>
    /// <c>S* = 50 + 60(s−e) − 20·税负 + 10·政策 + 10·信仰 − 15·矛盾 + 8·地位</c>，
    /// <c>S ← clamp(S + 0.35(S*−S) + ξ, 0, 100)</c>，<c>ξ ~ N(0, 2)</c>。
    /// M1 只有基线与 s−e 项：税制在 M4、政策卡 M5、信仰 M7、阶级矛盾 M4、地位 M8。
    /// 不参与的各项按 0 写进 Explain——让玩家看得见「这一项还没算」，比藏着不说好。
    /// </summary>
    public static void Satisfaction(EntityId town, HookArgs args, IStepContext ctx)
    {
        bool anyRebellious = false;

        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            if (ctx.GetInt(town, F.TownPop(cid)) <= 0) continue;

            Fix sat = ctx.GetFix(town, F.TownSat(cid), Fix.FromInt(50));
            Fix s = ctx.GetFix(town, F.TownRation(cid), Fix.Zero);
            Fix e = cls.GetFix("satisfaction.expects");
            Fix star = Fix.FromInt((int)cls.GetInt("satisfaction.baseline", 50))
                       + Fix.Mul(Fix.FromInt(60), s - e);       // 其余四项 M1 为 0
            star = star.Clamp(Fix.Zero, Hundred);

            var stream = ctx.Rng.Stream(town, $"satisfaction.{F.Bare(cid)}.T{ctx.Time.Turn}");
            Fix xi = stream.NextGaussian(Fix.Zero, Fix.FromInt(2));   // ξ ~ N(0, 2)（§4.2）
            Fix pull = Fix.Mul(Fix.FromPercent(35), star - sat);      // 0.35(S*−S)
            Fix next = (sat + pull + xi).Clamp(Fix.Zero, Hundred);

            ctx.Mutate(town, F.TownSat(cid), next,
                $"S ← S + 0.35(S*−S) + ξ（§4.2 公式三）：{sat.Text} → {next.Text}");

            string status = next > Fix.FromInt(55) ? "服从" : next >= Fix.FromInt(30) ? "抱怨" : "反抗";
            ctx.Mutate(town, F.TownStatus(cid), status, "§4.2 三档：>55 服从 / 30–55 抱怨 / <30 反抗");
            if (next < Fix.FromInt(30)) anyRebellious = true;

            ctx.Explain(town, $"{cls.GetStr("title")}满意度（§5.4 公式三）",
                "S* = 50 + 60(s−e) − 20·税负 + 10·政策 + 10·信仰 − 15·矛盾 + 8·地位",
                [$"s（口粮满足率）= {s.Text}，e（期望）= {e.Text} ⇒ 60×(s−e) = {(s - e).Text} × 60",
                 $"基线 50；税负/政策/信仰/矛盾/地位 = 0（M4·M5·M7·M4·M8 接入）",
                 $"⇒ S* = {star.Text}，本年 S = {sat.Text}",
                 $"收敛 0.35(S*−S) = {pull.Text}，噪声 ξ ~ N(0,2) = {xi.Text}",
                 $"状态「{status}」"],
                $"S ← {next.Text}（clamp 0–100）");
        }

        // §4.2 的升级闸门：S<30 要连续 2 年才升级到怠工/罢工/暴动，所以这里只记年数，罚则 M4 兑现
        int prev = (int)ctx.GetInt(town, F.TownRiot);
        int riot = anyRebellious ? prev + 1 : 0;
        ctx.Mutate(town, F.TownRiot, riot,
            riot == 0 ? "没有阶级在反抗线以下，倒计时清零" : $"连续 {riot} 年有阶级 S<30（§4.2 满 2 年才升级）");
    }

    // ================= 第 13 步 demography：人口增长（附录 A §8）=================

    /// <summary>
    /// <c>ΔP = P × (0.04·粮安 − 0.03·拥挤 − 0.02·疫病 + 0.01·医疗)</c>。
    /// 粮安 = <c>clamp(2s−1, −1, 1)</c>：刚好糊口算 0，有余粮才为正，缺粮为负——
    /// 于是歉收真的会减员，而不是 GDD 反复警告的「无人口自动产出」那类 bug。
    /// 拥挤/疫病/医疗在 M3/M7 接入，这里显式按 0 写进 Explain。
    /// </summary>
    public static void Demography(EntityId town, HookArgs args, IStepContext ctx)
    {
        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            long pop = ctx.GetInt(town, F.TownPop(cid));
            if (pop <= 0) continue;

            Fix ration = ctx.GetFix(town, F.TownRation(cid), Fix.Zero);
            Fix secure = (Fix.Mul(ration, Fix.FromInt(2)) - Fix.One).Clamp(Fix.MinValue, Fix.One);
            Fix rate = Fix.Div(Fix.Mul(Fix.FromInt(4), secure), Hundred);   // 0.04 × 粮安
            Fix delta = Fix.Mul(Fix.FromInt(pop), rate);

            // 零头累积：不满一个百人单位（100 人）不进位，否则人口会凭空跳涨
            Fix carry = ctx.GetFix(town, F.TownGrowth(cid), Fix.Zero) + delta;
            long heads = carry.FloorToInt();
            if (heads != 0) ctx.AddFix(town, F.TownGrowth(cid), -Fix.FromInt(heads), "零头结算：进出人口后扣掉整数部分");
            if (heads == 0) continue;

            long after = Math.Max(0, pop + heads);
            long applied = after - pop;
            ctx.AddInt(town, F.TownPop(cid), applied, $"第 13 步人口 {pop} → {after} 个百人单位（附录 A §8）");

            ctx.Explain(town, "人口变动（附录 A §8）",
                "ΔP = P × (0.04·粮安 − 0.03·拥挤 − 0.02·疫病 + 0.01·医疗)",
                [$"P = {pop}，s = {ration.Text} ⇒ 粮安 = clamp(2s−1) = {secure.Text}",
                 "拥挤 / 疫病 / 医疗 = 0（M3 的市场、M7 的宗教接入）",
                 $"⇒ 增长率 {rate.Text}/年 ⇒ ΔP = {delta.Text} 个百人单位，累积零头后本年净 {applied}"],
                $"{cls.GetStr("title")}人口 {pop} → {after}（{(applied >= 0 ? "增" : "减")}{Math.Abs(applied) * 100} 人）");
        }
    }

    // ================= 第 15 步 wrapup：年度报告的「国势」栏（§1.3 第一屏）=================

    /// <summary>
    /// 全局聚合（不是主题级：它数的是「全国」）。Core 只负责把 <c>report.*</c> 字段收进年报的国势栏，
    /// 具体报什么、怎么算，全在这里。上一年的 <c>report.*</c> 由 <see cref="Simulation.AdvanceTurn"/> 回合首清掉。
    /// </summary>
    public static void Report(IStepContext ctx)
    {
        long towns = 0, pop = 0, unrest = 0;
        Fix produced = Fix.Zero, eaten = Fix.Zero, stock = Fix.Zero;
        Fix minSat = Fix.FromInt(100);

        foreach (var town in ctx.EntitiesOfKind(F.KindTown))
        {
            towns++;
            pop += TownOps.TotalPop(ctx, town.Id);
            produced += ctx.GetFix(town.Id, F.TownOutput("good.food"), Fix.Zero);
            stock += ctx.GetFix(town.Id, F.TownStock("good.food"), Fix.Zero);
            eaten += Fix.Mul(ctx.Data.Get("class.peasant").GetFix("consumes.food"),
                ctx.GetInt(town.Id, F.TownPop("class.peasant")));
            Fix sat = ctx.GetFix(town.Id, F.TownSat("class.peasant"), Fix.FromInt(50));
            if (sat < minSat) minSat = sat;
            if (ctx.GetInt(town.Id, F.TownRiot) > 0) unrest++;
        }

        var g = World.GlobalId;
        ctx.Mutate(g, F.ReportTowns, towns, "年报：城镇数");
        ctx.Mutate(g, F.ReportPop, pop, "年报：总人口（百人单位）");
        ctx.Mutate(g, F.ReportFoodOut, produced, "年报：本年粮产");
        ctx.Mutate(g, F.ReportFoodEat, eaten, "年报：本年口粮需求");
        ctx.Mutate(g, F.ReportStock, stock, "年报：存粮");
        ctx.Mutate(g, F.ReportMinSat, minSat, "年报：最低阶级满意度");
        ctx.Mutate(g, F.ReportUnrest, unrest, "年报：有阶级处于反抗线的城镇数（§4.2）");

        if (unrest > 0)
            ctx.Note($"告警：{unrest} 城有阶级满意度跌破 30，按 §4.2 再有一年就要怠工/抗税");
    }

    /// <summary>把 (实得, 需求) 折成整数百分比，只为 Note 的一行文本；不参与任何结算。</summary>
    private static long Percent(Fix got, Fix want) => want.IsZero ? 100 : got.Raw * 100 / want.Raw;
}

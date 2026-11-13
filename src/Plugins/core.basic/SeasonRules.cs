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
    /// <c>Out_c = min( P_c × λ_c × Tech × η(S_c) × statusMult , Cap_c × Facility_c )</c>，
    /// 其中 <c>η(S) = 0.6 + 0.4 × S/100</c>。
    ///
    /// 罚则照 §4.2 表：怠工 −15%（×0.85）、罢工/抗税 −40% 且该阶级税 −50%（×0.60；
    /// 税制未接，先只落产出并写明）、暴动沿用罢工档（GDD 未给暴动产出，占位等裁决）。
    ///
    /// 三段式（都在第 3 步内按 §2.3 的第 3–5 步顺序推进，第 4/5 步的 hook 仍不单挂——
    /// 这条实现偏差从 M4 切片一延续，见 CHANGELOG-DESIGN D-038/D-040）：
    /// A 第一产业（粮 / 料）受辖地潜力 <c>Cap × Facility</c> 约束（§4.9 算例）；
    /// B 第二产业（器 / 奢）受原料约束 <c>outputCost.*</c>（§4.3 工人产器耗料 1.2）；
    /// C 其余（银）只受人的上限。
    /// Tech = 1.00 恒（科技卡 M6）。
    /// </summary>
    public static void Produce(EntityId town, HookArgs args, IStepContext ctx)
    {
        Fix tech = Fix.One;                               // M6 换成科技卡倍率（1.0–2.5）

        // §2.3 把产出拆成第 3/4/5 三步（农业 / 工业 / 服务）：
        // 本处理器挂三次，按商品的 step 字段各产各的；output.* 只在第 3 步清一次，
        // 后面两步继续累加，否则第 4 步会把第 3 步的粮产清掉。
        string stepId = args.GetStr("stepId");
        bool isAgricultureStep = string.Equals(stepId, "agriculture", StringComparison.Ordinal);
        if (isAgricultureStep)
        {
            foreach (var good in ctx.Data.OfType(F.TypeGood))
                ctx.Mutate(town, F.TownOutput(good.Id), Fix.Zero,
                    $"第 3 步清 {good.GetStr("title")} 本期产出（上回合已 AddFix 进 stock）");
        }

        Fix facility = ctx.GetFix(town, F.TownFacility, Fix.One);
        Fix capFood = LandCap(ctx, town, F.HexYieldFood);
        Fix capMat = LandCap(ctx, town, F.HexYieldMat);

        // 解释文本要复用的农民产粮中间量（§6：解释不许重算公式）
        long pPeasant = 0;
        Fix peasantLambda = Fix.Zero, peasantEta = Fix.Zero, peasantStatusMult = Fix.One, peasantSoft = Fix.Zero;
        string peasantStatus = "";

        // ---- 第一趟 A：第一产业（粮 / 料），受地约束 ----
        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            long pop = ctx.GetInt(town, F.TownPop(cid));
            if (pop <= 0) continue;

            Fix eta = Eta(town, ctx, cid);
            string status = ctx.GetStr(town, F.TownStatus(cid));
            Fix statusMult = StatusMult(status);
            Fix mult = Fix.Mul(tech, Fix.Mul(eta, statusMult));

            foreach (var kv in OutputKeys(cls))
            {
                string goodShort = kv.Key["output.".Length..];
                if (goodShort is not ("food" or "mat")) continue;

                Fix lambda = kv.Value.AsFix();
                if (lambda.IsZero) continue;

                string goodId = "good." + goodShort;
                if (!ctx.Data.TryGet(goodId, out var goodA)) continue;
                if (!string.Equals(goodA.GetStr("step", "agriculture"), stepId, StringComparison.Ordinal))
                    continue;   // §2.3：粮在第 3 步、料在第 4 步

                Fix soft = Fix.Mul(Fix.Mul(Fix.FromInt(pop), lambda), mult);
                Fix cap = goodShort == "food" ? capFood : capMat;
                Fix hard = Fix.Mul(cap, facility);
                Fix gross = Fix.Min(soft, hard);

                Emit(town, ctx, cls, goodId, goodShort, gross,
                    $"§4.2 公式一：P={pop} × λ={lambda.Text} × η×状态={mult.Text}，地限 Cap×Facility={hard.Text}");

                if (cid == "class.peasant" && goodShort == "food")
                {
                    pPeasant = pop;
                    peasantLambda = lambda;
                    peasantEta = eta;
                    peasantStatusMult = statusMult;
                    peasantStatus = status;
                    peasantSoft = soft;
                }
            }
        }

        // ---- 第二趟 B/C：工业与服务；先扣原料再产成品（§4.3 / §4.9）----
        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            long pop = ctx.GetInt(town, F.TownPop(cid));
            if (pop <= 0) continue;

            Fix eta = Eta(town, ctx, cid);
            Fix statusMult = StatusMult(ctx.GetStr(town, F.TownStatus(cid)));
            Fix mult = Fix.Mul(tech, Fix.Mul(eta, statusMult));

            // 本步没有该阶级的产出就整段跳过：否则原料会在第 3/5 步被扣掉却什么都不产
            bool producesHere = OutputKeys(cls).Any(kv =>
            {
                string s = kv.Key["output.".Length..];
                return ctx.Data.TryGet("good." + s, out var g)
                       && string.Equals(g.GetStr("step", "agriculture"), stepId, StringComparison.Ordinal);
            });
            if (!producesHere) continue;
            Fix ratio = Fix.One;
            var inputs = new List<(string goodId, Fix need)>();
            foreach (var kv in cls.Scalars)
            {
                if (!kv.Key.StartsWith("outputCost.", StringComparison.Ordinal)) continue;
                Fix cost = kv.Value.AsFix();
                if (cost.IsZero) continue;

                string inShort = kv.Key["outputCost.".Length..];
                string inId = "good." + inShort;
                if (!ctx.Data.TryGet(inId, out _)) continue;

                Fix need = Fix.Mul(Fix.FromInt(pop), cost);
                if (need.IsZero) continue;
                Fix avail = ctx.GetFix(town, F.TownStock(inId), Fix.Zero);
                if (avail < need)
                {
                    Fix r = Fix.Div(avail, need);
                    if (r < Fix.Zero) r = Fix.Zero;
                    if (r < ratio) ratio = r;
                }
                inputs.Add((inId, need));
            }

            foreach (var (inId, need) in inputs)
            {
                Fix used = Fix.Mul(need, ratio);
                if (used.IsZero) continue;
                ctx.AddFix(town, F.TownStock(inId), -used,
                    $"§4.3 原料约束：{cls.GetStr("title")} {pop} 人需 {need.Text} 料，只能满足 {ratio.Text} → 实耗 {used.Text}");
            }

            foreach (var kv in OutputKeys(cls))
            {
                string goodShort = kv.Key["output.".Length..];
                if (goodShort is "food" or "mat") continue;

                Fix lambda = kv.Value.AsFix();
                if (lambda.IsZero) continue;

                string goodId = "good." + goodShort;
                if (!ctx.Data.TryGet(goodId, out _)) continue;

                Fix soft = Fix.Mul(Fix.Mul(Fix.FromInt(pop), lambda), mult);
                Fix gross = Fix.Mul(soft, ratio);   // 没有 outputCost 的阶级 ratio 恒为 1.00
                Emit(town, ctx, cls, goodId, goodShort, gross,
                    $"§4.2 公式一：P={pop} × λ={lambda.Text} × η×状态={mult.Text}" +
                    (ratio < Fix.One ? $"，原料只够 {ratio.Text}" : ""));
            }
        }

        // ---- 聚合解释：公式一正文（§8.4 要求公式原样出现在解释里）----
        Fix totalFoodOut = ctx.GetFix(town, F.TownOutput("good.food"), Fix.Zero);
        if (isAgricultureStep && !totalFoodOut.IsZero)
        {
            Fix hard = Fix.Mul(capFood, facility);
            ctx.Explain(town, "粮食产出（§5.2 公式一）",
                "Out = min( P × λ × Tech × η(S) × statusMult , Cap × Facility )",
                [$"P（农民人口）= {pPeasant} 个百人单位 = {pPeasant * 100} 人",
                 $"λ = {peasantLambda.Text} 粮/人/年（data/core.basic/class.json）",
                 $"Tech = {tech.Text}（科技卡 M6 接入，现在恒 1.00）",
                 $"η(S) = 0.6 + 0.4 × S/100 = {peasantEta.Text}（满意的人干活多）",
                 $"状态「{peasantStatus}」× statusMult = {peasantStatusMult.Text}（§4.2：怠工 0.85、罢工/暴动 0.60）",
                 $"⇒ 人的上限 soft = {peasantSoft.Text} 石",
                 $"Cap（辖地 ΣY）= {capFood.Text} 石",
                 $"Facility = {facility.Text}（第 2 步折算，§4.2 封顶 1.45）",
                 $"⇒ 地的上限 hard = {hard.Text} 石"],
                $"Out = {(peasantSoft <= hard ? "人" : "地")}为紧约束 → {totalFoodOut.Text} 石");
        }
    }

    // ========== 第 10 步 consumption：口粮、满足率、仓满抛洒与损耗（§4.2 s_c、§5.4 步 6）==========

    /// <summary>
    /// 第 10 步：口粮、满足率、仓满抛洒与年损耗。
    ///
    /// **短缺时按需求比例配给**（GDD v1.1 §5.4）：先汇总全镇每种商品的总需求，
    /// 再算统一配给率 <c>s = min(1, 可用 / 总需求)</c>，最后每个阶级按自己的需求乘同一个率。
    /// 禁止按阶级 id、数据文件顺序或遍历顺序分配——旧实现按 <c>class.artist → … → class.worker</c>
    /// 的 id 序扣粮，等于让文艺家先吃饱、农民吃剩饭（REVIEW-M0-M4 §3.1）。
    /// 配给率仍是公式三的输入，公式本身不变；价格仍只是信号（M5a 再接购买力）。
    /// </summary>
    public static void Consume(EntityId town, HookArgs args, IStepContext ctx)
    {
        // ---- 先清零本期所有商品的 consumed/spoiled/spilled ----
        foreach (var good in ctx.Data.OfType(F.TypeGood))
        {
            ctx.Mutate(town, F.TownConsumed(good.Id), Fix.Zero, "第 10 步清本期消耗");
            ctx.Mutate(town, F.TownSpoiled(good.Id), Fix.Zero, "第 10 步清本期损耗");
            ctx.Mutate(town, F.TownSpilled(good.Id), Fix.Zero, "第 10 步清本期抛洒");
        }

        // ---- 第一遍：汇总每种商品的总需求（只报数，不扣库存）----
        var wants = new List<(string Cid, string GoodShort, Fix Want)>();
        var totalWant = new SortedDictionary<string, Fix>(StringComparer.Ordinal);

        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            long pop = ctx.GetInt(town, F.TownPop(cid));
            if (pop <= 0)
            {
                ctx.Mutate(town, F.TownRation(cid), Fix.One, "P = 0 ⇒ 自动 1.00 满足率");
                continue;
            }

            var consumeKeys = cls.Scalars
                .Where(kv => kv.Key.StartsWith("consumes.", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal);

            foreach (var kv in consumeKeys)
            {
                Fix need = kv.Value.AsFix();
                if (need.IsZero) continue;

                string goodShort = kv.Key["consumes.".Length..];
                string goodId = "good." + goodShort;
                if (!ctx.Data.TryGet(goodId, out _)) continue;

                Fix want = Fix.Mul(need, pop);
                wants.Add((cid, goodShort, want));
                totalWant[goodId] = totalWant.TryGetValue(goodId, out Fix acc) ? acc + want : want;
            }
        }

        // ---- 第二遍：每种商品一个全镇统一的配给率（此时库存尚未被任何阶级扣走）----
        var ratio = new SortedDictionary<string, Fix>(StringComparer.Ordinal);
        foreach (var pair in totalWant)
        {
            Fix have = ctx.GetFix(town, F.TownStock(pair.Key), Fix.Zero);
            ratio[pair.Key] = pair.Value.IsZero
                ? Fix.One
                : Fix.Min(Fix.One, Fix.Div(have, pair.Value));
        }

        // ---- 第三遍：每个阶级按同一比率扣自己的那份 ----
        var firstRatio = new SortedDictionary<string, Fix>(StringComparer.Ordinal);
        var foodRatio = new SortedDictionary<string, Fix>(StringComparer.Ordinal);
        var firstGood = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (cid, goodShort, want) in wants)
        {
            string goodId = "good." + goodShort;
            Fix r = ratio[goodId];
            Fix got = Fix.Mul(want, r);

            ctx.AddFix(town, F.TownStock(goodId), -got,
                $"第 10 步 {ctx.Data.Get(cid).GetStr("title")} 耗 {goodShort} −{got.Text}" +
                $"（需求 {want.Text} × 配给率 {r.Text}，§5.4 按需求比例）");
            ctx.AddFix(town, F.TownConsumed(goodId), got,
                $"§5.9 守恒：{ctx.Data.Get(cid).GetStr("title")} 耗 {goodShort} {got.Text}");

            if (!firstRatio.TryGetValue(cid, out Fix rmin) || r < rmin) firstRatio[cid] = r;
            if (!firstGood.ContainsKey(cid)) firstGood[cid] = goodShort;
            if (goodShort == "food") foodRatio[cid] = r;
        }

        // ---- 满足率 s_c：优先口粮配给率，没有口粮需求的阶级取自己见过的最低配给率 ----
        foreach (var pair in firstRatio)
        {
            string cid = pair.Key;
            Fix ration = foodRatio.TryGetValue(cid, out Fix fr) ? fr : pair.Value;
            ctx.Mutate(town, F.TownRation(cid), ration,
                $"s_c = min(1, 可用/总需求)（GDD v1.1 §5.4，各阶级同率），公式三的输入");

            if (ration < Fix.One)
            {
                ctx.Note($"{ctx.GetStr(town, F.TownName)}：{ctx.Data.Get(cid).GetStr("title")}的" +
                         $"{(foodRatio.ContainsKey(cid) ? "food" : firstGood[cid])}只够 {ration.Text} 的配给率");
            }
        }

        // ---- 仓满抛洒 + 年损耗（循环所有商品）----
        foreach (var good in ctx.Data.OfType(F.TypeGood))
        {
            Fix stock = ctx.GetFix(town, F.TownStock(good.Id), Fix.Zero);
            if (stock.IsZero) continue;

            // 仓满抛洒
            Fix stockCap = ctx.GetFix(town, F.TownStockCap(good.Id), Fix.MaxValue);
            Fix spilled = Fix.Zero;
            if (stock > stockCap)
            {
                spilled = stock - stockCap;
                ctx.Mutate(town, F.TownStock(good.Id), stockCap,
                    $"{good.GetStr("title")}仓容 {stockCap.Text} 已满，抛洒 {spilled.Text}（§5.4 步 6）");
            }
            ctx.Mutate(town, F.TownSpilled(good.Id), spilled, "§5.9 守恒：仓满抛洒");

            // 年损耗
            Fix rate = ctx.GetFix(town, F.TownSpoilage(good.Id), Fix.Zero);
            Fix rotten = Fix.Mul(stock - spilled, rate);
            if (!rotten.IsZero)
            {
                ctx.AddFix(town, F.TownStock(good.Id), -rotten,
                    $"{good.GetStr("title")}损耗 {(stock - spilled).Text} × {rate.Text} = −{rotten.Text}（§5.7）");
                // 年报：只累粮的损耗（ReportFoodRot 语义是粮损耗，非粮损耗切片一不单独报）
                if (good.Id == "good.food")
                    ctx.AddFix(World.GlobalId, F.ReportFoodRot, rotten, "年报：本年粮损耗合计");
            }
            ctx.Mutate(town, F.TownSpoiled(good.Id), rotten, "§5.9 守恒：年损耗");
        }
    }

    // ================= 第 12 步 satisfaction：公式三（§4.2 / 附录 A §3）=================

    /// <summary>
    /// <c>S* = 基线 + 60(s−e) − 20·税负 + 10·政策 + 10·信仰 − 15·矛盾 + 8·地位</c>，
    /// <c>S ← clamp(S + 0.35(S*−S) + ξ, 0, 100)</c>，<c>ξ ~ N(0, 2)</c>。
    ///
    /// 状态机（§4.2）：S&gt;55 服从 / 30≤S≤55 抱怨 / S&lt;30 反抗。
    /// 反抗<b>每连续 2 年升一级</b>：第 2–3 年怠工、第 4–5 年罢工、第 6 年起暴动。
    /// 计数按阶级各记一份（`town.riot.&lt;阶级&gt;`），不再共用一个全局计数。
    /// 进入暴动后启动 §4.2 的「3 年控制权倒计时」，归零则该城脱离原属国（镇压/让步归 M7）。
    /// </summary>
    public static void Satisfaction(EntityId town, HookArgs args, IStepContext ctx)
    {
        long riotMax = 0;
        bool rioting = false;

        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            if (ctx.GetInt(town, F.TownPop(cid)) <= 0) continue;

            Fix sat = ctx.GetFix(town, F.TownSat(cid), Fix.FromInt(50));
            Fix s = ctx.GetFix(town, F.TownRation(cid), Fix.Zero);
            Fix e = cls.GetFix("satisfaction.expects");
            Fix baseline = Fix.FromInt((int)cls.GetInt("satisfaction.baseline", 50));
            Fix premium = Fix.Mul(Fix.FromInt(60), s - e);       // 60(s−e)
            // §4.5 公式三的后两项：−20·税负 − 15·矛盾。
            // 税负由第 11 步（M5a 税制）写 town.tax.<阶级>，此前恒 0；
            // 矛盾_c 是阶级数据里的结构性冲突强度（0–1），M4 先配 0.00，标定归 M5b。
            Fix taxBurden = ctx.GetFix(town, F.TownTaxBurden(cid), Fix.Zero);
            Fix conflict = cls.GetFix("satisfaction.conflict", Fix.Zero);
            Fix star = (baseline + premium
                        - Fix.Mul(Fix.FromInt(20), taxBurden)
                        - Fix.Mul(Fix.FromInt(15), conflict)).Clamp(Fix.Zero, Hundred);

            var stream = ctx.Rng.Stream(town, $"satisfaction.{F.Bare(cid)}.T{ctx.Time.Turn}");
            Fix xi = stream.NextGaussian(Fix.Zero, Fix.FromInt(2));   // ξ ~ N(0, 2)（§4.2）
            Fix pull = Fix.Mul(Fix.FromPercent(35), star - sat);      // 0.35(S*−S)
            Fix next = (sat + pull + xi).Clamp(Fix.Zero, Hundred);

            ctx.Mutate(town, F.TownSat(cid), next,
                $"S ← S + 0.35(S*−S) + ξ（§4.2 公式三）：{sat.Text} → {next.Text}");

            // ---- 状态机（§4.2）：连续 S<30 的年数按阶级各记一份 ----
            string prevStatus = ctx.GetStr(town, F.TownStatus(cid));
            ctx.Mutate(town, F.TownStatusPrev(cid), prevStatus,
                "状态机：存上回合状态（§5.9 的 monotonic 不变量要读）");

            long prevYears = ctx.GetInt(town, F.TownRiotOf(cid));
            long years;
            string nextStatus;

            if (next > Fix.FromInt(55))
            {
                nextStatus = "服从";
                years = 0;
            }
            else if (next >= Fix.FromInt(30))
            {
                nextStatus = "抱怨";
                years = 0;
            }
            else
            {
                years = prevYears + 1;
                long level = years / 2;      // §4.2「连续 2 年 S<30 才升一级」
                nextStatus = level >= 3 ? "暴动" : level >= 2 ? "罢工" : level >= 1 ? "怠工" : "抱怨";
            }

            if (years != prevYears)
                ctx.Mutate(town, F.TownRiotOf(cid), years,
                    years == 0
                        ? $"{cls.GetStr("title")}回到 S≥30，连续反抗年数清零"
                        : $"{cls.GetStr("title")}连续 {years} 年 S<30（§4.2 每 2 年升一级）");

            ctx.Mutate(town, F.TownStatus(cid), nextStatus,
                $"§4.2 五档：{prevStatus} → {nextStatus}" +
                (years > 0 ? $"（连续 S<30 第 {years} 年）" : ""));

            if (years > riotMax) riotMax = years;
            if (nextStatus == "暴动") rioting = true;

            ctx.Explain(town, $"{cls.GetStr("title")}满意度（§4.2 公式三）",
                "S* = 基线 + 60(s−e) − 20·税负 + 10·政策 + 10·信仰 − 15·矛盾 + 8·地位",
                [$"s（口粮满足率）= {s.Text}，e（期望）= {e.Text} ⇒ s−e = {(s - e).Text} ⇒ 60×(s−e) = {premium.Text}",
                 $"基线 = {baseline.Text}（data/core.basic/class.json 的 satisfaction.baseline）",
                 "税负 / 政策 / 信仰 / 矛盾 / 地位 = 0（M7·M5·M7·M4·M8 接入）",
                 $"⇒ S* = {star.Text}，本年 S = {sat.Text}",
                 $"收敛 0.35(S*−S) = {pull.Text}，噪声 ξ ~ N(0,2) = {xi.Text}",
                 $"状态「{nextStatus}」（{prevStatus} → {nextStatus}，连续 S<30 {years} 年）"],
                $"S ← {next.Text}（clamp 0–100）");
        }

        // ---- 暴动倒计时（§4.2 第 3 级：3 年 → 城镇易主/独立）----
        long countdown = ctx.GetInt(town, F.TownCountdown, 3);
        if (rioting)
        {
            countdown--;
            if (countdown <= 0)
            {
                if (!F.IsBlank(ctx.GetStr(town, F.TownOwner)))
                {
                    ctx.Mutate(town, F.TownOwner, "",
                        "§4.2 暴动倒计时归零：城镇脱离原属国（镇压/让步归 M7 政策，M4 无军力可镇压）");
                    ctx.Mutate(town, F.TownSovereignty, "independent", "§4.2 暴动倒计时归零 → 独立");
                    ctx.Note($"告警：{ctx.GetStr(town, F.TownName)} 因连续暴动脱离原属国，成为独立城镇（§4.2）");
                    foreach (var cls in ctx.Data.OfType(F.TypeClass))
                    {
                        if (ctx.GetStr(town, F.TownStatus(cls.Id)) != "暴动") continue;
                        ctx.Mutate(town, F.TownStatus(cls.Id), "罢工",
                            "让步：暴动平息一档，重新给 3 年观察期（§4.2 的让步选项）");
                        ctx.Mutate(town, F.TownRiotOf(cls.Id), 3L,
                            "让步后反抗年数回到第 3 年（对应罢工档）");
                    }
                }
                countdown = 3;
            }
            ctx.Mutate(town, F.TownCountdown, countdown, $"§4.2 暴动控制权倒计时 → {countdown} 年");
        }
        else if (countdown != 3)
        {
            ctx.Mutate(town, F.TownCountdown, 3L, "不再有阶级暴动，控制权倒计时复位为 3 年");
        }

        // ---- 全局聚合（年报与旧读者仍看 town.riot）----
        ctx.Mutate(town, F.TownRiot, riotMax,
            riotMax == 0 ? "没有阶级在反抗线以下，殿报计数清零"
                         : $"本城最长连续 S<30 年数 {riotMax}（§4.2；分阶级计数见 town.riot.*）");
    }

    // ================= 第 13 步 demography：人口增长（附录 A §8）=================

    /// <summary>
    /// <c>ΔP = P × (0.04·粮安 − 0.03·拥挤 − 0.02·疫病 + 0.01·医疗)</c>。
    /// 粮安 = <c>clamp(2s−1, −1, 1)</c>：刚好糊口算 0，有余粮才为正，缺粮为负——
    /// 于是歉收真的会减员，而不是 GDD 反复警告的「无人口自动产出」那类 bug。
    /// 拥挤/疫病/医疗在 M3/M7 接入，这里显式按 0 写进 Explain。
    ///
    /// 定点的一条硬规矩：<b>小于 0.01 的量绝不先物化再相乘</b>。0.04·粮安 在缺粮年份是
    /// 0.00xx，×100 定点会把中间量直接截成 0.00，人口就此冻死；所以这里写成 P × 粮安 × 4 ÷ 100，
    /// 只在最后截断一次。同理，M3 接「拥挤/疫病/医疗」时也要往这个乘积里加项，
    /// 不许先算出年增长率再乘 P。
    /// </summary>
    public static void Demography(EntityId town, HookArgs args, IStepContext ctx)
    {
        // 住房容量（§4.7 的 拥挤 = max(0, 总人口 / 住房容量 − 1)）。
        // GDD 没定义住房容量从哪来；M4 用「辖地自然粮潜力 ÷ 人均口粮」代理——
        // 也就是这块地<b>不靠设施改良</b>时能养多少人（设施是增产，不是盖房）。
        // 没有这一项，粮安在 s=0.5 才归零，人口会冲到承载力 2 倍、
        // 把 S* 压到怠工线（seed 42 的 200 年死亡螺旋）。
        Fix total = Fix.FromInt(TownOps.TotalPop(ctx, town));
        Fix foodNeed = Fix.Zero;
        foreach (var cls in ctx.Data.OfType(F.TypeClass))
            foodNeed += Fix.Mul(cls.GetFix("consumes.food"), ctx.GetInt(town, F.TownPop(cls.Id)));
        Fix housingLand = LandCap(ctx, town, F.HexYieldFood);
        Fix foodPerPop = total.IsZero ? Fix.Zero : Fix.Div(foodNeed, total);
        Fix housing = foodPerPop.IsZero ? Fix.Zero : Fix.Div(housingLand, foodPerPop);
        Fix crowding = housing.IsZero
            ? Fix.Zero
            : Fix.Max(Fix.Zero, Fix.Div(total, housing) - Fix.One);

        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            long pop = ctx.GetInt(town, F.TownPop(cid));
            if (pop <= 0) continue;

            Fix ration = ctx.GetFix(town, F.TownRation(cid), Fix.Zero);
            Fix secure = (Fix.Mul(ration, Fix.FromInt(2)) - Fix.One).Clamp(Fix.MinValue, Fix.One);
            // 0.04·粮安 只有 0.00xx 的量级，别把它先物化出来：×100 定点下 s ≤ 0.62 时
            // 0.04×粮安 正好截成 0.00，再乘人口还是 0，于是人口永远冻在地块上限上
            // （seed 42 跑到第 83 年 72 个百人单位之后一动不动 117 年，就是这么来的）。
            // 把 P 先乘进去，全程只在最后截断一次：ΔP = P × (4·粮安 − 3·拥挤) ÷ 100。
            Fix growth = Fix.Mul(Fix.FromInt(4), secure) - Fix.Mul(Fix.FromInt(3), crowding);
            Fix delta = Fix.Div(Fix.Mul(Fix.FromInt(pop), growth), 100);

            // 零头累积。两个坑都踩过，所以这一段写得格外明白：
            // 1) 必须先落账再取整数。只在「进了位」的那年才写回 carry 的话，
            //    没进位的年份增量会被整年丢掉，人口永远涨不起来（§10.7 纪律 4：派生量也要留痕）。
            // 2) 取整用「向零截断」而不是 floor：floor(-0.16) = -1，
            //    一场小歉收就会凭空抹掉一个百人单位 = 100 人，方向完全错。
            ctx.AddFix(town, F.TownGrowth(cid), delta, $"第 13 步增长量 {delta.Text} 个百人单位，先进零头桶");
            Fix carry = ctx.GetFix(town, F.TownGrowth(cid), Fix.Zero);
            long heads = carry.Raw / Fix.Scale;                 // 向零截断
            if (heads == 0) continue;

            ctx.AddFix(town, F.TownGrowth(cid), -Fix.FromInt(heads), "零头结算：进出人口后扣掉整数部分");
            long after = Math.Max(0, pop + heads);
            long applied = after - pop;
            if (applied != heads)
                ctx.AddFix(town, F.TownGrowth(cid), Fix.FromInt(heads - applied), "减到 0 为止，剩下的零头退回桶里");
            ctx.AddInt(town, F.TownPop(cid), applied, $"第 13 步人口 {pop} → {after} 个百人单位（附录 A §8）");

            ctx.Explain(town, "人口变动（附录 A §8）",
                "ΔP = P × (0.04·粮安 − 0.03·拥挤 − 0.02·疫病 + 0.01·医疗)",
                [$"P = {pop}，s = {ration.Text} ⇒ 粮安 = clamp(2s−1) = {secure.Text}",
                 $"住房容量 = 辖地自然粮潜力 {housingLand.Text} ÷ 人均口粮 {foodPerPop.Text} = {housing.Text}，总人口 {total.Text} ⇒ 拥挤 = {crowding.Text}",
                 "疫病 / 医疗 = 0（M7 的宗教与医疗接入）",
                 $"⇒ ΔP = {pop} × (4×{secure.Text} − 3×{crowding.Text})% = {delta.Text} 个百人单位（先乘 P 再取百分比，免得 0.00xx 落在 1/100 刻度上归零），累积零头后本年净 {applied}"],
                $"{cls.GetStr("title")}人口 {pop} → {after}（{(applied >= 0 ? "增" : "减")}{Math.Abs(applied) * 100} 人）");
        }

        Convert(town, ctx);

        // §4.5/§5.9：写全阶级人口合计。转换只搬人不造人，Σ 阶级 == 总数 必须恒成立，
        // basic.population 不变量在第 13 步末校验它（REVIEW-M0-M4 §3.3）。
        long popTotal = TownOps.TotalPop(ctx, town);
        ctx.Mutate(town, F.TownPopTotal, popTotal,
            $"§4.5 全阶级人口合计 {popTotal} 个百人单位（第 13 步末，供人口守恒不变量读）");
    }

    /// <summary>
    /// 阶级转化（§4.7）：每年<b>单阶级</b>转入 ≤ 总人口 2%，从农民池里出人。
    ///
    /// GDD 给的驱动量是「阶级吸引力 = 收入 + 就业 + 地位 + 福利 − 生活成本 − 风险」，
    /// 其中收入/工资要等 M5 的发放（第 9 步）才有数。所以 M4 用 <c>shareTarget</c>
    /// 占位：每个已解锁阶级朝「占总人口的一定比例」收敛，单年仍受 2% 上限约束。
    /// 这是**占位标定**，已记 CHANGELOG-DESIGN D-041 待裁决。
    /// </summary>
    private static void Convert(EntityId town, IStepContext ctx)
    {
        long total = TownOps.TotalPop(ctx, town);
        if (total <= 0) return;

        Fix capPerClass = Fix.Mul(Fix.FromInt(total), Fix.FromPercent(2));   // 总人口 2%/年
        long peasant = ctx.GetInt(town, F.TownPop("class.peasant"));

        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            if (cid == "class.peasant") continue;

            Fix share = cls.GetFix("shareTarget", Fix.Zero);
            if (share.IsZero) continue;
            if (!ctx.Get(town, F.TownUnlocked(cid)).AsBool()) continue;

            long pop = ctx.GetInt(town, F.TownPop(cid));
            Fix target = Fix.Mul(Fix.FromInt(total), share);
            Fix diff = target - Fix.FromInt(pop);
            if (diff.IsZero) continue;

            // 高于结构目标时反向回流农民（同样受「单阶级 ≤ 总人口 2%/年」约束）。
            // 只做单向流入的话，小人口时整数进位造成的超额会被 4%/年的人口增长永久锁住
            // （seed 7 的工人曾稳定在 22.5%，目标是 12%，见 D-046 的全阶级基线）。
            Fix want = diff > Fix.Zero
                ? Fix.Min(diff, capPerClass)
                : -Fix.Min(-diff, capPerClass);
            ctx.AddFix(town, F.TownConvert(cid), want,
                diff > Fix.Zero
                    ? $"§4.7 转化：{cls.GetStr("title")}目标 {target.Text}，现 {pop}，本年最多转 {want.Text}（零头入桶）"
                    : $"§4.7 回流：{cls.GetStr("title")}目标 {target.Text}，现 {pop}，本年最多回流 {(-want).Text}（零头入桶）");

            Fix carry = ctx.GetFix(town, F.TownConvert(cid), Fix.Zero);
            long heads = carry.Raw / Fix.Scale;
            if (heads > 0)
            {
                long movable = Math.Max(0, peasant - 1);   // 农民至少留 1 个百人单位，否则没人种地
                if (movable <= 0) continue;
                if (heads > movable) heads = movable;      // 剩下的零头留在桶里等明年

                ctx.AddFix(town, F.TownConvert(cid), -Fix.FromInt(heads), "转化零头结算：出人后扣掉整数部分");
                ctx.AddInt(town, F.TownPop("class.peasant"), -heads,
                    $"§4.7 阶级转化：{heads} 个百人单位离开农民（目标份额 {share.Text}）");
                ctx.AddInt(town, F.TownPop(cid), heads,
                    $"§4.7 阶级转化：{cls.GetStr("title")} +{heads} 个百人单位");
                peasant -= heads;

                ctx.Note($"{ctx.GetStr(town, F.TownName)}：{heads * 100} 人转为{cls.GetStr("title")}（§4.7 转化，目标份额 {share.Text}）");
            }
            else if (heads < 0)
            {
                long back = Math.Min(-heads, pop);
                if (back <= 0) continue;

                ctx.AddFix(town, F.TownConvert(cid), Fix.FromInt(back), "回流零头结算：出人后扣掉整数部分");
                ctx.AddInt(town, F.TownPop(cid), -back,
                    $"§4.7 阶级回流：{cls.GetStr("title")} 高于结构目标，{back} 个百人单位回到农民");
                ctx.AddInt(town, F.TownPop("class.peasant"), back,
                    $"§4.7 阶级回流：{back} 个百人单位回到农民（目标份额 {share.Text}）");
                peasant += back;
            }
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

            // 口粮需求 = 全阶级 consumes.food × 人口（M4 起不只农民）
            foreach (var cls in ctx.Data.OfType(F.TypeClass))
                eaten += Fix.Mul(cls.GetFix("consumes.food"), ctx.GetInt(town.Id, F.TownPop(cls.Id)));

            // 最低满意取「所有有人在的阶级」，不能只看农民——
            // 旧写法把「文艺家满意度」这类值固化成「农民满意度」（REVIEW-M0-M4 §3.2）
            foreach (var cls in ctx.Data.OfType(F.TypeClass))
            {
                if (ctx.GetInt(town.Id, F.TownPop(cls.Id)) <= 0) continue;
                Fix s = ctx.GetFix(town.Id, F.TownSat(cls.Id), Fix.FromInt(50));
                if (s < minSat) minSat = s;
            }
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

    // ---- 工具 ----

    /// <summary>辖地潜力之和（§3.6 的 <c>Cap_g = Σ Y_g</c>），按地块 id 升序累加。</summary>
    private static Fix LandCap(IStepContext ctx, EntityId town, string yieldPath)
    {
        Fix sum = Fix.Zero;
        foreach (var t in TownOps.TilesOf(ctx, town)) sum += ctx.GetFix(t, yieldPath, Fix.Zero);
        return sum;
    }

    /// <summary><c>η(S) = 0.6 + 0.4 × S/100</c>（§4.2）。</summary>
    private static Fix Eta(EntityId town, IStepContext ctx, string classDefId)
    {
        Fix sat = ctx.GetFix(town, F.TownSat(classDefId), Fix.FromInt(50));
        return Fix.FromPercent(60) + Fix.Mul(Fix.FromPercent(40), Fix.Div(sat, Hundred));
    }

    /// <summary>状态机罚则（§4.2）：服从/抱怨 1.00、怠工 0.85、罢工/暴动 0.60。</summary>
    private static Fix StatusMult(string status) => status switch
    {
        "怠工" => Fix.FromPercent(85),
        "罢工" or "暴动" => Fix.FromPercent(60),
        _ => Fix.One
    };

    /// <summary>某阶级的全部 <c>output.*</c> 键（排除 <c>outputCost.*</c>），按点分路径序数排序。</summary>
    private static IEnumerable<KeyValuePair<string, SimValue>> OutputKeys(DataEntry cls) =>
        cls.Scalars
            .Where(kv => kv.Key.StartsWith("output.", StringComparison.Ordinal)
                      && !kv.Key.StartsWith("outputCost.", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal);

    /// <summary>把一份产出同时写进 <c>town.output.*</c>（本期）与 <c>town.stock.*</c>（库存）。</summary>
    private static void Emit(EntityId town, IStepContext ctx, DataEntry cls,
        string goodId, string goodShort, Fix gross, string why)
    {
        if (gross.IsZero) return;
        ctx.AddFix(town, F.TownOutput(goodId), gross,
            $"{cls.GetStr("title")}×{goodShort}：{gross.Text}（{why}）");
        ctx.AddFix(town, F.TownStock(goodId), gross,
            $"第 3 步 {cls.GetStr("title")} 产 {goodShort} +{gross.Text}");
    }

    /// <summary>把 (实得, 需求) 折成整数百分比，只为 Note 的一行文本；不参与任何结算。</summary>
    private static long Percent(Fix got, Fix want) => want.IsZero ? 100 : got.Raw * 100 / want.Raw;
}

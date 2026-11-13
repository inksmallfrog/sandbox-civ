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
    /// 其中 <c>η(S) = 0.6 + 0.4 × S/100</c>。状态机 multiplier：服从/抱怨=100%、怠工=50%、罢工/暴动=0%。
    ///
    /// M4 循环全部阶级 × 全部 output.* 商品（原来只处理 peasant → food）。
    /// hard cap（Cap × Facility）目前只对农民产粮生效——其他非第一产业阶级先只用 soft cap。
    /// Tech = 1.00 恒（科技卡 M6）；原料约束 outputCost.* 推切片二。
    /// </summary>
    public static void Produce(EntityId town, HookArgs args, IStepContext ctx)
    {
        Fix tech = Fix.One;                               // M6 换成科技卡倍率（1.0–2.5）

        // ---- 先清零本期所有商品的 output 字段（Produce 每回合重算）----
        foreach (var good in ctx.Data.OfType(F.TypeGood))
            ctx.Mutate(town, F.TownOutput(good.Id), Fix.Zero,
                $"第 3 步清 {good.GetStr("title")} 本期产出（上回合已 AddFix 进 stock）");

        // ---- 循环全部阶级 × 全部 output.* 键 ----
        var tiles = TownOps.TilesOf(ctx, town);
        Fix capGrain = Fix.Zero;
        foreach (var t in tiles) capGrain += ctx.GetFix(t, F.HexYieldFood, Fix.Zero);
        Fix facility = ctx.GetFix(town, F.TownFacility, Fix.One);

        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            long pop = ctx.GetInt(town, F.TownPop(cid));
            if (pop <= 0) continue;

            Fix sat = ctx.GetFix(town, F.TownSat(cid), Fix.FromInt(50));
            Fix eta = Fix.FromPercent(60) + Fix.Mul(Fix.FromPercent(40), Fix.Div(sat, Hundred));

            // 状态机罚则（GDD §4.2 状态）：服从/抱怨 100%、怠工 50%、罢工/暴动 0%
            string status = ctx.GetStr(town, F.TownStatus(cid));
            Fix statusMult = status switch
            {
                "怠工" => Fix.FromPercent(50),
                "罢工" or "暴动" => Fix.Zero,
                _ => Fix.One
            };

            // 循环 output.* 所有键，按商品短名分组累加
            var outputKeys = cls.Scalars
                .Where(kv => kv.Key.StartsWith("output.", StringComparison.Ordinal) && !kv.Key.StartsWith("outputCost.", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal);

            foreach (var kv in outputKeys)
            {
                Fix lambda = kv.Value.AsFix();
                if (lambda.IsZero) continue;

                string goodShort = kv.Key["output.".Length..];
                string goodId = "good." + goodShort;
                if (!ctx.Data.TryGet(goodId, out _)) continue;

                // soft cap：P × λ × Tech × η(S) × statusMult
                Fix soft = Fix.Mul(
                    Fix.Mul(Fix.FromInt(pop), lambda),
                    Fix.Mul(tech, Fix.Mul(eta, statusMult)));

                // hard cap 只对农民产粮生效（§5.2：第一产业受地约束）
                Fix gross;
                if (cid == "class.peasant" && goodShort == "food")
                {
                    Fix hard = Fix.Mul(capGrain, facility);
                    gross = Fix.Min(soft, hard);
                }
                else
                {
                    gross = soft;
                }

                // 累积 output 字段（多阶级产同一商品会 AddFix 累加）
                ctx.AddFix(town, F.TownOutput(goodId), gross,
                    $"{cls.GetStr("title")}×{goodShort}：{gross.Text}（§4.2 公式一，mult={statusMult.Text}）");

                // 加进 stock（M3 坑 2 已修：Pricing 第 6 步读的 stock 已包含产出）
                ctx.AddFix(town, F.TownStock(goodId), gross,
                    $"第 3 步 {cls.GetStr("title")} 产 {goodShort} +{gross.Text}");
            }
        }

        // ---- 聚合解释：公式一正文（§8.4 要求公式原样出现在解释里）----
        Fix totalFoodOut = ctx.GetFix(town, F.TownOutput("good.food"), Fix.Zero);
        if (!totalFoodOut.IsZero)
        {
            var peasant = ctx.Data.Get("class.peasant");
            long pPeasant = ctx.GetInt(town, F.TownPop("class.peasant"));
            Fix lambda = peasant.GetFix("output.food");
            Fix sat = ctx.GetFix(town, F.TownSat("class.peasant"), Fix.FromInt(50));
            Fix eta = Fix.FromPercent(60) + Fix.Mul(Fix.FromPercent(40), Fix.Div(sat, Hundred));
            Fix soft = Fix.Mul(Fix.Mul(Fix.FromInt(pPeasant), lambda), Fix.Mul(tech, eta));
            Fix hard = Fix.Mul(capGrain, facility);
            string status = ctx.GetStr(town, F.TownStatus("class.peasant"));

            ctx.Explain(town, "粮食产出（§5.2 公式一）",
                "Out = min( P × λ × Tech × η(S) , Cap × Facility )",
                [$"P（农民人口）= {pPeasant} 个百人单位 = {pPeasant * 100} 人",
                 $"λ = {lambda.Text} 粮/人/年（data/core.basic/class.json）",
                 $"Tech = {tech.Text}（科技卡 M6 接入，现在恒 1.00）",
                 $"η(S) = 0.6 + 0.4 × {sat.Text}/100 = {eta.Text}（满意的人干活多）",
                 $"状态「{status}」× statusMult = {(status switch { "怠工" => "50%", "罢工" or "暴动" => "0%", _ => "100%" })}",
                 $"⇒ 人的上限 soft = {soft.Text} 石",
                 $"Cap（辖地 ΣY）= {capGrain.Text} 石",
                 $"Facility = {facility.Text}（第 2 步折算，§4.2 封顶 1.45）",
                 $"⇒ 地的上限 hard = {hard.Text} 石"],
                $"Out = {(soft <= hard ? "人" : "地")}为紧约束 → {totalFoodOut.Text} 石");
        }
    }

    // ========== 第 10 步 consumption：口粮、满足率、仓满抛洒与损耗（§4.2 s_c、§5.4 步 6）==========

    /// <summary>
    /// M4：循环全部阶级 × 全部 consumes.* 商品（原来只处理 peasant → food）。
    /// 简化决策：ration 取 food 的 got/want（§4.2 的 s_c），没有 food 需求的阶级取第一个消耗商品的 ration。
    /// 仓满抛洒 + 损耗循环所有商品（不是只 food）。
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

        // ---- 循环全部阶级 × 全部 consumes.* 键 ----
        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            long pop = ctx.GetInt(town, F.TownPop(cid));
            if (pop <= 0)
            {
                ctx.Mutate(town, F.TownRation(cid), Fix.One, "P = 0 ⇒ 自动 1.00 满足率");
                continue;
            }

            Fix? foodGot = null, foodWant = null;  // 用于计算该阶级的 ration（简化取 food）
            Fix firstGot = Fix.Zero, firstWant = Fix.Zero;
            string? firstGoodShort = null;
            bool firstSeen = false;

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
                Fix have = ctx.GetFix(town, F.TownStock(goodId), Fix.Zero);
                Fix got = Fix.Min(have, want);

                // 扣库存
                ctx.AddFix(town, F.TownStock(goodId), -got,
                    $"第 10 步 {cls.GetStr("title")} 耗 {goodShort} −{got.Text}（需求 {want.Text}）");
                // 累积 consumed（§5.9 守恒）
                ctx.AddFix(town, F.TownConsumed(goodId), got,
                    $"§5.9 守恒：{cls.GetStr("title")} 耗 {goodShort} {got.Text}");

                // 记录第一个 seen 的商品（fallback ration）
                if (!firstSeen) { firstSeen = true; firstGot = got; firstWant = want; firstGoodShort = goodShort; }

                // 记录 food（优先用于 ration 计算）
                if (goodShort == "food") { foodGot = got; foodWant = want; }
            }

            // ration：优先 food，否则第一个消耗商品（§4.2 的 s_c = min(1, 实得/需求)）
            Fix chosenGot, chosenWant;
            if (foodGot.HasValue) { chosenGot = foodGot.Value; chosenWant = foodWant!.Value; }
            else if (firstSeen) { chosenGot = firstGot; chosenWant = firstWant; }
            else { chosenGot = Fix.Zero; chosenWant = Fix.Zero; }

            Fix ration = chosenWant.IsZero ? Fix.One : Fix.Div(chosenGot, chosenWant);
            ctx.Mutate(town, F.TownRation(cid), ration,
                $"s_c = min(1, 实得/需求)（§4.2），公式三的输入" +
                (chosenWant.IsZero ? "" : $"（{firstGoodShort ?? "?"}：{chosenGot.Text}/{chosenWant.Text}）"));

            if (ration < Fix.One)
                ctx.Note($"{ctx.GetStr(town, F.TownName)}：{cls.GetStr("title")}的{firstGoodShort ?? "口粮"}只够 {Percent(chosenGot, chosenWant)}%");
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
    /// M4 状态机 5 档（§4.2）：服从 / 抱怨 / 怠工（S<30 满 2 年产出折半）/
    /// 罢工（满 3 年产出归零）/ 暴动（满 4 年城镇倒计时）。简化决策：先用全局 riot 计数，
    /// 所有阶级共享；per-class riot 推切片二。
    /// </summary>
    public static void Satisfaction(EntityId town, HookArgs args, IStepContext ctx)
    {
        bool anyRebellious = false;
        int riot = (int)ctx.GetInt(town, F.TownRiot);  // 先读当前 riot，后面会更新

        foreach (var cls in ctx.Data.OfType(F.TypeClass))
        {
            string cid = cls.Id;
            if (ctx.GetInt(town, F.TownPop(cid)) <= 0) continue;

            Fix sat = ctx.GetFix(town, F.TownSat(cid), Fix.FromInt(50));
            Fix s = ctx.GetFix(town, F.TownRation(cid), Fix.Zero);
            Fix e = cls.GetFix("satisfaction.expects");
            Fix baseline = Fix.FromInt((int)cls.GetInt("satisfaction.baseline", 50));
            Fix premium = Fix.Mul(Fix.FromInt(60), s - e);       // 60(s−e)
            Fix star = (baseline + premium).Clamp(Fix.Zero, Hundred);

            var stream = ctx.Rng.Stream(town, $"satisfaction.{F.Bare(cid)}.T{ctx.Time.Turn}");
            Fix xi = stream.NextGaussian(Fix.Zero, Fix.FromInt(2));   // ξ ~ N(0, 2)（§4.2）
            Fix pull = Fix.Mul(Fix.FromPercent(35), star - sat);      // 0.35(S*−S)
            Fix next = (sat + pull + xi).Clamp(Fix.Zero, Hundred);

            ctx.Mutate(town, F.TownSat(cid), next,
                $"S ← S + 0.35(S*−S) + ξ（§4.2 公式三）：{sat.Text} → {next.Text}");

            // ---- 状态机 5 档（§4.2）----
            string prevStatus = ctx.GetStr(town, F.TownStatus(cid));
            ctx.Mutate(town, F.TownStatusPrev(cid), prevStatus,
                "状态机：存上回合状态（§5.9 的 monotonic 不变量要读）");

            string nextStatus;
            if (next > Fix.FromInt(55))
                nextStatus = "服从";
            else if (next >= Fix.FromInt(30))
                nextStatus = "抱怨";
            else
            {
                anyRebellious = true;
                nextStatus = riot >= 4 ? "暴动" : riot >= 3 ? "罢工" : riot >= 2 ? "怠工" : "抱怨";
            }

            ctx.Mutate(town, F.TownStatus(cid), nextStatus,
                $"§4.2 五档：{prevStatus} → {nextStatus}" +
                (next < Fix.FromInt(30) ? $"（连续 S<30 第 {riot + 1} 年）" : ""));

            ctx.Explain(town, $"{cls.GetStr("title")}满意度（§4.2 公式三）",
                "S* = 基线 + 60(s−e) − 20·税负 + 10·政策 + 10·信仰 − 15·矛盾 + 8·地位",
                [$"s（口粮满足率）= {s.Text}，e（期望）= {e.Text} ⇒ s−e = {(s - e).Text} ⇒ 60×(s−e) = {premium.Text}",
                 $"基线 = {baseline.Text}（data/core.basic/class.json 的 satisfaction.baseline）",
                 "税负 / 政策 / 信仰 / 矛盾 / 地位 = 0（M4·M5·M7·M4·M8 接入）",
                 $"⇒ S* = {star.Text}，本年 S = {sat.Text}",
                 $"收敛 0.35(S*−S) = {pull.Text}，噪声 ξ ~ N(0,2) = {xi.Text}",
                 $"状态「{nextStatus}」（{prevStatus} → {nextStatus}）"],
                $"S ← {next.Text}（clamp 0–100）");
        }

        // §4.2 的升级闸门：S<30 连续年数
        int prevRiot = riot;
        int newRiot = anyRebellious ? prevRiot + 1 : 0;
        ctx.Mutate(town, F.TownRiot, newRiot,
            newRiot == 0 ? "没有阶级在反抗线以下，倒计时清零"
            : $"连续 {newRiot} 年有阶级 S<30（§4.2 满 2/3/4 年 → 怠工/罢工/暴动）");
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
            // 把 P 先乘进去，全程只在最后截断一次：ΔP = P × 粮安 × 4 ÷ 100。
            // 其余三项（拥挤 / 疫病 / 医疗）M1 为 0，接入后同样按「先乘 P」的写法加进来。
            Fix delta = Fix.Div(Fix.Mul(Fix.Mul(Fix.FromInt(pop), 4), secure), 100);

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
                 "拥挤 / 疫病 / 医疗 = 0（M3 的市场、M7 的宗教接入）",
                 $"⇒ ΔP = {pop} × {secure.Text} × 4% = {delta.Text} 个百人单位（先乘 P 再取百分比，免得 0.00xx 落在 1/100 刻度上归零），累积零头后本年净 {applied}"],
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

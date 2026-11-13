using SimCore;

namespace CoreBasic;

/// <summary>
/// 城镇市场出清 —— 公式二（GDD §5.3 / 附录 A §2）。
///
/// 这是 M3 新增的步骤：在第 6 步 Summer、OnPriceForm hook、local 层、每城每物跑一次。
/// 它**不修改库存**，只更新价格字段；实际库存变动在第 10 步 Autumn 的 Consumption。
/// 原因是市场"先出清、后消费"（§5.4 的内循环顺序）——价格反映的是本期供需预期，
/// 真实消费行为在后面发生。
///
/// 公式（城镇内循环，α = 0.30）：
/// <code>
/// p_{t+1} = clamp( p_t × (1 + 0.30 × (D − S) / max(S, 0.5) + shock_t) , 0.2 × p0, 5 × p0 )
/// </code>
///
/// 定点纪律（BALANCE.md §2）：gap = (D−S)/max(S, 0.5) 在库存很低时可能产生很大的数，
/// 但它不先被截断——我们先算完整个括号里的 change，再乘 p_prev，最后 clamp。
/// shock = N(0, 0.04) 的 0.04 是定点百分数，别先乘成 0 再乘 p。
/// </summary>
public static class Pricing
{
    /// <summary>
    /// 第 6 步首：城镇市场出清（公式二·本地层）。
    ///
    /// 只对粮（good.food）跑价格——其余商品保持 basePrice 直到 M4（八阶级）接入后再循环。
    /// D/S 写回 town.demand.food / town.supply.food，供守恒不变量（第 10 步末）使用。
    /// </summary>
    public static void Price(EntityId town, HookArgs args, IStepContext ctx)
    {
        var good = ctx.Data.Get("good.food");
        Fix p0 = good.GetFix("basePrice");           // 4.00（GDD §5.1）
        Fix priceFloor = Fix.Mul(p0, Fix.FromPercent(20));   // 0.2 × p0 = 0.80
        Fix priceCeil = Fix.Mul(p0, Fix.FromInt(5));          // 5 × p0 = 20.00

        // ---- p_prev：上期价格，首次用 p0 ----
        Fix p_prev = ctx.GetFix(town, F.TownPrice("good.food"), p0);

        // ---- S（本期可售）= 第 6 步时的库存 ----
        // 第 3 步 Produce 已经把 output 加进 stock 了（AddFix），所以 stock 就是
        // 建城仓底 + 以前各回合结转 + 本期产出。第 6 步还没消费，所以这就是本期可售。
        Fix S = ctx.GetFix(town, F.TownStock("good.food"), Fix.Zero);
        Fix output = ctx.GetFix(town, F.TownOutput("good.food"), Fix.Zero);
        ctx.Mutate(town, F.TownSupply("good.food"), S,
            $"本期可售 S = 当前库存 {S.Text}（含本期产出 {output.Text}）");

        // ---- D（本期需求）= need × pop（预计消费）----
        // 第 6 步还没消费，所以 D 是预计值（§5.4：市场先出清，后消费）
        var peasant = ctx.Data.Get("class.peasant");
        Fix need = peasant.GetFix("consumes.food");  // 1.00 粮/人/年
        long pop = ctx.GetInt(town, F.TownPop("class.peasant"));
        Fix D = Fix.Mul(Fix.FromInt(pop), need);
        ctx.Mutate(town, F.TownDemand("good.food"), D,
            $"本期需求 D = 农民 {pop} 百人单位 × 口粮 {need.Text} 石（§5.3）");

        // ---- gap = (D − S) / max(S, 0.5) ----
        // max(S, 0.5) 是防除零的闸门，写成 max(S, Fix.FromPercent(50)) 即 max(S, 0.50)
        Fix denom = Fix.Max(S, Fix.FromPercent(50));
        Fix gap = Fix.Div(D - S, denom);

        // ---- α = 0.30（城镇内循环）----
        Fix alpha = Fix.FromPercent(30);

        // ---- shock ~ N(0, 0.04)：天气/灾害冲击 ----
        var stream = ctx.Rng.Stream(town, $"price.{F.Bare("good.food")}.T{ctx.Time.Turn}");
        Fix shock = stream.NextGaussian(Fix.Zero, Fix.FromPercent(4));

        // ---- change = 1 + α × gap + shock ----
        // 定点纪律：先把 α×gap 乘完（不会先变成 0），再加 1 和 shock
        Fix change = Fix.One + Fix.Mul(alpha, gap) + shock;

        // ---- p_new = p_prev × change，然后 clamp [priceFloor, priceCeil] ----
        Fix p_new = Fix.Mul(p_prev, change).Clamp(priceFloor, priceCeil);

        ctx.Mutate(town, F.TownPrice("good.food"), p_new,
            $"公式二：粮价 {p_prev.Text} → {p_new.Text}（§5.3）");

        // 写因果解释（§8.4：让玩家看得见「为什么变了」）
        string direction = D > S ? "供不应求" : D < S ? "供过于求" : "供需平衡";
        string hint = D > S && p_new > p_prev ? "↑" : D < S && p_new < p_prev ? "↓" : "↔";

        ctx.Explain(town, "粮价（§5.3 公式二）",
            "p_{t+1} = clamp( p_t × (1 + 0.30 × (D − S) / max(S, 0.5) + shock) , 0.2×p0, 5×p0 )",
            [$"p_t（上期价格）= {p_prev.Text} 银/石",
             $"D（本期需求）= {D.Text} 石（{direction}）",
             $"S（本期可售）= {S.Text} 石（当前库存，含本期产出 {output.Text}）",
             $"(D − S) = {(D - S).Text}，max(S, 0.5) = {denom.Text} ⇒ gap = {gap.Text}",
             $"α × gap = {Fix.Mul(alpha, gap).Text}（城镇内循环 α = 0.30）",
             $"shock ~ N(0, 0.04) = {shock.Text}（随机天气/灾害）",
             $"⇒ change = 1 + 0.30 × gap + shock = {change.Text}",
             $"clamp 区间 [{priceFloor.Text}, {priceCeil.Text}]（0.2×p0 ~ 5×p0）"],
            $"p_{{t+1}} = {p_prev.Text} × {change.Text} = {p_new.Text}，clamp 后仍在 [{priceFloor.Text}, {priceCeil.Text}] 内 {hint}");
    }
}

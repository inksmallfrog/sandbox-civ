using SimCore;

namespace CoreBasic;

/// <summary>
/// 城镇市场出清 —— 公式二（GDD §5.3 / 附录 A §2）。
///
/// 第 6 步 Summer、OnPriceForm hook、local 层、每城每物跑一次。
/// 它**不修改库存**，只更新价格字段；实际库存变动在第 10 步 Autumn 的 Consumption。
/// 原因是市场"先出清、后消费"（§5.4 的内循环顺序）——价格反映的是本期供需预期，
/// 真实消费行为在后面发生。
///
/// 公式（城镇内循环，α = 0.30）：
/// <code>
/// p_{t+1} = clamp( p_t × (1 + 0.30 × (D − S) / max(S, 0.5) + shock_t) , 0.2 × p0, 5 × p0 )
/// </code>
///
/// M4 循环全部商品（除 silver 货币不参与价格求解，§5.1）。
/// </summary>
public static class Pricing
{
    private static readonly Fix Alpha = Fix.FromPercent(30);   // α = 0.30（城镇内循环）

    /// <summary>
    /// 第 6 步首：城镇市场出清（公式二·本地层）。
    ///
    /// M4 循环所有商品（除 silver），对每种商品独立跑价格求解。
    /// D = 循环所有阶级的 consumes.* 汇总该商品的总需求（预计消费）。
    /// S = 当前库存（已包含 Produce 第 3 步 AddFix 的本期产出）。
    /// </summary>
    public static void Price(EntityId town, HookArgs args, IStepContext ctx)
    {
        foreach (var good in ctx.Data.OfType(F.TypeGood))
        {
            // silver = 货币单位，不参与价格求解（§5.1 脚注）
            if (good.Id == "good.silver") continue;

            Fix p0 = good.GetFix("basePrice");
            Fix priceFloor = Fix.Mul(p0, Fix.FromPercent(20));   // 0.2 × p0
            Fix priceCeil = Fix.Mul(p0, Fix.FromInt(5));          // 5 × p0

            string goodShort = F.Bare(good.Id);  // food / mat / goods / lux
            string title = good.GetStr("title");

            // ---- p_prev：上期价格，首次用 p0 ----
            Fix p_prev = ctx.GetFix(town, F.TownPrice(good.Id), p0);

            // ---- S（本期可售）= 第 6 步时的库存 ----
            // Produce 第 3 步已经 AddFix 进 stock，所以 stock 已包含本期产出
            Fix S = ctx.GetFix(town, F.TownStock(good.Id), Fix.Zero);
            Fix output = ctx.GetFix(town, F.TownOutput(good.Id), Fix.Zero);
            ctx.Mutate(town, F.TownSupply(good.Id), S,
                $"本期可售 S = 当前库存 {S.Text}（含本期产出 {output.Text}）");

            // ---- D（本期需求）= 循环所有阶级的 consumes.{goodShort} × pop 汇总 ----
            Fix D = Fix.Zero;
            foreach (var cls in ctx.Data.OfType(F.TypeClass))
            {
                long pop = ctx.GetInt(town, F.TownPop(cls.Id));
                if (pop <= 0) continue;
                // 注意：consumes.* 键在 JSON 里是点分路径 "consumes.food"，key 是完整的 "consumes.food"
                // 我们要找的是 consumes.{goodShort}，所以拼完整路径去 Scalars 里找
                string path = "consumes." + goodShort;
                Fix need = cls.GetFix(path, Fix.Zero);
                if (need.IsZero) continue;
                D += Fix.Mul(Fix.FromInt(pop), need);
            }
            ctx.Mutate(town, F.TownDemand(good.Id), D,
                $"本期需求 D = 全阶级汇总 {D.Text}（§5.3：市场先出清后消费，故为预计值）");

            // ---- gap = (D − S) / max(S, 0.5) ----
            Fix denom = Fix.Max(S, Fix.FromPercent(50));
            Fix gap = Fix.Div(D - S, denom);

            // ---- shock ~ N(0, 0.04)：天气/灾害冲击 ----
            var stream = ctx.Rng.Stream(town, $"price.{goodShort}.T{ctx.Time.Turn}");
            Fix shock = stream.NextGaussian(Fix.Zero, Fix.FromPercent(4));

            // ---- change = 1 + α × gap + shock ----
            Fix change = Fix.One + Fix.Mul(Alpha, gap) + shock;

            // ---- p_new = p_prev × change，然后 clamp [priceFloor, priceCeil] ----
            Fix p_new = Fix.Mul(p_prev, change).Clamp(priceFloor, priceCeil);

            ctx.Mutate(town, F.TownPrice(good.Id), p_new,
                $"公式二：{title}价 {p_prev.Text} → {p_new.Text}（§5.3）");

            // 因果解释（§8.4）
            string direction = D > S ? "供不应求" : D < S ? "供过于求" : "供需平衡";
            string hint = D > S && p_new > p_prev ? "↑" : D < S && p_new < p_prev ? "↓" : "↔";

            ctx.Explain(town, $"{title}价（§5.3 公式二）",
                "p_{t+1} = clamp( p_t × (1 + 0.30 × (D − S) / max(S, 0.5) + shock) , 0.2×p0, 5×p0 )",
                [$"p_t（上期价格）= {p_prev.Text}",
                 $"D（本期需求）= {D.Text}（{direction}）",
                 $"S（本期可售）= {S.Text}（含本期产出 {output.Text}）",
                 $"(D − S) = {(D - S).Text}，max(S, 0.5) = {denom.Text} ⇒ gap = {gap.Text}",
                 $"α × gap = {Fix.Mul(Alpha, gap).Text}",
                 $"shock ~ N(0, 0.04) = {shock.Text}",
                 $"⇒ change = 1 + 0.30 × gap + shock = {change.Text}",
                 $"clamp [{priceFloor.Text}, {priceCeil.Text}]（0.2×p0 ~ 5×p0）"],
                $"p_{{t+1}} = {p_prev.Text} × {change.Text} = {p_new.Text}，clamp 后仍在 [{priceFloor.Text}, {priceCeil.Text}] 内 {hint}");
        }
    }
}

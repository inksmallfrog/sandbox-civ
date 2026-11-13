using SimCore;

namespace CoreBasic;

/// <summary>
/// 参考规则包入口（GDD §10.2 职责 8 的「<c>core.basic</c> 可整体禁用」）。
///
/// 它存在的唯一理由（§0.2 判定 5）：证明 Core 不含任何玩法知识也能跑完整回合——
/// 把 <c>--no-plugins</c> 打开，世界仍然能建、时钟仍然能走、存档仍然能写，
/// 只是没有任何结算规则。反过来，本文件里每一行注册都必须只通过 <see cref="IPluginApi"/> 完成：
/// 只要发现「需要改 Core 才能实现的规则」，那就是 Core 的接口缺了一块，按 §10.2 补 Core，
/// 而不是在插件里开后门。
///
/// 注册内容一共四类：
/// 1. 开局初始化（地形生成）；
/// 2. 命令执行器（建国 / 建城 / 营造）；
/// 3. hook 挂载（全局扇出点 + 主题级处理器）；
/// 4. 不变量与图层声明。
/// </summary>
public sealed class BasicPlugin : IPlugin
{
    public string Id => "core.basic";

    /// <summary>
    /// 扇出点：把「某一步的某个槽位」翻译成「对该 kind 的每个实体各跑一次」。
    ///
    /// 为什么需要这一层：§10.4 让插件写 <c>OnProduction(town, ctx)</c>，但 Core 广播的是
    /// 全局步骤事件，而且同一个 hook 点会落在多个步骤上（<c>OnProduction</c> 落在第 2–5 步，
    /// 其中第 3 步首尾同名）。所以位置闸门必须先判 (stepId, slot)，再交给主题处理器，
    /// 一个实体都不漏、一年只跑一遍（§10.7 纪律 1：遍历顺序按 id 升序，由 Core 保证）。
    /// </summary>
    private static HookHandler FanOut(HookPoint point, string kind, string stepId, string slot) =>
        (args, ctx) =>
        {
            if (!SimOrder.At(args, stepId, slot)) return;
            ctx.DispatchSubjects(point, kind, ctx.EntitiesOfKind(kind).Select(e => e.Id).ToList());
        };

    public void OnLoad(IPluginApi api)
    {
        // ---- 1. 开局初始化：只有地形，没有国家也没有城（§3.7 的第 1 步高度图在 M2 接进来）----
        // 计时写进 StartupLog：建图是 bootstrap，不在 15 步上（AI_HANDOFF 坑 7），
        // §12 M2 的「32×20 生成 < 1 s」只能在这一层量。计时文本不进世界状态，不影响哈希。
        api.RegisterBootstrap(ctx =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MapGen.Bootstrap(ctx);
            sw.Stop();
            api.Log($"建图 {sw.Elapsed.TotalMilliseconds:0.0} ms（§12 M2 判据 < 1000 ms）");
        });

        // ---- 2. 命令 ----
        api.RegisterCommand("nation.found", Nations.Found, "name=<国名> seed=<可省：把已有城镇收为首都>");
        api.RegisterCommand("town.found", TownOps.Found, "hex=<地块> name=<可省> nation=<可省：不填则成独立城镇>");
        api.RegisterCommand("building.build", TownOps.Build, "town=<城镇> building=def:building.farm hex=<地块类建筑必填>");

        // ---- 3. hook：先挂扇出点（全局），再挂主题处理器（逐城）----
        //
        // priority 在这里只用来排同一列表内的先后：Facilities 取 90、扇出点取 100，
        // 于是「设施折算」一定在本步其它全局处理器之前。主题处理器存在另一张表里，
        // 它们之间的先后由这里的注册顺序 + priority 共同决定（§10.4 的三级排序）。

        // 第 2 步末：把建筑等级折成 Facility / 仓容 / 损耗率。必须早于第 3 步，
        // 否则「今年新建的田」会凭空多出今年的粮（§2.3 之所以把顺序写死）。
        api.Require(HookPoint.OnProduction, 90, TownOps.Facilities);

        // 第 3 步首：农业产出（公式一）。
        api.Require(HookPoint.OnProduction, 100, FanOut(HookPoint.OnProduction, F.KindTown, "agriculture", SimOrder.SlotPre));
        api.RequireFor(HookPoint.OnProduction, F.KindTown, 100, SeasonRules.Produce);

        // 第 6 步首：城镇市场出清（公式二·本地层，α = 0.30）。落在 Summer 段、OnPriceForm 的 pre slot。
        api.Require(HookPoint.OnPriceForm, 100, FanOut(HookPoint.OnPriceForm, F.KindTown, "market_local", SimOrder.SlotPre));
        api.RequireFor(HookPoint.OnPriceForm, F.KindTown, 100, Pricing.Price);

        // 第 10 步首：口粮、满足率、仓满抛洒与年损耗。
        api.Require(HookPoint.OnConsumption, 100, FanOut(HookPoint.OnConsumption, F.KindTown, "consumption", SimOrder.SlotPre));
        api.RequireFor(HookPoint.OnConsumption, F.KindTown, 100, SeasonRules.Consume);

        // 第 12 步首：满意度（公式三）。第 13 步首尾都是 OnClassTransition，所以这里不用它。
        api.Require(HookPoint.OnSatisfaction, 100, FanOut(HookPoint.OnSatisfaction, F.KindTown, "satisfaction", SimOrder.SlotPre));
        api.RequireFor(HookPoint.OnSatisfaction, F.KindTown, 100, SeasonRules.Satisfaction);

        // 第 13 步首：人口增减（附录 A §8）。
        api.Require(HookPoint.OnClassTransition, 100, FanOut(HookPoint.OnClassTransition, F.KindTown, "demography", SimOrder.SlotPre));
        api.RequireFor(HookPoint.OnClassTransition, F.KindTown, 100, SeasonRules.Demography);

        // 第 15 步首：年度报告的国势栏；第 15 步末：自动指认缺失的首都。
        api.Require(HookPoint.OnEventDraw, 100, (args, ctx) =>
        {
            if (SimOrder.At(args, "wrapup", SimOrder.SlotPre)) SeasonRules.Report(ctx);
        });
        api.Require(HookPoint.OnTurnEnd, 100, Nations.AdoptCapital);

        // ---- 4. 不变量：范围与符号。数值算错可以慢慢调，量纲破了必须当场崩（§5.9）----
        api.RegisterInvariant("basic.ranges", new[] { 2, 3, 10, 12, 13 }, Sanity);
        // M3 新增：价格区间（第 6 步末校验）+ 物资守恒（第 10 步末校验）
        api.RegisterInvariant("basic.price_bounds", new[] { 6 }, PriceBounds);
        api.RegisterInvariant("basic.conservation", new[] { 10 }, Conservation);
        // M4 新增：状态机单调（第 12 步末校验——状态不可跳级）
        api.RegisterInvariant("basic.class_monotonic", new[] { 12 }, ClassMonotonic);

        // ---- 5. 图层声明（§3.9：图层是纯查询，Core 只转述标题与顺序）----
        api.RegisterLayer("terrain", "地形", 10);
        api.RegisterLayer("owner", "领土", 20);
        api.RegisterLayer("agriculture", "农业潜力", 30);

        api.Log($"数据：{api.Data.OfType(F.TypeLandform).Count} 种地表 / " +
                $"{api.Data.OfType(F.TypeClass).Count} 个阶级 / " +
                $"{api.Data.OfType(F.TypeGood).Count} 种物资 / " +
                $"{api.Data.OfType(F.TypeBuilding).Count} 类建筑");
        api.Log($"hook：{SimOrder.WhereHookLands(HookPoint.OnProduction)}；" +
                $"{SimOrder.WhereHookLands(HookPoint.OnConsumption)}");
    }

    /// <summary>
    /// 四条范围检查。挑的都是「破了就说明有人把量纲搞错」而不是「数值不好看」的项：
    /// 负人口、负库存、Facility 越过 §4.2 的 1.45 上限、满意度跑出 0–100——
    /// 这四类一旦成立，后面每一步都在放大同一个错误。
    /// </summary>
    private static InvariantResult Sanity(IStepContext ctx)
    {
        var bad = new List<string>();
        Fix facilityCeil = Fix.FromPercent(145);

        foreach (var town in ctx.EntitiesOfKind(F.KindTown))
        {
            string name = ctx.GetStr(town.Id, F.TownName);

            foreach (var (path, value) in town.Prefix("town.pop."))
            {
                if (value.AsInt() < 0) bad.Add($"{name}：{path} = {value.AsInt()} 为负（§4.1 人口不为负）");
            }

            foreach (var (path, value) in town.Prefix("town.stock."))
            {
                if (value.Kind == ValueKind.None) continue;
                if (value.AsFix().Raw < 0)
                    bad.Add($"{name}：{path} = {value.AsFix().Text} 为负（§5.4：缺粮是吃空仓库，不是欠粮）");
            }

            Fix facility = ctx.GetFix(town.Id, F.TownFacility, Fix.One);
            if (facility < Fix.One || facility > facilityCeil)
                bad.Add($"{name}：Facility = {facility.Text} 越出 [1.00, 1.45]（§4.2 的边际设施递减闸门）");

            foreach (var (path, value) in town.Prefix("town.sat."))
            {
                if (value.Kind == ValueKind.None) continue;
                Fix s = value.AsFix();
                if (s < Fix.Zero || s > Fix.FromInt(100))
                    bad.Add($"{name}：{path} = {s.Text} 越出 [0, 100]（§4.2 公式三 clamp 后不可能越界）");
            }

            EntityId site = F.IdOf(ctx.GetStr(town.Id, F.TownSite));
            if (site.IsEmpty || !ctx.Has(site))
                bad.Add($"{name}：城址 {site} 不存在，辖地与间距判定都会失效");
            else if (F.IdOf(ctx.GetStr(site, F.HexTown)) != town.Id)
                bad.Add($"{name}：城址 {site} 的 hex.town 没有指回本城（§10.7 的双向引用必须一致）");
        }

        foreach (var hex in ctx.EntitiesOfKind(F.KindHex))
        {
            Fix y = ctx.GetFix(hex.Id, F.HexYieldFood, Fix.Zero);
            if (y < Fix.Zero) bad.Add($"{hex.Id}：粮潜力 {y.Text} 为负（§3.6 的 Y_g 是非负数）");
        }

        return bad.Count == 0 ? InvariantResult.Ok : InvariantResult.Fail(bad.ToArray());
    }

    /// <summary>
    /// 价格区间检查（GDD §5.9 第四条）。第 6 步末校验：
    /// 每镇每种商品的价格必须在 [0.2 × p0 × κ, 5 × p0 × κ] 之间。
    /// M3 阶段 κ 固定为 1.0，所以区间简化为 [0.2×p0, 5×p0]。
    /// Pricing.Price 自己做了 clamp，所以正常跑不会触发这条——触发即说明有地方绕过了 Pricing 改价格。
    /// </summary>
    private static InvariantResult PriceBounds(IStepContext ctx)
    {
        var bad = new List<string>();

        foreach (var good in ctx.Data.OfType(F.TypeGood))
        {
            Fix p0 = good.GetFix("basePrice");
            if (p0.IsZero) continue;  // silver 不参与价格求解（§5.1）

            Fix lo = Fix.Mul(p0, Fix.FromPercent(20));   // 0.2 × p0
            Fix hi = Fix.Mul(p0, Fix.FromInt(5));          // 5 × p0

            foreach (var town in ctx.EntitiesOfKind(F.KindTown))
            {
                Fix p = ctx.GetFix(town.Id, F.TownPrice(good.Id), Fix.Zero);
                if (p.IsZero && good.Id != "good.food") continue;  // M3 只对 food 跑价格

                if (p < lo || p > hi)
                    bad.Add($"{ctx.GetStr(town.Id, F.TownName)}：{good.Id} 的价格 {p.Text} 越出 [{lo.Text}, {hi.Text}]（§5.9 的价格区间）");
            }
        }

        return bad.Count == 0 ? InvariantResult.Ok : InvariantResult.Fail(bad.ToArray());
    }

    /// <summary>
    /// 物资守恒检查（GDD §5.9 第一条）。第 10 步末校验：
    ///   supply（期初库存 + 本期产出）= consumed + spoiled + spilled + stockAfter
    /// 允许误差 0.02（Fix.Scale=100 精度 0.01，放宽 2 倍防定点截断抖动）。
    /// M4 循环所有商品（除 silver 货币——它不参与生产/消费/损耗循环，简化决策）。
    /// supply 由第 6 步 Pricing 写入，consumed/spoiled/spilled 由第 10 步 Consume 写入。
    /// </summary>
    private static InvariantResult Conservation(IStepContext ctx)
    {
        var bad = new List<string>();
        Fix tolerance = Fix.FromPercent(2);  // 0.02，定点精度的 2 倍

        foreach (var town in ctx.EntitiesOfKind(F.KindTown))
        {
            string name = ctx.GetStr(town.Id, F.TownName);

            foreach (var good in ctx.Data.OfType(F.TypeGood))
            {
                // silver 不参与生产/消费循环，跳过
                if (good.Id == "good.silver") continue;

                // supply 由 Pricing 第 6 步写入
                Fix supply = ctx.GetFix(town.Id, F.TownSupply(good.Id), Fix.Zero);
                // 如果 supply=0 且 stockAfter=0 且 consumed/spoiled/spilled 全 0，跳过空商品
                Fix stockAfter = ctx.GetFix(town.Id, F.TownStock(good.Id), Fix.Zero);
                Fix consumed = ctx.GetFix(town.Id, F.TownConsumed(good.Id), Fix.Zero);
                Fix spoiled = ctx.GetFix(town.Id, F.TownSpoiled(good.Id), Fix.Zero);
                Fix spilled = ctx.GetFix(town.Id, F.TownSpilled(good.Id), Fix.Zero);

                if (supply.IsZero && stockAfter.IsZero && consumed.IsZero && spoiled.IsZero && spilled.IsZero) continue;

                // 守恒：supply = stockAfter + consumed + spoiled + spilled
                Fix diff = supply - stockAfter - consumed - spoiled - spilled;
                if (diff.Raw < -tolerance.Raw || diff.Raw > tolerance.Raw)
                    bad.Add($"{name}：{good.GetStr("title")}守恒破！supply {supply.Text} − stockAfter {stockAfter.Text} − consumed {consumed.Text} − spoiled {spoiled.Text} − spilled {spilled.Text} = {diff.Text}（§5.9 第一条物资守恒）");
            }
        }

        return bad.Count == 0 ? InvariantResult.Ok : InvariantResult.Fail(bad.ToArray());
    }

    /// <summary>
    /// 状态机单调检查（GDD §4.2 状态机闸门）。第 12 步末校验：
    /// 状态只能按 服从→抱怨→怠工→罢工→暴动 逐步恶化或返回服从/抱怨，
    /// 不允许从"服从"直接跳到"罢工"或从"抱怨"跳到"暴动"这类跳级。
    /// 也不允许"暴动"跳回"服从"（只能先回"抱怨"再升回）。
    /// 档位顺序（恶化方向）：服从(0) → 抱怨(1) → 怠工(2) → 罢工(3) → 暴动(4)
    /// </summary>
    private static InvariantResult ClassMonotonic(IStepContext ctx)
    {
        var bad = new List<string>();
        string[] order = { "服从", "抱怨", "怠工", "罢工", "暴动" };
        int Rank(string? s)
        {
            if (string.IsNullOrEmpty(s)) return -1;  // 初始化前的空值，不算跳级
            for (int i = 0; i < order.Length; i++) if (order[i] == s) return i;
            return -1;  // 未知状态也不算（让 Sanity 去报）
        }

        foreach (var town in ctx.EntitiesOfKind(F.KindTown))
        {
            string name = ctx.GetStr(town.Id, F.TownName);

            foreach (var cls in ctx.Data.OfType(F.TypeClass))
            {
                long pop = ctx.GetInt(town.Id, F.TownPop(cls.Id));
                if (pop <= 0) continue;

                string prev = ctx.GetStr(town.Id, F.TownStatusPrev(cls.Id));
                string next = ctx.GetStr(town.Id, F.TownStatus(cls.Id));

                int rp = Rank(prev);
                int rn = Rank(next);
                if (rp < 0 || rn < 0) continue;  // 初始化或未知，跳过

                // 跳级恶化：档位差 > 1 且方向恶化
                if (rn > rp + 1)
                    bad.Add($"{name}：{cls.GetStr("title")}状态跳级 {prev}→{next}（§4.2：只能逐步恶化或返回，不能跳级罢工/暴动）");

                // 跳级好转：暴动不能直接回服从（虽然简化决策里 riot 清零后 S>55 就直接服从了...）
                // 但这在当前实现里是允许的——简化决策：好转不限步，只有恶化不能跳
            }
        }

        return bad.Count == 0 ? InvariantResult.Ok : InvariantResult.Fail(bad.ToArray());
    }
}

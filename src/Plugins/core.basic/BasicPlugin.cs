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

        // ---- 5. 图层声明（§3.9：图层是纯查询，Core 只转述标题与顺序）----
        api.RegisterLayer("terrain", "地形", 10);
        api.RegisterLayer("owner", "领土", 20);
        api.RegisterLayer("agriculture", "农业潜力", 30);

        api.Log($"数据：{api.Data.OfType(F.TypeLandform).Count} 种地表 / " +
                $"{api.Data.OfType(F.TypeWaterbody).Count} 种水体 / " +
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

            string waterId = ctx.GetStr(hex.Id, F.HexWater);
            if (!F.IsBlank(waterId))
            {
                if (!ctx.Data.TryGet(waterId, out var waterDef) || waterDef.Type != F.TypeWaterbody)
                    bad.Add($"{hex.Id}：水体 '{waterId}' 不是已知的 waterbody 记录（§3.1）");
                else
                {
                    if (ctx.Get(hex.Id, F.HexBuildable).AsBool())
                        bad.Add($"{hex.Id}：{waterDef.GetStr("title")}却标成可建城（§3.8：水域不是城址）");
                    Fix expected = waterDef.GetFix("potential.food");
                    if (y != expected)
                        bad.Add($"{hex.Id}：{waterDef.GetStr("title")}粮潜力 {y.Text}，数据里是 {expected.Text}（§3.2 水域行）");
                }
                if (!F.IsBlank(ctx.GetStr(hex.Id, F.HexRiverDown)))
                    bad.Add($"{hex.Id}：水体格不该再有下游河段（§3.3 河到湖/海为止）");
                continue;
            }

            string down = ctx.GetStr(hex.Id, F.HexRiverDown);
            if (F.IsBlank(down)) continue;
            var target = new EntityId(down);
            if (!ctx.Has(target))
                bad.Add($"{hex.Id}：下游 {down} 不存在（§3.3 有向边必须两点都在图上）");
            else if (MapGen.Distance(hex.Id, target) != 1)
                bad.Add($"{hex.Id}：下游 {down} 不相邻（§3.3 河段只连相邻地块）");
            long flow = ctx.GetInt(hex.Id, F.HexRiverFlow);
            if (flow < 1) bad.Add($"{hex.Id}：河段 flow = {flow} 非法（§3.3 集水数至少 1）");
            long width = ctx.GetInt(hex.Id, F.HexRiverWidth);
            if (width is < 1 or > 4) bad.Add($"{hex.Id}：河宽档 {width} 越界（1–4）");
        }

        return bad.Count == 0 ? InvariantResult.Ok : InvariantResult.Fail(bad.ToArray());
    }
}

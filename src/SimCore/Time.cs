namespace SimCore;

/// <summary>
/// 回合结构、结算顺序与全局标识（GDD §10.2 职责 2 与 §11.2 的实体命名）。
///
/// 本文件里没有任何玩法数值——Core 只负责「什么时候结算」，不负责「结算出什么」。
/// <see cref="SimOrder.Steps"/> 就是 GDD §2.3 那张表的代码化身：顺序固定、不可插队，
/// 插件只能往这些点上挂 hook（§10.4）。顺序一变，同年自噬的反馈环就会重新出现。
/// </summary>
public enum Season
{
    /// <summary>意图段：玩家与 AI 下命令，尚未产生后果。</summary>
    Intent = 0,
    Spring = 1,
    Summer = 2,
    Autumn = 3,
    Winter = 4,
}

/// <summary>
/// 模拟时钟。1 回合 = 1 年（GDD §2.1），四季只在结算内部推进，不作为玩家可等的粒度。
/// 这个类型是值语义的小结构体，复制一份传给插件上下文，插件改不到全局时钟。
/// </summary>
public sealed class SimTime
{
    public int Turn { get; set; }
    public Season Season { get; set; } = Season.Intent;

    /// <summary>公元元年对应的起始年，用于把回合号翻成人读的年份。</summary>
    public int YearBase { get; init; } = 1;

    public int Year => YearBase + Turn - 1;
    public string SeasonText => Season switch
    {
        Season.Intent => "意图",
        Season.Spring => "春",
        Season.Summer => "夏",
        Season.Autumn => "秋",
        Season.Winter => "冬",
        _ => "?"
    };

    public string Text => $"第 {Turn} 回合（公元 {Year} 年）· {SeasonText}";

    internal SimTime Clone() => new() { Turn = Turn, Season = Season, YearBase = YearBase };
}

/// <summary>
/// 一个结算步骤的定义。<see cref="Pre"/> / <see cref="Post"/> 是这一步首尾广播的 hook 点，
/// 由 <see cref="SimOrder.Steps"/> 逐条指定（GDD §2.3 的 Pre/Post 列）。
/// </summary>
public sealed record SimStep(
    int Index,
    string Id,
    string Title,
    Season Season,
    HookPoint Pre,
    HookPoint Post,
    string Layer = "")
{
    public string Label => $"{Index:D2} {Title}（{SeasonText}）";

    private string SeasonText => Season switch
    {
        Season.Intent => "意图",
        Season.Spring => "春",
        Season.Summer => "夏",
        Season.Autumn => "秋",
        Season.Winter => "冬",
        _ => "?"
    };
}

/// <summary>§2.3 的十五步。索引从 1 开始，与文档编号一致。</summary>
public static class SimOrder
{
    /// <summary>
    /// 步骤序列。这一张表就是「规则书的目录」：
    /// 第 15 行必须等于 GDD §2.3 的第 15 步，改顺序等于改物理定律，要走 CHANGELOG-DESIGN 裁决。
    /// <c>Pre</c> / <c>Post</c> 标注该步首尾广播哪些 hook 点（一个点可以同时是某步的 Pre 和另一段的 Post）。
    /// </summary>
    public static readonly IReadOnlyList<SimStep> Steps = new List<SimStep>
    {
        new(1,  "apply_commands",  "应用已批准的命令与政策（扣 AP）", Season.Intent, HookPoint.OnTurnStart, HookPoint.OnActionValidate),
        new(2,  "buildings",       "建筑与设施生效/完工",             Season.Spring,  HookPoint.OnTurnStart, HookPoint.OnProduction),
        new(3,  "agriculture",     "农业产出（春播秋收的净效果）",     Season.Spring,  HookPoint.OnProduction, HookPoint.OnProduction, "agriculture"),
        new(4,  "industry",        "工业生产（含原料约束）",           Season.Spring,  HookPoint.OnProduction, HookPoint.OnProduction, "industry"),
        new(5,  "services",        "服务与非物质产出",                Season.Spring,  HookPoint.OnProduction, HookPoint.OnProduction, "services"),
        new(6,  "market_local",    "城镇市场出清（公式二·本地层）",   Season.Summer,  HookPoint.OnPriceForm, HookPoint.OnConsumption, "local"),
        new(7,  "trade_domestic",  "国内贸易（运输成本加权）",         Season.Summer,  HookPoint.OnPriceForm, HookPoint.OnMigration, "domestic"),
        new(8,  "trade_foreign",   "国际贸易（关税、关系、封锁）",     Season.Summer,  HookPoint.OnPriceForm, HookPoint.OnDiplomaticAction, "foreign"),
        new(9,  "payout",          "工资、利润、俸禄、军饷发放",       Season.Autumn,  HookPoint.OnAllocate, HookPoint.OnAllocate),
        new(10, "consumption",     "阶级消费与需求满足率",            Season.Autumn,  HookPoint.OnConsumption, HookPoint.OnConsumption),
        new(11, "treasury",        "税收与财政结算（含腐败漏损）",     Season.Autumn,  HookPoint.OnAllocate, HookPoint.OnAllocate),
        new(12, "satisfaction",    "满意度更新与阶级状态迁移（公式三）", Season.Winter, HookPoint.OnSatisfaction, HookPoint.OnClassTransition),
        new(13, "demography",      "人口增长、阶级转化、城镇间迁移",   Season.Winter,  HookPoint.OnClassTransition, HookPoint.OnMigration),
        new(14, "military",        "军事结算（补给、战斗、占领）",     Season.Winter,  HookPoint.OnLogisticsCheck, HookPoint.OnBattleResolve),
        new(15, "wrapup",          "外交、名人诉求、事件抽取、年度报告、快照", Season.Winter, HookPoint.OnEventDraw, HookPoint.OnTurnEnd),
    };

    /// <summary>某个季节包含哪些步骤，无头报告的分组输出用。</summary>
    public static IReadOnlyList<SimStep> OfSeason(Season s) => Steps.Where(st => st.Season == s).ToList();

    public static SimStep ById(string id) =>
        Steps.FirstOrDefault(s => s.Id == id) ?? throw new SimStateException($"结算步骤不存在：{id}");

    /// <summary>
    /// 把 hook 点翻译成人读的「挂在哪一步」（§10.4 的点位于 SIM_ORDER.md 有完整表）。
    /// 这张表同时是 PLUGIN_API.md 的生成源：插件作者看得到自己的处理器落在哪，才知道先后。
    /// </summary>
    public static string WhereHookLands(HookPoint p) => p switch
    {
        HookPoint.OnActionValidate => "第 1 步（逐条命令）",
        HookPoint.OnProduction => "第 2–5 步",
        HookPoint.OnPriceForm => "第 6–8 步",
        HookPoint.OnConsumption => "第 6、10 步",
        HookPoint.OnAllocate => "第 9、11 步",
        HookPoint.OnMigration => "第 7、13 步",
        HookPoint.OnSatisfaction or HookPoint.OnClassTransition => "第 12–13 步",
        HookPoint.OnLogisticsCheck or HookPoint.OnBattleResolve => "第 14 步",
        HookPoint.OnEventDraw or HookPoint.OnEventApply => "第 15 步",
        HookPoint.OnHeroRoll or HookPoint.OnAppointment or HookPoint.OnDemand => "意图段（命令执行器内）",
        HookPoint.OnDiplomaticAction => "第 8、15 步",
        HookPoint.OnTurnStart => "第 1–2 步首",
        HookPoint.OnTurnEnd => "第 15 步末",
        HookPoint.OnBeforeSave => "写档前",
        HookPoint.OnLoad => "读档后",
        HookPoint.OnMigrate => "迁移链中",
        _ => "—"
    };

    // ---- 下面三个是「规则书 §2.3 的机器可读版」，SIM_ORDER.md 由它们生成，不许手写第二份 ----

    /// <summary>步骤号 → 该步首尾广播的点。第 3 步特殊：首尾都是 OnProduction，靠 <see cref="SlotKey"/> 区分。</summary>
    public static (HookPoint Pre, HookPoint Post) PointsOf(int stepIndex)
    {
        var s = stepIndex is >= 1 and <= 15 ? Steps[stepIndex - 1]
            : throw new SimStateException($"步骤号越界：{stepIndex}");
        return (s.Pre, s.Post);
    }

    /// <summary>hook 参数里表示「步首/步末」的键名，插件用它分辨同一步的两次广播。</summary>
    public const string SlotKey = "slot";
    public const string SlotPre = "pre";
    public const string SlotPost = "post";

    // ---- 位置门闸：插件分辨「这次广播是哪一步的哪一段」的唯一手段（§2.3 第 3 步首尾同名）----

    /// <summary>广播参数是否恰好落在某步的某一段。<c>slot</c> 取 <see cref="SlotPre"/> 或 <see cref="SlotPost"/>。</summary>
    public static bool At(HookArgs args, string stepId, string slot) =>
        args.GetStr("stepId") == stepId && args.GetStr(SlotKey) == slot;

    /// <summary>广播是否落在某一步（不分首尾）。同一步首尾都挂同一个点时，别用它——会跑两遍。</summary>
    public static bool InStep(HookArgs args, string stepId) => args.GetStr("stepId") == stepId;

    /// <summary>人读的结算顺序表（SIM_ORDER.md 的生成源）。</summary>
    public static IReadOnlyList<string> Describe() =>
        Steps.Select(s => $"{s.Index:D2} {s.Title} | {s.Season} | pre={s.Pre} | post={s.Post} | layer={(s.Layer == "" ? "—" : s.Layer)}").ToList();

    /// <summary>某个 hook 点实际会落在哪些步骤上，PLUGIN_API.md 的表由它生成。</summary>
    public static IReadOnlyList<string> DescribeHookMap() =>
        Enum.GetValues<HookPoint>()
            .Where(p => p != HookPoint.Count)
            .Select(p =>
            {
                var hits = Steps.Where(s => s.Pre == p || s.Post == p)
                    .Select(s => $"{s.Index:D2}{(s.Pre == p ? "pre" : "")}{(s.Post == p ? "post" : "")}")
                    .ToList();
                return $"{p} → {(hits.Count == 0 ? "不落在任何步骤（意图段 / 存档期 / 命令执行器内）" : string.Join(", ", hits))}";
            })
            .ToList();
}

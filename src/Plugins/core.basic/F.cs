using SimCore;

namespace CoreBasic;

/// <summary>
/// 字段路径常量（GDD 附录 D 的命名规范在这里落成代码）。
///
/// 两条约定，DATA_SCHEMA.md 里有完整说明：
/// 1. 路径形如 <c>实体种类.组件.字段</c>，一律小写点分；
/// 2. 引用数据包 id 时<b>去掉类型前缀</b>——所以农民人口是 <c>town.pop.peasant</c>
///    而不是 <c>town.pop.class.peasant</c>。短一格的收益是 <c>Prefix("town.pop.")</c>
///    能一次取到全部阶级，UI 也不必再拼一遍前缀；代价是数据包 id 的短名必须全局唯一
///    （<c>class.peasant</c> 与 <c>good.peasant</c> 这种撞名要在数据校验那一关就炸掉）。
/// </summary>
public static class F
{
    // ---- 实体种类（附录 D：nation / town / hero / unit / hex）----
    public const string KindHex = "hex";
    public const string KindTown = "town";
    public const string KindNation = "nation";

    // ---- 数据包类型（type 字段，见 data/_schema.json）----
    public const string TypeLandform = "landform";
    public const string TypeWaterbody = "waterbody";
    public const string TypeShape = "shape";
    public const string TypeResource = "resource";
    public const string TypeClass = "class";
    public const string TypeGood = "good";
    public const string TypeBuilding = "building";

    // ---- 全局标量（寄居在 world.0000 上）----
    public const string ConfigMapColumns = "config.mapColumns"; // int：scenario 包可以在 bootstrap 前改写它
    public const string ConfigMapRows = "config.mapRows";

    // ---- 地块 ----
    public const string HexLandform = "hex.landform";    // str: landform.*
    public const string HexQ = "hex.q";                   // int: 轴坐标（§3.1），与 id 互为冗余，读档后校验
    public const string HexR = "hex.r";
    public const string HexHeight = "hex.height";         // int 0–100：§3.7 第 1 步的高度图留痕，形胜识别要读
    public const string HexTemp = "hex.temp";             // int 0–100：§3.7 第 3 步温度（纬度梯度 + 噪声）
    public const string HexRain = "hex.rain";             // int 0–100：§3.7 第 3 步降水（两层值噪声）
    public const string HexMoveCost = "hex.moveCost";      // int: §3.6 地表通行成本
    public const string HexDefense = "hex.defense";        // int: §3.6 地表防御
    public const string HexBuildable = "hex.buildable";    // bool: 可否建城（§3.2；水域恒为 false）
    public const string HexWater = "hex.water";            // str: waterbody.*（湖/近海），空 = 陆地（§3.1 水体层）
    public const string HexShape = "hex.shape";            // str: shape.*（§3.4 形胜层），空 = 无形胜
    public const string HexResource = "hex.resource";      // str: resource.*（§3.5 门槛资源），空 = 无资源
    public const string HexRiverDown = "hex.river.down";   // str: 下游地块 id，空 = 无河段（§3.3 有向边 from→to）
    public const string HexRiverFlow = "hex.river.flow";   // int: 本格集水数（含自身），§3.3 的 flow
    public const string HexRiverWidth = "hex.river.width"; // int 1–4：河宽档，渲染与后续河运/渡口读它
    public const string HexYieldFood = "hex.yield.food";   // fix: Y_g 粮潜力（§3.6 第一行）
    public const string HexYieldMat = "hex.yield.mat";     // fix: Y_g 料潜力（§3.6 第一行的另一个 g）
    public const string HexOwner = "hex.owner";            // str: 辖于哪个城镇，空 = 无主
    public const string HexTown = "hex.town";              // str: 城址格才有值
    public static string HexBuilding(string buildingDefId) => "hex.building." + Bare(buildingDefId);

    // ---- 城镇（§4.1 的数据模型）----
    public const string TownName = "town.name";
    public const string TownOwner = "town.owner";              // str: nation.*，空 = 独立城镇（§4.1 owner 可为 null）
    public const string TownSovereignty = "town.sovereignty";   // core/province/autonomous/vassal/occupied/independent
    public const string TownTier = "town.tier";                 // village/town/city/metropolis
    public const string TownFounded = "town.founded";            // int: 建城回合
    public const string TownSite = "town.site";                  // str: 城址地块 id。辖地半径、间距、寻路都从它算，不必回扫全图
    public const string TownTiles = "town.tiles";                // int: 辖地格数（派生量，缓存给 UI 直读）
    public const string TownFacility = "town.facility";          // fix: Facility_c，第 2 步算好给第 3 步读
    public const string TownRiot = "town.riot";                  // int: 连续 S<30 的年数（§4.2 的升级闸门）

    public static string TownPop(string classDefId) => "town.pop." + Bare(classDefId);
    public static string TownSat(string classDefId) => "town.sat." + Bare(classDefId);
    public static string TownRation(string classDefId) => "town.ration." + Bare(classDefId);
    public static string TownGrowth(string classDefId) => "town.growth." + Bare(classDefId);
    public static string TownStatus(string classDefId) => "town.status." + Bare(classDefId);
    public static string TownStatusPrev(string classDefId) => "town.statusPrev." + Bare(classDefId);
    public static string TownWealth(string classDefId) => "town.wealth." + Bare(classDefId);
    public static string TownBuilding(string buildingDefId) => "town.building." + Bare(buildingDefId);
    public static string TownStock(string goodDefId) => "town.stock." + Bare(goodDefId);
    public static string TownStockCap(string goodDefId) => "town.stockCap." + Bare(goodDefId);
    public static string TownSpoilage(string goodDefId) => "town.spoilage." + Bare(goodDefId);
    public static string TownOutput(string goodDefId) => "town.output." + Bare(goodDefId);
    public static string TownPrice(string goodDefId) => "town.price." + Bare(goodDefId);
    public static string TownDemand(string goodDefId) => "town.demand." + Bare(goodDefId);
    public static string TownSupply(string goodDefId) => "town.supply." + Bare(goodDefId);
    public static string TownConsumed(string goodDefId) => "town.consumed." + Bare(goodDefId);
    public static string TownSpoiled(string goodDefId) => "town.spoiled." + Bare(goodDefId);
    public static string TownSpilled(string goodDefId) => "town.spilled." + Bare(goodDefId);

    // ---- 国家 ----
    public const string NationName = "nation.name";
    public const string NationPlayer = "nation.player";          // bool：本机是否直接扮演它
    public const string NationCapital = "nation.capital";
    public const string NationStockFood = "nation.stock.food";

    // ---- 第 15 步写的年度报告（壳层首页直读；Simulation.AdvanceTurn 每回合先清掉上一年的）----
    public const string ReportTowns = "report.towns";
    public const string ReportPop = "report.pop";
    public const string ReportFoodOut = "report.food.out";
    public const string ReportFoodEat = "report.food.eat";
    public const string ReportFoodRot = "report.food.rot";
    public const string ReportStock = "report.food.stock";
    public const string ReportMinSat = "report.minSat";
    public const string ReportUnrest = "report.unrest";
    public const string ReportFoodPrice = "report.food.price";

    /// <summary>去掉数据包 id 的类型前缀：<c>class.peasant</c> → <c>peasant</c>。</summary>
    public static string Bare(string defId)
    {
        int dot = defId.IndexOf('.');
        return dot < 0 ? defId : defId[(dot + 1)..];
    }

    /// <summary><c>def:building.farm</c> → <c>building.farm</c>；命令参数允许两种写法。</summary>
    public static string BareId(string raw) =>
        raw.StartsWith(DataRegistry.DefPrefix, StringComparison.Ordinal)
            ? raw[DataRegistry.DefPrefix.Length..] : raw;

    public static bool IsBlank(string s) => string.IsNullOrEmpty(s);

    /// <summary>SimValue → EntityId。空串与非字符串都得到 <see cref="EntityId"/> 的空值。</summary>
    public static EntityId Id(SimValue v) =>
        v.Kind == ValueKind.Str && !string.IsNullOrEmpty(v.Text) ? new EntityId(v.Text) : default;

    /// <summary>字符串字段 → EntityId，空串得到空 id（用来和 <see cref="Id"/> 对称地比较）。</summary>
    public static EntityId IdOf(string? value) =>
        string.IsNullOrEmpty(value) ? default : new EntityId(value);

    public static bool IsEmpty(EntityId id) => id.IsEmpty;
}

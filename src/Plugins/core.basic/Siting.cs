using SimCore;

namespace CoreBasic;

/// <summary>
/// 选址（GDD §6.6 的「在哪建城」与 §3.4 形胜的最小前身）。
///
/// 存在的理由：命令必须能被<b>与种子无关</b>地写出来。无头标定、测试脚本、AI 托管
/// 都要能说「开一座城」而不必先知道这局哪格是平原；否则同一段脚本换个种子就全废。
/// 所以 <c>town.found</c> 的 hex 允许写 <c>auto</c>，由这里按下面这条明说的规则选：
///
/// <code>
/// score(候选城址) = Σ 半径 2 内无主格的 Y_g 粮潜力 × 100 − 已开发格数
/// </code>
///
/// 平票取 hex id 的 ordinal 最小者（§10.7 纪律 1）。这条规则是 M1 的简化版：
/// 没有形胜（§3.4）、没有资源点（§3.5）、没有河流夹角（§3.3），M2 补齐时
/// 只改这个函数，命令面与脚本都不用动。
/// </summary>
public static class Siting
{
    public const string Auto = "auto";

    public static bool IsAuto(EntityId id) =>
        !id.IsEmpty && string.Equals(id.Value, Auto, StringComparison.Ordinal);

    /// <summary>
    /// 找一座可建城的地块。返回空 id 表示全图没有合法城址——
    /// 调用方把它翻译成一句人话的驳回理由，而不是默默建在 (0,0)。
    /// </summary>
    public static EntityId BestTownSite(IStepContext ctx, out string why)
    {
        why = "";
        var occupied = new List<EntityId>();
        foreach (var t in ctx.EntitiesOfKind(F.KindTown))
        {
            EntityId site = F.IdOf(ctx.GetStr(t.Id, F.TownSite));
            if (!site.IsEmpty) occupied.Add(site);
        }

        (long score, string id, EntityId hex)? best = null;

        foreach (var hex in ctx.EntitiesOfKind(F.KindHex))
        {
            if (!ctx.Get(hex.Id, F.HexBuildable).AsBool()) continue;         // §3.2：山地不可建城
            if (!F.IsBlank(ctx.GetStr(hex.Id, F.HexOwner))) continue;       // 已有主的格不做城址
            var (hq, hr) = MapGen.Coords(hex.Id);
            bool tooClose = false;
            foreach (var o in occupied)
            {
                var (q, r) = MapGen.Coords(o);
                if (MapGen.Distance(q - hq, r - hr) < TownOps.MinTownSpacing) { tooClose = true; break; }
            }
            if (tooClose) continue;

            long score = ScoreSite(ctx, hq, hr);
            string id = hex.Id.Value;
            if (best is null || score > best.Value.score ||
                (score == best.Value.score && string.CompareOrdinal(id, best.Value.id) < 0))
                best = (score, id, hex.Id);
        }

        if (best is null)
        {
            why = $"半径内已无满足 {TownOps.MinTownSpacing} 格间距的可建城地块";
            return default;
        }
        why = $"score = {best.Value.score / 100.0:0.00}（辖地粮潜力和，见 Siting 的公式）";
        return best.Value.hex;
    }

    /// <summary>候选城址的评分：半径 2 内全部无主格的 Y_g 之和（定点放大 100 倍取整，避免浮点比较）。</summary>
    private static long ScoreSite(IStepContext ctx, int hq, int hr)
    {
        long sumRaw = 0;
        foreach (var hex in ctx.EntitiesOfKind(F.KindHex))
        {
            var (q, r) = MapGen.Coords(hex.Id);
            int d = MapGen.Distance(q - hq, r - hr);
            if (d > TownOps.ClaimRadius) continue;
            if (!F.IsBlank(ctx.GetStr(hex.Id, F.HexOwner))) continue;       // 别人的辖地不计入
            sumRaw += ctx.GetFix(hex.Id, F.HexYieldFood, Fix.Zero).Raw;
        }
        return sumRaw;
    }

    /// <summary>
    /// 给地块类建筑挑一格辖地：<c>Y_g</c> 最高、且该建筑尚未盖满的那格。
    /// 平票按 id ordinal 最小（同一座城两次点同一栋田，会长在同一格上，这是可预期的）。
    /// </summary>
    public static EntityId BestBuildingHex(IStepContext ctx, EntityId town, string buildingDefId)
    {
        var def = ctx.Data.Get(buildingDefId);
        int max = (int)def.GetInt("maxLevel", 1);
        (long y, string id, EntityId hex)? best = null;

        foreach (var tile in TownOps.TilesOf(ctx, town))
        {
            if (ctx.GetInt(tile, F.HexBuilding(def.Id)) >= max) continue;
            long y = ctx.GetFix(tile, F.HexYieldFood, Fix.Zero).Raw;
            string id = tile.Value;
            if (best is null || y > best.Value.y ||
                (y == best.Value.y && string.CompareOrdinal(id, best.Value.id) < 0))
                best = (y, id, tile);
        }
        return best is null ? default : best.Value.hex;
    }
}

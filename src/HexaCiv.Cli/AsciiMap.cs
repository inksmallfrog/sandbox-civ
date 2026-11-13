using System.Text;
using CoreBasic;
using SimCore;

namespace HexaCiv.Cli;

/// <summary>
/// ASCII 地图（§3.1 轴坐标的人读视图，§3.9 的 32 × 20 矩形）。
///
/// 它的价值不在好看，而在「一眼看出种子有没有长出合理的图」：
/// 边缘高地有没有围住中央低地、可建城格够不够、城是不是挤在一起。
/// Godot 壳层负责真正的渲染，这个版本是给无头调试、20 张图验收与文档截图用的。
///
/// 四张图固定输出：主图（按 <c>--layer</c> 选地形/领土/农业/资源/人口）、形胜分布、资源分布。
/// 形胜与资源用独立小图而不是叠在主图上，因为两套字母会撞（平原 P 与隘口 P、森林 F 与冲积扇 F）。
/// </summary>
public static class AsciiMap
{
    public static string Render(Simulation sim, string layer = "terrain")
    {
        int columns = (int)sim.World.GetInt(World.GlobalId, "meta.mapColumns", MapGen.MapColumns);
        int rows = (int)sim.World.GetInt(World.GlobalId, "meta.mapRows", MapGen.MapRows);
        var world = sim.World;
        string layerTitle = sim.Layers.FirstOrDefault(l => l.Id == layer)?.Title ?? layer;

        var sb = new StringBuilder();
        sb.AppendLine($"地图 {columns} × {rows}（§3.9；每行左边是 r，顶部一行是个位列，重复 0–9）");
        sb.AppendLine($"主图图层：{layerTitle}（{layer}）");
        AppendGrid(sb, columns, rows, (col, row) =>
        {
            var ent = world.Get(MapGen.HexIdAt(col, row));
            return ent is null ? "·" : LayerGlyph(sim, ent, layer);
        });

        sb.AppendLine();
        sb.AppendLine("形胜分布（§3.4；.=无形胜，~=水域）");
        AppendGrid(sb, columns, rows, (col, row) =>
        {
            var ent = world.Get(MapGen.HexIdAt(col, row));
            if (ent is null) return "·";
            if (!string.IsNullOrEmpty(ent.GetStr(F.HexTown))) return "@";
            string shape = ent.GetStr(F.HexShape);
            if (shape.Length > 0 && sim.Data.TryGet(shape, out var def)) return def.GetStr("glyph", "?");
            if (!string.IsNullOrEmpty(ent.GetStr(F.HexWater))) return "~";
            return ".";
        });

        sb.AppendLine();
        sb.AppendLine("门槛资源分布（§3.5；.=无资源，~=水域）");
        AppendGrid(sb, columns, rows, (col, row) =>
        {
            var ent = world.Get(MapGen.HexIdAt(col, row));
            if (ent is null) return "·";
            if (!string.IsNullOrEmpty(ent.GetStr(F.HexTown))) return "@";
            string resource = ent.GetStr(F.HexResource);
            if (resource.Length > 0 && sim.Data.TryGet(resource, out var def)) return def.GetStr("glyph", "?");
            if (!string.IsNullOrEmpty(ent.GetStr(F.HexWater))) return "~";
            return ".";
        });

        sb.AppendLine();
        sb.AppendLine("图例：主图大写=无主格，小写=已入辖地，@=城址，~=水域（湖/近海），+=河段（§3.3 有向边）；字母取自地表 id 短名首字母");
        foreach (var lf in sim.Data.OfType(F.TypeLandform))
            sb.AppendLine($"  {Initial(lf.Id)} = {lf.GetStr("title")}（Y粮 {lf.GetFix("potential.food").Text}，可建城 {(lf.Get("buildable").AsBool() ? "是" : "否")}）");
        foreach (var wb in sim.Data.OfType(F.TypeWaterbody))
            sb.AppendLine($"  ~ = {wb.GetStr("title")}（Y粮 {wb.GetFix("potential.food").Text}，不可建城）");
        foreach (var sh in sim.Data.OfType(F.TypeShape))
            sb.AppendLine($"  {sh.GetStr("glyph")} = {sh.GetStr("title")}（§3.4）");
        foreach (var rs in sim.Data.OfType(F.TypeResource))
            sb.AppendLine($"  {rs.GetStr("glyph")} = {rs.GetStr("title")}（§3.5）");

        var tally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var waterTally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var shapeTally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var resourceTally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int rivers = 0;
        foreach (var h in world.OfKind(F.KindHex))
        {
            string key = h.GetStr(F.HexLandform);
            tally[key] = tally.GetValueOrDefault(key) + 1;
            string water = h.GetStr(F.HexWater);
            if (water.Length > 0) waterTally[water] = waterTally.GetValueOrDefault(water) + 1;
            string shape = h.GetStr(F.HexShape);
            if (shape.Length > 0) shapeTally[shape] = shapeTally.GetValueOrDefault(shape) + 1;
            string resource = h.GetStr(F.HexResource);
            if (resource.Length > 0) resourceTally[resource] = resourceTally.GetValueOrDefault(resource) + 1;
            if (h.GetStr(F.HexRiverDown).Length > 0) rivers++;
        }
        sb.AppendLine();
        sb.AppendLine($"共 {world.OfKind(F.KindHex).Count} 格：" +
                      string.Join("、", tally.Select(kv => $"{sim.Data.Get(kv.Key).GetStr("title")} {kv.Value}")));
        sb.AppendLine($"水域 " + string.Join("、", waterTally.Select(kv =>
                          $"{sim.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格")) +
                      $"；河段 {rivers} 段（§3.3）");
        sb.AppendLine($"形胜 " + string.Join("、", shapeTally.Select(kv =>
                          $"{sim.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格")) +
                      $"（§3.4；覆盖 {shapeTally.Values.Sum()} 格）");
        sb.AppendLine($"资源 " + string.Join("、", resourceTally.Select(kv =>
                          $"{sim.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格")) +
                      $"（§3.5；覆盖 {resourceTally.Values.Sum()} 格）");
        return sb.ToString();
    }

    private static void AppendGrid(StringBuilder sb, int columns, int rows, Func<int, int, string> glyph)
    {
        sb.AppendLine("     " + string.Concat(Enumerable.Range(0, columns).Select(c => $"{(char)('0' + c % 10)} ")));
        for (int row = 0; row < rows; row++)
        {
            var cells = new StringBuilder();
            for (int col = 0; col < columns; col++) cells.Append(glyph(col, row)).Append(' ');
            sb.AppendLine($"r{row:00} |{cells}|");
        }
    }

    /// <summary>主图一格：壳层只读字段，不判规则。</summary>
    private static string LayerGlyph(Simulation sim, Entity hex, string layer)
    {
        string land = hex.GetStr(F.HexLandform);
        string glyph = Initial(land);
        bool water = hex.GetStr(F.HexWater).Length > 0;
        bool town = hex.GetStr(F.HexTown).Length > 0;

        switch (layer)
        {
            case "owner":
                if (town) return "@";
                if (water) return "~";
                return hex.GetStr(F.HexOwner).Length > 0 ? glyph.ToLowerInvariant() : ".";

            case "agriculture":
                if (town) return "@";
                if (water) return "~";
                int y = hex.GetFix(F.HexYieldFood, Fix.Zero).FloorToInt();
                return y >= 10 ? "*" : ((char)('0' + Math.Max(0, y))).ToString();

            case "resource":
                if (town) return "@";
                string resource = hex.GetStr(F.HexResource);
                if (resource.Length > 0 && sim.Data.TryGet(resource, out var rs)) return rs.GetStr("glyph", "?");
                return water ? "~" : ".";

            case "population":
                if (town) return "@";
                if (water) return "~";
                string owner = hex.GetStr(F.HexOwner);
                if (owner.Length == 0) return ".";
                long pop = sim.World.GetInt(new EntityId(owner), F.TownPop("class.peasant"), 0);
                return pop >= 10 ? "#" : ((char)('0' + Math.Max(0, (int)pop))).ToString();

            default:
                if (town) return "@";
                if (water) return "~";
                if (!string.IsNullOrEmpty(hex.GetStr(F.HexRiverDown))) return "+";
                if (!string.IsNullOrEmpty(hex.GetStr(F.HexOwner))) return glyph.ToLowerInvariant();
                return glyph;
        }
    }

    /// <summary>数据 id 短名的首字母：<c>landform.plain</c> → P。零配置，加一种地表自动多一个图例。</summary>
    private static string Initial(string defId)
    {
        int dot = defId.LastIndexOf('.');
        string bare = dot < 0 ? defId : defId[(dot + 1)..];
        return bare.Length == 0 ? "?" : bare[..1].ToUpperInvariant();
    }
}

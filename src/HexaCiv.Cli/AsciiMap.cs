using System.Text;
using CoreBasic;
using SimCore;

namespace HexaCiv.Cli;

/// <summary>
/// ASCII 地图（§3.1 轴坐标的人读视图，§3.9 的 32 × 20 矩形）。
///
/// 它的价值不在好看，而在「一眼看出种子有没有长出合理的图」：
/// 边缘高地有没有围住中央低地、可建城格够不够、城是不是挤在一起。
/// Godot 壳层负责真正的渲染，这个版本是给无头调试与文档截图用的。
/// </summary>
public static class AsciiMap
{
    public static string Render(Simulation sim)
    {
        int columns = (int)sim.World.GetInt(World.GlobalId, "meta.mapColumns", MapGen.MapColumns);
        int rows = (int)sim.World.GetInt(World.GlobalId, "meta.mapRows", MapGen.MapRows);
        var world = sim.World;

        var sb = new StringBuilder();
        sb.AppendLine($"地图 {columns} × {rows}（§3.9；每行左边是 r，顶部一行是个位列，重复 0–9）");
        sb.AppendLine("     " + string.Concat(Enumerable.Range(0, columns).Select(c => $"{(char)('0' + c % 10)} ")));
        for (int row = 0; row < rows; row++)
        {
            var cells = new StringBuilder();
            for (int col = 0; col < columns; col++)
            {
                var id = MapGen.HexIdAt(col, row);
                var ent = world.Get(id);
                cells.Append(ent is null ? "· " : Glyph(world, ent) + " ");
            }
            sb.AppendLine($"r{row:00} |{cells}|");
        }

        sb.AppendLine();
        sb.AppendLine("图例：大写=无主格，小写=已入辖地，@=城址，~=水域（湖/近海），+=河段（§3.3 有向边）；字母取自地表 id 短名首字母");
        foreach (var lf in sim.Data.OfType("landform"))
            sb.AppendLine($"  {Initial(lf.Id)} = {lf.GetStr("title")}（Y粮 {lf.GetFix("potential.food").Text}，可建城 {(lf.Get("buildable").AsBool() ? "是" : "否")}）");
        foreach (var wb in sim.Data.OfType("waterbody"))
            sb.AppendLine($"  ~ = {wb.GetStr("title")}（Y粮 {wb.GetFix("potential.food").Text}，不可建城）");

        var tally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var waterTally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int rivers = 0;
        foreach (var h in world.OfKind("hex"))
        {
            string key = h.GetStr("hex.landform");
            tally[key] = tally.GetValueOrDefault(key) + 1;
            string water = h.GetStr("hex.water");
            if (water.Length > 0) waterTally[water] = waterTally.GetValueOrDefault(water) + 1;
            if (h.GetStr("hex.river.down").Length > 0) rivers++;
        }
        sb.AppendLine();
        sb.AppendLine($"共 {world.OfKind("hex").Count} 格：" +
                      string.Join("、", tally.Select(kv => $"{sim.Data.Get(kv.Key).GetStr("title")} {kv.Value}")));
        sb.AppendLine($"水域 " + string.Join("、", waterTally.Select(kv =>
                          $"{sim.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格")) +
                      $"；河段 {rivers} 段（§3.3）");
        return sb.ToString();
    }

    private static string Glyph(World world, Entity hex)
    {
        string land = hex.GetStr("hex.landform");
        string glyph = Initial(land);
        if (!string.IsNullOrEmpty(hex.GetStr("hex.town"))) return "@";
        if (!string.IsNullOrEmpty(hex.GetStr("hex.water"))) return "~";
        if (!string.IsNullOrEmpty(hex.GetStr("hex.river.down"))) return "+";
        if (!string.IsNullOrEmpty(hex.GetStr("hex.owner"))) return glyph.ToLowerInvariant();
        return glyph;
    }

    /// <summary>数据 id 短名的首字母：<c>landform.plain</c> → P。零配置，加一种地表自动多一个图例。</summary>
    private static string Initial(string defId)
    {
        int dot = defId.LastIndexOf('.');
        string bare = dot < 0 ? defId : defId[(dot + 1)..];
        return bare.Length == 0 ? "?" : bare[..1].ToUpperInvariant();
    }
}

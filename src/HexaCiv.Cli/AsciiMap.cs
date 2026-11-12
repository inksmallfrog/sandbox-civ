using System.Text;
using SimCore;

namespace HexaCiv.Cli;

/// <summary>
/// ASCII 地图（§3.1 轴坐标的人读视图）。
///
/// 它的价值不在好看，而在「一眼看出种子有没有长出合理的图」：
/// 盆地有没有被高地围住、可建城格够不够、城是不是挤在一起。
/// Godot 壳层负责真正的渲染，这个版本是给无头调试与文档截图用的。
/// </summary>
public static class AsciiMap
{
    public static string Render(Simulation sim)
    {
        int radius = (int)sim.World.GetInt(World.GlobalId, "meta.mapRadius", 4);
        var world = sim.World;

        var sb = new StringBuilder();
        sb.AppendLine("        " + string.Join("", Enumerable.Range(-radius, radius * 2 + 1).Select(q => $"q{q:+0;-0} ")));
        for (int r = -radius; r <= radius; r++)
        {
            int pad = Math.Max(0, (r + radius));                       // 六边形每行左移量
            int qLo = Math.Max(-radius, -r - radius), qHi = Math.Min(radius, -r + radius);
            var cells = new StringBuilder();
            for (int q = qLo; q <= qHi; q++)
            {
                var id = new EntityId($"hex.{q:000}.{r:000}");
                var ent = world.Get(id);
                cells.Append(ent is null ? "· " : Glyph(world, ent) + " ");
            }
            sb.AppendLine($"r{r:+0;-0} {new string(' ', pad)}|{cells}|  ({qLo}…{qHi})");
        }

        sb.AppendLine();
        sb.AppendLine("图例：大写=无主格，小写=已入辖地，C=城址；字母取自地表 id 短名首字母");
        foreach (var lf in sim.Data.OfType("landform"))
            sb.AppendLine($"  {Initial(lf.Id)} = {lf.GetStr("title")}（Y粮 {lf.GetFix("potential.food").Text}，可建城 {(lf.Get("buildable").AsBool() ? "是" : "否")}）");

        var tally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var h in world.OfKind("hex"))
        {
            string key = h.GetStr("hex.landform");
            tally[key] = tally.GetValueOrDefault(key) + 1;
        }
        sb.AppendLine();
        sb.AppendLine($"共 {world.OfKind("hex").Count} 格：" +
                      string.Join("、", tally.Select(kv => $"{sim.Data.Get(kv.Key).GetStr("title")} {kv.Value}")));
        return sb.ToString();
    }

    private static string Glyph(World world, Entity hex)
    {
        string land = hex.GetStr("hex.landform");
        string glyph = Initial(land);
        if (!string.IsNullOrEmpty(hex.GetStr("hex.town"))) return "C";
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

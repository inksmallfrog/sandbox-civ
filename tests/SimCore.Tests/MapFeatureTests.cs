using SimCore;

namespace HexaCiv.Tests;

/// <summary>
/// M2 的地形特征（GDD §3.4 十二种形胜 + §3.5 五种门槛资源 + §3.9 图层）。
///
/// 这一批断言分两层：
/// 1. **数据结构**：每格标签都能在数据里找到，倍率按 §3.6 的乘法链落进 Y_g；
/// 2. **识别规则**：隘口必须两侧夹山、河口必须入海、上游高地必须是源头……
///    规则与实现各写一遍，撞在一起才算「贴着 §3.4 写代码」被机器看着。
/// 另有两条 **20 张图验收**（§12 M2「同形胜在 20 张图里人工可认出」）：
/// 这是验收条的机器侧证据，人工目检仍看 `sim map --seed 1..20` 的 ASCII。
/// </summary>
public class MapFeatureTests
{
    private static Simulation Open(ulong seed) =>
        new Simulation(new SimConfig { Seed = seed }).Bootstrap();

    private static readonly string[] ShapeIds =
    [
        "shape.basin", "shape.valley", "shape.pass", "shape.estuary", "shape.delta", "shape.headwater",
        "shape.gorge", "shape.island", "shape.oasis", "shape.saltlake", "shape.fan", "shape.volcano"
    ];

    private static readonly string[] ResourceIds =
    [
        "resource.iron", "resource.horse", "resource.salt", "resource.spice", "resource.fish"
    ];

    private static Entity? Neighbor(Simulation sim, Entity hex, int dir)
    {
        var (q, r) = MapGen.Coords(hex.Id);
        var (dq, dr) = MapGen.Neighbors[dir];
        return sim.World.Get(MapGen.HexId(q + dq, r + dr));
    }

    private static bool IsHigh(Entity? hex) => hex is not null && hex.GetInt(F.HexHeight) >= 62;

    private static bool HasOppositeHigh(Simulation sim, Entity hex)
    {
        for (int k = 0; k < 3; k++)
            if (IsHigh(Neighbor(sim, hex, k)) && IsHigh(Neighbor(sim, hex, k + 3))) return true;
        return false;
    }

    private static bool IsWater(Entity hex) => hex.GetStr(F.HexWater).Length > 0;

    [Fact]
    public void 形胜数据是GDD十二种且资源数据是GDD五种()
    {
        var sim = Open(7);
        Assert.Equal(ShapeIds.OrderBy(id => id, StringComparer.Ordinal),
            sim.Data.OfType(F.TypeShape).Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(ResourceIds.OrderBy(id => id, StringComparer.Ordinal),
            sim.Data.OfType(F.TypeResource).Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void 每格的形胜与资源标签都能解析到数据()
    {
        var sim = Open(7);
        foreach (var hex in sim.World.OfKind("hex"))
        {
            string shape = hex.GetStr(F.HexShape);
            if (shape.Length > 0)
                Assert.Equal(F.TypeShape, sim.Data.Get(shape).Type);

            string resource = hex.GetStr(F.HexResource);
            if (resource.Length > 0)
                Assert.Equal(F.TypeResource, sim.Data.Get(resource).Type);
        }
    }

    [Fact]
    public void 形胜与资源倍率按3_6节叠进粮料潜力()
    {
        // 实现与文档各算一遍：§3.6 第一行 Y_g = 地表潜力 × 形胜倍率 × 资源倍率 × 水利系数。
        // 绿洲在荒漠上的 +1 地力（bonus.food）按 §3.4 的「局部粮潜力」解释，记在 CHANGELOG D-036。
        var sim = Open(7);
        var hexes = sim.World.OfKind("hex");
        var byId = hexes.ToDictionary(h => h.Id.Value, h => h, StringComparer.Ordinal);

        var wet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hex in hexes)
        {
            string down = hex.GetStr(F.HexRiverDown);
            if (down.Length > 0) { wet.Add(hex.Id.Value); wet.Add(down); }
        }
        foreach (var lake in hexes.Where(h => h.GetStr(F.HexWater) == "water.lake"))
        {
            var (q, r) = MapGen.Coords(lake.Id);
            foreach (var (dq, dr) in MapGen.Neighbors)
                if (byId.TryGetValue(MapGen.HexId(q + dq, r + dr).Value, out var nb) && !IsWater(nb))
                    wet.Add(nb.Id.Value);
        }

        foreach (var hex in hexes)
        {
            string shapeId = hex.GetStr(F.HexShape);
            string resourceId = hex.GetStr(F.HexResource);
            var shape = shapeId.Length > 0 ? sim.Data.Get(shapeId) : null;
            var resource = resourceId.Length > 0 ? sim.Data.Get(resourceId) : null;
            Fix shapeFood = shape?.GetFix("yield.food", Fix.One) ?? Fix.One;
            Fix shapeMat = shape?.GetFix("yield.mat", Fix.One) ?? Fix.One;
            Fix bonusFood = shape?.GetFix("bonus.food", Fix.Zero) ?? Fix.Zero;
            Fix bonusMat = shape?.GetFix("bonus.mat", Fix.Zero) ?? Fix.Zero;
            Fix resFood = resource?.GetFix("yield.food", Fix.One) ?? Fix.One;
            Fix resMat = resource?.GetFix("yield.mat", Fix.One) ?? Fix.One;

            if (IsWater(hex))
            {
                var water = sim.Data.Get(hex.GetStr(F.HexWater));
                Fix bonus = resourceId == "resource.fish" ? resource!.GetFix("bonus.food", Fix.Zero) : Fix.Zero;
                Assert.Equal(water.GetFix("potential.food") + bonus, hex.GetFix(F.HexYieldFood));
                Assert.Equal(Fix.Zero, hex.GetFix(F.HexYieldMat));
                continue;
            }

            var lf = sim.Data.Get(hex.GetStr(F.HexLandform));
            Fix expectedFood = Fix.Mul(Fix.Mul(lf.GetFix("potential.food") + bonusFood, shapeFood), resFood);
            Fix expectedMat = Fix.Mul(Fix.Mul(lf.GetFix("potential.mat") + bonusMat, shapeMat), resMat);
            if (wet.Contains(hex.Id.Value))
            {
                expectedFood = Fix.Mul(expectedFood, Fix.FromPercent(115));
                expectedMat = Fix.Mul(expectedMat, Fix.FromPercent(115));
            }
            Assert.Equal(expectedFood, hex.GetFix(F.HexYieldFood));
            Assert.Equal(expectedMat, hex.GetFix(F.HexYieldMat));
        }
    }

    [Fact]
    public void 盆地是被高地围出的低地群()
    {
        var sim = Open(1);
        var basins = sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.basin").ToList();
        Assert.NotEmpty(basins);
        foreach (var hex in basins)
        {
            Assert.False(IsWater(hex), $"{hex.Id} 是盆地却在水里");
            Assert.True(hex.GetInt(F.HexHeight) <= 45, $"{hex.Id} 是盆地但高度 {hex.GetInt(F.HexHeight)} 不低");
        }
        // 至少一个盆底格的两圈里有一半以上是高地（§3.4「环形高地包围」）。
        Assert.Contains(basins, hex =>
        {
            var (q, r) = MapGen.Coords(hex.Id);
            int high = 0, total = 0;
            for (int dq = -2; dq <= 2; dq++)
                for (int dr = -2; dr <= 2; dr++)
                {
                    int dist = MapGen.Distance(dq, dr);
                    if (dist is < 1 or > 2) continue;
                    var nb = sim.World.Get(MapGen.HexId(q + dq, r + dr));
                    if (nb is null) continue;
                    total++;
                    if (nb.GetInt(F.HexHeight) >= 62) high++;
                }
            return total > 0 && high * 100 >= total * 45;
        });
    }

    [Fact]
    public void 隘口是山脊缺口()
    {
        var sim = Open(1);
        var passes = sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.pass").ToList();
        Assert.NotEmpty(passes);
        foreach (var hex in passes)
        {
            Assert.False(IsWater(hex));
            Assert.True(hex.GetInt(F.HexHeight) <= 55, $"{hex.Id} 是隘口但不在低处");
            Assert.True(HasOppositeHigh(sim, hex), $"{hex.Id} 是隘口但两侧没有高地");
            Assert.True(hex.GetInt(F.HexMoveCost) <= 2, $"{hex.Id} 是隘口但通行成本 {hex.GetInt(F.HexMoveCost)} 没有降低");
        }
    }

    [Fact]
    public void 河口与三角洲都在河入海处()
    {
        var sim = Open(1);
        var mouths = sim.World.OfKind("hex")
            .Where(h => h.GetStr(F.HexShape) is "shape.estuary" or "shape.delta").ToList();
        Assert.NotEmpty(mouths);

        foreach (var hex in mouths)
        {
            string down = hex.GetStr(F.HexRiverDown);
            Assert.False(string.IsNullOrEmpty(down), $"{hex.Id} 是河口却没有下游");
            var target = sim.World.Get(new EntityId(down));
            Assert.NotNull(target);
            Assert.Equal("water.sea", target!.GetStr(F.HexWater));

            int flow = (int)hex.GetInt(F.HexRiverFlow);
            if (hex.GetStr(F.HexShape) == "shape.delta")
                Assert.True(flow >= 20, $"{hex.Id} 是三角洲但集水只有 {flow}（§3.4 大河末端）");
            else
                Assert.True(flow < 20, $"{hex.Id} 是河口但集水 {flow} 已达到大河门槛");
        }
    }

    [Fact]
    public void 上游高地是河流源头()
    {
        var sim = Open(1);
        var sources = sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.headwater").ToList();
        Assert.NotEmpty(sources);

        foreach (var hex in sources)
        {
            Assert.False(string.IsNullOrEmpty(hex.GetStr(F.HexRiverDown)), $"{hex.Id} 是上游高地却不是河");
            for (int dir = 0; dir < 6; dir++)
            {
                var nb = Neighbor(sim, hex, dir);
                if (nb is null) continue;
                Assert.NotEqual(hex.Id.Value, nb.GetStr(F.HexRiverDown));
            }
        }
    }

    [Fact]
    public void 峡谷是夹岸高地的河段()
    {
        var sim = Open(1);
        var gorges = sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.gorge").ToList();
        Assert.NotEmpty(gorges);
        foreach (var hex in gorges)
        {
            Assert.False(string.IsNullOrEmpty(hex.GetStr(F.HexRiverDown)), $"{hex.Id} 是峡谷却不是河");
            Assert.True(HasOppositeHigh(sim, hex), $"{hex.Id} 是峡谷但两岸没有高地");
        }
    }

    [Fact]
    public void 湖岛四面临水且绿洲在荒漠临水()
    {
        var sim = Open(1);

        foreach (var hex in sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.island"))
        {
            int water = 0;
            for (int dir = 0; dir < 6; dir++)
                if (Neighbor(sim, hex, dir) is { } nb && IsWater(nb)) water++;
            Assert.True(water >= 4, $"{hex.Id} 是湖岛/半岛但只挨着 {water} 面水");
        }

        foreach (var hex in sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.oasis"))
        {
            Assert.Equal("landform.desert", hex.GetStr(F.HexLandform));
            bool water = !string.IsNullOrEmpty(hex.GetStr(F.HexRiverDown));
            for (int dir = 0; dir < 6; dir++)
            {
                var nb = Neighbor(sim, hex, dir);
                if (nb is not null && (!string.IsNullOrEmpty(nb.GetStr(F.HexRiverDown))
                                       || nb.GetStr(F.HexWater) == "water.lake")) water = true;
            }
            Assert.True(water, $"{hex.Id} 是绿洲但附近没有河/湖");
        }
    }

    [Fact]
    public void 盐湖是干旱湖泊且冲积扇在山前()
    {
        var sim = Open(1);
        var saltlakes = sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.saltlake").ToList();
        Assert.NotEmpty(saltlakes);

        foreach (var hex in saltlakes)
        {
            Assert.Equal("water.lake", hex.GetStr(F.HexWater));
            Assert.True(hex.GetInt(F.HexRain) <= 35, $"{hex.Id} 是盐湖但降水 {hex.GetInt(F.HexRain)} 不干旱");
        }

        foreach (var hex in sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.fan"))
        {
            Assert.False(IsWater(hex));
            bool mountain = false, water = !string.IsNullOrEmpty(hex.GetStr(F.HexRiverDown));
            for (int dir = 0; dir < 6; dir++)
            {
                var nb = Neighbor(sim, hex, dir);
                if (nb is null) continue;
                if (nb.GetInt(F.HexHeight) >= 62 || nb.GetStr(F.HexLandform) == "landform.mountain") mountain = true;
                if (!string.IsNullOrEmpty(nb.GetStr(F.HexRiverDown)) || nb.GetStr(F.HexWater) == "water.lake") water = true;
            }
            Assert.True(mountain, $"{hex.Id} 是冲积扇但不在山前");
            Assert.True(water, $"{hex.Id} 是冲积扇但附近没有河/湖");
        }
    }

    [Fact]
    public void 火山在山地且每图最多两座()
    {
        var sim = Open(1);
        var volcanoes = sim.World.OfKind("hex").Where(h => h.GetStr(F.HexShape) == "shape.volcano").ToList();
        Assert.InRange(volcanoes.Count, 1, 2);
        foreach (var hex in volcanoes)
        {
            Assert.False(IsWater(hex));
            Assert.True(hex.GetInt(F.HexHeight) >= 80 || hex.GetStr(F.HexLandform) == "landform.mountain",
                $"{hex.Id} 是火山却不在山地");
        }
    }

    [Fact]
    public void 渔汛让水域粮潜力加一()
    {
        var sim = Open(1);
        var water = sim.World.OfKind("hex").Where(IsWater).ToList();
        var fish = water.FirstOrDefault(h => h.GetStr(F.HexResource) == "resource.fish");
        var plain = water.FirstOrDefault(h => h.GetStr(F.HexResource) != "resource.fish");
        Assert.NotNull(fish);
        Assert.NotNull(plain);

        var bonus = sim.Data.Get("resource.fish").GetFix("bonus.food");
        Assert.Equal(sim.Data.Get(fish!.GetStr(F.HexWater)).GetFix("potential.food") + bonus,
            fish.GetFix(F.HexYieldFood));
        Assert.Equal(sim.Data.Get(plain!.GetStr(F.HexWater)).GetFix("potential.food"),
            plain.GetFix(F.HexYieldFood));
    }

    [Fact]
    public void 二十个种子里十二种形胜都出现过()
    {
        // §12 M2 验收原文：「同形胜在 20 张图里人工可认出」。
        // 机器侧只证「十二种都长出来了」，像不像仍要人看 ASCII（sim map --seed 1..20）。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 1; seed <= 20; seed++)
            foreach (var hex in Open(seed).World.OfKind("hex"))
            {
                string shape = hex.GetStr(F.HexShape);
                if (shape.Length > 0) seen.Add(shape);
            }

        var missing = ShapeIds.Where(id => !seen.Contains(id)).ToList();
        Assert.True(missing.Count == 0, $"20 个种子里没长出：{string.Join("、", missing)}");
    }

    [Fact]
    public void 二十个种子里五种门槛资源都出现过()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 1; seed <= 20; seed++)
            foreach (var hex in Open(seed).World.OfKind("hex"))
            {
                string resource = hex.GetStr(F.HexResource);
                if (resource.Length > 0) seen.Add(resource);
            }

        var missing = ResourceIds.Where(id => !seen.Contains(id)).ToList();
        Assert.True(missing.Count == 0, $"20 个种子里没生成：{string.Join("、", missing)}");
    }

    [Fact]
    public void 同种子同形胜资源换种子换形胜资源()
    {
        static string Fingerprint(Simulation sim) => string.Join(";", sim.World.OfKind("hex")
            .Select(h => $"{h.Id.Value}={h.GetStr(F.HexShape)}/{h.GetStr(F.HexResource)}"));

        Assert.Equal(Fingerprint(Open(7)), Fingerprint(Open(7)));
        Assert.NotEqual(Fingerprint(Open(7)), Fingerprint(Open(8)));
    }

    [Fact]
    public void 图层声明补齐了资源与人口密度()
    {
        var sim = Open(7);
        var ids = sim.Layers.Select(l => l.Id).ToList();
        Assert.Contains("resource", ids);
        Assert.Contains("population", ids);
        Assert.True(ids.Count >= 5, $"图层只有 {ids.Count} 个：{string.Join("、", ids)}");
    }
}

using SimCore;

namespace CoreBasic;

/// <summary>
/// 地图生成（GDD §3.7 六步管线的前两步：高度图 → 地表分类，再算 §3.6 的 Y_g 粮潜力）。
///
/// M1 的取舍与后续接口：
/// - 只跑「盆地 + 外环高地」这一条最出效果的地形（§1.2 要的盆地/山谷/山水夹缝在这里已经看得出来）。
/// - 没有河流（§3.3）、形胜（§3.4）、资源（§3.5）、图层 3–5（§3.9）——那是 M2 的活；
///   届时 <see cref="CoreInfo.MapGenVersion"/> 必须 +1 并写进存档（§3.7 末行、§10.6）。
/// - 噪声只用 + − × ÷ 与 Math.Sqrt（IEEE 754 要求平方根正确舍入），不用 Pow/Exp/三角函数：
///   §10.7 只禁止 float 进入结算比较，但生成器要求更强——跨平台必须逐格一致，
///   否则同一个种子在两台机器上长出两副地图，联机哈希永远对不上。
/// </summary>
public static class MapGen
{
    /// <summary>默认地图半径（轴坐标到中心的最大距离）。半径 4 = 61 格，够摆出盆地又不至于刷屏。</summary>
    public const int DefaultRadius = 4;

    /// <summary>pointy-top 六边形的六个邻居方向（轴坐标，§3.1）。行军与辖地都按这个邻接表走。</summary>
    public static readonly (int dq, int dr)[] Neighbors =
        [(+1, 0), (+1, -1), (0, -1), (-1, 0), (-1, +1), (0, +1)];

    /// <summary>轴坐标到中心的距离（立方坐标取三轴绝对值的最大者）。</summary>
    public static int Distance(int dq, int dr) => (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;

    /// <summary>两个地块的距离。</summary>
    public static int Distance(EntityId a, EntityId b)
    {
        var (aq, ar) = Coords(a);
        var (bq, br) = Coords(b);
        return Distance(aq - bq, ar - br);
    }

    /// <summary>地块 id <c>hex.q.r</c> → 轴坐标。id 与 <see cref="F.HexQ"/> 字段互为冗余，这里以 id 为准。</summary>
    public static (int q, int r) Coords(EntityId hex)
    {
        var parts = hex.Value?.Split('.', 3);
        if (parts is { Length: 3 } && parts[0] == "hex"
            && int.TryParse(parts[1], out int q) && int.TryParse(parts[2], out int r))
            return (q, r);
        throw new SimDataException($"地块 id 不是 hex.q.r 形式：'{hex}'（附录 D §3.1）");
    }

    public static EntityId HexId(int q, int r) => new($"hex.{q:000}.{r:000}");

    /// <summary>新开局建图。由 <see cref="BasicPlugin"/> 注册为 bootstrap，输入只有种子与数据（§8.3）。</summary>
    public static void Bootstrap(IStepContext ctx)
    {
        int radius = (int)ctx.GetInt(World.GlobalId, F.ConfigMapRadius, DefaultRadius);
        if (radius is < 1 or > 12)
            throw new SimDataException($"{F.ConfigMapRadius}={radius} 越界（1–12），拒绝建图");

        var grades = GradeTable(ctx.Data.OfType(F.TypeLandform));

        var tally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int created = 0;
        for (int q = -radius; q <= radius; q++)
            for (int r = Math.Max(-radius, -q - radius); r <= Math.Min(radius, -q + radius); r++)
            {
                int dist = Distance(q, r);
                double rim = (double)dist / radius;

                // §3.7 第 1 步：高度 = 外环抬升 + 两层值噪声。盆地由「中心低、四周高」自然围出来。
                double noise = 0.62 * Smooth(ctx, q, r, 3, 1) + 0.38 * Smooth(ctx, q, r, 2, 7919);
                double raw = 8 + 86 * rim * Math.Sqrt(rim) + 30 * (noise - 0.5);
                int h = (int)Math.Round(raw);
                if (h < 0) h = 0; else if (h > 100) h = 100;

                var landform = Classify(grades, h);
                tally[landform.Id] = tally.GetValueOrDefault(landform.Id) + 1;

                EntityId id = HexId(q, r);
                var ent = ctx.CreateEntity(F.KindHex, "新开局建图（§3.7 第 1–2 步）", id);
                ctx.Mutate(id, F.HexQ, q, "轴坐标 q（§3.1），与 id 互为冗余，读档后可互相校验");
                ctx.Mutate(id, F.HexR, r, "轴坐标 r");
                ctx.Mutate(id, F.HexHeight, h, "高度图（§3.7 第 1 步）。形胜识别（§3.4）在 M2 读它");
                ctx.Mutate(id, F.HexLandform, landform.Id, $"高度 {h} 落进 [{FloorOf(grades, landform.Id)}, {CeilOf(grades, landform.Id)}] 这一档（§3.2）");
                ctx.Mutate(id, F.HexMoveCost, landform.GetInt("moveCost"), "§3.6 地表通行成本：行军与运输都读它");
                ctx.Mutate(id, F.HexDefense, landform.GetInt("defense"), "§3.6 地表防御：M1 只写不算，军事在 M6");
                ctx.Mutate(id, F.HexBuildable, SimValue.OfBool(landform.Get("buildable").AsBool()), "§3.2 可否建城（山地只能做隘口）");
                ctx.Mutate(id, F.HexYieldFood, landform.GetFix("potential.food"),
                    "Y_g 粮潜力（§3.6 第一行）。M1 无形胜/资源/水利，后三个倍率暂为 1.00");
                created++;
            }

        var detail = string.Join("、", tally.Select(kv =>
            $"{ctx.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格"));
        ctx.Note($"地图：半径 {radius} 共 {created} 格 —— {detail}");
        ctx.Mutate(World.GlobalId, "meta.mapRadius", radius, "建图半径写回全局，UI 画边界与无头报告都要读");
        ctx.Mutate(World.GlobalId, "meta.mapTiles", created, "地块总数");
    }

    /// <summary>
    /// 高度档表：把数据包里的地表按 <c>potential.food</c> <b>升序</b>排，
    /// 第 i 档占高度区间 [100−20(i+1), 100−20i)。于是最不能种地的（山地）住最高的那一档。
    /// 加一种地表 = 加一行数据并给它一个潜力值，这段代码不用动。
    /// </summary>
    private static List<(int floor, DataEntry entry)> GradeTable(IReadOnlyList<DataEntry> landforms)
    {
        if (landforms.Count == 0)
            throw new SimDataException($"数据包里没有 type={F.TypeLandform} 的记录，地图生成器无从分类");
        if (landforms.Count > 5)
            throw new SimDataException($"地表 {landforms.Count} 类 > 5：每档只剩 {100 / landforms.Count} 高度，分类会退化（§3.2 是 9 类配 9 档，M2 再扩）");

        var ordered = landforms
            .OrderBy(e => e.GetFix("potential.food").Raw)
            .ThenBy(e => e.Id, StringComparer.Ordinal)     // 潜力相同按 id 定序，平票裁决跨平台一致（§10.7 纪律 1）
            .ToList();

        int bands = ordered.Count;
        int span = 100 / bands;
        var table = new List<(int, DataEntry)>(bands);
        for (int i = 0; i < bands; i++)
            table.Add((Math.Max(100 - span * (i + 1), 0), ordered[i]));
        return table;
    }

    private static DataEntry Classify(List<(int floor, DataEntry entry)> grades, int height)
    {
        foreach (var (floor, entry) in grades)
            if (height >= floor) return entry;
        return grades[^1].entry;
    }

    private static int FloorOf(List<(int floor, DataEntry entry)> grades, string id) =>
        grades.First(g => g.entry.Id == id).floor;

    private static int CeilOf(List<(int floor, DataEntry entry)> grades, string id)
    {
        int i = grades.FindIndex(g => g.entry.Id == id);
        return i == 0 ? 100 : grades[i - 1].floor - 1;
    }

    /// <summary>
    /// 格点值噪声：同一个 (i, j, cell, salt) 永远给同一个数，且不依赖任何随机流的调用顺序。
    /// 这是 §8.3「同种子同输入同输出」的最强形式——连调用次数都不敏感，插一种地形不会挪动别处的噪声。
    /// </summary>
    private static double Smooth(IStepContext ctx, int q, int r, int cell, long salt)
    {
        double x = (double)q / cell, y = (double)r / cell;
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double fx = x - xi, fy = y - yi;
        double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);   // smoothstep：只用乘加

        double a = Corner(ctx, xi, yi, salt), b = Corner(ctx, xi + 1, yi, salt);
        double c = Corner(ctx, xi, yi + 1, salt), d = Corner(ctx, xi + 1, yi + 1, salt);
        double top = a + (b - a) * sx;
        double bot = c + (d - c) * sx;
        return top + (bot - top) * sy;
    }

    private static double Corner(IStepContext ctx, int i, int j, long salt)
    {
        // StableHash 是 FNV-1a 64，低位偏弱，再叠一层 SplitMix64 的最终混合把弱位打散。
        ulong h = StableHash.Mix(StableHash.Of($"g{i},j{j},s{salt}"), ctx.Rng.WorldSeed);
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
        return (h >> 11) * (1.0 / 9007199254740992.0);   // 高 53 位 → [0,1)
    }
}

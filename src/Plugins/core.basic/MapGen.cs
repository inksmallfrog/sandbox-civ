using SimCore;

namespace CoreBasic;

/// <summary>
/// 地图生成（GDD §3.7 六步管线的前两步：高度图 → 地表分类，再算 §3.6 的 Y_g 粮潜力）。
///
/// M2 切片一的取舍与后续接口：
/// - 地图形状已是 §3.9 / §13.1 的 <b>32 × 20 矩形</b>：按 odd-r 偏移坐标铺格，存的是轴坐标，
///   于是屏幕上是矩形、<see cref="Distance(int,int)"/> 仍是标准轴距（§3.1）。
/// - 只跑「边缘高地 + 中央低地」这一条最出效果的地形；河流（§3.3）、形胜（§3.4）、
///   资源（§3.5）、图层 4–5（§3.9）仍是后续切片。
/// - 形状改了就必须递增 <see cref="CoreInfo.MapGenVersion"/> 并写进存档（§3.7 末行、§10.6）；
///   旧种子会长出新地图，这是明写要求，不是回归。
/// - 噪声只用整数定点与整数平方根，不经过 float/double：地图字段进入世界哈希，
///   必须让同一个种子在不同平台逐格生成同一张图（§10.7）。
/// </summary>
public static class MapGen
{
    private const long NoiseScale = 1_000_000;

    /// <summary>默认地图宽度（列数，§3.9 / §13.1 的 32 × 20）。</summary>
    public const int MapColumns = 32;

    /// <summary>默认地图高度（行数）。</summary>
    public const int MapRows = 20;

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

    /// <summary>
    /// odd-r 偏移坐标 (col, row) → 轴坐标 (q, r)。屏幕上的「列」在奇数行右移半格，
    /// 换成轴坐标后 x = √3·size·(q + r/2) 正好还是矩形（§3.9 的 32 × 20）。
    /// 存世界的是轴坐标，所以距离与邻接仍走 §3.1 的标准公式。
    /// </summary>
    public static (int q, int r) AxialOfOffset(int col, int row) =>
        (col - ((row - (row & 1)) / 2), row);

    /// <summary>矩形地图上第 (col, row) 格的地块 id。</summary>
    public static EntityId HexIdAt(int col, int row)
    {
        var (q, r) = AxialOfOffset(col, row);
        return HexId(q, r);
    }

    /// <summary>新开局建图。由 <see cref="BasicPlugin"/> 注册为 bootstrap，输入只有种子与数据（§8.3）。</summary>
    public static void Bootstrap(IStepContext ctx)
    {
        int columns = (int)ctx.GetInt(World.GlobalId, F.ConfigMapColumns, MapColumns);
        int rows = (int)ctx.GetInt(World.GlobalId, F.ConfigMapRows, MapRows);
        if (columns is < 8 or > 128)
            throw new SimDataException($"{F.ConfigMapColumns}={columns} 越界（8–128），拒绝建图");
        if (rows is < 8 or > 128)
            throw new SimDataException($"{F.ConfigMapRows}={rows} 越界（8–128），拒绝建图");

        var grades = GradeTable(ctx.Data.OfType(F.TypeLandform));

        int maxEdgeCol = (columns - 1) / 2;
        int maxEdgeRow = (rows - 1) / 2;

        var tally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int created = 0;
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                var (q, r) = AxialOfOffset(col, row);

                // §3.7 第 1 步：高度 = 边缘抬升 + 两层值噪声。
                // 边缘量取「离四条边最近的距离」分别在两轴上归一化后取大者，
                // 于是高地沿着矩形轮廓围一圈，中央留出可种地的低地（大陆轮廓 + 边缘衰减）。
                long rimCol = (long)(maxEdgeCol - Math.Min(col, columns - 1 - col)) * NoiseScale / maxEdgeCol;
                long rimRow = (long)(maxEdgeRow - Math.Min(row, rows - 1 - row)) * NoiseScale / maxEdgeRow;
                long rim = Math.Max(rimCol, rimRow);
                long noise = (62 * Smooth(ctx, q, r, 3, 1) + 38 * Smooth(ctx, q, r, 2, 7919)) / 100;
                long rimCurve = rim * IntegerSqrt(rim * NoiseScale) / NoiseScale;
                long raw = 8 * NoiseScale + 86 * rimCurve + 30 * (noise - NoiseScale / 2);
                int h = (int)RoundScaled(raw);
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
                    "Y_g 粮潜力（§3.6 第一行）。M2 切片一仍无形胜/资源/水利，后三个倍率暂为 1.00");
                created++;
            }

        var detail = string.Join("、", tally.Select(kv =>
            $"{ctx.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格"));
        ctx.Note($"地图：{columns} × {rows} 共 {created} 格 —— {detail}");
        ctx.Mutate(World.GlobalId, "meta.mapColumns", columns, "建图列数写回全局，UI 画边界与无头报告都要读（§3.9）");
        ctx.Mutate(World.GlobalId, "meta.mapRows", rows, "建图行数写回全局");
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
    private static long Smooth(IStepContext ctx, int q, int r, int cell, long salt)
    {
        int xi = FloorDiv(q, cell), yi = FloorDiv(r, cell);
        long sx = Smoothstep(q - xi * cell, cell), sy = Smoothstep(r - yi * cell, cell);

        long a = Corner(ctx, xi, yi, salt), b = Corner(ctx, xi + 1, yi, salt);
        long c = Corner(ctx, xi, yi + 1, salt), d = Corner(ctx, xi + 1, yi + 1, salt);
        long top = Lerp(a, b, sx);
        long bot = Lerp(c, d, sx);
        return Lerp(top, bot, sy);
    }

    private static long Corner(IStepContext ctx, int i, int j, long salt)
    {
        // StableHash 是 FNV-1a 64，低位偏弱，再叠一层 SplitMix64 的最终混合把弱位打散。
        ulong h = StableHash.Mix(StableHash.Of($"g{i},j{j},s{salt}"), ctx.Rng.WorldSeed);
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
        return (long)(((UInt128)(h >> 11) * (ulong)NoiseScale) >> 53);   // 高 53 位定点映射到 [0, 1)
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private static long Smoothstep(int fraction, int cell)
    {
        long t = (long)fraction * NoiseScale / cell;
        long tSquared = t * t / NoiseScale;
        return tSquared * (3 * NoiseScale - 2 * t) / NoiseScale;
    }

    private static long Lerp(long from, long to, long amount) =>
        from + (to - from) * amount / NoiseScale;

    private static long IntegerSqrt(long value)
    {
        long low = 0, high = NoiseScale;
        while (low < high)
        {
            long mid = (low + high + 1) / 2;
            if (mid <= value / mid) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    private static long RoundScaled(long value) => value >= 0
        ? (value + NoiseScale / 2) / NoiseScale
        : -((-value + NoiseScale / 2) / NoiseScale);
}

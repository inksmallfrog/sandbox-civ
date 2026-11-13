using SimCore;

namespace CoreBasic;

/// <summary>
/// 地图生成（GDD §3.7 六步管线的前四步：高度图 → 温度/降水 → 地表分类 → Y_g 粮潜力）。
///
/// M2 切片二的取舍与后续接口：
/// - 地图形状是 §3.9 / §13.1 的 <b>32 × 20 矩形</b>：按 odd-r 偏移坐标铺格，存的是轴坐标，
///   于是屏幕上是矩形、<see cref="Distance(int,int)"/> 仍是标准轴距（§3.1）。
/// - 高度图为「大陆轮廓（中央高、边缘低）+ 两层值噪声」；温度按行给纬度梯度，降水用两层值噪声（§3.7 第 3 步）。
/// - 九类地表（§3.2 全部行）由 <b>数据里声明的气候区间</b>命中，不在代码里写阈值：
///   加一种地表 = 加一行 JSON，分类代码不用动。
/// - 河流（§3.3）、形胜（§3.4）、资源（§3.5）、图层 4–5（§3.9）仍是后续切片。
/// - 任何生成算法改动都必须递增 <see cref="CoreInfo.MapGenVersion"/> 并写进存档（§3.7 末行、§10.6）；
///   旧种子会长出新地图，这是明写要求，不是回归。
/// - 噪声只用整数定点，不经过 float/double：地图字段进入世界哈希，
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

        var climate = ClimateTable(ctx.Data.OfType(F.TypeLandform));

        int maxEdgeCol = (columns - 1) / 2;
        int maxEdgeRow = (rows - 1) / 2;

        var tally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int created = 0;
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                var (q, r) = AxialOfOffset(col, row);

                // §3.7 第 1 步：大陆轮廓（中央高、边缘低）+ 两层值噪声。
                // inland 取「离四条边最近的距离」在两轴归一化后的较小者，于是四周是低地海岸、
                // 中央抬成高原，山脊由 broad/detail 两层噪声决定。
                long inlandCol = (long)Math.Min(col, columns - 1 - col) * NoiseScale / maxEdgeCol;
                long inlandRow = (long)Math.Min(row, rows - 1 - row) * NoiseScale / maxEdgeRow;
                long inland = Math.Min(inlandCol, inlandRow);
                long broad = Smooth(ctx, q, r, 6, 4241);
                long detail = Smooth(ctx, q, r, 2, 7919);
                long raw = 2 * NoiseScale + 80 * inland + (40 * broad + 24 * detail - 32 * NoiseScale);
                int h = (int)RoundScaled(raw);
                if (h < 0) h = 0; else if (h > 100) h = 100;

                // §3.7 第 3 步：温度按行给纬度梯度（北冷南暖），叠一层小噪声防止出现完美横带；
                // 降水用两层值噪声。两者都只用于分类，进入世界哈希，所以同样必须整数定点。
                int t = (int)RoundScaled((long)row * 100 * NoiseScale / (rows - 1)
                                         + (Smooth(ctx, q, r, 6, 3301) - NoiseScale / 2) * 20);
                if (t < 0) t = 0; else if (t > 100) t = 100;
                int p = (int)RoundScaled(58 * Smooth(ctx, q, r, 7, 5501) + 42 * Smooth(ctx, q, r, 3, 9001));
                if (p < 0) p = 0; else if (p > 100) p = 100;

                bool edge = col == 0 || row == 0 || col == columns - 1 || row == rows - 1;
                var landform = Classify(climate, h, t, p, edge);
                tally[landform.Id] = tally.GetValueOrDefault(landform.Id) + 1;

                EntityId id = HexId(q, r);
                ctx.CreateEntity(F.KindHex, "新开局建图（§3.7 第 1–4 步）", id);
                ctx.Mutate(id, F.HexQ, q, "轴坐标 q（§3.1），与 id 互为冗余，读档后可互相校验");
                ctx.Mutate(id, F.HexR, r, "轴坐标 r");
                ctx.Mutate(id, F.HexHeight, h, "高度图（§3.7 第 1 步）。形胜识别（§3.4）在 M2 读它");
                ctx.Mutate(id, F.HexTemp, t, "温度（§3.7 第 3 步）：纬度梯度 + 小噪声；冻土分类读它");
                ctx.Mutate(id, F.HexRain, p, "降水（§3.7 第 3 步）：两层值噪声；荒漠/湿地分类读它");
                ctx.Mutate(id, F.HexLandform, landform.Id,
                    $"高度 {h} / 温度 {t} / 降水 {p}{(edge ? " / 边缘" : "")} 命中「{landform.GetStr("title")}」的气候区间（§3.2 / §3.7 第 4 步）");
                ctx.Mutate(id, F.HexMoveCost, landform.GetInt("moveCost"), "§3.6 地表通行成本：行军与运输都读它");
                ctx.Mutate(id, F.HexDefense, landform.GetInt("defense"), "§3.6 地表防御：M1 只写不算，军事在 M6");
                ctx.Mutate(id, F.HexBuildable, SimValue.OfBool(landform.Get("buildable").AsBool()), "§3.2 可否建城（山地只能做隘口）");
                ctx.Mutate(id, F.HexYieldFood, landform.GetFix("potential.food"),
                    "Y_g 粮潜力（§3.6 第一行）。M2 切片二仍无形胜/资源/水利，后三个倍率暂为 1.00");
                created++;
            }

        var detailTally = string.Join("、", tally.Select(kv =>
            $"{ctx.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格"));
        ctx.Note($"地图：{columns} × {rows} 共 {created} 格 —— {detailTally}");
        ctx.Mutate(World.GlobalId, "meta.mapColumns", columns, "建图列数写回全局，UI 画边界与无头报告都要读（§3.9）");
        ctx.Mutate(World.GlobalId, "meta.mapRows", rows, "建图行数写回全局");
        ctx.Mutate(World.GlobalId, "meta.mapTiles", created, "地块总数");
    }

    /// <summary>
    /// 气候分类表：每条数据自带 <c>class.*</c> 区间与优先级（§3.2 的地表表就是数据）。
    /// 按优先级从高到低匹配，第一个命中的就是该格地表；区间有空档时直接抛异常，
    /// 逼数据作者把九类地表铺满整个 (高度, 温度, 降水, 边缘) 空间，而不是悄悄退化成某一类。
    /// </summary>
    private sealed record ClimateRule(
        DataEntry Entry, int Priority,
        int HeightMin, int HeightMax,
        int TempMin, int TempMax,
        int RainMin, int RainMax,
        bool EdgeOnly);

    private static List<ClimateRule> ClimateTable(IReadOnlyList<DataEntry> landforms)
    {
        if (landforms.Count == 0)
            throw new SimDataException($"数据包里没有 type={F.TypeLandform} 的记录，地图生成器无从分类");

        return landforms
            .Select(e => new ClimateRule(
                e,
                (int)e.GetInt("class.priority"),
                (int)e.GetInt("class.height.min", 0),
                (int)e.GetInt("class.height.max", 100),
                (int)e.GetInt("class.temp.min", 0),
                (int)e.GetInt("class.temp.max", 100),
                (int)e.GetInt("class.rain.min", 0),
                (int)e.GetInt("class.rain.max", 100),
                e.Get("class.edge").AsBool()))
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Entry.Id, StringComparer.Ordinal)   // 同优先级按 id 定序（§10.7 纪律 1）
            .ToList();
    }

    private static DataEntry Classify(List<ClimateRule> rules, int height, int temp, int rain, bool edge)
    {
        foreach (var rule in rules)
            if (height >= rule.HeightMin && height <= rule.HeightMax
                && temp >= rule.TempMin && temp <= rule.TempMax
                && rain >= rule.RainMin && rain <= rule.RainMax
                && (!rule.EdgeOnly || edge))
                return rule.Entry;

        throw new SimDataException(
            $"高度 {height} / 温度 {temp} / 降水 {rain} / 边缘 {edge} 没有匹配的地表：§3.2 的 class.* 区间有空档");
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

    private static long RoundScaled(long value) => value >= 0
        ? (value + NoiseScale / 2) / NoiseScale
        : -((-value + NoiseScale / 2) / NoiseScale);
}

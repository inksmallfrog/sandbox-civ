using SimCore;

namespace CoreBasic;

/// <summary>
/// 地图生成（GDD §3.7 六步管线的前四步 + §3.3 河网 + §3.1 水体层）。
///
/// M2 切片三的取舍与后续接口：
/// - 地图形状是 §3.9 / §13.1 的 <b>32 × 20 矩形</b>：按 odd-r 偏移坐标铺格，存的是轴坐标，
///   于是屏幕上是矩形、<see cref="Distance(int,int)"/> 仍是标准轴距（§3.1）。
/// - 高度图为「大陆轮廓（中央高、边缘低）+ 两层值噪声」；温度按行给纬度梯度，降水用两层值噪声（§3.7 第 3 步）。
/// - 九类地表（§3.2 全部行）由 <b>数据里声明的气候区间</b>命中，不在代码里写阈值；
///   水体是叠加层：最外一圈是海、被淹的洼地是湖，`hex.landform` 仍保留九类地表（§3.1）。
/// - 河网按 §3.3 存成 <b>有向边</b>：每格写 `hex.river.down`（下游地块）、`hex.river.flow`、`hex.river.width`；
///   生成用优先淹没法，parent 链必然入海，不会成环、不会有无出口的内陆湖（§3.7 公平性检查的一条）。
/// - §3.6 水利系数：河/湖相邻的陆格 Y_g × 1.15；水体格自己的 Y 走 `water.*` 数据（湖 2 / 近海 1）。
/// - 形胜（§3.4）、门槛资源（§3.5）、图层 4–5（§3.9）仍是后续切片；
///   深海（§3.2 水域行）等 M7 的海军切片再进来。
/// - 任何生成算法改动都必须递增 <see cref="CoreInfo.MapGenVersion"/> 并写进存档（§3.7 末行、§10.6）；
///   旧种子会长出新地图，这是明写要求，不是回归。
/// - 噪声与河网只用整数定点，不经过 float/double：地图字段进入世界哈希，
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

    /// <summary>
    /// 河段最小集水格数：含自身至少 12 格集水才画一段河。
    /// 阈值不是「像不像真的」的审美参数：定得太低（2）时 32×20 上有 336 段河，
    /// 一半的陆格都吃到 §3.6 的 1.15，水利系数就失去了区分度。
    /// </summary>
    private const int RiverMinFlow = 12;

    /// <summary>湖泊最小集水格数：淹没洼地只要有上游来水（含自身 ≥ 2）就记成湖（§3.1 湖）。</summary>
    private const int LakeMinFlow = 2;

    /// <summary>每多 4 格集水加一档河宽（§3.3 的 width 是档位，渲染与后续河运/渡口共用）。</summary>
    private const int RiverWidthStep = 4;
    private const int RiverMaxWidth = 4;

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
        var water = WaterTable(ctx.Data.OfType(F.TypeWaterbody));
        if (!water.TryGetValue("water.lake", out var lakeDef) || !water.TryGetValue("water.sea", out var seaDef))
            throw new SimDataException("数据包缺少 water.lake / water.sea（§3.2 的水域行），无法建水体");

        int count = columns * rows;
        var heights = new int[count];
        var temps = new int[count];
        var rains = new int[count];
        var landforms = new DataEntry[count];
        var ids = new EntityId[count];
        var sea = new bool[count];

        // §3.7 第 1–4 步：高度 → 温度/降水 → 地表。分类方式和切片二完全一致，
        // 水体是叠加层（§3.1）：这里的 landform 保留九类地表，hex.water 只额外说明「是不是水」。
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                int i = row * columns + col;
                var (q, r) = AxialOfOffset(col, row);
                ids[i] = HexId(q, r);

                long inlandCol = (long)Math.Min(col, columns - 1 - col) * NoiseScale / ((columns - 1) / 2);
                long inlandRow = (long)Math.Min(row, rows - 1 - row) * NoiseScale / ((rows - 1) / 2);
                long inland = Math.Min(inlandCol, inlandRow);
                long broad = Smooth(ctx, q, r, 6, 4241);
                long detail = Smooth(ctx, q, r, 2, 7919);
                long raw = 2 * NoiseScale + 80 * inland + (40 * broad + 24 * detail - 32 * NoiseScale);
                int h = (int)RoundScaled(raw);
                if (h < 0) h = 0; else if (h > 100) h = 100;
                heights[i] = h;

                int t = (int)RoundScaled((long)row * 100 * NoiseScale / (rows - 1)
                                         + (Smooth(ctx, q, r, 6, 3301) - NoiseScale / 2) * 20);
                if (t < 0) t = 0; else if (t > 100) t = 100;
                temps[i] = t;

                int p = (int)RoundScaled(58 * Smooth(ctx, q, r, 7, 5501) + 42 * Smooth(ctx, q, r, 3, 9001));
                if (p < 0) p = 0; else if (p > 100) p = 100;
                rains[i] = p;

                // 地图最外一圈是海（§3.1 近海），内圈仍按九类地表分类——
                // 这样「同形胜可叠加在地表上」的那条数据规则不用为水体重写一遍。
                sea[i] = col == 0 || row == 0 || col == columns - 1 || row == rows - 1;
                landforms[i] = Classify(climate, heights[i], temps[i], rains[i], sea[i]);
            }

        // §3.7 第 3 步（河网）：沿高程下降，保证每条河最终入海或入湖。
        // 做法是「优先淹没法」：从所有海格出发，按「淹没水位」从低到高扩张，
        // 每格记录把它淹掉的上游格作为下游 parent——于是 parent 链天然指向海，不会成环，
        // 也不会出现无出口的内陆湖。全程整数，比较用 (水位, hex id) 双键，跨平台确定（§10.7）。
        var level = new int[count];
        var parent = new int[count];
        Array.Fill(parent, -1);
        var visited = new bool[count];
        var frontier = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (!sea[i]) continue;
            visited[i] = true;
            level[i] = heights[i];
            frontier.Add(i);
        }

        var order = new List<int>(count);              // 被发现（= 被淹）的先后；parent 一定先于 child
        while (frontier.Count > 0)
        {
            int pick = 0;
            for (int k = 1; k < frontier.Count; k++)
                if (Lower(frontier[k], frontier[pick], level, ids)) pick = k;
            int cur = frontier[pick];
            frontier[pick] = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);

            foreach (var (dq, dr) in Neighbors)
            {
                if (!TryNeighbor(cur, dq, dr, columns, rows, out int nb) || visited[nb]) continue;
                visited[nb] = true;
                level[nb] = Math.Max(heights[nb], level[cur]);
                parent[nb] = cur;
                order.Add(nb);
                frontier.Add(nb);
            }
        }

        // 集水数：逆着发现顺序累加，child 一定先于 parent，一笔不漏。
        var flow = new int[count];
        Array.Fill(flow, 1);
        for (int k = order.Count - 1; k >= 0; k--)
        {
            int i = order[k];
            flow[parent[i]] += flow[i];
        }

        // 被淹没的陆格 = 湖泊（§3.1 湖）。只认有上游来水的洼地，零散小坑不画成湖。
        // 湖和河的阈值分开：湖只需要「有水进来」，河要有足够大的集水面才值得画（见 RiverMinFlow）。
        var lake = new bool[count];
        for (int i = 0; i < count; i++)
            lake[i] = !sea[i] && level[i] > heights[i] && flow[i] >= LakeMinFlow;

        var river = new bool[count];
        var riverWidth = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (sea[i] || lake[i] || parent[i] < 0 || flow[i] < RiverMinFlow) continue;
            river[i] = true;
            riverWidth[i] = Math.Clamp(1 + (flow[i] - RiverMinFlow) / RiverWidthStep, 1, RiverMaxWidth);
        }

        // §3.6 水利系数：河/湖相邻 1.15。河的「相邻」按边定义——边的两个端点都算；
        // 湖按距离 1 的陆格算。水体格自己的 Y 走水体数据，不叠这个系数。
        var watered = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (!river[i]) continue;
            watered[i] = true;
            watered[parent[i]] = true;
        }
        for (int i = 0; i < count; i++)
        {
            if (!lake[i]) continue;
            foreach (var (dq, dr) in Neighbors)
                if (TryNeighbor(i, dq, dr, columns, rows, out int nb) && !sea[nb] && !lake[nb])
                    watered[nb] = true;
        }

        var tally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var waterTally = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int rivers = 0;

        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                int i = row * columns + col;
                var (q, r) = AxialOfOffset(col, row);
                EntityId id = ids[i];
                var landform = landforms[i];
                bool isSea = sea[i], isLake = lake[i];
                string waterId = isSea ? seaDef.Id : isLake ? lakeDef.Id : "";
                DataEntry waterDef = isSea ? seaDef : lakeDef;

                tally[landform.Id] = tally.GetValueOrDefault(landform.Id) + 1;
                if (waterId.Length > 0)
                    waterTally[waterId] = waterTally.GetValueOrDefault(waterId) + 1;

                ctx.CreateEntity(F.KindHex, "新开局建图（§3.7 第 1–4 步 + §3.1 水体 + §3.3 河网）", id);
                ctx.Mutate(id, F.HexQ, q, "轴坐标 q（§3.1），与 id 互为冗余，读档后可互相校验");
                ctx.Mutate(id, F.HexR, r, "轴坐标 r");
                ctx.Mutate(id, F.HexHeight, heights[i], "高度图（§3.7 第 1 步）。形胜识别（§3.4）在 M2 读它");
                ctx.Mutate(id, F.HexTemp, temps[i], "温度（§3.7）：纬度梯度 + 小噪声；冻土分类读它");
                ctx.Mutate(id, F.HexRain, rains[i], "降水（§3.7）：两层值噪声；荒漠/湿地分类读它");
                ctx.Mutate(id, F.HexLandform, landform.Id,
                    $"高度 {heights[i]} / 温度 {temps[i]} / 降水 {rains[i]}{(isSea ? " / 边缘" : "")} 命中「{landform.GetStr("title")}」的气候区间（§3.2 / §3.7 第 4 步）");
                ctx.Mutate(id, F.HexMoveCost, landform.GetInt("moveCost"), "§3.6 地表通行成本：行军与运输都读它（水上行军归 M7）");
                ctx.Mutate(id, F.HexDefense, landform.GetInt("defense"), "§3.6 地表防御：M1 只写不算，军事在 M7");
                ctx.Mutate(id, F.HexBuildable, SimValue.OfBool(waterId.Length == 0 && landform.Get("buildable").AsBool()),
                    waterId.Length == 0
                        ? "§3.2 可否建城（山地只能做隘口）"
                        : $"§3.8：水域不是城址（{waterDef.GetStr("title")}）");

                if (waterId.Length > 0)
                {
                    ctx.Mutate(id, F.HexWater, waterId, $"{waterDef.GetStr("title")}（§3.1 水体层）");
                    Fix y = waterDef.GetFix("potential.food");
                    ctx.Mutate(id, F.HexYieldFood, y,
                        $"{waterDef.GetStr("title")}（§3.2 水域行）：粮潜力 {y.Text}，不叠地表潜力与水利系数");
                }
                else
                {
                    Fix potential = landform.GetFix("potential.food");
                    Fix y = watered[i] ? Fix.Mul(potential, Fix.FromPercent(115)) : potential;
                    ctx.Mutate(id, F.HexYieldFood, y, watered[i]
                        ? $"Y_g = 地表潜力 {potential.Text} × 水利系数 1.15（§3.6：河/湖相邻）"
                        : $"Y_g 粮潜力（§3.6 第一行）：仍无形胜/资源倍率，水利系数 1.00");
                }

                if (!river[i]) continue;
                rivers++;
                ctx.Mutate(id, F.HexRiverDown, ids[parent[i]].Value,
                    "§3.3 河流有向边：本格 → 下游（沿淹没水位下降的方向）");
                ctx.Mutate(id, F.HexRiverFlow, flow[i], $"§3.3 flow：集水 {flow[i]} 格（含自身）");
                ctx.Mutate(id, F.HexRiverWidth, riverWidth[i],
                    $"§3.3 width：{riverWidth[i]} 档（每 {RiverWidthStep} 格集水 +1，上限 {RiverMaxWidth}）");
            }

        var detailTally = string.Join("、", tally.Select(kv =>
            $"{ctx.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格"));
        var waterDetail = string.Join("、", waterTally.Select(kv =>
            $"{ctx.Data.Get(kv.Key).GetStr("title")} {kv.Value} 格"));
        ctx.Note($"地图：{columns} × {rows} 共 {count} 格 —— {detailTally}；水域 {waterDetail}；河段 {rivers} 段");
        ctx.Mutate(World.GlobalId, "meta.mapColumns", columns, "建图列数写回全局，UI 画边界与无头报告都要读（§3.9）");
        ctx.Mutate(World.GlobalId, "meta.mapRows", rows, "建图行数写回全局");
        ctx.Mutate(World.GlobalId, "meta.mapTiles", count, "地块总数");
        ctx.Mutate(World.GlobalId, "meta.mapWater", (long)waterTally.Values.Sum(),
            "水域格数（湖 + 近海，§3.1），图层与无头报告读它");
        ctx.Mutate(World.GlobalId, "meta.mapRivers", rivers, "河段数（§3.3），无头报告与跨宿主对账读它");
    }

    /// <summary>水体数据按 id 建表（§3.2 水域行只认 water.lake / water.sea 两条）。</summary>
    private static Dictionary<string, DataEntry> WaterTable(IReadOnlyList<DataEntry> waterbodies)
    {
        var table = new Dictionary<string, DataEntry>(StringComparer.Ordinal);
        foreach (var e in waterbodies) table[e.Id] = e;
        return table;
    }

    /// <summary>淹没优先队列的定序：先比水位，再比 hex id 的 ordinal（§10.7 纪律 1）。</summary>
    private static bool Lower(int a, int b, int[] level, EntityId[] ids) =>
        level[a] != level[b] ? level[a] < level[b]
                             : string.CompareOrdinal(ids[a].Value, ids[b].Value) < 0;

    /// <summary>轴坐标邻居 → 矩形网格下标；出界返回 false。用它就不必把 6 个方向写成 6 段判断。</summary>
    private static bool TryNeighbor(int index, int dq, int dr, int columns, int rows, out int neighbor)
    {
        int row = index / columns, col = index % columns;
        var (q, r) = AxialOfOffset(col, row);
        int nr = r + dr;
        if (nr < 0 || nr >= rows) { neighbor = -1; return false; }
        int nc = q + dq + ((nr - (nr & 1)) / 2);
        if (nc < 0 || nc >= columns) { neighbor = -1; return false; }
        neighbor = nr * columns + nc;
        return true;
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

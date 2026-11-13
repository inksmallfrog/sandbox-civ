using SimCore;

namespace CoreBasic;

/// <summary>
/// 形胜识别（GDD §3.4 十二种）与门槛资源生成（§3.5 五种）。
///
/// 这一层只做「从地图事实推出标签」，不写世界字段，也不碰 Y_g——
/// <see cref="MapGen"/> 拿到标签后按 §3.6 的乘法链把倍率落进 <c>hex.yield.food/mat</c>。
/// 拆成独立文件的原因是 MapGen 已经负责高度/气候/河网三件事，再把十二条识别规则塞进去会过一千行。
///
/// 三条纪律：
/// 1. 全部整数比较与整数哈希，不出现 float/double/Pow/Exp/三角函数（§10.7）。
/// 2. 遍历顺序固定为「网格下标升序 + 邻居表固定顺序」，组件与平票都按 hex id 的 ordinal 收口。
/// 3. 识别阈值是生成器参数，不是玩法数值；改它们会重排地图哈希，必须进 CHANGELOG。
///    GDD §3.4 只给了部分经济倍率（盆地 1.3 / 山谷料 1.2 / 三角洲 1.5 / 绿洲 1.5 / 火山料 1.5），
///    识别阈值与其余倍率的占位值记在 docs/CHANGELOG-DESIGN.md D-036。
/// </summary>
internal static class Features
{
    /// <summary>GDD §3.4 的十二种形胜，顺序只用于报错与盘点（同一格冲突由数据里的 priority 决定）。</summary>
    public static readonly string[] ShapeIds =
    [
        "shape.basin", "shape.valley", "shape.pass", "shape.estuary", "shape.delta", "shape.headwater",
        "shape.gorge", "shape.island", "shape.oasis", "shape.saltlake", "shape.fan", "shape.volcano"
    ];

    /// <summary>GDD §3.5 的五种门槛资源；顺序就是同一格的优先级（实际地形互斥，几乎不会撞）。</summary>
    public static readonly string[] ResourceIds =
    [
        "resource.iron", "resource.horse", "resource.salt", "resource.spice", "resource.fish"
    ];

    /// <summary>「大河」的门槛：集水 ≥ 20 格的河入海才算三角洲，否则只算河口（§3.4）。</summary>
    public const int DeltaMinFlow = 20;

    /// <summary>高地门槛：§3.2 的丘陵从高度 60 起，形胜的「环山/夹山」统一按 62 判，避免贴边抖动。</summary>
    private const int HighHeight = 62;

    /// <summary>低地门槛：盆地/山谷/隘口都要求候选格本身在低处。</summary>
    private const int LowHeight = 45;

    /// <summary>山谷/隘口的低地槽门槛：比盆地宽一点，避免把盆地边缘算成山谷。</summary>
    private const int CorridorLowHeight = 55;

    private const int BasinMinTiles = 4;
    private const int BasinMaxTiles = 20;

    /// <summary>火山稀有度：每千个山地格约 12 个候选，再按哈希只留最多 2 个（§3.4「稀有」）。</summary>
    private const int VolcanoPerMille = 30;
    private const int VolcanoMaxPerMap = 2;

    public sealed record Result(
        string[] Shape,
        string[] Resource,
        SortedDictionary<string, int> ShapeTally,
        SortedDictionary<string, int> ResourceTally);

    private sealed class Grid
    {
        public required int Columns { get; init; }
        public required int Rows { get; init; }
        public required int[] Heights { get; init; }
        public required int[] Temps { get; init; }
        public required int[] Rains { get; init; }
        public required bool[] Sea { get; init; }
        public required bool[] Lake { get; init; }
        public required bool[] River { get; init; }
        public required int[] Flow { get; init; }
        public required int[] Parent { get; init; }
        public required DataEntry[] Landforms { get; init; }
        public required EntityId[] Ids { get; init; }

        public int Count => Columns * Rows;
        public bool IsLand(int i) => !Sea[i] && !Lake[i];
        public bool IsWater(int i) => Sea[i] || Lake[i];
    }

    /// <summary>
    /// 入口：先识别形胜，再按形胜（盐湖）与地表生成资源。返回与网格下标对齐的两个标签数组。
    /// 数据里缺任何一条 §3.4/§3.5 记录都直接抛——「十二种」「五种」是 GDD 的清单，不是可选装饰。
    /// </summary>
    public static Result Identify(
        IStepContext ctx,
        int columns, int rows,
        int[] heights, int[] temps, int[] rains,
        bool[] sea, bool[] lake, bool[] river, int[] flow, int[] parent,
        DataEntry[] landforms, EntityId[] ids,
        IReadOnlyDictionary<string, DataEntry> shapes,
        IReadOnlyDictionary<string, DataEntry> resources)
    {
        foreach (string id in ShapeIds)
            if (!shapes.ContainsKey(id))
                throw new SimDataException($"数据包缺少 {id}（§3.4 十二种形胜），无法识别");
        foreach (string id in ResourceIds)
            if (!resources.ContainsKey(id))
                throw new SimDataException($"数据包缺少 {id}（§3.5 五种门槛资源），无法生成");

        var g = new Grid
        {
            Columns = columns, Rows = rows,
            Heights = heights, Temps = temps, Rains = rains,
            Sea = sea, Lake = lake, River = river, Flow = flow, Parent = parent,
            Landforms = landforms, Ids = ids,
        };

        var shape = new string[g.Count];
        var shapePriority = new int[g.Count];
        Array.Fill(shape, "");

        IdentifyBasins(g, shape, shapePriority, shapes);
        IdentifyValleys(g, shape, shapePriority, shapes);
        IdentifyPasses(g, shape, shapePriority, shapes);
        IdentifyRivers(g, shape, shapePriority, shapes);
        IdentifyIslands(g, shape, shapePriority, shapes);
        IdentifyOasis(g, shape, shapePriority, shapes);
        IdentifySaltLakes(g, shape, shapePriority, shapes);
        IdentifyFans(g, shape, shapePriority, shapes);
        IdentifyVolcanoes(ctx, g, shape, shapePriority, shapes);

        var resource = GenerateResources(ctx, g, shape, resources);

        var shapeTally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var resourceTally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < g.Count; i++)
        {
            if (shape[i].Length > 0) shapeTally[shape[i]] = shapeTally.GetValueOrDefault(shape[i]) + 1;
            if (resource[i].Length > 0) resourceTally[resource[i]] = resourceTally.GetValueOrDefault(resource[i]) + 1;
        }

        return new Result(shape, resource, shapeTally, resourceTally);
    }

    // ================= 形胜：盆地 / 山谷 / 隘口 =================

    /// <summary>
    /// 盆地：环形高地围出的低地。判据不是「整块低地组件」——32×20 上低地常连成一大片，
    /// 真正的盆地是「周围两圈里高地的占比够高」的那一小块。所以先在低地格里筛出环山候选，
    /// 再把相邻候选收成 6–20 格的一组；这样连着一个出口的盆地也能被认出来（§3.4 的「1–2 出口」）。
    /// </summary>
    private static void IdentifyBasins(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        var candidate = new bool[g.Count];
        for (int i = 0; i < g.Count; i++)
        {
            if (!g.IsLand(i) || g.Heights[i] > LowHeight) continue;
            if (TouchesBorder(i, g.Columns, g.Rows)) continue;

            // 完整看距离 1–2 的 18 格：只沿 6 条轴走会漏掉距离 2 的 6 个「斜角」，
            // 排水口恰好落在斜角时会被误判成「环不闭合」。
            int high = 0, total = 0;
            var (q, r) = MapGen.Coords(g.Ids[i]);
            for (int dq = -2; dq <= 2; dq++)
                for (int dr = -2; dr <= 2; dr++)
                {
                    int dist = MapGen.Distance(dq, dr);
                    if (dist is < 1 or > 2) continue;
                    if (!TryAxial(g, q + dq, r + dr, out int nb)) continue;
                    total++;
                    if (g.Heights[nb] >= HighHeight) high++;
                }
            if (total > 0 && high * 100 >= total * 45) candidate[i] = true;
        }

        // 候选格只说明「自己周围两圈够高」；盆地的「8–20 格低地」要从候选格向低地扩一圈。
        // 限定距离 ≤2，既不会把整片低地算进来，也能把雕刻出来的 7 格盆底（中心 + 六邻）收全。
        var claimed = new bool[g.Count];
        var queue = new List<int>();
        for (int seed = 0; seed < g.Count; seed++)
        {
            if (!candidate[seed] || claimed[seed]) continue;

            var comp = new List<int>();
            queue.Clear();
            queue.Add(seed);
            claimed[seed] = true;
            var (sq, sr) = MapGen.Coords(g.Ids[seed]);

            for (int head = 0; head < queue.Count; head++)
            {
                int cur = queue[head];
                comp.Add(cur);
                var (cq, cr) = MapGen.Coords(g.Ids[cur]);
                for (int dir = 0; dir < 6; dir++)
                {
                    if (!TryN(g, cur, dir, out int nb) || claimed[nb]) continue;
                    if (!g.IsLand(nb) || g.Heights[nb] > LowHeight) continue;
                    var (nq, nr) = MapGen.Coords(g.Ids[nb]);
                    if (MapGen.Distance(nq - sq, nr - sr) > 2) continue;
                    claimed[nb] = true;
                    queue.Add(nb);
                }
            }

            if (comp.Count < BasinMinTiles || comp.Count > BasinMaxTiles) continue;
            foreach (int t in comp) Assign(shape, priority, t, "shape.basin", shapes);
        }
    }

    /// <summary>
    /// 山谷：低地格被两侧高地夹住。按格判而不是按整块低地组件判——低地常连成一大片，
    /// 按组件会把整条河谷沿岸都算成山谷；这里只标「确实两山夹一槽」的槽底（§3.4）。
    /// 盆地/隘口/峡谷的 priority 更高，会在同一格覆盖它。
    /// </summary>
    private static void IdentifyValleys(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        for (int i = 0; i < g.Count; i++)
        {
            if (!g.IsLand(i) || g.Heights[i] > CorridorLowHeight) continue;
            if (shape[i] == "shape.basin") continue;
            if (!HasOppositeHigh(g, i, HighHeight, out _)) continue;

            int highNeighbors = 0;
            for (int k = 0; k < 6; k++)
                if (TryN(g, i, k, out int nb) && g.Heights[nb] >= HighHeight) highNeighbors++;
            if (highNeighbors >= 2) Assign(shape, priority, i, "shape.valley", shapes);
        }
    }

    /// <summary>
    /// 隘口：低地格两侧被高地夹住（山脊上的孔洞），且它所在的低地组件要么是 1–2 格的小孔、
    /// 要么是连接两片大低地的通道——排除 6–15 格的山谷（§3.4）。
    /// </summary>
    private static void IdentifyPasses(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        int[] lowComp = ComponentSizes(g, i => g.IsLand(i) && g.Heights[i] <= CorridorLowHeight);

        for (int i = 0; i < g.Count; i++)
        {
            if (!g.IsLand(i) || g.Heights[i] > CorridorLowHeight) continue;
            if (lowComp[i] is > 2 and < 16) continue;                 // 山谷，不是隘口
            if (!HasOppositeHigh(g, i, HighHeight, out _)) continue;

            int highNeighbors = 0;
            for (int dir = 0; dir < 6; dir++)
                if (TryN(g, i, dir, out int nb) && g.Heights[nb] >= HighHeight) highNeighbors++;
            if (highNeighbors < 3) continue;

            Assign(shape, priority, i, "shape.pass", shapes);
        }
    }

    // ================= 形胜：河口 / 三角洲 / 上游高地 / 峡谷 =================

    /// <summary>河流类形胜：河入海分河口/三角洲，无上游来水的河段是上游高地，夹岸高地的河段是峡谷（§3.4）。</summary>
    private static void IdentifyRivers(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        for (int i = 0; i < g.Count; i++)
        {
            if (!g.River[i]) continue;

            int parent = g.Parent[i];
            if (parent >= 0 && g.Sea[parent])
                Assign(shape, priority, i, g.Flow[i] >= DeltaMinFlow ? "shape.delta" : "shape.estuary", shapes);

            bool hasUpstream = false;
            for (int dir = 0; dir < 6; dir++)
                if (TryN(g, i, dir, out int nb) && g.River[nb] && g.Parent[nb] == i) { hasUpstream = true; break; }
            if (!hasUpstream) Assign(shape, priority, i, "shape.headwater", shapes);

            if (HasOppositeHigh(g, i, HighHeight, out _))
                Assign(shape, priority, i, "shape.gorge", shapes);
        }
    }

    // ================= 形胜：湖岛 / 绿洲 / 盐湖 / 冲积扇 / 火山 =================

    /// <summary>湖岛/半岛：陆格至少 4 条边挨着水（§3.4）。</summary>
    private static void IdentifyIslands(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        for (int i = 0; i < g.Count; i++)
        {
            if (!g.IsLand(i)) continue;
            int water = 0;
            for (int dir = 0; dir < 6; dir++)
                if (TryN(g, i, dir, out int nb) && g.IsWater(nb)) water++;
            if (water >= 4) Assign(shape, priority, i, "shape.island", shapes);
        }
    }

    /// <summary>绿洲：荒漠格临河或临湖（§3.4「荒漠中成串」；临海不算，那是海岸）。</summary>
    private static void IdentifyOasis(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        for (int i = 0; i < g.Count; i++)
        {
            if (!g.IsLand(i) || g.Landforms[i].Id != "landform.desert") continue;
            bool water = g.River[i];
            if (!water)
                for (int dir = 0; dir < 6; dir++)
                    if (TryN(g, i, dir, out int nb) && (g.River[nb] || g.Lake[nb])) { water = true; break; }
            if (water) Assign(shape, priority, i, "shape.oasis", shapes);
        }
    }

    /// <summary>盐湖：内流湖的末端 + 干旱气候（雨 ≤ 35）。湖泊本身是终点的判据在河网生成时已保证（§3.4）。</summary>
    private static void IdentifySaltLakes(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        for (int i = 0; i < g.Count; i++)
            if (g.Lake[i] && g.Rains[i] <= 35)
                Assign(shape, priority, i, "shape.saltlake", shapes);
    }

    /// <summary>冲积扇：山前坡地（低处、挨着山地）且临河（§3.4）。</summary>
    private static void IdentifyFans(
        Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        for (int i = 0; i < g.Count; i++)
        {
            if (!g.IsLand(i) || g.Heights[i] > LowHeight) continue;

            bool mountain = false, water = g.River[i];
            for (int dir = 0; dir < 6; dir++)
            {
                if (!TryN(g, i, dir, out int nb)) continue;
                if (g.Heights[nb] >= HighHeight || g.Landforms[nb].Id == "landform.mountain") mountain = true;
                if (g.River[nb] || g.Lake[nb]) water = true;
            }
            if (mountain && water) Assign(shape, priority, i, "shape.fan", shapes);
        }
    }

    /// <summary>火山：山地格按确定性哈希稀疏抽取，最多 2 座（§3.4「稀有」）。</summary>
    private static void IdentifyVolcanoes(
        IStepContext ctx, Grid g, string[] shape, int[] priority, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        var candidates = new List<(ulong hash, string id, int index)>();
        for (int i = 0; i < g.Count; i++)
        {
            if (!g.IsLand(i)) continue;
            if (g.Heights[i] < 80 && g.Landforms[i].Id != "landform.mountain") continue;
            ulong h = TileHash(ctx.Rng.WorldSeed, "volcano", g.Ids[i]);
            if ((int)(h % 1000) < VolcanoPerMille) candidates.Add((h, g.Ids[i].Value, i));
        }

        candidates.Sort((a, b) => a.hash != b.hash
            ? a.hash.CompareTo(b.hash)
            : string.CompareOrdinal(a.id, b.id));

        for (int k = 0; k < Math.Min(VolcanoMaxPerMap, candidates.Count); k++)
            Assign(shape, priority, candidates[k].index, "shape.volcano", shapes);
    }

    // ================= 门槛资源（§3.5）=================

    /// <summary>
    /// 资源按「地形资格 + 每格确定性哈希」生成；每格的资源写一个（§3.5 是标签，不是第 6 种商品）。
    /// 生成后再补一遍「每种至少一格」：32×20 上某些地形可能整片不出现，缺一种会让门槛玩法直接断线。
    /// </summary>
    private static string[] GenerateResources(
        IStepContext ctx, Grid g, string[] shape, IReadOnlyDictionary<string, DataEntry> resources)
    {
        var resource = new string[g.Count];
        Array.Fill(resource, "");

        for (int i = 0; i < g.Count; i++)
        {
            foreach (string rid in ResourceIds)
            {
                if (!Eligible(rid, i, g, shape)) continue;
                if ((int)(TileHash(ctx.Rng.WorldSeed, rid, g.Ids[i]) % 1000) >= Rate(rid)) continue;
                resource[i] = rid;
                break;
            }
        }

        foreach (string rid in ResourceIds)
        {
            if (Array.Exists(resource, r => r == rid)) continue;

            int best = -1;
            ulong bestHash = 0;
            for (int i = 0; i < g.Count; i++)
            {
                if (resource[i].Length > 0 || !Eligible(rid, i, g, shape)) continue;
                ulong h = TileHash(ctx.Rng.WorldSeed, rid, g.Ids[i]);
                if (best < 0 || h > bestHash) { best = i; bestHash = h; }
            }
            if (best >= 0) resource[best] = rid;
        }

        return resource;
    }

    private static bool Eligible(string rid, int i, Grid g, string[] shape) => rid switch
    {
        "resource.iron" => g.IsLand(i) && (g.Landforms[i].Id == "landform.hill" || g.Landforms[i].Id == "landform.mountain"),
        "resource.horse" => g.IsLand(i) && g.Landforms[i].Id == "landform.grassland",
        "resource.salt" => (g.IsLand(i) && g.Landforms[i].Id == "landform.desert" && g.Rains[i] <= 30)
                           || shape[i] == "shape.saltlake",
        "resource.spice" => g.IsLand(i) && g.Landforms[i].Id == "landform.forest" && g.Temps[i] >= 50,
        "resource.fish" => g.IsWater(i) && shape[i] != "shape.saltlake",
        _ => false,
    };

    private static int Rate(string rid) => rid switch
    {
        "resource.iron" => 120,
        "resource.horse" => 120,
        "resource.salt" => 180,
        "resource.spice" => 140,
        "resource.fish" => 300,
        _ => 0,
    };

    // ================= 工具 =================

    private static void Assign(
        string[] shape, int[] priority, int index, string id, IReadOnlyDictionary<string, DataEntry> shapes)
    {
        int p = (int)shapes[id].GetInt("priority", 100);
        if (p <= priority[index] && shape[index].Length > 0) return;
        shape[index] = id;
        priority[index] = p;
    }

    private static bool HasOppositeHigh(Grid g, int i, int minHeight, out int dir)
    {
        for (int k = 0; k < 3; k++)
            if (TryN(g, i, k, out int a) && TryN(g, i, k + 3, out int b)
                && g.Heights[a] >= minHeight && g.Heights[b] >= minHeight)
            { dir = k; return true; }
        dir = -1;
        return false;
    }

    private static bool TryN(Grid g, int index, int dir, out int neighbor) =>
        MapGen.TryNeighbor(index, MapGen.Neighbors[dir].dq, MapGen.Neighbors[dir].dr, g.Columns, g.Rows, out neighbor);

    /// <summary>轴坐标 → 矩形网格下标；出界返回 false。</summary>
    private static bool TryAxial(Grid g, int q, int r, out int index)
    {
        if (r < 0 || r >= g.Rows) { index = -1; return false; }
        int col = q + ((r - (r & 1)) / 2);
        if (col < 0 || col >= g.Columns) { index = -1; return false; }
        index = r * g.Columns + col;
        return true;
    }

    private static bool TouchesBorder(int index, int columns, int rows)
    {
        int row = index / columns, col = index % columns;
        return col == 0 || row == 0 || col == columns - 1 || row == rows - 1;
    }

    /// <summary>低地组件：按网格下标升序做 BFS，组件内部再按 id 序数排序，跨平台确定（§10.7）。</summary>
    private static List<List<int>> Components(Grid g, Func<int, bool> inside)
    {
        var seen = new bool[g.Count];
        var result = new List<List<int>>();
        var queue = new List<int>();

        for (int start = 0; start < g.Count; start++)
        {
            if (seen[start] || !inside(start)) continue;
            var comp = new List<int>();
            queue.Clear();
            queue.Add(start);
            seen[start] = true;

            for (int head = 0; head < queue.Count; head++)
            {
                int cur = queue[head];
                comp.Add(cur);
                for (int dir = 0; dir < 6; dir++)
                {
                    if (!TryN(g, cur, dir, out int nb) || seen[nb] || !inside(nb)) continue;
                    seen[nb] = true;
                    queue.Add(nb);
                }
            }

            comp.Sort((a, b) => string.CompareOrdinal(g.Ids[a].Value, g.Ids[b].Value));
            result.Add(comp);
        }
        return result;
    }

    /// <summary>每个格所属低地组件的格数（不在低地/陆地的格为 0）。</summary>
    private static int[] ComponentSizes(Grid g, Func<int, bool> inside)
    {
        var size = new int[g.Count];
        foreach (var comp in Components(g, inside))
            foreach (int i in comp) size[i] = comp.Count;
        return size;
    }

    private static ulong TileHash(ulong seed, string salt, EntityId id) =>
        StableHash.Mix(StableHash.Of(salt + ":" + id.Value), seed);
}

using SimCore;

namespace CoreBasic;

/// <summary>
/// 地图骨架雕刻（GDD §3.7 第 6 步的「形胜识别 + 出生点公平性修正」里，**保证战略点存在**的那一半）。
///
/// 为什么需要它：纯噪声图在 32×20 上经常长不出「环形高地围出的低地」和「两山夹一槽」——
/// 高地往往是一整块，低地连成一片，于是 §3.4 的盆地/山谷/峡谷只能靠运气。
/// M2 的验收要求「同形胜在 20 张图里人工可认出」，所以生成器必须主动雕刻几处骨架，
/// 而不是把识别规则放松到「到处都是形胜」（那会让形胜失去 §3.4 的战略含义）。
///
/// 三处雕刻，全部按种子哈希选点、按固定方向展开，跨平台确定（§10.7）：
/// 1. 盆地：内陆低地选一点，压平 7 格低地，把距离 2 的一圈抬到丘陵高度；
/// 2. 山谷：内陆高地选一点，沿一个方向挖 6–10 格槽，槽两侧抬到丘陵高度；
/// 3. 峡谷：河网生成后，挑一段高流量河，把两岸抬到丘陵高度并把河床压低（§3.4 的深窄河谷）。
/// 雕刻只改 `heights`，随后仍走同一个气候分类；不写任何玩法字段，也不改河网走向。
/// </summary>
internal static class Sculpt
{
    /// <summary>盆地内圈的低地高度（低于 §3.4 的盆地判据 ≤45）。</summary>
    private const int BasinFloor = 30;

    /// <summary>盆地外圈与山谷两侧的「丘陵」高度（≥ Features 的 HighHeight=62）。</summary>
    private const int HighWall = 66;

    /// <summary>山谷槽底高度（≤ Features 的 CorridorLowHeight=55）。</summary>
    private const int ValleyFloor = 46;

    /// <summary>内陆安全边距：中心离地图边界至少 4 格，保证距离 2 的环还在图内。</summary>
    private const int SafeMargin = 4;

    /// <summary>盆地点选范围：太低会撞海岸，太高会撞山地；15–55 是「低地候选」。</summary>
    private const int BasinPickMin = 15;
    private const int BasinPickMax = 55;

    /// <summary>山谷起点必须是高地（≥65），否则挖出来的槽没有「两山夹」的形。</summary>
    private const int ValleyPickMin = 65;

    /// <summary>雕刻盆地与山谷（在气候分类与河网之前调用）。</summary>
    public static void CarveBasinAndValley(
        IStepContext ctx, int columns, int rows, int[] heights, bool[] sea, EntityId[] ids)
    {
        var basin = PickBasin(ctx, columns, rows, heights, sea, ids);
        if (basin >= 0)
        {
            CarveBasin(ctx, columns, rows, heights, sea, ids, basin);
            CarveValley(ctx, columns, rows, heights, sea, ids, basin);
        }
    }

    /// <summary>
    /// 雕刻峡谷（在河网之后调用）：返回被改动的格下标，调用方据此重新分类地表。
    /// 选河段的条件是「两岸都是陆地」——否则抬高的会是湖/海，视觉上不成立。
    /// </summary>
    public static List<int> CarveGorge(
        int columns, int rows, int[] heights, bool[] sea, bool[] lake, bool[] river, int[] flow, int[] parent, EntityId[] ids)
    {
        var affected = new List<int>();
        int best = -1;
        int bestDir = -1;

        for (int i = 0; i < heights.Length; i++)
        {
            if (!river[i] || parent[i] < 0 || sea[i] || lake[i]) continue;

            int dir = DirectionTo(columns, rows, i, parent[i]);
            if (dir < 0) continue;

            // 河的两岸 = 与河流方向垂直的两对邻居；优先选「两岸都是陆地」的那一对。
            int pair = -1;
            foreach (int candidate in new[] { (dir + 1) % 6, (dir + 2) % 6 })
            {
                if (TryNeighbor(columns, rows, i, candidate, out int a)
                    && TryNeighbor(columns, rows, i, (candidate + 3) % 6, out int b)
                    && !sea[a] && !lake[a] && !sea[b] && !lake[b])
                { pair = candidate; break; }
            }
            if (pair < 0) continue;

            if (best < 0 || flow[i] > flow[best]
                || (flow[i] == flow[best] && string.CompareOrdinal(ids[i].Value, ids[best].Value) < 0))
            {
                best = i;
                bestDir = pair;
            }
        }

        if (best < 0) return affected;

        heights[best] = Math.Min(heights[best], ValleyFloor);
        affected.Add(best);
        foreach (int dir in new[] { bestDir, (bestDir + 3) % 6 })
        {
            if (!TryNeighbor(columns, rows, best, dir, out int nb)) continue;
            heights[nb] = Math.Max(heights[nb], HighWall);
            affected.Add(nb);
        }
        return affected;
    }

    private static int PickBasin(
        IStepContext ctx, int columns, int rows, int[] heights, bool[] sea, EntityId[] ids)
    {
        var candidates = new List<(ulong hash, string id, int index)>();
        for (int i = 0; i < heights.Length; i++)
        {
            if (sea[i]) continue;
            int row = i / columns, col = i % columns;
            if (col < SafeMargin || row < SafeMargin || col >= columns - SafeMargin || row >= rows - SafeMargin) continue;
            if (heights[i] < BasinPickMin || heights[i] > BasinPickMax) continue;
            candidates.Add((TileHash(ctx.Rng.WorldSeed, "basin", ids[i]), ids[i].Value, i));
        }
        if (candidates.Count == 0) return -1;

        candidates.Sort((a, b) => a.hash != b.hash
            ? a.hash.CompareTo(b.hash)
            : string.CompareOrdinal(a.id, b.id));
        return candidates[0].index;
    }

    private static void CarveBasin(
        IStepContext ctx, int columns, int rows, int[] heights, bool[] sea, EntityId[] ids, int center)
    {
        if (sea[center]) return;
        heights[center] = Math.Min(heights[center], BasinFloor);

        for (int dir = 0; dir < 6; dir++)
        {
            if (!TryNeighbor(columns, rows, center, dir, out int nb) || sea[nb]) continue;
            heights[nb] = Math.Min(heights[nb], BasinFloor);
        }

        // 距离 2 的一圈：12 格（轴坐标距离公式）；抬到丘陵高度，形成「环形高地」。
        // 但要留一个比盆底更低的排水口，否则优先生成会把盆地灌成湖（§3.1 湖泊），
        // 盆地就不再是 §3.4 的「可建的盆地级战略点」了。排水口只开 1–2 格，对应 1–2 个出口。
        var (cq, cr) = MapGen.Coords(ids[center]);
        int outletDir = (int)(TileHash(ctx.Rng.WorldSeed, "basinout", ids[center]) % 6);
        for (int dq = -2; dq <= 2; dq++)
            for (int dr = -2; dr <= 2; dr++)
            {
                if (MapGen.Distance(dq, dr) != 2) continue;
                if (!TryAxial(columns, rows, cq + dq, cr + dr, out int index) || sea[index]) continue;
                heights[index] = Math.Max(heights[index], HighWall);
            }

        // 排水口要一路挖到海，不能只低一格：盆地外侧只要有一道高坎，优先淹没法仍会把盆底灌成湖。
        var (outDq, outDr) = MapGen.Neighbors[outletDir];
        for (int step = 2; ; step++)
        {
            if (!TryAxial(columns, rows, cq + outDq * step, cr + outDr * step, out int index)) break;
            if (sea[index]) break;
            heights[index] = Math.Min(heights[index], BasinFloor - 10);
        }
    }

    private static void CarveValley(
        IStepContext ctx, int columns, int rows, int[] heights, bool[] sea, EntityId[] ids, int basin)
    {
        var (bq, br) = MapGen.Coords(ids[basin]);
        var candidates = new List<(ulong hash, string id, int index)>();
        for (int i = 0; i < heights.Length; i++)
        {
            if (sea[i] || heights[i] < ValleyPickMin) continue;
            int row = i / columns, col = i % columns;
            if (col < SafeMargin || row < SafeMargin || col >= columns - SafeMargin || row >= rows - SafeMargin) continue;
            var (q, r) = MapGen.Coords(ids[i]);
            if (MapGen.Distance(q - bq, r - br) < 5) continue;          // 别和盆地环重叠
            candidates.Add((TileHash(ctx.Rng.WorldSeed, "valley", ids[i]), ids[i].Value, i));
        }
        if (candidates.Count == 0) return;

        candidates.Sort((a, b) => a.hash != b.hash
            ? a.hash.CompareTo(b.hash)
            : string.CompareOrdinal(a.id, b.id));
        int start = candidates[0].index;
        int dir = (int)(TileHash(ctx.Rng.WorldSeed, "valleydir", ids[start]) % 6);
        int length = 6 + (int)(TileHash(ctx.Rng.WorldSeed, "valleylen", ids[start]) % 5);   // 6–10 格

        int cur = start;
        for (int step = 0; step < length; step++)
        {
            if (sea[cur]) break;

            // 别让山谷切进盆地：盆地环一旦被挖开，盆底会被优先淹没法灌成湖（§3.1）。
            var (curQ, curR) = MapGen.Coords(ids[cur]);
            if (MapGen.Distance(curQ - bq, curR - br) <= 3) break;

            heights[cur] = Math.Min(heights[cur], ValleyFloor);

            // 槽两侧抬到丘陵高度：两山夹一槽的「两山」。
            foreach (int side in new[] { (dir + 1) % 6, (dir + 4) % 6 })
                if (TryNeighbor(columns, rows, cur, side, out int wall) && !sea[wall])
                    heights[wall] = Math.Max(heights[wall], HighWall);

            if (!TryNeighbor(columns, rows, cur, dir, out int next)) break;
            cur = next;
        }
    }

    private static int DirectionTo(int columns, int rows, int from, int to)
    {
        for (int dir = 0; dir < 6; dir++)
            if (TryNeighbor(columns, rows, from, dir, out int nb) && nb == to) return dir;
        return -1;
    }

    private static bool TryNeighbor(int columns, int rows, int index, int dir, out int neighbor)
    {
        var (dq, dr) = MapGen.Neighbors[dir];
        return MapGen.TryNeighbor(index, dq, dr, columns, rows, out neighbor);
    }

    /// <summary>轴坐标 → 矩形网格下标；出界返回 false（与 <see cref="MapGen.AxialOfOffset"/> 互逆）。</summary>
    private static bool TryAxial(int columns, int rows, int q, int r, out int index)
    {
        if (r < 0 || r >= rows) { index = -1; return false; }
        int col = q + ((r - (r & 1)) / 2);
        if (col < 0 || col >= columns) { index = -1; return false; }
        index = r * columns + col;
        return true;
    }

    private static ulong TileHash(ulong seed, string salt, EntityId id) =>
        StableHash.Mix(StableHash.Of(salt + ":" + id.Value), seed);
}

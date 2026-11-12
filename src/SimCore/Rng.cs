namespace SimCore;

/// <summary>
/// 跨平台稳定的字符串散列（FNV-1a 64）。
///
/// 为什么不用 <see cref="string.GetHashCode()"/>：.NET Core 3.0 起默认对字符串哈希
/// 加随机化种子，同一份数据每次进程启动结果都不同。分层随机（§8.3）与状态哈希
/// （§9.3）都要求「同样的输入在任何机器任何进程给出同样的位」，所以必须自带散列。
/// </summary>
public static class StableHash
{
    public const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>按 UTF-8 字节散列，不遍历 char：避免不同规范化形式给出不同结果。</summary>
    public static ulong Of(string? text)
    {
        if (text is null) return FnvOffset;
        ulong h = FnvOffset;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            h ^= b;
            h = unchecked(h * FnvPrime);
        }
        return h;
    }

    /// <summary>整数的定长混合，避免相邻整数散列值过于接近。</summary>
    public static ulong Of(long value)
    {
        unchecked
        {
            ulong x = ((ulong)value) * 0x9E3779B97F4A7C15UL;
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return x;
        }
    }

    public static ulong Mix(ulong a, ulong b) => unchecked((a ^ b) * 0x9E3779B97F4A7C15UL);

    /// <summary>16 位十六进制，用于日志、diff 与哈希摘要展示。</summary>
    public static string Hex(ulong v) => v.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// SplitMix64（GDD §8.3 指名算法）：64 位状态，一步一个 ulong，无表无分支，
/// 因此在任何 .NET 运行时上给出同样的序列——「同种子同命令流 ⇒ 位级相同」的地基。
/// </summary>
public sealed class RandomStream
{
    internal const ulong Gamma = 0x9E3779B97F4A7C15UL;
    private ulong _state;

    public RandomStream(ulong seed) => _state = unchecked(seed + Gamma);

    /// <summary>供存档写入 / 读回（§10.6 的 rngState）。</summary>
    public ulong State
    {
        get => _state;
        internal set => _state = value;
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += Gamma;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>取高 53 位，正好是 double 的有效位宽：不乘不除不超越位，跨平台无损。</summary>
    public ulong NextBits53() => NextUInt64() >> 11;

    /// <summary>[0,1) 均匀数。<b>只用于显示与标定统计</b>；结算请用 <see cref="Chance"/> 一类定点接口。</summary>
    public double NextUnitDouble() => NextBits53() * (1.0 / 9007199254740992.0);

    /// <summary>[minIncl, maxExcl)，Lemire 乘高位法（无取模偏置，无分支）。</summary>
    public int NextInt(int minIncl, int maxExcl)
    {
        if (maxExcl <= minIncl) return minIncl;
        UInt128 span = (UInt128)((uint)(maxExcl - minIncl));
        UInt128 product = (UInt128)(uint)(NextUInt64() >> 32) * span;
        return minIncl + (int)(uint)(product >> 32);
    }

    /// <summary>[minIncl, maxExcl)，long 版。UInt128 中间量保证 53 位全用上。</summary>
    public long NextLong(long minIncl, long maxExcl)
    {
        if (maxExcl <= minIncl) return minIncl;
        UInt128 span = (UInt128)(maxExcl - minIncl);
        UInt128 product = (UInt128)NextBits53() * span;
        return minIncl + (long)(product >> 53);
    }

    /// <summary>
    /// 概率 p（0–1 定点）掷点。p ≤ 0 必假，p ≥ 1 必真。
    /// 刻度与 <see cref="Fix"/> 一致：掷 0…99 共 100 个等可能值，小于 p.Raw 即命中，
    /// 命中率恰为 p.Raw/100，不引入浮点，也不依赖取模的低位偏置。
    /// </summary>
    public bool Chance(Fix p)
    {
        if (p.IsZeroOrLess) return false;
        if (p.IsOneOrMore) return true;
        return NextInt(0, (int)Fix.Scale) < p.Raw;
    }

    /// <summary>权重表抽取，返回下标；同权重以最小下标为准（可复现的平票裁决）。</summary>
    public int PickWeighted(IReadOnlyList<long> weights)
    {
        long total = 0;
        for (int i = 0; i < weights.Count; i++) total += Math.Max(0, weights[i]);
        if (total <= 0) return 0;
        long target = NextLong(0, total);
        long acc = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            acc += Math.Max(0, weights[i]);
            if (target < acc) return i;
        }
        return weights.Count - 1;
    }

    /// <summary>
    /// 近似正态：12 个 <c>[0,1)</c> 均匀定点数之和减其期望 5.94（Irwin–Hall），
    /// 均值 0、方差 ≈1，再乘标准差。全程加减与一次乘，不调 Math.Sqrt/Log，
    /// 因此跨平台位级一致。用途：§4.2 的 ξ ~ N(0,2)、§5.3 的 shock ~ N(0,0.04)。
    /// </summary>
    public Fix NextGaussian(Fix mean, Fix sd)
    {
        long sum = 0;
        for (int i = 0; i < 12; i++) sum += NextInt(0, (int)Fix.Scale);
        // 均匀支是 [0,100) 而不是 [0,100]，所以 12 个的期望是 12×49.5 = 594，不是 600。
        // 减错这个常数会让 ξ 的均值停在 −0.06：乘上 sd=2 就是每年 −0.12 的满意度，
        // 于是「没有任何灾年，民心也年年变差」——正是 §4.2 最不想要的系统性偏置。
        // （单测里跑 20 万次取均值才抓到它：25σ 的偏差，逐回合看是看不出来的。）
        Fix z = Fix.FromRaw(sum - 6 * (Fix.Scale - 1)); // sd = 1.00（定点 100）
        return mean + Fix.Mul(z, sd);
    }

    /// <summary>从已排序列表中取一个。列表顺序本身就是结果的一部分，所以要求调用方先排序。</summary>
    public T Pick<T>(IReadOnlyList<T> sortedItems)
    {
        if (sortedItems.Count == 0) throw new SimStateException("Pick 的候选列表为空");
        return sortedItems[NextInt(0, sortedItems.Count)];
    }

    /// <summary>不重复取 k 个下标（Fisher–Yates），返回升序结果。</summary>
    public List<int> SampleIndices(int count, int k)
    {
        if (k > count) k = count;
        if (k < 0) k = 0;
        var idx = new int[count];
        for (int i = 0; i < count; i++) idx[i] = i;
        for (int i = 0; i < k; i++)
        {
            int j = NextInt(i, count);
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }
        var result = new List<int>(k);
        for (int i = 0; i < k; i++) result.Add(idx[i]);
        result.Sort();
        return result;
    }

    /// <summary>d10（骰子记法，1..10 含端点）。抽卡属性区间 45 + 3d10 用。</summary>
    public int D10() => NextInt(1, 11);
}

/// <summary>
/// 分层确定性随机的入口（GDD §8.3）。
/// <code>seed(worldSeed, namespace) = SplitMix64(worldSeed ⊕ hash(entityId) ⊕ hash(namespace))</code>
///
/// 三条使用纪律：
/// 1. 一个随机点一个命名空间，不共享流；命名空间自带回合号，
///    例 <c>"hero.roll@town.0007@turn.0057"</c>。同回合内多次独立抽取用
///    <see cref="Stream(EntityId,string,long)"/> 显式给子键。
/// 2. 候选列表先按 id 排序再喂给 <see cref="RandomStream.PickWeighted"/>。
/// 3. 全局流 <see cref="Global"/> 只用于不隶属于任何实体的抽取（地图生成、
///    国家事件），它的状态进存档；命名空间流可从 worldSeed 重算，不入档。
/// </summary>
public sealed class RngFactory
{
    private readonly ulong _worldSeed;
    private readonly SortedDictionary<string, RandomStream> _streams = new(StringComparer.Ordinal);

    public RngFactory(ulong worldSeed)
    {
        _worldSeed = worldSeed;
        Global = new RandomStream(worldSeed);
    }

    public ulong WorldSeed => _worldSeed;

    /// <summary>全局流：唯一需要写入存档的 RNG 状态。</summary>
    public RandomStream Global { get; private set; }

    /// <summary>派生一条命名空间流；同参数 ⇒ 同初值 ⇒ 同序列。流会被缓存，同命名空间重复请求拿到同一个对象。</summary>
    public RandomStream Stream(string ns)
    {
        if (_streams.TryGetValue(ns, out var cached)) return cached;
        var s = new RandomStream(unchecked(_worldSeed ^ StableHash.Of(ns)));
        _streams[ns] = s;
        return s;
    }

    public RandomStream Stream(EntityId scope, string ns) => Stream($"{scope}@{ns}");

    public RandomStream Stream(EntityId scope, string ns, long subKey) => Stream($"{scope}@{ns}#{subKey}");

    /// <summary>一次性掷点：不缓存流，适合「查一次就算」的场合（例：地块形胜识别的抖动）。</summary>
    public RandomStream OneShot(string ns) => new(unchecked(_worldSeed ^ StableHash.Of(ns)));

    /// <summary>
    /// 回合末重置命名空间流缓存（由 <see cref="Simulation.AdvanceTurn"/> 固定调用）。
    /// 目的有二：内存不随回合数线性增长；以及——命名空间必须自带回合号，
    /// 若某插件跨回合复用同一个命名空间，重置会让它拿到全新的流，
    /// 于是「结果与本不该依赖上一回合的流」这一违规会立刻在基线模拟里显形。
    /// </summary>
    internal void ResetNamespaceStreams() => _streams.Clear();

    /// <summary>RNG 状态存档：全局流状态 + 命名空间流状态（排序后写出，保证存档字节可比对）。</summary>
    internal RngState CaptureState() =>
        new(Global.State, _streams.Select(kv => new RngStreamState(kv.Key, kv.Value.State)).ToList());

    internal void RestoreState(RngState state)
    {
        Global = new RandomStream(unchecked(state.GlobalState - RandomStream.Gamma));
        _streams.Clear();
        foreach (var s in state.Streams)
        {
            var stream = new RandomStream(0) { State = s.State };
            _streams[s.Namespace] = stream;
        }
    }
}

internal sealed record RngStreamState(string Namespace, ulong State);

internal sealed record RngState(ulong GlobalState, IReadOnlyList<RngStreamState> Streams);

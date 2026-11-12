namespace SimCore;

/// <summary>
/// 状态哈希（GDD §9.3：每回合末广播分块状态哈希；§10.8 确定性测试的地基）。
///
/// 分块 = 按 kind 分组。联机发现不一致时，先定位是哪个 kind 分块不同，
/// 再用回放（§9.3）把回合二分定位到具体步骤，不用整档比对。
/// </summary>
public sealed class StateHash
{
    /// <summary>kind → 该 kind 全部实体全部字段的哈希（分块，升序）。</summary>
    public IReadOnlyDictionary<string, string> Chunks { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    /// <summary>整局状态摘要：所有分块 + 回合号 + 命令日志 + 全局 RNG。</summary>
    public string Total { get; init; } = "";
    /// <summary>参与哈希的字段总数，用于报告与「空世界」判定。</summary>
    public int FieldCount { get; init; }

    /// <summary>计算当前世界的分块哈希与总哈希。</summary>
    public static StateHash Compute(World world, CommandBus commands, RngFactory rng)
    {
        var chunks = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        int fields = 0;

        foreach (var ent in world.All())
        {
            // 实体本身也要进哈希：只有字段没有实体的话，「删了一个空城镇」不会被发现
            ulong h = StableHash.Mix(StableHash.FnvOffset, StableHash.Of(ent.Kind));
            foreach (var kv in ent.Fields)
            {
                h = StableHash.Mix(h, StableHash.Of(kv.Key));
                h = StableHash.Mix(h, StableHash.Of(kv.Value.Canonical));
                fields++;
            }
            chunks.TryGetValue(ent.Kind, out ulong acc);
            chunks[ent.Kind] = StableHash.Mix(acc == 0 ? StableHash.FnvOffset : acc, StableHash.Of($"{ent.Id}#{StableHash.Hex(h)}"));
        }

        ulong total = StableHash.FnvOffset;
        foreach (var kv in chunks)
            total = StableHash.Mix(total, StableHash.Of(kv.Key + "=" + StableHash.Hex(kv.Value)));
        total = StableHash.Mix(total, StableHash.Of(world.GetInt(World.GlobalId, "meta.turn", -1)));
        total = StableHash.Mix(total, StableHash.Of(commands.LogHash));
        total = StableHash.Mix(total, rng.Global.State);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in chunks) result[kv.Key] = StableHash.Hex(kv.Value);
        return new StateHash
        {
            Chunks = result,
            Total = StableHash.Hex(total),
            FieldCount = fields
        };
    }

    /// <summary>两个哈希的第一个差异分块；联机校验报错用。</summary>
    public static string? FirstDifference(StateHash a, StateHash b)
    {
        foreach (var key in a.Chunks.Keys.Union(b.Chunks.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            a.Chunks.TryGetValue(key, out var x);
            b.Chunks.TryGetValue(key, out var y);
            if (!string.Equals(x, y, StringComparison.Ordinal))
                return $"分块 '{key}' 不一致：本机 {x ?? "(无)"} vs 对方 {y ?? "(无)"}";
        }
        return a.Total == b.Total ? null : $"总分不一致但分块相同:{a.Total} vs {b.Total}（很可能是 world 元数据或 RNG 状态）";
    }
}

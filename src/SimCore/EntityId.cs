namespace SimCore;

/// <summary>
/// 实体标识（附录 D：<c>nation.* town.* hero.* unit.* hex.q.r</c>）。
///
/// 用字符串而不是 int：存档可读、插件数据包能直接引用（<c>def:</c>），
/// 且跨版本不会因 id 复用而出错。
///
/// 排序用 <see cref="StringComparer.Ordinal"/>，全平台一致。
/// 因此所有自动生成的 id 必须零填充（<c>town.0007</c> 而不是 <c>town.7</c>），
/// 否则 "town.10" 会排在 "town.9" 前面，遍历顺序仍确定但与直觉不符。
/// </summary>
public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
{
    public readonly string Value;

    public EntityId(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new SimDataException("实体 id 不能为空");
        if (value.IndexOfAny(new[] { ' ', '\t', '\n', '\r' }) >= 0) throw new SimDataException($"实体 id 含空白字符：'{value}'");
        Value = value;
    }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>前缀，例 "town"。</summary>
    public string Prefix => Value.Length >= 4 ? Value[..4] : Value;

    /// <summary>把 "hex.21.34" 拆成 ["hex","21","34"]。</summary>
    public string[] Parts => Value.Split('.');

    public static EntityId New(string kind, int index) => new($"{kind}.{index:D4}");

    /// <summary>拼接子路径，例 <c>id.Field("stock.food")</c> 仍是一个实体 id 风格的路径（用于合成 key）。</summary>
    public string Field(string path) => $"{Value}.{path}";

    public bool Equals(EntityId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is EntityId e && Equals(e);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? "");
    public int CompareTo(EntityId other) => string.CompareOrdinal(Value, other.Value);

    public static bool operator ==(EntityId a, EntityId b) => a.CompareTo(b) == 0;
    public static bool operator !=(EntityId a, EntityId b) => a.CompareTo(b) != 0;
    public static bool operator <(EntityId a, EntityId b) => a.CompareTo(b) < 0;
    public static bool operator >(EntityId a, EntityId b) => a.CompareTo(b) > 0;

    public override string ToString() => Value ?? "(空)";
}

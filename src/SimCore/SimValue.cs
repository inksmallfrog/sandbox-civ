namespace SimCore;

/// <summary>状态值的四种形态。Core 不认识「粮」「农民」，只认识这四种（§10.2 职责 1）。</summary>
public enum ValueKind : byte
{
    None = 0,
    /// <summary>定点数，<see cref="SimValue.Number"/> 存 1/100 单位的 Raw。</summary>
    Fix = 1,
    /// <summary>整数：人口单位、回合号、等级、满意度（0–100）。</summary>
    Int = 2,
    /// <summary>字符串：名称、枚举标签（core/village/class.farmer）。</summary>
    Str = 3,
    Bool = 4,
}

/// <summary>
/// 世界存储里一个字段的最小可序列化值。
///
/// 为什么值是「扁平的四种」而不是任意对象：
/// Core 必须能做三件事——状态哈希、存档快照、因果 diff。三者都要求
/// 「同一个状态在任何机器上序列化成同一串字节」。只要 Core 不持有
/// 反射对象、不持有 float、不依赖字典枚举顺序，这三件事就免费成立。
/// </summary>
public readonly struct SimValue : IEquatable<SimValue>
{
    public readonly ValueKind Kind;
    public readonly long Number;
    public readonly string? Text;

    private SimValue(ValueKind kind, long number, string? text)
    {
        Kind = kind;
        Number = number;
        Text = text;
    }

    public static readonly SimValue Null = new(ValueKind.None, 0, null);
    public static readonly SimValue True = new(ValueKind.Bool, 1, null);
    public static readonly SimValue False = new(ValueKind.Bool, 0, null);

    public static SimValue Of(Fix f) => new(ValueKind.Fix, f.Raw, null);
    public static SimValue OfInt(long v) => new(ValueKind.Int, v, null);
    public static SimValue OfStr(string s) => new(ValueKind.Str, 0, s ?? throw new ArgumentNullException(nameof(s)));
    public static SimValue OfBool(bool b) => b ? True : False;

    public bool IsNull => Kind == ValueKind.None;

    public Fix AsFix() => Kind switch
    {
        ValueKind.Fix => Fix.FromRaw(Number),
        // 整数写进定点字段时按「整数单位」解释：数据表里 "pop": 6 与 "6.00" 同义
        ValueKind.Int => Fix.FromInt(Number),
        ValueKind.Bool => Fix.FromRaw(Number),
        _ => throw new SimStateException($"字段是 {Kind}，不能当定点读"),
    };

    public long AsInt() => Kind switch
    {
        ValueKind.Int => Number,
        ValueKind.Bool => Number,
        ValueKind.Fix => Number / Fix.Scale,
        _ => throw new SimStateException($"字段是 {Kind}，不能当整数读"),
    };

    public string AsString() => Kind switch
    {
        ValueKind.Str => Text!,
        ValueKind.None => "",
        _ => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public bool AsBool() => Number != 0;

    /// <summary>累加：数值型相加；非数值型返回自身（调用方不该对 Str 求和）。</summary>
    public SimValue Plus(SimValue other)
    {
        if (Kind == ValueKind.None) return other;
        if (other.Kind == ValueKind.None) return this;
        if (Kind == ValueKind.Str) throw new SimStateException("字符串字段不能累加，请改用 Set");
        if (Kind == other.Kind) return new SimValue(Kind, checked(Number + other.Number), null);
        // Fix + Int：统一到 Fix，避免精度丢失方向不一致
        if (Kind == ValueKind.Fix) return new SimValue(ValueKind.Fix, checked(Number + other.Number * Fix.Scale), null);
        if (other.Kind == ValueKind.Fix) return new SimValue(ValueKind.Fix, checked(Number * Fix.Scale + other.Number), null);
        throw new SimStateException($"类型不一致：{Kind} + {other.Kind}");
    }

    public bool Equals(SimValue other) =>
        Kind == other.Kind && Number == other.Number && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SimValue v && Equals(v);
    public override int GetHashCode() => HashCode.Combine((int)Kind, Number, Text);

    /// <summary>哈希与存档用的规范串（含类型标签，避免 1 与 "1" 混淆）。</summary>
    public string Canonical => Kind switch
    {
        ValueKind.None => "n",
        ValueKind.Fix => $"f{Number}",
        ValueKind.Int => $"i{Number}",
        ValueKind.Str => $"s{Text}",
        ValueKind.Bool => $"b{Number}",
        _ => "?"
    };

    /// <summary><see cref="Canonical"/> 的逆运算。存档读回与 diff 展示共用。</summary>
    public static SimValue ParseCanonical(string s)
    {
        if (string.IsNullOrEmpty(s)) return Null;
        switch (s[0])
        {
            case 'n': return Null;
            case 'f': return new SimValue(ValueKind.Fix, long.Parse(s.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture), null);
            case 'i': return new SimValue(ValueKind.Int, long.Parse(s.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture), null);
            case 'b': return new SimValue(ValueKind.Bool, long.Parse(s.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture), null);
            case 's': return new SimValue(ValueKind.Str, 0, s[1..]);
            default: throw new SimSaveException($"无法解析的规范值：'{s}'");
        }
    }

    public string Display => Kind switch
    {
        ValueKind.None => "—",
        ValueKind.Fix => Fix.FromRaw(Number).Text,
        ValueKind.Int => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ValueKind.Str => Text!,
        ValueKind.Bool => Number != 0 ? "是" : "否",
        _ => "?"
    };

    public override string ToString() => Display;
}

namespace SimCore;

/// <summary>
/// 100 分之一单位的定点数（GDD §10.7：关键量用 int 定点，银 ×100、物资 ×100）。
///
/// 为什么不用 float：确定性是联机、回放、调参三件事的共同地基（§8.3 规则 4）。
/// float 的乘除在不同 JIT/硬件上可能给出不同的最后一位，
/// 而位级一致是状态哈希校验的前提，所以所有进入 sim 的量都必须走定点。
///
/// 定点约定：
/// - <see cref="Raw"/> 为 1/100 单位整数；Fix(1.00) 的 Raw = 100。
/// - 乘除用 <see cref="UInt128"/> 中间量，避免两个 long 相乘在 64 位里回绕。
/// - 除法向零截断（不是银行家舍入），全平台一致。
/// - <see cref="ToDouble"/> 只允许用于显示与报告，禁止参与结算比较。
/// </summary>
public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>
{
    /// <summary>定点刻度：Raw = 实际值 × 100。</summary>
    public const long Scale = 100;

    public readonly long Raw;

    private Fix(long raw) => Raw = raw;

    // ---- 常用常量（避免各处再算一遍 1/100）----
    public static readonly Fix Zero = new(0);
    public static readonly Fix One = new(Scale);
    public static readonly Fix Half = new(Scale / 2);
    public static readonly Fix MinusOne = new(-Scale);
    public static readonly Fix MaxValue = new(long.MaxValue);
    public static readonly Fix MinValue = new(long.MinValue);

    public static Fix FromRaw(long raw) => new(raw);

    /// <summary>整数 → 定点（倍率、人口单位等本来就是整数的场合）。</summary>
    public static Fix FromInt(long v) => FromRaw(checked(v * Scale));

    /// <summary>百分比整数 → 定点：FromPercent(20) = 0.20。</summary>
    public static Fix FromPercent(int percent) => FromRaw(percent);

    /// <summary>
    /// 仅用于解析 data/*.json 里的十进制字面量（一次性，数据层 → 定点）。
    /// 结果按四舍五入落到 1/100，因此只要数据写的是两位小数就无损。
    /// </summary>
    public static Fix FromDecimalText(string text)
    {
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            throw new SimDataException($"无法把 '{text}' 解析为十进制数");
        return FromRaw((long)decimal.Round(d * Scale, MidpointRounding.AwayFromZero));
    }

    public static Fix FromIntPart(long whole) => FromInt(whole);

    // ---- 算术 ----
    public static Fix operator +(Fix a, Fix b) => FromRaw(checked(a.Raw + b.Raw));
    public static Fix operator -(Fix a, Fix b) => FromRaw(checked(a.Raw - b.Raw));
    public static Fix operator -(Fix a) => new(-a.Raw);

    /// <summary>定点乘法：a×b = (a.Raw × b.Raw) ÷ Scale。</summary>
    public static Fix Mul(Fix a, Fix b) => MulDiv(a.Raw, b.Raw, Scale);

    /// <summary>定点除法：向零截断。<paramref name="b"/> 为 0 时返回 0（调用方应先判分母，见 §5.3 的 max(S,0.5)）。</summary>
    public static Fix Div(Fix a, Fix b) => b.Raw == 0 ? Zero : MulDiv(a.Raw, Scale, b.Raw);

    /// <summary>
    /// (x × y) ÷ d 的 128 位中间量版本。
    /// net8.0 没有 <c>Int126</c>（只有 <c>UInt128</c>，见 ADR 记录），所以取绝对值走
    /// UInt128、符号单独记，最后再贴回去。这样两个 long 相乘绝不会在 64 位里回绕，
    /// 而截断方向仍是「向零」，与 Div 的文档一致、全平台一致。
    /// </summary>
    private static Fix MulDiv(long x, long y, long d)
    {
        if (d == 0) throw new SimStateException("定点除法分母为 0");
        bool negative = (x < 0) != (y < 0) != (d < 0);
        UInt128 p = Abs128(x) * Abs128(y) / Abs128(d);
        if (p > (UInt128)long.MaxValue)
            throw new SimStateException($"定点运算溢出：|结果| > long.MaxValue（{x} × {y} ÷ {d}）");
        long r = (long)p;
        return new(negative ? -r : r);
    }

    /// <summary>long 的绝对值升到 UInt128（能正确表示 long.MinValue，Math.Abs 不能）。</summary>
    private static UInt128 Abs128(long v) => v < 0 ? UInt128.Zero - (UInt128)(ulong)v : (UInt128)(ulong)v;

    /// <summary>a × n（整数倍率，例：人口单位 × 每人产出）。</summary>
    public static Fix Mul(Fix a, long n) => new(checked(a.Raw * n));

    /// <summary>a ÷ n（整数除数，向零截断）。</summary>
    public static Fix Div(Fix a, long n) => n == 0 ? Zero : new(a.Raw / n);

    public static Fix Min(Fix a, Fix b) => a.Raw <= b.Raw ? a : b;
    public static Fix Max(Fix a, Fix b) => a.Raw >= b.Raw ? a : b;

    public Fix Clamp(Fix lo, Fix hi) => Raw <= lo.Raw ? lo : (Raw >= hi.Raw ? hi : this);

    // ---- 比较 ----
    public bool IsZero => Raw == 0;
    public bool IsNegative => Raw < 0;
    public bool IsPositive => Raw > 0;
    public bool IsZeroOrLess => Raw <= 0;
    public bool IsOneOrMore => Raw >= Scale;
    public Fix Abs() => Raw < 0 ? new(-Raw) : this;

    public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
    public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
    public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
    public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;
    public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
    public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;

    public bool Equals(Fix other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fix f && Raw == f.Raw;
    public override int GetHashCode() => Raw.GetHashCode();
    public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

    // ---- 显示 ----
    /// <summary>格式化为人读的两位小数（-1.50 / 12.40）。仅用于报告与 UI。</summary>
    public string Text
    {
        get
        {
            // 先取绝对值再拆整数/小数位，否则 -0.50 会被 Raw/Scale 截成 "0.50"，丢掉符号。
            long m = Raw < 0 ? -Raw : Raw;
            string sign = Raw < 0 ? "-" : "";
            return $"{sign}{m / Scale}.{m % Scale:D2}";
        }
    }

    /// <summary>仅用于图表/CSV 导出，不得回流到结算。</summary>
    public double ToDouble() => (double)Raw / Scale;

    public override string ToString() => Text;

    /// <summary>
    /// 定点 → 整数（四舍五入，.5 远离零）。满意度、人口这类对外整数标量用它。
    /// 写成整数运算而不是 Math.Round(double)，是为了避免 float 参与结算（§10.7）。
    /// </summary>
    public int RoundToInt()
    {
        long q = Raw / Scale;
        long r = Raw % Scale;
        if (r == 0) return (int)q;
        // 余数绝对值 ≥ 半个刻度则进位，方向跟随符号
        if (Math.Abs(r) * 2 >= Scale) q += Raw > 0 ? 1 : -1;
        return (int)q;
    }

    /// <summary>定点 → 整数（向下取整，用于「能供养几人」这类 floor 语义）。</summary>
    public int FloorToInt()
    {
        long q = Raw / Scale;
        if (Raw % Scale != 0 && Raw < 0) q -= 1;
        return (int)q;
    }
}

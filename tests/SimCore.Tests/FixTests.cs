using SimCore;

namespace HexaCiv.Tests;

/// <summary>
/// 定点算术（§10.7 的地基）。这里错一个符号，联机哈希、回放、标定全都不成立，
/// 所以每条都写成「手算得出的期望值」，不是「跑出什么记什么」。
/// </summary>
public class FixTests
{
    [Fact]
    public void 刻度与构造()
    {
        Assert.Equal(100, Fix.Scale);
        Assert.Equal(1234, Fix.FromRaw(1234).Raw);
        Assert.Equal(200, Fix.FromInt(2).Raw);
        Assert.Equal(4, Fix.FromPercent(4).Raw);          // 0.04
        Assert.Equal(-194, Fix.FromDecimalText("-1.94").Raw);
        Assert.Equal("1.00", Fix.One.Text);
        Assert.Equal("-1.94", Fix.FromRaw(-194).Text);
        Assert.Equal("-0.50", Fix.FromRaw(-50).Text);     // 曾经被截成 "0.50" 的那条
    }

    /// <summary>
    /// 回归：<c>Abs128</c> 里先升位再取负会算出 2^128−2^64+97 而不是 97，
    /// 于是 −0.97 × 2.00 被误报成「定点溢出」。M1 跑到第 6 回合就炸过一次。
    /// </summary>
    [Fact]
    public void 负数乘法不误报溢出()
    {
        Fix a = Fix.FromRaw(-97);      // −0.97
        Fix b = Fix.FromInt(2);        // 2.00
        Assert.Equal(-194, Fix.Mul(a, b).Raw);   // −0.97 × 2.00 = −1.94
        Assert.Equal(194, Fix.Mul(-a, b).Raw);   // 0.97 × 2.00
        Assert.Equal(-194, Fix.Mul(b, a).Raw);   // 交换律：2.00 × −0.97
        Assert.Equal(-97, Fix.Mul(a, Fix.One).Raw);   // 乘 1 恒等
        Assert.Equal(0, Fix.Mul(a, Fix.Zero).Raw);
    }

    [Fact]
    public void 除法向零截断且负零不出现()
    {
        // (x × y) ÷ d 走的是同一套符号奇偶 + 向零截断，三个操作数各自的符号都要算
        Assert.Equal(Fix.FromRaw(194), Fix.Div(Fix.FromRaw(-97), Fix.FromRaw(-50)));  // −0.97 ÷ −0.50 = 1.94
        Assert.Equal(Fix.FromRaw(-194), Fix.Div(Fix.FromRaw(-97), Fix.FromRaw(50)));  // −0.97 ÷ 0.50 = −1.94
        Assert.Equal(Fix.FromRaw(-194), Fix.Div(Fix.FromRaw(97), Fix.FromRaw(-50)));  // 0.97 ÷ −0.50 = −1.94
        // 向零：结果不足 1 个刻度时得 0，而不是 −1
        Assert.Equal(Fix.Zero, Fix.Div(Fix.FromRaw(-1), Fix.FromInt(100)));
        // 除以 0 按约定返回 0（调用方负责先判分母，§5.3 的 max(S,0.5)）
        Assert.Equal(Fix.Zero, Fix.Div(Fix.FromInt(7), Fix.Zero));
        Assert.Throws<SimStateException>(() => Fix.Mul(Fix.FromInt(1_000_000), Fix.FromInt(1_000_000_000_000)));
    }

    [Fact]
    public void 取整三种语义各自正确()
    {
        Assert.Equal(2, Fix.FromRaw(199).RoundToInt());      // 四舍五入
        Assert.Equal(3, Fix.FromRaw(250).RoundToInt());      // .5 远离零 → 3，不是银行家舍入的 2
        Assert.Equal(-2, Fix.FromRaw(-150).RoundToInt());    // .5 远离零
        Assert.Equal(1, Fix.FromRaw(199).FloorToInt());      // 向下取整
        Assert.Equal(-2, Fix.FromRaw(-101).FloorToInt());
        Assert.Equal(0, Fix.FromRaw(50).FloorToInt());
        // 人口零头桶用的是「向零截断」：-0.16 必须得 0，向下取整会凭空饿死 100 人
        Assert.Equal(0, Fix.FromRaw(-16).Raw / Fix.Scale);
    }

    [Fact]
    public void 区间限制按附录A的写法()
    {
        // 粮安 = clamp(2s−1, −1, 1)
        Fix Secure(Fix s) => (Fix.Mul(s, 2) - Fix.One).Clamp(Fix.MinValue, Fix.One);
        Assert.Equal(-100, Secure(Fix.Zero).Raw);            // 颗粒无收 → −1
        Assert.Equal(0, Secure(Fix.Half).Raw);               // 刚好糊口 → 0（这才叫「安」）
        Assert.Equal(100, Secure(Fix.One).Raw);              // 翻倍余粮 → +1，且封顶
        Assert.Equal(60, Secure(Fix.FromRaw(80)).Raw);       // s=0.80 → 0.60

        // η(S) = 0.6 + 0.4·S/100，S∈[0,100]
        static Fix Eta(Fix sat) => Fix.FromPercent(60) + Fix.Mul(Fix.FromPercent(40), Fix.Div(sat, Fix.FromInt(100)));
        Assert.Equal(60, Eta(Fix.Zero).Raw);
        Assert.Equal(100, Eta(Fix.FromInt(100)).Raw);
        Assert.Equal(80, Eta(Fix.FromInt(50)).Raw);

        // Facility = 1 + 0.15×等级（≤1.45）
        static Fix Facility(int level) => Fix.Min(Fix.One + Fix.FromPercent(15 * level), Fix.FromRaw(145));
        Assert.Equal(100, Facility(0).Raw);
        Assert.Equal(145, Facility(3).Raw);
        Assert.Equal(145, Facility(9).Raw);                  // 顶到 1.45 不再涨
    }

    /// <summary>存档与状态哈希都建立在 Canonical 文本上：它不是无损的，整条链路就不是无损的。</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(194L)]
    [InlineData(-194L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void 定点值的规范文本可无损往返(long raw)
    {
        string text = SimValue.Of(Fix.FromRaw(raw)).Canonical;
        Assert.Equal(raw, SimValue.ParseCanonical(text).AsFix().Raw);
    }
}

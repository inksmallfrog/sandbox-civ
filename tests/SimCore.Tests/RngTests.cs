using SimCore;

namespace HexaCiv.Tests;

/// <summary>
/// 分层 RNG（§8.3）。这三条性质是联机、回放、抽卡公示的共同前提，
/// 任何一条破了，「同种子同命令流 → 位级一致」就是空话：
///   规则 1 同参数必同序列（可复现）
///   规则 2 不同命名空间互不污染（改一个系统的掷点频率不能挪动另一个的序列）
///   规则 3 命名空间自带回合号（跨回合复用要能在基线模拟里显形）
/// </summary>
public class RngTests
{
    private static ulong[] Draw(RandomStream s, int n)
    {
        var a = new ulong[n];
        for (int i = 0; i < n; i++) a[i] = s.NextUInt64();
        return a;
    }

    [Fact]
    public void 同种子同命名空间_序列必相同()
    {
        var a = new RngFactory(20261113);
        var b = new RngFactory(20261113);
        Assert.Equal(Draw(a.Stream("hero.roll@town.0007@turn.0057"), 32),
                     Draw(b.Stream("hero.roll@town.0007@turn.0057"), 32));
    }

    [Fact]
    public void 换一个命名空间不得影响另一条流()
    {
        // 先各开一条流跑一段，再在第三条上疯狂取数，前两条的下一个值必须纹丝不动
        var f = new RngFactory(7);
        RandomStream x = f.Stream("market.price@turn.0003");
        RandomStream y = f.Stream("event.draw@turn.0003");
        ulong x0 = x.NextUInt64(), y0 = y.NextUInt64();
        for (int i = 0; i < 5000; i++) f.Stream($"noise@{i}").NextUInt64();
        Assert.NotEqual(x0, x.NextUInt64());   // x 自己往前走
        Assert.NotEqual(y0, y.NextUInt64());
        // 但重开同名流得到的仍是那条独立序列的第一个值
        Assert.Equal(x0, new RngFactory(7).Stream("market.price@turn.0003").NextUInt64());
        Assert.Equal(y0, new RngFactory(7).Stream("event.draw@turn.0003").NextUInt64());
        // 而不同命名空间的第一个值不该相等（撞车说明哈希没把命名空间混进去）
        Assert.NotEqual(x0, y0);
    }

    [Fact]
    public void 换种子必须换序列()
    {
        Assert.NotEqual(
            Draw(new RngFactory(1).Stream("event.draw@turn.0001"), 8),
            Draw(new RngFactory(2).Stream("event.draw@turn.0001"), 8));
    }

    [Fact]
    public void 同工厂内重复请求同一命名空间拿到同一条流()
    {
        var f = new RngFactory(11);
        Assert.Same(f.Stream("a"), f.Stream("a"));
        Assert.NotSame(f.Stream("a"), f.Stream("b"));
        // 子键形式：同回合的两次独立抽取必须走不同的流
        Assert.NotSame(f.Stream(new EntityId("town.0001"), "roll", 1),
                       f.Stream(new EntityId("town.0001"), "roll", 2));
    }

    [Fact]
    public void OneShot_不吃缓存也不喂缓存()
    {
        var f = new RngFactory(13);
        ulong before = f.Stream("map.gen").NextUInt64();
        var fresh = new RngFactory(13);
        ulong baseline = fresh.Stream("map.gen").NextUInt64();
        Assert.Equal(before, baseline);                        // 没被 OneShot 污染
        Assert.Equal(baseline, f.OneShot("map.gen").NextUInt64());  // OneShot 从同一个初值出发
        Assert.NotEqual(baseline, f.Stream("map.gen").NextUInt64()); // 但流本身往前走
    }

    [Fact]
    public void 取值范围与边界()
    {
        var s = new RngFactory(3).Stream("range.check");
        for (int i = 0; i < 20_000; i++)
        {
            Assert.InRange(s.NextInt(1, 11), 1, 10);
            Assert.InRange(s.D10(), 1, 10);
            Assert.InRange(s.NextLong(-5, 5), -5L, 4L);
            Assert.InRange(s.NextUnitDouble(), 0.0, 0.999_999_999_999_999_9);
        }
        Assert.False(s.Chance(Fix.Zero));
        Assert.False(s.Chance(Fix.FromRaw(-1)));
        Assert.True(s.Chance(Fix.One));
        Assert.True(s.Chance(Fix.FromRaw(500)));
    }

    [Fact]
    public void 加权抽取尊重零权重且可复现()
    {
        var w = new long[] { 55, 30, 12, 3 };          // 附录 A §9 的稀有度分布
        var counts = new int[4];
        var s = new RngFactory(99).Stream("gacha@turn.0001");
        for (int i = 0; i < 100_000; i++) counts[s.PickWeighted(w)]++;
        // 55/30/12/3 ±3σ：只卡量级，不卡到个位（卡个位就成了把随机数表写进断言）
        Assert.InRange(counts[0], 54_000, 56_000);
        Assert.InRange(counts[3], 2_400, 3_600);
        Assert.Equal(counts.Sum(), 100_000);

        var zero = new long[] { 0, 1, 0 };
        for (int i = 0; i < 500; i++) Assert.Equal(1, new RngFactory(5).Stream($"z@{i}").PickWeighted(zero));
    }

    [Fact]
    public void 正态扰动均值为零且离散度等于标称标准差()
    {
        // 同种子两次抽出来的序列必须逐位相同（这条挂了，S* 的哈希就对不上）
        var a = new RngFactory(21).Stream("satisfaction@turn.0002");
        var b = new RngFactory(21).Stream("satisfaction@turn.0002");
        for (int i = 0; i < 200; i++) Assert.Equal(a.NextGaussian(Fix.Zero, Fix.FromInt(2)).Raw, b.NextGaussian(Fix.Zero, Fix.FromInt(2)).Raw);

        // ξ ~ N(0,2)（§4.2 / 附录 A §3）。Irwin–Hall 的均值常数是唯一容易写错的地方：
        // 均匀支取的是 [0,100)，期望 49.5 而不是 50，减 600 会留下每年 −0.12 的系统性偏置。
        var s = new RngFactory(22).Stream("satisfaction.mean");
        const int n = 200_000;
        double sum = 0, sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double v = s.NextGaussian(Fix.Zero, Fix.FromInt(2)).ToDouble();
            sum += v; sumSq += v * v;
        }
        double mean = sum / n;
        double sd = Math.Sqrt(sumSq / n - mean * mean);
        Assert.InRange(mean, -0.03, 0.03);      // σ/√n ≈ 0.0045，留 6σ 余量
        Assert.InRange(sd, 1.94, 2.06);         // 文档说 sd=2，那就得是 2
    }

    [Fact]
    public void 回合边界重置命名空间流缓存()
    {
        // §8.3 纪律 1 的执法机制：命名空间必须自带回合号。跨回合复用同一条流，
        // 下一回合拿到的会是重置后的新流——于是「依赖上一回合流状态」的规则立刻显形。
        var sim = new Simulation(new SimConfig { Seed = 4, NoPlugins = true }).Bootstrap(loadData: false);
        RandomStream s0 = sim.Rng.Stream("test@turn.0001");
        ulong first = s0.NextUInt64();
        s0.NextUInt64();

        sim.AdvanceTurn();
        RandomStream again = sim.Rng.Stream("test@turn.0001");
        Assert.NotSame(s0, again);
        Assert.Equal(first, again.NextUInt64());   // 缓存被清，同一个命名空间从头再来
    }

    [Fact]
    public void 全局流状态可存可取()
    {
        var sim = new Simulation(new SimConfig { Seed = 8, NoPlugins = true }).Bootstrap(loadData: false);
        ulong before = sim.Rng.Global.State;
        sim.Rng.Global.NextUInt64();
        Assert.NotEqual(before, sim.Rng.Global.State);
    }
}

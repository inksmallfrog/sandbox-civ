using SimCore;

namespace HexaCiv.Tests;

/// <summary>
/// 结算顺序表 + 数据注册表 + 插件装载（§2.3 / §10.5 / §10.4）。
/// 这三样都是「物理定律」，一旦漂移，所有历史存档与基线报告同时作废，
/// 所以它们值得被逐条钉死，而不是靠 code review。
/// </summary>
public class OrderAndDataTests
{
    // ================= §2.3 十五步 =================

    [Fact]
    public void 结算表就是GDD那张表()
    {
        Assert.Equal(15, SimOrder.Steps.Count);
        for (int i = 0; i < 15; i++)
        {
            Assert.Equal(i + 1, SimOrder.Steps[i].Index);           // 编号从 1 起，与文档一致
            Assert.False(string.IsNullOrWhiteSpace(SimOrder.Steps[i].Id));
            Assert.False(string.IsNullOrWhiteSpace(SimOrder.Steps[i].Title));
        }
        Assert.Equal(15, SimOrder.Steps.Select(s => s.Id).Distinct().Count());
        // 逐个点名：改任何一个 id 都要走 CHANGELOG-DESIGN 裁决，这条测试就是那道门
        Assert.Equal(new[]
        {
            "apply_commands", "buildings", "agriculture", "industry", "services",
            "market_local", "trade_domestic", "trade_foreign", "payout", "consumption",
            "treasury", "satisfaction", "demography", "military", "wrapup",
        }, SimOrder.Steps.Select(s => s.Id).ToArray());
        Assert.Throws<SimStateException>(() => SimOrder.ById("nope"));
        Assert.Throws<SimStateException>(() => SimOrder.PointsOf(0));
        Assert.Throws<SimStateException>(() => SimOrder.PointsOf(16));
    }

    [Fact]
    public void 四季分布与意图段()
    {
        Assert.Equal(new[] { 1 }, SimOrder.Steps.Where(s => s.Season == Season.Intent).Select(s => s.Index));
        Assert.Equal(4, SimOrder.OfSeason(Season.Spring).Count);   // 2–5
        Assert.Equal(3, SimOrder.OfSeason(Season.Summer).Count);   // 6–8
        Assert.Equal(3, SimOrder.OfSeason(Season.Autumn).Count);   // 9–11
        Assert.Equal(4, SimOrder.OfSeason(Season.Winter).Count);   // 12–15
        Assert.Equal(15, SimOrder.OfSeason(Season.Intent).Count + SimOrder.Steps.Count - 1);
    }

    /// <summary>
    /// §2.3 的陷阱：第 3/4/5/9/10 步首尾广播同一个点。Core 无法靠「点」区分它们，
    /// 只能靠 stepId + slot。这条测试钉住「哪些步会重复广播」这个事实——
    /// 将来有人把某步拆成首尾不同的两个点，这条会红，提醒他去改插件的门闸。
    /// </summary>
    [Fact]
    public void 首尾同名的步必须靠slot区分()
    {
        var doubled = SimOrder.Steps.Where(s => s.Pre == s.Post).Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // 六步：3/4/5 都是 OnProduction，9/11 都是 OnAllocate，10 是 OnConsumption。
        // 挂在这几个点上的处理器如果不看 slot，同一年就会跑两遍——M1 写设施系数时踩过。
        Assert.Equal(new[] { "agriculture", "consumption", "industry", "payout", "services", "treasury" }, doubled);

        var args = new HookArgs { Subject = World.GlobalId }.With("stepId", "agriculture").With("slot", SimOrder.SlotPost);
        Assert.True(SimOrder.At(args, "agriculture", SimOrder.SlotPost));
        Assert.False(SimOrder.At(args, "agriculture", SimOrder.SlotPre));    // 同一步的另一段
        Assert.False(SimOrder.At(args, "industry", SimOrder.SlotPost));      // 同一个点的另一步
        Assert.True(SimOrder.InStep(args, "agriculture"));
        Assert.False(SimOrder.InStep(args, "industry"));
        // 完全没带 stepId 的广播（例：意图段/存档期）不能误命中任何门闸
        Assert.False(SimOrder.At(new HookArgs(), "agriculture", SimOrder.SlotPost));
    }

    [Fact]
    public void 每个hook点都说得出落在哪()
    {
        var map = SimOrder.DescribeHookMap();
        Assert.Equal(Enum.GetValues<HookPoint>().Length - 1, map.Count);     // Count 是哨兵，不算
        // 「—」意味着 SIM_ORDER.md 会生成一行没人能懂的表，必须逐点解释
        Assert.DoesNotContain(map, line => line.EndsWith("—", StringComparison.Ordinal));
        Assert.Contains(map, l => l.StartsWith("OnProduction", StringComparison.Ordinal) && l.Contains("03prepost"));
        Assert.Contains(map, l => l.StartsWith("OnConsumption", StringComparison.Ordinal) && l.Contains("10prepost"));
    }

    // ================= 数据注册表 =================

    private const string SchemaJson = """
        {
          "types": {
            "gizmo": {
              "idPrefix": "gizmo.",
              "required": ["title", "weight"],
              "scalars": { "title": "str", "weight": "fix", "note": "str" }
            },
            "holder": {
              "idPrefix": "holder.",
              "required": ["title"],
              "scalars": { "title": "str" },
              "refs": { "holds": "gizmo" }
            }
          }
        }
        """;

    private static string Pack(string entriesJson) =>
        "{ \"type\": \"gizmo\", \"entries\": [" + entriesJson + "] }";

    [Fact]
    public void 合法数据包按id序数返回且哈希稳定()
    {
        using var tmp = new TmpDir("data-ok");
        tmp.Write("_schema.json", SchemaJson);
        tmp.Write("base/a.json", Pack("""{ "id": "gizmo.zz", "title": "末", "weight": 1.50 }"""));
        tmp.Write("base/b.json", Pack("""{ "id": "gizmo.ab", "title": "首", "weight": 0.25 }"""));

        var reg = DataRegistry.Load(tmp.Path, stopOnFirstError: true);
        Assert.Equal(2, reg.Count);
        // §10.7 纪律 1：OfType 的顺序不能是「文件系统给的顺序」，必须是 id 序数
        Assert.Equal(new[] { "gizmo.ab", "gizmo.zz" }, reg.OfType("gizmo").Select(e => e.Id).ToArray());
        Assert.Equal(25, reg.OfType("gizmo")[0].GetFix("weight").Raw);          // 0.25 → 定点 25
        Assert.Equal(150, reg.ById("gizmo.zz").GetFix("weight").Raw);
        // Types 只列「真的有条目进来」的类型。schema 里声明了却一条都没用到的类型
        // 不出现在这里——那是无害的（留着给下一个包）；反过来「没声明就进来」是错的，
        // 由严格模式那条校验兜住，见下面的 严格模式拒收坏数据。
        Assert.Equal(new[] { "gizmo" }, reg.Types.ToArray());

        // 同内容换文件枚举顺序，dataHash 必须一致，否则「同数据同哈希」这条不成立
        using var tmp2 = new TmpDir("data-ok2");
        tmp2.Write("_schema.json", SchemaJson);
        tmp2.Write("base/zz-a.json", tmp.Read("base/a.json"));
        tmp2.Write("base/aa-b.json", tmp.Read("base/b.json"));
        Assert.Equal(reg.DataHash, DataRegistry.Load(tmp2.Path, stopOnFirstError: true).DataHash);
    }

    [Fact]
    public void 严格模式拒收坏数据()
    {
        // 缺必填
        using var missing = new TmpDir("data-required");
        missing.Write("_schema.json", SchemaJson);
        missing.Write("base/a.json", Pack("""{ "id": "gizmo.a", "title": "没有 weight" }"""));
        var e1 = Assert.Throws<SimDataException>(() => DataRegistry.Load(missing.Path, stopOnFirstError: true));
        Assert.Contains("weight", e1.Message, StringComparison.Ordinal);

        // id 前缀不符
        using var prefix = new TmpDir("data-prefix");
        prefix.Write("_schema.json", SchemaJson);
        prefix.Write("base/a.json", Pack("""{ "id": "wrong.a", "title": "x", "weight": 1 }"""));
        Assert.Throws<SimDataException>(() => DataRegistry.Load(prefix.Path, stopOnFirstError: true));

        // 类型没在 schema 里声明
        using var unknown = new TmpDir("data-type");
        unknown.Write("_schema.json", SchemaJson);
        unknown.Write("base/a.json", """{ "type": "nope", "entries": [ { "id": "nope.a" } ] }""");
        Assert.Throws<SimDataException>(() => DataRegistry.Load(unknown.Path, stopOnFirstError: true));

        // 引用悬空
        using var dangling = new TmpDir("data-ref");
        dangling.Write("_schema.json", SchemaJson);
        dangling.Write("base/a.json", Pack("""{ "id": "gizmo.a", "title": "x", "weight": 1 }"""));
        dangling.Write("base/h.json", """{ "type": "holder", "entries": [ { "id": "holder.h", "title": "持", "holds": "def:gizmo.ghost" } ] }""");
        Assert.Throws<SimDataException>(() => DataRegistry.Load(dangling.Path, stopOnFirstError: true));

        // 宽容模式：同样的坏数据只记警告，不开锅
        var lenient = DataRegistry.Load(missing.Path, stopOnFirstError: false);
        Assert.NotEmpty(lenient.Warnings);
    }

    // ================= 插件装载（M1 验收：非法插件被拒载）=================

    private static void MakePluginDir(TmpDir root, string id, string manifestJson) =>
        root.Write(id + "/manifest.json", manifestJson);

    [Fact]
    public void 清单有问题就拒载()
    {
        // id 与目录名不一致
        using var a = new TmpDir("plugin-id");
        MakePluginDir(a, "wrong", """{ "id": "other", "version": "1.0.0", "api": "1.0" }""");
        Assert.Throws<SimPluginException>(() => new PluginHost().Plan(a.Path));

        // 缺 api
        using var b = new TmpDir("plugin-api");
        MakePluginDir(b, "noplag", """{ "id": "noplag", "version": "1.0.0" }""");
        Assert.Throws<SimPluginException>(() => new PluginHost().Plan(b.Path));

        // 主版本不符（§10.5 的 api 版本检查）
        using var c = new TmpDir("plugin-major");
        MakePluginDir(c, "oldplug", """{ "id": "oldplug", "version": "1.0.0", "api": "0.9" }""");
        var e = Assert.Throws<SimPluginException>(() => new PluginHost().Plan(c.Path));
        Assert.Contains("Core API", e.Message, StringComparison.Ordinal);

        // 目录里连清单都没有 → 跳过而不是炸（允许 plugins 目录下躺着别的东西）
        using var d = new TmpDir("plugin-nomanifest");
        d.Write("stray/readme.txt", "不是插件");
        Assert.Empty(new PluginHost().Plan(d.Path));

        Assert.Throws<SimPluginException>(() => new PluginHost().Plan(Path.Combine(a.Path, "does-not-exist")));
    }

    [Fact]
    public void 依赖决定装载顺序且禁止成环()
    {
        using var t = new TmpDir("plugin-deps");
        MakePluginDir(t, "b.two", """{ "id": "b.two", "version": "1.0.0", "api": "1.0", "dependencies": ["a.one"] }""");
        MakePluginDir(t, "a.one", """{ "id": "a.one", "version": "1.0.0", "api": "1.0" }""");
        Assert.Equal(new[] { "a.one", "b.two" }, new PluginHost().Plan(t.Path).Select(d => d.Manifest.Id).ToArray());

        // 依赖缺失
        using var miss = new TmpDir("plugin-miss");
        MakePluginDir(miss, "c.three", """{ "id": "c.three", "version": "1.0.0", "api": "1.0", "dependencies": ["a.one"] }""");
        Assert.Throws<SimPluginException>(() => new PluginHost().Plan(miss.Path));

        // 环
        using var ring = new TmpDir("plugin-ring");
        MakePluginDir(ring, "r.one", """{ "id": "r.one", "version": "1.0.0", "api": "1.0", "dependencies": ["r.two"] }""");
        MakePluginDir(ring, "r.two", """{ "id": "r.two", "version": "1.0.0", "api": "1.0", "dependencies": ["r.one"] }""");
        var e = Assert.Throws<SimPluginException>(() => new PluginHost().Plan(ring.Path));
        Assert.Contains("循环依赖", e.Message, StringComparison.Ordinal);

        // 禁用：被禁的插件不算依赖缺失的反面——它会让依赖它的那个也装载不了
        var host = new PluginHost { Disabled = { "a.one" } };
        Assert.Throws<SimPluginException>(() => host.Plan(t.Path));
    }

    /// <summary>
    /// 键名写错必须当场炸。反序列化对认不出的键默认是「跳过」，于是 <c>loadPriority</c>（驼峰）
    /// 会静默落回默认 100、<c>dataPacks</c> 落回空表——插件照样加载，只是声明全废。
    /// M1 真踩过：core.basic 的清单就是这么写了三个月的驼峰，直到 sim check 打出 priority=100 才发现。
    /// </summary>
    [Fact]
    public void 清单键名拼错就拒载而不是静默落回默认值()
    {
        using var t = new TmpDir("plugin-camel");
        MakePluginDir(t, "x.one", """{ "id": "x.one", "version": "1.0.0", "api": "1.0", "loadPriority": 10 }""");
        var e = Assert.Throws<SimPluginException>(() => new PluginHost().Plan(t.Path));
        Assert.Contains("不认识的键", e.Message, StringComparison.Ordinal);
        Assert.Contains("loadPriority", e.Message, StringComparison.Ordinal);

        // 以 @ 开头的键是给人看的注释字段，合法（core.basic 的清单里就有 @comment）
        using var ok = new TmpDir("plugin-comment");
        MakePluginDir(ok, "y.one", """{ "@comment": "说明", "id": "y.one", "version": "1.0.0", "api": "1.0" }""");
        Assert.Equal(new[] { "y.one" }, new PluginHost().Plan(ok.Path).Select(d => d.Manifest.Id).ToArray());
    }

    [Fact]
    public void 仓库里真的装着core_basic()
    {
        // 这一条是给 CI 兜底的：如果 core.basic 没被编出来，上面所有「无插件」的
        // 测试照样绿，而玩家拿到的版本却什么都没得玩。
        var sim = new Simulation(new SimConfig { Seed = 1 }).Bootstrap();
        Assert.Contains("core.basic", sim.Plugins.Ids());
        Assert.NotEmpty(sim.Data.OfType(F.TypeClass));
        Assert.Contains(sim.StartupLog, l => l.Contains("core.basic", StringComparison.Ordinal));

        // 清单里写的东西必须真的绑定上，而不是解析成功但全是默认值。
        var m = sim.Plugins.Find("core.basic")!.Manifest;
        Assert.Equal(10, m.LoadPriority);
        Assert.Contains("core.basic", m.DataPacks);
        Assert.True(m.EnabledByDefault);
        Assert.NotEmpty(m.Title);
    }
}

internal static class DataRegistryTestExtensions
{
    public static DataEntry ById(this DataRegistry reg, string id) =>
        reg.TryGet(id, out var e) ? e : throw new SimDataException($"测试想要的数据条目不存在：{id}");
}

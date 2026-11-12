namespace SimCore;

/// <summary>不变量检查函数：返回违规描述列表，空列表表示通过。</summary>
public delegate InvariantResult InvariantCheck(IStepContext ctx);

/// <summary>不变量检查结果。允许多条违规一次报全（§10.6「失败要报哪一项、差多少」）。</summary>
public sealed record InvariantResult(IReadOnlyList<string> Violations)
{
    public static readonly InvariantResult Ok = new(Array.Empty<string>());
    public bool Passed => Violations.Count == 0;
    public static InvariantResult Fail(params string[] v) => new(v);
}

/// <summary>一条不变量规则：名字 + 生效的步骤集合 + 检查函数。</summary>
public sealed record InvariantRule(string Name, string Owner, IReadOnlyList<int> Steps, InvariantCheck Check, bool Fatal = true)
{
    /// <summary>该规则是否在第 <paramref name="step"/> 步末生效。</summary>
    public bool AppliesTo(int step) => Steps.Count == 0 || Steps.Contains(step);
}

/// <summary>
/// 不变量守卫（GDD §10.2 职责 6 / §5.9）。
///
/// 关键态度：违反即报错，不静默扩散。经济系统一旦守恒破了，后面每一步都在
/// 放大误差，标定与联机校验都会变成追不上的坑；所以宁可崩在当回合。
///
/// Core 只提供守卫与三条结构性检查（它不懂物资守恒）；
/// 守恒类规则由玩法插件注册（econ.local 注册物资守恒、town.classes 注册人口守恒…）。
/// </summary>
public sealed class InvariantGuard
{
    private readonly List<InvariantRule> _rules = new();

    /// <summary>插件注册自定义不变量。<paramref name="steps"/> 为空表示每步末都跑。</summary>
    public void Register(string name, string owner, IReadOnlyList<int> steps, InvariantCheck check, bool fatal = true)
    {
        if (_rules.Any(r => r.Name == name)) throw new SimPluginException(owner, $"不变量名重复:{name}");
        _rules.Add(new InvariantRule(name, owner, steps, check, fatal));
    }

    public IReadOnlyList<InvariantRule> Rules => _rules;

    /// <summary>卸载插件时回滚其不变量（§10.5「不允许半加载」的对称操作）。</summary>
    public void Remove(string name) => _rules.RemoveAll(r => r.Name == name);

    /// <summary>某步末注册了多少条不变量，启动日志与 TEST_PLAN 对账用。</summary>
    public int CountForStep(int step) => _rules.Count(r => r.AppliesTo(step));

    /// <summary>在某个阶段末调用。返回本回合累计违规（Fatal 的会同时进异常）。</summary>
    internal IReadOnlyList<string> RunAfter(int step, IStepContext ctx, bool throwOnFatal)
    {
        var found = new List<string>();
        foreach (var rule in _rules.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (!rule.AppliesTo(step)) continue;
            InvariantResult res;
            try
            {
                res = rule.Check(ctx);
            }
            catch (Exception e) when (e is not SimException)
            {
                // 检查函数本身抛异常：视为违规，不能因为「检查器坏了」就放过
                res = InvariantResult.Fail($"{rule.Name} 检查器抛出 {e.GetType().Name}: {e.Message}");
            }
            foreach (var v in res.Violations)
                found.Add($"[{rule.Name}] {(rule.Fatal ? "" : "(非致命) ")}{v}");
        }

        if (found.Count > 0 && throwOnFatal)
            throw new SimInvariantException(found);
        return found;
    }

    /// <summary>Core 自带的三条结构性检查：不依赖任何玩法知识。</summary>
    public void RegisterCoreChecks()
    {
        // C1 无主变更：任何写入都必须带 reason（§8.4 因果可读的下限）
        Register("core.has_reason", "core", new[] { 15 }, ctx =>
        {
            var bad = ctx.Journal.Diffs.Where(d => string.IsNullOrWhiteSpace(d.Reason)).Take(5).ToList();
            return bad.Count == 0 ? InvariantResult.Ok
                : InvariantResult.Fail($"有 {ctx.Journal.Diffs.Count(d => string.IsNullOrWhiteSpace(d.Reason))} 笔变更没有 reason，例：{bad[0].Id}.{bad[0].Path}");
        });

        // C2 字段不得留在 None 值上（None 表示不存在，应该走 Remove）
        Register("core.no_empty_field", "core", new[] { 15 }, ctx =>
        {
            var empties = ctx.World.Flatten().Where(t => t.Value.IsNull).Select(t => $"{t.Id}.{t.Path}").Take(5).ToList();
            return empties.Count == 0 ? InvariantResult.Ok
                : InvariantResult.Fail($"存在值为 None 的字段（应删除而不是置空）：{string.Join(", ", empties)}");
        });

        // C3 遍历顺序无关性自检：同一状态两次哈希必须相同
        Register("core.hash_stable", "core", new[] { 15 }, ctx =>
        {
            var h1 = StateHash.Compute(ctx.World, ctx.Commands, ctx.Rng).Total;
            var h2 = StateHash.Compute(ctx.World, ctx.Commands, ctx.Rng).Total;
            return h1 == h2 ? InvariantResult.Ok : InvariantResult.Fail($"同状态两次哈希不同：{h1} vs {h2}（存在依赖枚举顺序或浮点的代码）");
        });
    }
}

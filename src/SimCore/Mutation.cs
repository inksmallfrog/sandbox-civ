namespace SimCore;

/// <summary>状态变更的三种操作。</summary>
public enum MutationOp : byte
{
    Set = 0,
    /// <summary>对数值字段累加（delta 可为负）。</summary>
    Add = 1,
    Remove = 2,
}

/// <summary>
/// 一次状态变更的记录（GDD §8.4：任何数值变化，UI 必须能展开成「结果 ← 公式 ← 各项数值」）。
/// Core 不做因果链 UI，但必须把「谁在什么时候因为什么把它从 A 改成 B」留下来，
/// 否则 §14 的「经济不可解释 = P0」无从判定。
/// </summary>
public readonly record struct Diff(
    int Turn,
    int Step,
    EntityId Id,
    string Path,
    string Before,
    string After,
    MutationOp Op,
    string Reason)
{
    public string DeltaText => Op switch
    {
        MutationOp.Set => $"{Before} → {After}",
        MutationOp.Add => $"{Before} +({Reason}) → {After}",
        MutationOp.Remove => $"{Before} 删除",
        _ => After
    };

    /// <summary>人读的一行因果，供 CLI 报告与 UI 折叠列表直接渲染。</summary>
    public string Render(World world)
    {
        string who = world.Get(Id) is { } e ? $"{e.Kind}:{Id}" : Id.ToString();
        return $"[{Turn:D3}/{Step:D2}] {who}.{Path} {DeltaText}　——因为 {Reason}";
    }
}

/// <summary>
/// 一条「公式级」解释（§8.4 三段式里的第二、三段）。
/// 插件在算完一个数后调 <see cref="IStepContext.Explain"/>，把公式原文与各项数值交代清楚。
/// </summary>
public sealed record Explanation(
    int Turn,
    int Step,
    EntityId Subject,
    string Title,
    string Formula,
    IReadOnlyList<string> Terms,
    string Result);

/// <summary>本回合的变更日志与解释日志。回合末由 <see cref="TurnReport"/> 打包给壳层。</summary>
public sealed class TurnJournal
{
    private readonly List<Diff> _diffs = new();
    private readonly List<Explanation> _explains = new();
    private readonly List<string> _notes = new();

    public IReadOnlyList<Diff> Diffs => _diffs;
    public IReadOnlyList<Explanation> Explanations => _explains;
    public IReadOnlyList<string> Notes => _notes;

    internal void AddDiff(Diff d) => _diffs.Add(d);
    internal void AddExplain(Explanation e) => _explains.Add(e);
    internal void AddNote(string n) => _notes.Add(n);

    public IEnumerable<Diff> ForEntity(EntityId id) => _diffs.Where(d => d.Id == id);
    public IEnumerable<Diff> ForField(EntityId id, string path) => _diffs.Where(d => d.Id == id && d.Path == path);

    /// <summary>
    /// 渲染某个字段的因果链：结果 → 涉及的公式与各项 → 逐笔变更。
    /// §8.4 要求的「玩家要能说清为什么粮价涨了」就是这条输出。
    /// </summary>
    public string RenderChain(World world, EntityId id, string path)
    {
        var sb = new System.Text.StringBuilder();
        var sim = world.Get(id);
        sb.AppendLine($"结果：{(sim is null ? id.ToString() : sim.Kind + ":" + id)}.{path} = {(sim?.Get(path).Display ?? "—")}");

        var rel = _diffs.Where(d => d.Id == id && d.Path == path).ToList();
        if (rel.Count == 0)
        {
            sb.AppendLine("（本回合该字段无变更）");
            return sb.ToString();
        }

        foreach (var d in rel)
            sb.AppendLine($"  {d.Step:D2} 步 {SimOrder.Steps[d.Step - 1].Title}：{d.DeltaText}");

        var ex = _explains.Where(e => e.Subject == id && _diffs.Any(d => d.Id == id && d.Path == path)).ToList();
        foreach (var e in ex)
        {
            sb.AppendLine($"  公式：{e.Formula}");
            foreach (var t in e.Terms) sb.AppendLine($"    各项：{t}");
            sb.AppendLine($"    得：{e.Result}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// 插件在结算步骤里拿到的唯一上下文（GDD §10.4：插件通过 ctx.Mutate 提交修改）。
///
/// 纪律：
/// - 改状态只能走 <see cref="Mutate(EntityId, string, SimValue, string)"/> / <see cref="Add"/> / <see cref="RemoveField"/>，
///   每一笔都带 reason，这样 §8.4 的因果链与 diff 日志才是完整的。
/// - <see cref="World.WriteRaw"/> 是 Core 自己的后门（meta.* 与存档重建），插件拿不到；
///   绕过本接口改状态的那些变更在不变量检查里就是「无因变更」。
/// </summary>
public interface IStepContext
{
    World World { get; }
    SimTime Time { get; }
    RngFactory Rng { get; }
    DataRegistry Data { get; }
    TurnJournal Journal { get; }

    /// <summary>命令总线。只读用途：查本回合已受理的命令、算 AP 预算。</summary>
    CommandBus Commands { get; }

    /// <summary>当前结算步骤号（1–15），0 表示不在结算中。</summary>
    int Step { get; }

    SimValue Get(EntityId id, string path);
    Fix GetFix(EntityId id, string path, Fix fallback = default);
    long GetInt(EntityId id, string path, long fallback = 0);
    string GetStr(EntityId id, string path, string fallback = "");
    bool Has(EntityId id);
    IReadOnlyList<Entity> EntitiesOfKind(string kind);

    /// <summary>赋值。reason 是必填的：没有理由的变更等于不可解释的变更（§8.4）。</summary>
    void Mutate(EntityId id, string path, SimValue value, string reason);
    void Mutate(EntityId id, string path, Fix value, string reason);
    void Mutate(EntityId id, string path, long value, string reason);
    void Mutate(EntityId id, string path, string value, string reason);

    /// <summary>数值累加（delta 带符号）。</summary>
    void Add(EntityId id, string path, SimValue delta, string reason);
    void AddFix(EntityId id, string path, Fix delta, string reason);
    void AddInt(EntityId id, string path, long delta, string reason);
    void RemoveField(EntityId id, string path, string reason);

    Entity CreateEntity(string kind, string reason, EntityId? explicitId = null);
    void DestroyEntity(EntityId id, string reason);

    /// <summary>登记一条公式级解释（§8.4 第二、三段）。</summary>
    void Explain(EntityId subject, string title, string formula, IReadOnlyList<string> terms, string result);

    /// <summary>写一条人读日志（年度报告的原料）。</summary>
    void Note(string message);

    /// <summary>插件向 hook 注册自己的处理器。只允许在 OnLoad 阶段调用。</summary>
    void Require(HookPoint point, int priority, HookHandler handler);

    /// <summary>注册主题级处理器：对某 kind 的每个实体各跑一次（§10.4 的 OnProduction(town, ctx)）。</summary>
    void RequireFor(HookPoint point, string entityKind, int priority, SubjectHandler handler);

    /// <summary>
    /// 主题级分派：把该 (点, kind) 上注册的所有处理器，按实体 id 升序逐个跑。
    /// 由拥有该概念的插件调用，Core 只认 kind 字符串。
    /// </summary>
    void DispatchSubjects(HookPoint point, string entityKind, IReadOnlyList<EntityId> subjects,
        Func<EntityId, HookArgs>? argsFactory = null);
}

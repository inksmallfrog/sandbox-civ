namespace SimCore;

/// <summary>
/// <see cref="IStepContext"/> 的实现。Core 内部使用，插件只看到接口。
///
/// 这里集中了三条纪律的强制点：
/// 1. 写入必须带 reason（否则进 <see cref="InvariantGuard"/> 的 core.has_reason 检查）；
/// 2. 只读步骤禁止写入；
/// 3. 加载期之外禁止新增 hook 注册（防止插件在结算中偷偷改变执行集合）。
/// </summary>
internal sealed class StepContext : IStepContext
{
    private readonly Simulation _sim;
    private readonly HookRegistry _hooks;

    public StepContext(Simulation sim, int step, bool allowRegister)
    {
        _sim = sim;
        Step = step;
        AllowRegister = allowRegister;
        _hooks = sim.Hooks;
        Journal = sim.CurrentJournal;
    }

    public World World => _sim.World;
    public SimTime Time => _sim.Time;
    public RngFactory Rng => _sim.Rng;
    public DataRegistry Data => _sim.Data;
    public CommandBus Commands => _sim.Commands;
    public TurnJournal Journal { get; }
    public int Step { get; }
    public bool AllowRegister { get; }

    public SimValue Get(EntityId id, string path) => World.GetField(id, path);
    public Fix GetFix(EntityId id, string path, Fix fallback = default) => World.GetFix(id, path, fallback);
    public long GetInt(EntityId id, string path, long fallback = 0) => World.GetInt(id, path, fallback);
    public string GetStr(EntityId id, string path, string fallback = "") => World.GetStr(id, path, fallback);
    public bool Has(EntityId id) => World.Has(id);
    public IReadOnlyList<Entity> EntitiesOfKind(string kind) => World.OfKind(kind);

    private void Guard(string what)
    {
        // 结算外的写入只允许发生在 bootstrap / restore 路径，由 Simulation 自己调用
        if (Step == -1) throw new SimStateException($"注册期不得{what}：OnLoad 只负责注册，不负责改世界");
    }

    public void Mutate(EntityId id, string path, SimValue value, string reason)
    {
        Guard("写状态");
        var ent = World.GetRequired(id);
        var before = ent.Get(path).Canonical;
        World.WriteRaw(id, path, value);
        Journal.AddDiff(new Diff(Time.Turn, Step, id, path, before, value.Canonical, MutationOp.Set, reason));
    }

    public void Mutate(EntityId id, string path, Fix value, string reason) => Mutate(id, path, SimValue.Of(value), reason);
    public void Mutate(EntityId id, string path, long value, string reason) => Mutate(id, path, SimValue.OfInt(value), reason);
    public void Mutate(EntityId id, string path, string value, string reason) => Mutate(id, path, SimValue.OfStr(value), reason);

    public void Add(EntityId id, string path, SimValue delta, string reason)
    {
        Guard("写状态");
        var ent = World.GetRequired(id);
        var cur = ent.Get(path);
        var before = cur.Canonical;
        var after = cur.IsNull ? delta : cur.Plus(delta);
        World.WriteRaw(id, path, after);
        Journal.AddDiff(new Diff(Time.Turn, Step, id, path, before, after.Canonical, MutationOp.Add, reason));
    }

    public void AddFix(EntityId id, string path, Fix delta, string reason) => Add(id, path, SimValue.Of(delta), reason);
    public void AddInt(EntityId id, string path, long delta, string reason) => Add(id, path, SimValue.OfInt(delta), reason);

    public void RemoveField(EntityId id, string path, string reason)
    {
        Guard("删字段");
        var ent = World.GetRequired(id);
        var before = ent.Get(path).Canonical;
        World.WriteRaw(id, path, SimValue.Null);
        Journal.AddDiff(new Diff(Time.Turn, Step, id, path, before, "n", MutationOp.Remove, reason));
    }

    public Entity CreateEntity(string kind, string reason, EntityId? explicitId = null)
    {
        Guard("建实体");
        var ent = World.Create(kind, explicitId);
        Journal.AddDiff(new Diff(Time.Turn, Step, ent.Id, "@created", "n", SimValue.OfStr(kind).Canonical, MutationOp.Set, reason));
        return ent;
    }

    public void DestroyEntity(EntityId id, string reason)
    {
        Guard("删实体");
        var ent = World.GetRequired(id);
        string kind = ent.Kind;
        World.Remove(id);
        Journal.AddDiff(new Diff(Time.Turn, Step, id, "@destroyed", SimValue.OfStr(kind).Canonical, "n", MutationOp.Remove, reason));
    }

    public void Explain(EntityId subject, string title, string formula, IReadOnlyList<string> terms, string result) =>
        Journal.AddExplain(new Explanation(Time.Turn, Step, subject, title, formula, terms, result));

    public void Note(string message) => Journal.AddNote($"[{Time.Text}] {message}");

    public void Require(HookPoint point, int priority, HookHandler handler)
    {
        AssertRegisterPhase();
        _hooks.Register(point, _sim.LoadingPluginId ?? "core", priority, handler);
    }

    public void RequireFor(HookPoint point, string entityKind, int priority, SubjectHandler handler)
    {
        AssertRegisterPhase();
        _hooks.RegisterSubject(point, entityKind, _sim.LoadingPluginId ?? "core", priority, handler);
    }

    public void DispatchSubjects(HookPoint point, string entityKind, IReadOnlyList<EntityId> subjects,
        Func<EntityId, HookArgs>? argsFactory = null)
    {
        var ordered = subjects.OrderBy(x => x, EntityIdComparer.OrdinalInstance).ToList();
        _hooks.DispatchSubjects(point, entityKind, ordered, this, argsFactory);
    }

    private void AssertRegisterPhase()
    {
        if (AllowRegister) return;
        throw new SimPluginException(_sim.LoadingPluginId ?? "?",
            $"只能在插件 OnLoad 阶段注册 hook；当前在步骤 {Step} 尝试注册");
    }
}

/// <summary>按 ordinal 比较实体 id。所有「按 id 升序」的地方都用它，避免默认比较带来的隐性差异。</summary>
public sealed class EntityIdComparer : IComparer<EntityId>
{
    public static readonly EntityIdComparer OrdinalInstance = new();
    public int Compare(EntityId a, EntityId b) => a.CompareTo(b);
}

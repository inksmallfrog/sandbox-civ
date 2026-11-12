namespace SimCore;

/// <summary>
/// 全部 hook 点（GDD §10.4）。插件之间禁止直接调用，只能挂这些点。
///
/// 注意 Core 的签名里没有出现「城镇」「阶级」「商品」这些类型：
/// hook 用 <see cref="HookArgs"/>（主题实体 + 具名参数袋）传递，
/// 槽位名由插件与数据表约定，写在 docs/PLUGIN_API.md。
/// 这样 Core 才能满足 §10.2「Core 不认识农民、粮食、宗教」。
/// </summary>
public enum HookPoint
{
    // 回合级
    OnTurnStart = 0,
    OnActionValidate = 1,

    // 结算 15 步上的插入点
    OnProduction = 2,
    OnConsumption = 3,
    OnPriceForm = 4,
    OnAllocate = 5,
    OnSatisfaction = 6,
    OnClassTransition = 7,
    OnMigration = 8,
    OnLogisticsCheck = 9,
    OnBattleResolve = 10,
    OnEventDraw = 11,
    OnEventApply = 12,
    OnHeroRoll = 13,
    OnAppointment = 14,
    OnDemand = 15,
    OnDiplomaticAction = 16,

    // 收尾与持久化
    OnTurnEnd = 17,
    OnBeforeSave = 18,
    OnLoad = 19,
    OnMigrate = 20,

    /// <summary>数量，供数组分配。</summary>
    Count = 21,
}

/// <summary>hook 参数的具名槽位（字符串键，插件自订；Core 不解释含义）。</summary>
public sealed class HookArgs
{
    private readonly SortedDictionary<string, SimValue> _values = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, EntityId> _entities = new(StringComparer.Ordinal);

    /// <summary>本 hook 的主题实体，例：OnProduction 的城镇。可为空（全局性 hook）。</summary>
    public EntityId Subject { get; init; }

    public HookArgs With(string key, SimValue value)
    {
        _values[key] = value;
        return this;
    }

    public HookArgs With(string key, Fix value) => With(key, SimValue.Of(value));
    public HookArgs With(string key, long value) => With(key, SimValue.OfInt(value));
    public HookArgs With(string key, string value) => With(key, SimValue.OfStr(value));
    public HookArgs With(string key, EntityId value)
    {
        _entities[key] = value;
        return this;
    }

    public bool TryGet(string key, out SimValue value) => _values.TryGetValue(key, out value!);
    public SimValue Get(string key) => _values.TryGetValue(key, out var v) ? v : SimValue.Null;
    public Fix GetFix(string key, Fix fallback = default) => _values.TryGetValue(key, out var v) && !v.IsNull ? v.AsFix() : fallback;
    public long GetInt(string key, long fallback = 0) => _values.TryGetValue(key, out var v) && !v.IsNull ? v.AsInt() : fallback;
    public string GetStr(string key, string fallback = "") => _values.TryGetValue(key, out var v) && !v.IsNull ? v.AsString() : fallback;
    public bool TryGetEntity(string key, out EntityId id) => _entities.TryGetValue(key, out id);
    public EntityId GetEntity(string key) => _entities.TryGetValue(key, out var id) ? id : default;
    public bool HasSubject => !Subject.IsEmpty;

    /// <summary>结果槽位：验证类 hook（OnActionValidate）用它写回合法性与 AP 折扣。</summary>
    public SimValue Result
    {
        get => _values.TryGetValue("result", out var v) ? v : SimValue.Null;
        set => _values["result"] = value;
    }

    public IReadOnlyDictionary<string, SimValue> Values => _values;
    public IReadOnlyDictionary<string, EntityId> Entities => _entities;

    public static HookArgs Empty => new();
}

/// <summary>一个 hook 处理器。<paramref name="args"/> 可被读写（这是插件之间唯一的通信方式）。</summary>
public delegate void HookHandler(HookArgs args, IStepContext ctx);

/// <summary>
/// 主题级处理器：对某 kind 的每个实体各跑一次。
/// Core 只提供机制（按 id 升序遍历某个 kind），「谁是主题」由插件自己决定，
/// 这样 §10.4 的 <c>OnProduction(town, ctx)</c> 才有落点而 Core 仍然不认识城镇。
/// </summary>
public delegate void SubjectHandler(EntityId subject, HookArgs args, IStepContext ctx);

/// <summary>
/// hook 注册表。同一 hook 点内按 (priority 升序, pluginId 升序, 注册序号升序) 执行，
/// 三级排序保证「注册顺序」以外的因素不参与结果（§10.4 最后一句）。
/// </summary>
public sealed class HookRegistry
{
    private readonly List<(HookPoint Point, int Priority, string PluginId, int Seq, HookHandler Handler)> _handlers = new();
    private readonly Dictionary<HookPoint, List<(int Priority, string PluginId, int Seq, HookHandler Handler)>> _index = new();
    private readonly Dictionary<(HookPoint, string), List<(int Priority, string PluginId, int Seq, SubjectHandler Handler)>> _subjects = new();
    private int _seq;

    /// <summary>已注册的 hook 点（供 PLUGIN_API 文档与自检用）。</summary>
    public IReadOnlyList<HookPoint> RegisteredPoints => _index.Keys.Union(_subjects.Keys.Select(k => k.Item1)).OrderBy(p => (int)p).ToList();

    /// <summary>已注册的主题 hook 的 (点, kind) 清单，插件面板用。</summary>
    public IReadOnlyList<string> RegisteredSubjectLines =>
        _subjects.Keys.OrderBy(k => (int)k.Item1).ThenBy(k => k.Item2, StringComparer.Ordinal)
            .Select(k => $"{k.Item1} × {k.Item2}（{_subjects[k].Count} 个处理器）").ToList();

    public void Register(HookPoint point, string pluginId, int priority, HookHandler handler)
    {
        if (handler is null) throw new SimPluginException(pluginId, $"{point} 的 handler 为 null");
        if (priority < 0 || priority > 1000) throw new SimPluginException(pluginId, $"{point} 的 priority={priority} 越界（0–1000）");
        _handlers.Add((point, priority, pluginId, _seq, handler));
        if (!_index.TryGetValue(point, out var list)) _index[point] = list = new();
        list.Add((priority, pluginId, _seq, handler));
        list.Sort(static (a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.PluginId, b.PluginId);
            if (c != 0) return c;
            return a.Seq.CompareTo(b.Seq);
        });
        _seq++;
    }

    /// <summary>注册主题级处理器（对某 kind 的每个实体各跑一次）。</summary>
    public void RegisterSubject(HookPoint point, string kind, string pluginId, int priority, SubjectHandler handler)
    {
        if (string.IsNullOrEmpty(kind)) throw new SimPluginException(pluginId, $"{point} 的主题 kind 为空");
        var key = (point, kind);
        if (!_subjects.TryGetValue(key, out var list)) _subjects[key] = list = new();
        list.Add((priority, pluginId, _seq, handler));
        list.Sort(static (a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.PluginId, b.PluginId);
            if (c != 0) return c;
            return a.Seq.CompareTo(b.Seq);
        });
        _seq++;
    }

    /// <summary>依次调用某 hook 点的所有全局处理器。异常直接上抛——不允许插件静默失败。</summary>
    public void Dispatch(HookPoint point, HookArgs args, IStepContext ctx)
    {
        if (!_index.TryGetValue(point, out var list) || list.Count == 0) return;
        // 拷贝一份：允许 handler 在运行时注册（例：事件解锁新 hook）
        var snapshot = list.ToArray();
        foreach (var h in snapshot) h.Handler(args, ctx);
    }

    /// <summary>
    /// 主题级分派：对 <paramref name="subjects"/>（必须已按 id 升序）逐个跑该 (点,kind) 的处理器。
    /// 每个主题一份新的 <see cref="HookArgs"/>，避免插件之间互相污染参数。
    /// </summary>
    public void DispatchSubjects(HookPoint point, string kind, IReadOnlyList<EntityId> subjects,
        IStepContext ctx, Func<EntityId, HookArgs>? argsFactory = null)
    {
        if (!_subjects.TryGetValue((point, kind), out var list) || list.Count == 0) return;
        var snapshot = list.ToArray();
        foreach (var id in subjects)
        {
            var args = argsFactory?.Invoke(id) ?? new HookArgs { Subject = id };
            foreach (var h in snapshot) h.Handler(id, args, ctx);
        }
    }

    public int Count(HookPoint point) => _index.TryGetValue(point, out var l) ? l.Count : 0;

    internal void UnregisterAll(string pluginId)
    {
        _handlers.RemoveAll(h => h.PluginId == pluginId);
        foreach (var kv in _index)
        {
            kv.Value.RemoveAll(h => h.PluginId == pluginId);
            kv.Value.Sort(static (a, b) =>
            {
                int c = a.Priority.CompareTo(b.Priority);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.PluginId, b.PluginId);
                if (c != 0) return c;
                return a.Seq.CompareTo(b.Seq);
            });
        }
        foreach (var kv in _subjects)
        {
            kv.Value.RemoveAll(h => h.PluginId == pluginId);
            kv.Value.Sort(static (a, b) =>
            {
                int c = a.Priority.CompareTo(b.Priority);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.PluginId, b.PluginId);
                if (c != 0) return c;
                return a.Seq.CompareTo(b.Seq);
            });
        }
    }
}

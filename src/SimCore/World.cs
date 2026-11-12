namespace SimCore;

/// <summary>
/// 一个实体 = id + kind + 一张按字段路径排序的值表（GDD §10.2 职责 1）。
/// 刻意不做完整 ECS：字典 + 组件字段 + 类型化访问器足够，
/// 而且状态哈希 / 存档 / 因果 diff 全都免费拿到。
/// </summary>
public sealed class Entity
{
    public readonly EntityId Id;

    /// <summary>实体类别，取自 <c>data/kinds.json</c>：hex / town / nation / hero / unit / world。</summary>
    public readonly string Kind;

    /// <summary>组件字段表，键为 "组件.字段[.子路径]"，按 ordinal 排序。</summary>
    /// <remarks>排序字典不是优化，是正确性：任何遍历都必须与插入顺序无关（§10.7）。</remarks>
    internal readonly SortedDictionary<string, SimValue> Fields = new(StringComparer.Ordinal);

    internal Entity(EntityId id, string kind)
    {
        Id = id;
        Kind = kind;
    }

    public SimValue Get(string path) => Fields.TryGetValue(path, out var v) ? v : SimValue.Null;
    public bool Has(string path) => Fields.ContainsKey(path);
    public Fix GetFix(string path) => Get(path).AsFix();
    public long GetInt(string path) => Get(path).AsInt();
    public string GetStr(string path) => Get(path).AsString();
    public bool GetBool(string path) => Get(path).AsBool();

    /// <summary>缺省值版本：字段不存在或为 None 时返回 <paramref name="fallback"/>。</summary>
    public long GetInt(string path, long fallback) => Get(path).Kind switch
    {
        ValueKind.None => fallback,
        _ => Get(path).AsInt()
    };

    public Fix GetFix(string path, Fix fallback) => Get(path).Kind switch
    {
        ValueKind.None => fallback,
        _ => Get(path).AsFix()
    };

    /// <summary>某组件下的全部字段，如 <c>Prefix("stock.")</c>。</summary>
    public IEnumerable<KeyValuePair<string, SimValue>> Prefix(string componentPrefix) =>
        Fields.Where(kv => kv.Key.StartsWith(componentPrefix, StringComparison.Ordinal));

    public IReadOnlyList<string> FieldPaths(string componentPrefix) =>
        Fields.Keys.Where(k => k.StartsWith(componentPrefix, StringComparison.Ordinal)).ToList();

    public int Count(string componentPrefix) => Fields.Count(kv => kv.Key.StartsWith(componentPrefix, StringComparison.Ordinal));

    public override string ToString() => $"{Kind}:{Id}({Fields.Count} 字段)";
}

/// <summary>
/// 世界存储：<c>id → Entity</c>（GDD §10.2 职责 1）。
///
/// Core 只保证四件事：id 唯一、kind 分组可枚举、字段可读写、遍历顺序确定。
/// 「城镇有几个农民」「粮价多少」这些语义全在插件与数据包里，Core 一概不知。
/// </summary>
public sealed class World
{
    /// <summary>全局标量所寄居的实体 id（回合号、种子、世界世代等）。</summary>
    public static readonly EntityId GlobalId = new("world.0000");
    public const string GlobalKind = "world";

    private readonly SortedDictionary<EntityId, Entity> _entities = new();
    private readonly Dictionary<string, SortedDictionary<EntityId, Entity>> _byKind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _kindCounters = new(StringComparer.Ordinal);

    /// <summary>每次结构性变更（增删实体 / 改字段）自增，供缓存与 diff 检测。</summary>
    public long Revision { get; internal set; }

    public Entity Global => Get(GlobalId) ?? Create(GlobalKind, GlobalId);

    public int EntityCount => _entities.Count;

    public Entity Create(string kind, EntityId? explicitId = null)
    {
        EntityId id;
        if (explicitId is { } e && !e.IsEmpty)
        {
            id = e;
            if (_entities.ContainsKey(id)) throw new SimStateException($"实体已存在：{id}");
        }
        else
        {
            // 自动 id 走 per-kind 计数器并零填充，保证「同命令流 → 同 id」
            _kindCounters.TryGetValue(kind, out int n);
            _kindCounters[kind] = n + 1;
            id = EntityId.New(kind, n);
            while (_entities.ContainsKey(id))
            {
                _kindCounters[kind] = ++n;
                id = EntityId.New(kind, n);
            }
        }

        var ent = new Entity(id, kind);
        _entities.Add(id, ent);
        if (!_byKind.TryGetValue(kind, out var group))
            _byKind[kind] = group = new SortedDictionary<EntityId, Entity>();
        group.Add(id, ent);
        Revision++;
        return ent;
    }

    public bool Remove(EntityId id)
    {
        if (!_entities.TryGetValue(id, out var ent)) return false;
        _entities.Remove(id);
        if (_byKind.TryGetValue(ent.Kind, out var group)) group.Remove(id);
        Revision++;
        return true;
    }

    public bool Has(EntityId id) => _entities.ContainsKey(id);
    public Entity? Get(EntityId id) => _entities.TryGetValue(id, out var e) ? e : null;
    public Entity GetRequired(EntityId id) => Get(id) ?? throw new SimStateException($"实体不存在：{id}");

    /// <summary>全部实体，按 id ordinal 升序（§10.7：禁止依赖字典枚举顺序）。</summary>
    public IReadOnlyList<Entity> All() => _entities.Values.ToList();

    /// <summary>某 kind 的实体，按 id ordinal 升序。</summary>
    public IReadOnlyList<Entity> OfKind(string kind) =>
        _byKind.TryGetValue(kind, out var g) ? g.Values.ToList() : Array.Empty<Entity>();

    public IReadOnlyList<string> Kinds() => _byKind.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    // ---- 字段访问（只读；带 reason 的写入请走 MutationContext）----
    public SimValue GetField(EntityId id, string path) => Get(id)?.Get(path) ?? SimValue.Null;
    public Fix GetFix(EntityId id, string path) => Get(id)!.GetFix(path);
    public long GetInt(EntityId id, string path) => Get(id)!.GetInt(path);
    public string GetStr(EntityId id, string path) => Get(id)!.GetStr(path);

    /// <summary>带缺省值的读取：实体或字段不存在时返回 <paramref name="fallback"/>。元数据读取的主力。</summary>
    public long GetInt(EntityId id, string path, long fallback) => Get(id)?.GetInt(path, fallback) ?? fallback;
    public Fix GetFix(EntityId id, string path, Fix fallback) => Get(id)?.GetFix(path, fallback) ?? fallback;
    public string GetStr(EntityId id, string path, string fallback = "")
    {
        var e = Get(id);
        if (e is null || e.Get(path).IsNull) return fallback;
        return e.Get(path).AsString();
    }
    public bool GetBool(EntityId id, string path, bool fallback = false)
    {
        var e = Get(id);
        if (e is null || e.Get(path).IsNull) return fallback;
        return e.Get(path).AsBool();
    }

    /// <summary>
    /// 底层写入。<b>插件不要直接调这里</b>——请走 <see cref="IStepContext.Mutate(EntityId, string, SimValue, string)"/>，
    /// 否则因果链（§8.4）与 diff 日志会缺一块。Core 与存档重建才用这个入口。
    /// </summary>
    internal void WriteRaw(EntityId id, string path, SimValue value)
    {
        var ent = GetRequired(id);
        if (value.IsNull) ent.Fields.Remove(path);
        else ent.Fields[path] = value;
        Revision++;
    }

    internal void MergeInto(Entity target, Entity source)
    {
        foreach (var kv in source.Fields) target.Fields[kv.Key] = kv.Value;
    }

    /// <summary>把字段路径展平成 (path, canonical) 序列，供哈希与存档使用。</summary>
    public IEnumerable<(EntityId Id, string Path, SimValue Value)> Flatten()
    {
        foreach (var ent in _entities.Values)
            foreach (var kv in ent.Fields)
                yield return (ent.Id, kv.Key, kv.Value);
    }

    /// <summary>kind 计数器：存档必须带，否则读档后新建实体会撞 id。</summary>
    internal IReadOnlyDictionary<string, int> KindCountersSnapshot() =>
        _kindCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    internal void RestoreKindCounters(IReadOnlyDictionary<string, int> counters)
    {
        _kindCounters.Clear();
        foreach (var kv in counters) _kindCounters[kv.Key] = kv.Value;
    }
}

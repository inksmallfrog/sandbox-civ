using System.Text.Json;

namespace SimCore;

/// <summary>
/// 一条数据记录（GDD §10.5：加内容 = 加文件）。
///
/// 同时保留两种视图：
/// - <see cref="Scalars"/>：展平后的标量字段（键为点分路径），Core 用它做 schema 校验、
///   id 唯一性检查与数据哈希；
/// - <see cref="Root"/>：原始 JSON 元素，插件用它遍历嵌套结构（需求篮子、条件数组等）。
///
/// Core 解释「这条记录的 labor 字段是 0.9」这种事实，但不知道 labor 是什么意思。
/// </summary>
public sealed class DataEntry
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    /// <summary>来源文件（相对 data/ 的路径），报错与热重载时要用。</summary>
    public required string Source { get; init; }
    /// <summary>数据包名（顶层目录名或 pack 字段），用于 DLC / 卡包启用禁用。</summary>
    public string Pack { get; init; } = "base";
    public JsonElement Root { get; init; }
    /// <summary>点分路径 → 标量值。</summary>
    public IReadOnlyDictionary<string, SimValue> Scalars { get; init; } = new Dictionary<string, SimValue>();

    public SimValue Get(string path) => Scalars.TryGetValue(path, out var v) ? v : SimValue.Null;
    public Fix GetFix(string path, Fix fallback = default) => Get(path).Kind switch
    {
        ValueKind.None => fallback,
        _ => Get(path).AsFix()
    };
    public long GetInt(string path, long fallback = 0) => Get(path).Kind switch
    {
        ValueKind.None => fallback,
        _ => Get(path).AsInt()
    };
    public string GetStr(string path, string fallback = "") => Get(path).Kind switch
    {
        ValueKind.None => fallback,
        _ => Get(path).AsString()
    };
    public bool Has(string path) => Scalars.ContainsKey(path);

    /// <summary>取嵌套数组字段，缺失返回空数组。</summary>
    public JsonElement GetArray(string path)
    {
        if (!TryResolve(Root, path, out var el)) return default;
        return el.ValueKind == JsonValueKind.Array ? el : default;
    }

    public JsonElement GetObject(string path)
    {
        if (!TryResolve(Root, path, out var el)) return default;
        return el.ValueKind == JsonValueKind.Object ? el : default;
    }

    internal static bool TryResolve(JsonElement root, string dottedPath, out JsonElement found)
    {
        found = default;
        JsonElement cur = root;
        foreach (var seg in dottedPath.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object) return false;
            if (!cur.TryGetProperty(seg, out cur)) return false;
        }
        found = cur;
        return true;
    }

    public override string ToString() => $"{Type}:{Id}";
}

/// <summary>某个数据类型的校验规则（来自 <c>data/_schema.json</c>）。</summary>
public sealed class TypeSchema
{
    public required string Type { get; init; }
    /// <summary>id 必须以此开头，例 type=class → 前缀 "class."。</summary>
    public string IdPrefix { get; init; } = "";
    public IReadOnlyList<string> Required { get; init; } = Array.Empty<string>();
    /// <summary>点分路径 → 期望标量类型：fix / int / str / bool。</summary>
    public IReadOnlyDictionary<string, string> ScalarTypes { get; init; } = new Dictionary<string, string>();
    /// <summary>点分路径 → 引用的目标 type；值必须写成 "def:&lt;id&gt;"。</summary>
    public IReadOnlyDictionary<string, string> Refs { get; init; } = new Dictionary<string, string>();
}

/// <summary>数据包与校验（GDD §10.2 职责 4）。</summary>
public sealed class DataRegistry
{
    private readonly SortedDictionary<string, DataEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DataEntry>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeSchema> _schemas = new(StringComparer.Ordinal);

    public static readonly string SchemaFileName = "_schema.json";
    public const string DefPrefix = "def:";

    public int Count => _entries.Count;
    public IReadOnlyDictionary<string, TypeSchema> Schemas => _schemas;
    /// <summary>已加载的数据包名（升序），写进存档做版本核对。</summary>
    public IReadOnlyList<string> LoadedPacks { get; private set; } = Array.Empty<string>();
    /// <summary>数据内容哈希：任何一处数值改动都会改变它，标定报告必须带上它。</summary>
    public string DataHash { get; private set; } = "";
    /// <summary>校验错误（不致命的记这里，致命的直接抛）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    private readonly List<string> _warnings = new();

    public IReadOnlyList<DataEntry> OfType(string type) =>
        _byType.TryGetValue(type, out var l) ? l : Array.Empty<DataEntry>();

    public bool TryGet(string id, out DataEntry entry) => _entries.TryGetValue(id, out entry!);

    public DataEntry Get(string id) =>
        _entries.TryGetValue(id, out var e) ? e : throw new SimDataException($"数据不存在：{id}");

    /// <summary>取 <c>def:</c> 引用指向的记录。</summary>
    public DataEntry Resolve(string defRef)
    {
        if (!defRef.StartsWith(DefPrefix, StringComparison.Ordinal))
            throw new SimDataException($"不是 def: 引用：{defRef}");
        return Get(defRef[DefPrefix.Length..]);
    }

    public bool Exists(string defRef) =>
        defRef.StartsWith(DefPrefix, StringComparison.Ordinal) && _entries.ContainsKey(defRef[DefPrefix.Length..]);

    /// <summary>从目录加载全部数据包。<c>stopOnFirstError</c> 为真时，任一 schema 错误立刻抛。</summary>
    public static DataRegistry Load(string rootDir, bool stopOnFirstError = true)
    {
        var reg = new DataRegistry();
        if (!Directory.Exists(rootDir))
            throw new SimDataException($"数据目录不存在：{rootDir}");

        string schemaPath = Path.Combine(rootDir, SchemaFileName);
        if (File.Exists(schemaPath)) reg.LoadSchema(schemaPath);

        var files = Directory.EnumerateFiles(rootDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(SchemaFileName, StringComparison.Ordinal))
            .Where(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (string file in files) reg.LoadFile(file, rootDir);

        reg.ValidateRefs();
        reg.BuildDataHash();
        reg.LoadedPacks = reg._entries.Values.Select(e => e.Pack).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

        if (stopOnFirstError && reg._warnings.Count > 0)
            throw new SimDataException($"数据校验失败 {reg._warnings.Count} 条：\n  - " + string.Join("\n  - ", reg._warnings.Take(20)));
        return reg;
    }

    private void LoadSchema(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("types", out var types)) return;
        foreach (var prop in types.EnumerateObject())
        {
            var t = prop.Value;
            var scalar = new Dictionary<string, string>(StringComparer.Ordinal);
            if (t.TryGetProperty("scalars", out var s))
                foreach (var p in s.EnumerateObject()) scalar[p.Name] = p.Value.GetString() ?? "";
            var refs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (t.TryGetProperty("refs", out var r))
                foreach (var p in r.EnumerateObject()) refs[p.Name] = p.Value.GetString() ?? "";
            var req = new List<string>();
            if (t.TryGetProperty("required", out var q) && q.ValueKind == JsonValueKind.Array)
                foreach (var item in q.EnumerateArray()) req.Add(item.GetString() ?? "");

            _schemas[prop.Name] = new TypeSchema
            {
                Type = prop.Name,
                IdPrefix = t.TryGetProperty("idPrefix", out var pre) ? pre.GetString() ?? "" : prop.Name + ".",
                Required = req,
                ScalarTypes = scalar,
                Refs = refs,
            };
        }
    }

    private void LoadFile(string path, string rootDir)
    {
        string rel = Path.GetRelativePath(rootDir, path).Replace('\\', '/');
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }).RootElement;
        }
        catch (JsonException e)
        {
            throw new SimDataException($"{rel} 不是合法 JSON：{e.Message}");
        }

        string pack = rel.Contains('/') ? rel[..rel.IndexOf('/')] : "base";

        // 一个文件可以是 {"type": "...", "entries": [...]}，也可以是单个对象
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var entriesEl))
        {
            string type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(type))
                throw new SimDataException($"{rel} 缺少 type 字段");
            foreach (var item in entriesEl.EnumerateArray())
                AddEntry(item, type, rel, pack);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                string type = item.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                AddEntry(item, type, rel, pack);
            }
        }
        else
        {
            string type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            AddEntry(root, type, rel, pack);
        }
    }

    private void AddEntry(JsonElement el, string type, string source, string pack)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new SimDataException($"{source}：记录必须是对象");
        if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            throw new SimDataException($"{source}：记录缺少字符串 id（type={type}）");
        string id = idEl.GetString()!;

        if (string.IsNullOrEmpty(type))
            type = id.Contains('.') ? id[..id.IndexOf('.')] : "misc";

        if (_schemas.TryGetValue(type, out var sch) && !string.IsNullOrEmpty(sch.IdPrefix) && !id.StartsWith(sch.IdPrefix, StringComparison.Ordinal))
            _warnings.Add($"{source} {id}：type={type} 要求 id 前缀 '{sch.IdPrefix}'");

        if (_entries.ContainsKey(id))
            throw new SimDataException($"{source}：id 重复 {id}（已在 {_entries[id].Source} 定义）");

        var scalars = new SortedDictionary<string, SimValue>(StringComparer.Ordinal);
        Flatten(el, "", scalars);

        var entry = new DataEntry
        {
            Id = id,
            Type = type,
            Source = source,
            Pack = pack,
            Root = el,
            Scalars = scalars,
        };

        if (_schemas.TryGetValue(type, out var s2)) ValidateAgainst(s2, entry);

        _entries[id] = entry;
        if (!_byType.TryGetValue(type, out var list)) _byType[type] = list = new();
        list.Add(entry);
        // §10.7 纪律 1：OfType 是插件遍历数据的主要入口（阶级表、建筑表都要循环），
        // 它的顺序必须与「文件在磁盘上被枚举的顺序」无关，所以每次插入按 id 重排。
        // 数据量是几十条量级，重排的代价可以忽略；换来的是跨机器、跨打包方式的确定性。
        list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    private static void Flatten(JsonElement el, string prefix, SortedDictionary<string, SimValue> into)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.StartsWith("@", StringComparison.Ordinal)) continue; // 以 @ 开头的键视为注释/元数据，不进状态
            string key = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    // 带小数点 → 定点（×100）；纯整数 → 整数。数据作者想清楚写哪种。
                    into[key] = p.Value.RawTextContainsDot()
                        ? SimValue.Of(Fix.FromDecimalText(p.Value.GetRawText()))
                        : SimValue.OfInt(p.Value.GetInt64());
                    break;
                case JsonValueKind.String:
                    into[key] = SimValue.OfStr(p.Value.GetString()!);
                    break;
                case JsonValueKind.True:
                    into[key] = SimValue.True;
                    break;
                case JsonValueKind.False:
                    into[key] = SimValue.False;
                    break;
                case JsonValueKind.Object:
                    Flatten(p.Value, key, into);
                    break;
                // Array / Null 不进标量表（数组由插件用 GetArray 遍历）
            }
        }
    }

    private void ValidateAgainst(TypeSchema sch, DataEntry e)
    {
        foreach (string req in sch.Required)
            if (!e.Scalars.ContainsKey(req) && !DataEntry.TryResolve(e.Root, req, out _))
                _warnings.Add($"{e.Source} {e.Id}：缺少必填字段 '{req}'");

        foreach (var (path, want) in sch.ScalarTypes)
        {
            if (!e.Scalars.TryGetValue(path, out var v)) continue;
            string got = v.Kind switch
            {
                ValueKind.Fix => "fix",
                ValueKind.Int => "int",
                ValueKind.Str => "str",
                ValueKind.Bool => "bool",
                _ => "none"
            };
            if (got != want && !(want == "fix" && got == "int")) // 整数写成 2 而不是 2.0 是允许的
                _warnings.Add($"{e.Source} {e.Id}：字段 '{path}' 期望 {want}，实为 {got}");
        }
    }

    private void ValidateRefs()
    {
        foreach (var e in _entries.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (!_schemas.TryGetValue(e.Type, out var sch)) continue;
            foreach (var (path, target) in sch.Refs)
            {
                if (!e.Scalars.TryGetValue(path, out var v)) continue;
                if (v.Kind != ValueKind.Str) { _warnings.Add($"{e.Source} {e.Id}：引用 '{path}' 不是字符串"); continue; }
                string s = v.AsString();
                if (!s.StartsWith(DefPrefix, StringComparison.Ordinal))
                {
                    _warnings.Add($"{e.Source} {e.Id}：字段 '{path}' 应写成 \"{DefPrefix}<id>\"，实为 \"{s}\"");
                    continue;
                }
                string id = s[DefPrefix.Length..];
                if (!_entries.TryGetValue(id, out var t))
                    _warnings.Add($"{e.Source} {e.Id}：引用 '{path}' → {s} 指向不存在的记录");
                else if (t.Type != target)
                    _warnings.Add($"{e.Source} {e.Id}：引用 '{path}' → {s} 类型应为 {target}，实为 {t.Type}");
            }
        }
    }

    private void BuildDataHash()
    {
        var mix = new SortedList<string, string>(StringComparer.Ordinal);
        foreach (var e in _entries.Values)
            mix[e.Id] = e.Scalars.Count == 0 ? "-" : string.Join(";", e.Scalars.Select(kv => kv.Key + "=" + kv.Value.Canonical));
        ulong h = StableHash.FnvOffset;
        foreach (var kv in mix)
        {
            h = StableHash.Mix(h, StableHash.Of(kv.Key));
            h = StableHash.Mix(h, StableHash.Of(kv.Value));
        }
        DataHash = StableHash.Hex(h);
    }

    /// <summary>运行时加包（DLC / 创意工坊，GDD §11.1 之外的 M11 能力）。这里只做入口预留。</summary>
    public DataRegistry Clone()
    {
        var copy = new DataRegistry();
        foreach (var e in _entries.Values) copy._entries[e.Id] = e;
        foreach (var (k, v) in _byType) copy._byType[k] = new List<DataEntry>(v);
        foreach (var (k, v) in _schemas) copy._schemas[k] = v;
        copy.DataHash = DataHash;
        copy.LoadedPacks = LoadedPacks;
        return copy;
    }
}

file static class JsonElementExtensions
{
    /// <summary>用原始文本判断，避免 double 解析后再判小数带来的跨平台差异。</summary>
    public static bool RawTextContainsDot(this JsonElement el) => el.GetRawText().Contains('.');
}

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimCore;

/// <summary>存档里的一条实体。</summary>
public sealed record SaveEntity(string Id, string Kind, IReadOnlyList<(string Path, string Value)> Fields);

/// <summary>存档里的一条命令（回放的最小输入）。</summary>
public sealed record SaveCommand(int Turn, int Seq, string Issuer, string Type, string Reason, IReadOnlyList<(string Key, string Value)> Args);

/// <summary>
/// 存档结构（GDD §10.6）：
/// <c>save = schemaVersion + gameVersion + worldSeed + snapshot + commandLog + pluginVersions + rngState</c>
///
/// 压缩算法用的是 .NET 自带的 GZip，不是文档写的 zstd：
/// §10.1 纪律「SimCore 只能依赖 System.*」与 zstd 需要第三方包冲突，
/// 已在 docs/CHANGELOG-DESIGN.md 记录该冲突并提请裁决。接口是流，换算法不动调用方。
/// </summary>
public sealed class SaveGame
{
    public int SchemaVersion { get; init; } = CoreInfo.SchemaVersion;
    public string GameVersion { get; init; } = CoreInfo.GameVersion;
    public int MapGenVersion { get; init; } = CoreInfo.MapGenVersion;
    public ulong WorldSeed { get; init; }
    public int Turn { get; init; }
    public int YearBase { get; init; } = 1;
    public IReadOnlyDictionary<string, string> PluginVersions { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<SaveEntity> Entities { get; init; } = Array.Empty<SaveEntity>();
    public IReadOnlyList<SaveCommand> Commands { get; init; } = Array.Empty<SaveCommand>();
    public string DataHash { get; init; } = "";
    /// <summary>RNG：全局流状态 + 命名空间流状态（排序写出）。</summary>
    public ulong RngGlobalState { get; init; }
    public IReadOnlyList<(string Ns, ulong State)> RngStreams { get; init; } = Array.Empty<(string, ulong)>();
    /// <summary>kind 计数器，读档后新建实体要接着编号。</summary>
    public IReadOnlyDictionary<string, int> KindCounters { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
    /// <summary>写档时的状态哈希，加载后重算比对：不一致就是档坏了或迁移器漏字段。</summary>
    public string SnapshotHash { get; init; } = "";
    public string SavedUtcText { get; init; } = "";
}

/// <summary>存档迁移器：把 <c>From</c> 版结构改写成 <c>To</c> 版。结构改动必须附迁移器 + 旧档回归（§11.3）。</summary>
public interface ISaveMigrator
{
    int From { get; }
    int To { get; }
    string PluginId { get; }
    /// <summary>就地修改文档节点；抛异常即视为迁移失败，禁止静默吞掉。</summary>
    void Migrate(System.Text.Json.Nodes.JsonNode root);
}

/// <summary>
/// 存档读写（GDD §10.2 职责 7）。
///
/// 安全约定：先写临时文件、校验能读回、再原子替换，并把上一份留成 .bak。
/// 「存档失败不得覆盖最后一个有效存档」是硬要求——AI 开发周期里回归档是主要证据来源，
/// 一次覆盖事故会同时毁掉标定基线和交接文档的依据。
/// </summary>
public static class SaveStore
{
    public const string Extension = ".hcsave"; // HexaCiv Save

    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>从当前 sim 状态构造存档对象。</summary>
    public static SaveGame Capture(Simulation sim)
    {
        var entities = new List<SaveEntity>();
        foreach (var ent in sim.World.All())
            entities.Add(new SaveEntity(ent.Id.Value, ent.Kind,
                ent.Fields.Select(kv => (kv.Key, kv.Value.Canonical)).ToList()));

        var commands = sim.Commands.Log
            .OrderBy(c => c.Turn).ThenBy(c => c.Seq).ThenBy(c => c.Issuer, StringComparer.Ordinal)
            .Select(c => new SaveCommand(c.Turn, c.Seq, c.Issuer, c.Type, c.Reason,
                c.Args.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => (a.Key, a.Value.Canonical)).ToList()))
            .ToList();

        var rng = sim.RngCapture();
        var plugins = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in sim.Plugins.Loaded) plugins[p.Id] = p.Manifest.Version;

        var hash = StateHash.Compute(sim.World, sim.Commands, sim.Rng);
        var captured = new SaveGame
        {
            SchemaVersion = CoreInfo.SchemaVersion,
            GameVersion = CoreInfo.GameVersion,
            MapGenVersion = CoreInfo.MapGenVersion,
            WorldSeed = sim.Rng.WorldSeed,
            Turn = sim.Time.Turn,
            YearBase = sim.Time.YearBase,
            PluginVersions = plugins,
            Entities = entities,
            Commands = commands,
            DataHash = sim.Data.DataHash,
            RngGlobalState = rng.GlobalState,
            RngStreams = rng.Streams.Select(s => (s.Namespace, s.State)).OrderBy(x => x.Namespace, StringComparer.Ordinal).ToList(),
            KindCounters = sim.WorldKindCounters(),
            SnapshotHash = hash.Total,
            SavedUtcText = "", // 由调用方填；sim 内禁止读时钟（§10.7）
        };
        return captured;
    }

    /// <summary>序列化为 JSON 文本（未压缩）。独立暴露给回归测试做逐字节比对。</summary>
    public static string ToJson(SaveGame save)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", save.SchemaVersion);
            w.WriteString("gameVersion", save.GameVersion);
            w.WriteNumber("mapGenVersion", save.MapGenVersion);
            w.WriteString("worldSeed", save.WorldSeed.ToString(CultureInfo));
            w.WriteNumber("turn", save.Turn);
            w.WriteNumber("yearBase", save.YearBase);
            w.WriteString("dataHash", save.DataHash);
            w.WriteString("snapshotHash", save.SnapshotHash);
            if (save.SavedUtcText.Length > 0) w.WriteString("savedUtc", save.SavedUtcText);

            w.WriteStartObject("pluginVersions");
            foreach (var kv in save.PluginVersions) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();

            w.WriteStartObject("kindCounters");
            foreach (var kv in save.KindCounters) w.WriteNumber(kv.Key, kv.Value);
            w.WriteEndObject();

            w.WriteStartObject("rng");
            w.WriteString("global", save.RngGlobalState.ToString(CultureInfo));
            w.WriteStartArray("streams");
            foreach (var (ns, st) in save.RngStreams)
            {
                w.WriteStartObject();
                w.WriteString("ns", ns);
                w.WriteString("state", st.ToString(CultureInfo));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("entities");
            foreach (var e in save.Entities)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("kind", e.Kind);
                w.WriteStartObject("fields");
                foreach (var (path, val) in e.Fields) w.WriteString(path, val);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("commands");
            foreach (var c in save.Commands)
            {
                w.WriteStartObject();
                w.WriteNumber("turn", c.Turn);
                w.WriteNumber("seq", c.Seq);
                w.WriteString("issuer", c.Issuer);
                w.WriteString("type", c.Type);
                w.WriteString("reason", c.Reason);
                w.WriteStartObject("args");
                foreach (var (k, v) in c.Args) w.WriteString(k, v);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static readonly System.Globalization.CultureInfo CultureInfo = System.Globalization.CultureInfo.InvariantCulture;
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>把存档写成文件：GZip 压缩 + 原子替换 + .bak 保留。</summary>
    public static void Write(string path, SaveGame save, string? note = null)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(dir);
        string json = ToJson(save);
        string tmp = path + ".tmp";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        // 必须是无 BOM 的 UTF-8：System.Text.Encoding.UTF8 会先吐 EF BB BF，
        // 而 JSON 解析器把 BOM 当成值的开头，于是刚写出来的档自己读不回来。
        using (var sw = new StreamWriter(gz, Utf8NoBom))
            sw.Write(json);

        // 临时文件必须能读回来，否则不动正式档
        try
        {
            var probe = ReadJson(tmp);
            if (probe.RootElement.TryGetProperty("schemaVersion", out var sv) && sv.GetInt32() != save.SchemaVersion)
                throw new SimSaveException("临时存档自检失败：schemaVersion 不匹配");
        }
        catch (Exception e)
        {
            File.Delete(tmp);
            throw new SimSaveException($"存档自检失败，保留上一个有效存档：{e.Message}");
        }

        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
            File.Delete(path);
        }
        File.Move(tmp, path);
        if (note is not null) File.WriteAllText(path + ".note.txt", note);
    }

    /// <summary>读档（自动处理 .gz 与明文，跑迁移链）。</summary>
    public static SaveGame Read(string path)
    {
        if (!File.Exists(path)) throw new SimSaveException($"存档不存在：{path}");
        byte[] json = ReadBytes(path);
        JsonNode node;
        try
        {
            node = JsonNode.Parse(json!) ?? throw new SimSaveException($"{path} 是空档");
        }
        catch (JsonException e)
        {
            throw new SimSaveException($"{Path.GetFileName(path)} 内容不是合法 JSON（{e.Message}）。若文件被截断，请改用同名 .bak。");
        }
        var migrated = MigratorChain.Run(node, path, out var owned);
        using (owned) return Parse(migrated);
    }

    /// <summary>解压后的 JSON 文本。</summary>
    internal static string ReadText(string path) => System.Text.Encoding.UTF8.GetString(ReadBytes(path));

    internal static byte[] ReadBytes(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        bool gzipped = raw.Length >= 2 && raw[0] == 0x1f && raw[1] == 0x8b;
        byte[] json;
        if (gzipped)
        {
            using var ms = new MemoryStream(raw);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            json = outMs.ToArray();
        }
        else json = raw;

        if (json.Length == 0) throw new SimSaveException($"{Path.GetFileName(path)} 是空文件");
        // 容忍带 BOM 的档（手写的测试夹具、Windows 编辑器另存的明文 JSON）：
        // 跳过那 3 个字节，否则解析器报「0xEF 不是合法的值的开头」。
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
            json = json.AsSpan(3).ToArray();
        return json;
    }

    internal static JsonDocument ReadJson(string path)
    {
        try
        {
            return JsonDocument.Parse(ReadBytes(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException e)
        {
            throw new SimSaveException($"{Path.GetFileName(path)} 内容不是合法 JSON（{e.Message}）。若文件被截断，请改用同名 .bak。");
        }
    }

    /// <summary>JSON → SaveGame。</summary>
    internal static SaveGame Parse(JsonElement root)
    {
        int schema = root.GetProperty("schemaVersion").GetInt32();
        if (schema > CoreInfo.SchemaVersion)
            throw new SimSaveException($"存档 schemaVersion={schema} 比本程序（{CoreInfo.SchemaVersion}）更新，请用新版打开");

        var plugins = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("pluginVersions", out var pv))
            foreach (var p in pv.EnumerateObject()) plugins[p.Name] = p.Value.GetString() ?? "";

        var counters = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("kindCounters", out var kc))
            foreach (var p in kc.EnumerateObject()) counters[p.Name] = p.Value.GetInt32();

        ulong seed = root.TryGetProperty("worldSeed", out var ws) ? ulong.Parse(ws.GetString()!) : 0UL;

        var ents = new List<SaveEntity>();
        if (root.TryGetProperty("entities", out var es))
            foreach (var e in es.EnumerateArray())
            {
                var fields = new List<(string, string)>();
                if (e.TryGetProperty("fields", out var f))
                    foreach (var p in f.EnumerateObject()) fields.Add((p.Name, p.Value.GetString() ?? "n"));
                ents.Add(new SaveEntity(e.GetProperty("id").GetString()!, e.GetProperty("kind").GetString()!, fields));
            }

        var cmds = new List<SaveCommand>();
        if (root.TryGetProperty("commands", out var cs))
            foreach (var c in cs.EnumerateArray())
            {
                var args = new List<(string, string)>();
                if (c.TryGetProperty("args", out var a))
                    foreach (var p in a.EnumerateObject()) args.Add((p.Name, p.Value.GetString() ?? "n"));
                cmds.Add(new SaveCommand(
                    c.GetProperty("turn").GetInt32(),
                    c.GetProperty("seq").GetInt32(),
                    c.GetProperty("issuer").GetString() ?? "host",
                    c.GetProperty("type").GetString() ?? "",
                    c.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    args));
            }

        ulong rngGlobal = 0;
        var rngStreams = new List<(string, ulong)>();
        if (root.TryGetProperty("rng", out var rng))
        {
            if (rng.TryGetProperty("global", out var g)) rngGlobal = ulong.Parse(g.GetString()!);
            if (rng.TryGetProperty("streams", out var st))
                foreach (var s in st.EnumerateArray())
                    rngStreams.Add((s.GetProperty("ns").GetString()!, ulong.Parse(s.GetProperty("state").GetString()!)));
        }

        return new SaveGame
        {
            SchemaVersion = schema,
            GameVersion = root.TryGetProperty("gameVersion", out var gv) ? gv.GetString() ?? "" : "",
            MapGenVersion = root.TryGetProperty("mapGenVersion", out var mg) ? mg.GetInt32() : 0,
            WorldSeed = seed,
            Turn = root.GetProperty("turn").GetInt32(),
            YearBase = root.TryGetProperty("yearBase", out var yb) ? yb.GetInt32() : 1,
            PluginVersions = plugins,
            Entities = ents,
            Commands = cmds,
            DataHash = root.TryGetProperty("dataHash", out var dh) ? dh.GetString() ?? "" : "",
            RngGlobalState = rngGlobal,
            RngStreams = rngStreams,
            KindCounters = counters,
            SnapshotHash = root.TryGetProperty("snapshotHash", out var sh) ? sh.GetString() ?? "" : "",
            SavedUtcText = root.TryGetProperty("savedUtc", out var su) ? su.GetString() ?? "" : "",
        };
    }
}

/// <summary>
/// 迁移链：把旧 schemaVersion 一档一档推到当前版本。
/// 找不到路径就报错而不是「大概能读」——静默的旧档兼容是后期最难查的 bug。
/// </summary>
public static class MigratorChain
{
    private static readonly List<ISaveMigrator> Migrators = new();

    public static void Register(ISaveMigrator m)
    {
        if (Migrators.Any(x => x.From == m.From && x.PluginId == m.PluginId))
            throw new SimPluginException(m.PluginId, $"迁移器重复注册（from={m.From}）");
        Migrators.Add(m);
        Migrators.Sort(static (a, b) => a.From != b.From ? a.From.CompareTo(b.From) : string.CompareOrdinal(a.PluginId, b.PluginId));
    }

    public static IReadOnlyList<ISaveMigrator> Registered => Migrators;
    public static void Clear() => Migrators.Clear();

    /// <summary>逐版本推进；链为空时原样返回。返回可解析的 JsonElement（调用方负责释放 doc）。</summary>
    public static JsonElement Run(JsonNode node, string pathForError, out JsonDocument owned)
    {
        int schema = node["schemaVersion"]?.GetValue<int>() ?? 0;
        int guard = 0;
        while (schema < CoreInfo.SchemaVersion)
        {
            var step = Migrators.FirstOrDefault(m => m.From == schema);
            if (step is null)
                throw new SimSaveException($"{Path.GetFileName(pathForError)} 是 schema {schema}，缺少 {schema}→{schema + 1} 的迁移器（GDD §11.3：无迁移器不许合入）");
            step.Migrate(node);
            schema = step.To;
            node["schemaVersion"] = JsonValue.Create(schema);
            if (++guard > 64) throw new SimSaveException("迁移链疑似成环");
        }
        owned = JsonDocument.Parse(node.ToJsonString(), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return owned.RootElement;
    }
}

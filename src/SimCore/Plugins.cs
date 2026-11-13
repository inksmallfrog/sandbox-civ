using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimCore;

/// <summary>Core 对外接口版本（GDD §11.4：Core 接口变更必须走 CORE_API.md 版本号）。</summary>
public static class CoreInfo
{
    /// <summary>插件 manifest 的 <c>api</c> 字段必须与此同主版本，否则拒载。</summary>
    public const string ApiVersion = "1.0";
    public const int ApiMajor = 1;
    public const int ApiMinor = 0;

    /// <summary>存档结构版本。每次数据结构改动 +1，并必须附迁移器（§10.6）。</summary>
    public const int SchemaVersion = 1;

    /// <summary>游戏版本，进存档头部用于诊断。</summary>
    public const string GameVersion = "0.1.0";

    /// <summary>地图生成器版本（§3.7「生成器版本号写入存档」）。改生成管线必须 +1。</summary>
    public const int MapGenVersion = 3;

    public static string Describe => $"Core API {ApiVersion} · schema {SchemaVersion} · game {GameVersion} · mapgen {MapGenVersion}";
}

/// <summary>
/// 插件清单（GDD §10.5 的 manifest.json）。
/// JSON 键名按 GDD 的 snake_case 写死在 <see cref="JsonPropertyNameAttribute"/> 上，
/// C# 属性名保持 PascalCase——两边都不用猜，改名也不会悄悄破掉别人的插件包。
/// </summary>
public sealed class PluginManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";
    /// <summary>依赖的 Core API 主版本串，例 "1.0"。</summary>
    [JsonPropertyName("api")]
    public string Api { get; set; } = "";
    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = new();
    /// <summary>同层依赖时的排序键，小的先加载。</summary>
    [JsonPropertyName("load_priority")]
    public int LoadPriority { get; set; } = 100;
    /// <summary>入口 DLL（相对插件目录）；省略则取 <c>&lt;id&gt;.dll</c>。</summary>
    [JsonPropertyName("dll")]
    public string Dll { get; set; } = "";
    [JsonPropertyName("enabled_by_default")]
    public bool EnabledByDefault { get; set; } = true;
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
    /// <summary>随插件一起加载的数据包目录名（相对 data/）。空表示无数据。</summary>
    [JsonPropertyName("data_packs")]
    public List<string> DataPacks { get; set; } = new();

    public string EffectiveDll => string.IsNullOrEmpty(Dll) ? Id + ".dll" : Dll;

    /// <summary>manifest 认得的键（以 <c>@</c> 开头的除外，那是给人读的注释字段）。</summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "id", "version", "api", "dependencies", "load_priority", "dll",
        "enabled_by_default", "title", "summary", "data_packs"
    };

    public static PluginManifest? TryRead(string path)
    {
        // 为什么要有 UnknownKeys 这一关：deserialize 对认不出的键是「跳过」而不是「报错」，
        // 于是 "loadPriority"（驼峰）会被静默丢掉、priority 落回默认 100，插件照样加载——
        // 一个永远改不动的优先级、一个怎么也带不上的数据包，查起来极其费劲。拼错必须当场炸。
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            throw new SimPluginException(DirName(path), $"manifest.json 读不到：{e.Message}");
        }

        var opt = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = false
        };

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(text, opt);
        }
        catch (Exception e)
        {
            throw new SimPluginException(DirName(path), $"manifest.json 解析失败：{e.Message}");
        }
        if (manifest is null)
            throw new SimPluginException(DirName(path), "manifest.json 是空的");

        var bad = UnknownKeys(text);
        if (bad.Count > 0)
            throw new SimPluginException(manifest.Id.Length > 0 ? manifest.Id : DirName(path),
                "manifest.json 有不认识的键：" + string.Join(", ", bad) +
                $"（合法键：{string.Join(", ", KnownKeys)}；注释用 @ 开头的键或 // 行注释）");

        return manifest;
    }

    private static string DirName(string path) =>
        Path.GetDirectoryName(path) is { } d ? Path.GetFileName(d) : "?";

    /// <summary>根对象里不在 <see cref="KnownKeys"/> 且不以 <c>@</c> 开头的键。JSON 本身坏掉时返回空（让反序列化去报）。</summary>
    private static List<string> UnknownKeys(string json)
    {
        var bad = new List<string>();
        try
        {
            var docOpt = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using var doc = JsonDocument.Parse(json, docOpt);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return bad;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Length == 0 || (p.Name[0] != '@' && !KnownKeys.Contains(p.Name)))
                    bad.Add(p.Name);
        }
        catch (JsonException)
        {
            // 语法本身就不对：上面反序列化那关已经报过（或马上要报），这里不重复报
        }
        return bad;
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

/// <summary>命令执行器：第 1 步按命令类型分派。返回是否受理与实际执行率。</summary>
public delegate CommandResult CommandExecutor(Command cmd, IStepContext ctx);

/// <summary>世界初始化器：新开局时的建图、建国、建城（按插件顺序执行，输入只有种子与数据）。</summary>
public delegate void BootstrapInitializer(IStepContext ctx);

/// <summary>
/// 交给插件的注册面（GDD §10.4：插件之间禁止直接调用，只能经 hook）。
/// 插件能做的事在这里穷尽：注册 hook、命令执行器、开局初始化、不变量、结算只读步骤声明。
/// 想加新的能力必须走 <c>docs/CORE_API.md</c> 升版本，而不是绕过这个接口。
/// </summary>
public interface IPluginApi
{
    string PluginId { get; }
    PluginManifest Manifest { get; }
    DataRegistry Data { get; }

    /// <summary>注册 hook 处理器（全局级：一个 hook 点跑一次）。priority 0–1000，小的先跑；同 priority 按插件 id 定序。</summary>
    void Require(HookPoint point, int priority, HookHandler handler);

    /// <summary>
    /// 注册主题级处理器（§10.4 的 <c>OnProduction(town, ctx)</c>）：Core 按 id 升序，
    /// 对该 kind 的每个实体各跑一次。「哪些实体是主题」由调用 <see cref="IStepContext.DispatchSubjects"/>
    /// 的扇出点决定，所以 Core 仍然不认识城镇。
    /// </summary>
    void RequireFor(HookPoint point, string entityKind, int priority, SubjectHandler handler);

    /// <summary>注册命令执行器（<c>town.found</c> 之类）。同类型重复注册 = 报错。</summary>
    void RegisterCommand(string type, CommandExecutor executor, string argDocs = "");

    /// <summary>注册开局初始化器。执行顺序 = 插件加载顺序，输入只有种子与数据。</summary>
    void RegisterBootstrap(BootstrapInitializer init);

    /// <summary>注册不变量（§5.9）。steps 为空表示每步末都检查。</summary>
    void RegisterInvariant(string name, IReadOnlyList<int> steps, InvariantCheck check, bool fatal = true);

    /// <summary>注册图层声明（§3.9）。图层是纯查询，Core 只转述标题与顺序，不解释内容。</summary>
    void RegisterLayer(string id, string title, int order = 100);

    /// <summary>加载期日志，进启动日志与「插件」面板。</summary>
    void Log(string message);

    IReadOnlyList<LayerInfo> Layers { get; }
}

/// <summary>图层声明（§3.9 图层是纯查询，不改状态，所以只需要元数据）。</summary>
public sealed record LayerInfo(string Id, string Title, string Owner, int Order);

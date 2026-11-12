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
    public const int MapGenVersion = 1;

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

    public static PluginManifest? TryRead(string path)
    {
        try
        {
            var opt = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = false
            };
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), opt);
        }
        catch (Exception e)
        {
            throw new SimPluginException(
                Path.GetDirectoryName(path) is { } d ? Path.GetFileName(d) : "?",
                $"manifest.json 解析失败：{e.Message}");
        }
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

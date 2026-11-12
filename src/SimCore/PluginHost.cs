using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace SimCore;

/// <summary>
/// 插件入口（GDD §10.3 / ADR-012）。
/// 一个插件程序集至少有一个非抽象实现；<see cref="OnLoad"/> 只做注册，不碰世界状态。
/// </summary>
public interface IPlugin
{
    /// <summary>必须与 manifest.id 一致，Core 会校验。</summary>
    string Id { get; }
    /// <summary>注册期。只能调 <see cref="IPluginApi"/> 的 Register* / Require，不得读写 World。</summary>
    void OnLoad(IPluginApi api);
    /// <summary>可选：卸载（M11 热加载才用得上）。默认空实现。</summary>
    void OnUninstall() { }
}

/// <summary>已加载插件的运行时记录。</summary>
public sealed record LoadedPlugin(PluginManifest Manifest, IPlugin Instance, Assembly Assembly, string Directory)
{
    public string Id => Manifest.Id;
    public string Title => string.IsNullOrEmpty(Manifest.Title) ? Id : Manifest.Title;
}

/// <summary>在磁盘上发现的一个插件候选（目录 + manifest）。</summary>
public sealed record DiscoveredPlugin(PluginManifest Manifest, string Directory);

/// <summary>
/// 插件宿主（GDD §10.2 职责 8）。
///
/// 加载检查顺序（§10.5，任一失败拒绝加载并写日志，<b>不允许半加载</b>）：
/// 文件存在 → 版本兼容 → 依赖齐全 → 无循环依赖 → 数据 id 无重复 → 系统注册成功。
///
/// 发现方式：在插件根目录下每个子目录读一份 <c>manifest.json</c>；目录名必须等于 <c>id</c>。
/// 程序集解析优先复用<b>宿主上下文</b>里已加载的同名程序集（内置 <c>core.basic</c> 走这条路），
/// 其次把插件 dll 装进 <see cref="PluginLoadContext"/>（外部插件 / 创意工坊走这条路）——
/// 两条路都必须让插件绑定宿主那一份 SimCore，否则 <see cref="IPlugin"/> 会变成两个不同的 Type。
/// </summary>
public sealed class PluginHost
{
    /// <summary>宿主（SimCore 自己）所在的加载上下文：CLI 下是默认上下文，Godot 4 下是脚本上下文。</summary>
    internal static readonly AssemblyLoadContext HostContext =
        AssemblyLoadContext.GetLoadContext(typeof(PluginHost).Assembly) ?? AssemblyLoadContext.Default;

    private readonly List<LoadedPlugin> _loaded = new();
    private readonly Dictionary<string, LoadedPlugin> _byId = new(StringComparer.Ordinal);
    private readonly List<string> _log = new();

    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;
    public IReadOnlyList<string> LoadLog => _log;

    /// <summary>禁用的插件 id。关掉 core.basic 即可验证「Core 无玩法也能启动」（§0.2 判定 5）。</summary>
    public ISet<string> Disabled { get; } = new SortedSet<string>(StringComparer.Ordinal);

    public LoadedPlugin? Find(string id) => _byId.TryGetValue(id, out var p) ? p : null;
    public IReadOnlyList<string> Ids() => _loaded.Select(p => p.Id).ToList();

    /// <summary>扫目录读 manifest，做完整校验，返回拓扑序。不实例化，因此可单独用于「预检」命令。</summary>
    public IReadOnlyList<DiscoveredPlugin> Plan(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
            throw new SimPluginException("*", $"插件目录不存在:{pluginsRoot}");

        var found = new List<DiscoveredPlugin>();
        foreach (string dir in Directory.EnumerateDirectories(pluginsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            string mfPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(mfPath))
            {
                _log.Add($"跳过 {Path.GetFileName(dir)}：没有 manifest.json");
                continue;
            }
            var m = PluginManifest.TryRead(mfPath) ?? throw new SimPluginException(Path.GetFileName(dir), "manifest.json 内容为空");
            if (string.IsNullOrEmpty(m.Id)) m.Id = Path.GetFileName(dir);
            if (m.Id != Path.GetFileName(dir))
                throw new SimPluginException(m.Id, $"manifest.id 与目录名不一致（目录 {Path.GetFileName(dir)}）");
            found.Add(new DiscoveredPlugin(m, dir));
        }

        var available = new Dictionary<string, DiscoveredPlugin>(StringComparer.Ordinal);
        foreach (var d in found)
        {
            if (Disabled.Contains(d.Manifest.Id)) { _log.Add($"已禁用 {d.Manifest.Id}"); continue; }
            if (available.ContainsKey(d.Manifest.Id)) throw new SimPluginException(d.Manifest.Id, "插件 id 重复");
            available[d.Manifest.Id] = d;
        }

        // 版本兼容（§10.5「api 版本检查」）
        foreach (var d in available.Values)
        {
            var m = d.Manifest;
            if (string.IsNullOrEmpty(m.Api)) throw new SimPluginException(m.Id, "manifest 缺少 api 字段");
            int major = m.Api.Contains('.') ? int.Parse(m.Api.Split('.')[0]) : int.Parse(m.Api);
            if (major != CoreInfo.ApiMajor)
                throw new SimPluginException(m.Id, $"要求 Core API {m.Api}，本核是 {CoreInfo.ApiVersion}");
        }

        return TopoSort(available);
    }

    private static List<DiscoveredPlugin> TopoSort(Dictionary<string, DiscoveredPlugin> available)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<DiscoveredPlugin>();

        void Visit(DiscoveredPlugin d, List<string> path)
        {
            if (visited.Contains(d.Manifest.Id)) return;
            if (visiting.Contains(d.Manifest.Id))
                throw new SimPluginException(d.Manifest.Id, $"循环依赖：{string.Join(" → ", path.Append(d.Manifest.Id))}");
            visiting.Add(d.Manifest.Id);
            path.Add(d.Manifest.Id);
            foreach (string dep in d.Manifest.Dependencies.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!available.TryGetValue(dep, out var dd))
                    throw new SimPluginException(d.Manifest.Id, $"依赖缺失:{dep}（未安装或被禁用）");
                Visit(dd, path);
            }
            path.RemoveAt(path.Count - 1);
            visiting.Remove(d.Manifest.Id);
            visited.Add(d.Manifest.Id);
            order.Add(d);
        }

        foreach (var d in available.Values.OrderBy(x => x.Manifest.LoadPriority).ThenBy(x => x.Manifest.Id, StringComparer.Ordinal))
            Visit(d, new List<string>());

        return order;
    }

    /// <summary>
    /// 从磁盘目录加载。失败时已注册的内容由 <paramref name="registrar"/> 回滚（不允许半加载）。
    /// </summary>
    public void LoadFromDirectory(string pluginsRoot, IPluginRegistrar registrar)
    {
        foreach (var d in Plan(pluginsRoot))
            InstantiateAndLoad(d.Manifest, d.Directory, registrar);
    }

    /// <summary>
    /// 从已知程序集内置加载（无头测试、以及把 core.basic 编进解决方案的默认路径）。
    /// manifest 就地合成，因此目录检查与磁盘加载走同一套排序与校验。
    /// </summary>
    public void LoadFromAssemblies(IEnumerable<Assembly> assemblies, IPluginRegistrar registrar)
    {
        var available = new Dictionary<string, DiscoveredPlugin>(StringComparer.Ordinal);
        foreach (var asm in assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            foreach (var t in DiscoverTypes(asm))
            {
                IPlugin probe;
                try { probe = (IPlugin)Activator.CreateInstance(t)!; }
                catch (Exception e) { throw new SimPluginException(asm.GetName().Name ?? "?", $"探测 {t.Name} 失败：{e.Message}"); }
                if (available.ContainsKey(probe.Id)) throw new SimPluginException(probe.Id, "插件 id 在两个程序集里都出现");
                var manifest = new PluginManifest
                {
                    Id = probe.Id,
                    Version = asm.GetName().Version?.ToString(3) ?? "0.0.0",
                    Api = CoreInfo.ApiVersion,
                    LoadPriority = probe.Id.StartsWith("core.", StringComparison.Ordinal) ? 10 : 100,
                };
                available[manifest.Id] = new DiscoveredPlugin(manifest, AppContext.BaseDirectory);
            }
        }
        foreach (var d in TopoSort(available))
            InstantiateAndLoad(d.Manifest, d.Directory, registrar, assemblies.ToList());
    }

    private void InstantiateAndLoad(PluginManifest manifest, string dir, IPluginRegistrar registrar,
        IReadOnlyList<Assembly>? knownAssemblies = null)
    {
        var asm = ResolveAssembly(manifest, knownAssemblies, dir);

        Type? type = FindPluginType(asm, manifest.Id, out string why);
        if (type is null)
            throw new SimPluginException(manifest.Id, why);

        IPlugin instance;
        try { instance = (IPlugin)Activator.CreateInstance(type)!; }
        catch (Exception e) { throw new SimPluginException(manifest.Id, $"构造失败：{e.GetType().Name} {e.Message}"); }

        var api = registrar.BeginPlugin(manifest);
        try
        {
            instance.OnLoad(api);
        }
        catch (Exception e)
        {
            registrar.AbandonPlugin(manifest.Id);
            throw new SimPluginException(manifest.Id, $"OnLoad 抛出 {e.GetType().Name}: {e.Message}");
        }
        registrar.EndPlugin();

        var rec = new LoadedPlugin(manifest, instance, asm, dir);
        _loaded.Add(rec);
        _byId[manifest.Id] = rec;
        _log.Add($"已加载 {manifest.Id} v{manifest.Version}（api {manifest.Api}，priority {manifest.LoadPriority}）");
    }

    private static IEnumerable<Type> DiscoverTypes(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
        return types
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IPlugin).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
    }

    /// <summary>
    /// 在程序集里找 Id == <paramref name="pluginId"/> 的 <see cref="IPlugin"/> 实现；找不到时把「为什么」写进
    /// <paramref name="why"/>。§10.5 要求拒载必须能解释，所以这里一行都不吞：
    /// 类型装载失败、构造抛异常、实现了另一份同名 IPlugin、Id 不匹配——四种情况各说各话。
    /// （原来这里是个 <c>catch { return false; }</c>，真凶「插件里的 IPlugin 不是宿主那一份」
    /// 只会显示成「没有 IPlugin 实现」，害得调试从猜开始。）
    /// </summary>
    private static Type? FindPluginType(Assembly asm, string pluginId, out string why)
    {
        var notes = new List<string>();
        why = "";
        Type[] all;
        try
        {
            all = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            all = e.Types.Where(t => t is not null).ToArray()!;
            foreach (string m in e.LoaderExceptions
                         .Where(x => x is not null).Select(x => x!.Message).Distinct(StringComparer.Ordinal))
                notes.Add($"类型装载失败：{m}");
        }

        Type? expected = typeof(IPlugin);
        foreach (var t in all)
        {
            if (!t.IsClass || t.IsAbstract) continue;
            Type? iface;
            try { iface = t.GetInterfaces().FirstOrDefault(i => i.FullName == expected.FullName); }
            catch (Exception e) { notes.Add($"{t.FullName} 的接口读不出来：{e.GetType().Name} {e.Message}"); continue; }
            if (iface is null) continue;

            // 名字对得上但不是同一个 Type ⇒ 进程里有两份 SimCore，插件绑的是另一份。
            if (iface != expected)
            {
                notes.Add($"{t.FullName} 实现的是另一份 IPlugin（{Where(iface.Assembly)}），宿主这一份是 {Where(expected.Assembly)}");
                continue;
            }

            string id;
            try { id = ((IPlugin)Activator.CreateInstance(t)!).Id; }
            catch (Exception e) { notes.Add($"{t.FullName} 构造失败：{e.GetType().Name} {e.Message}"); continue; }
            if (id == pluginId) return t;
            notes.Add($"{t.FullName} 的 Id 是 {id}，不是 {pluginId}");
        }

        why = $"程序集 {asm.GetName().Name} 里没有 Id={pluginId} 的 IPlugin 实现"
            + $"（{Where(asm)}，可见类型 {all.Length} 个）";
        if (notes.Count > 0) why += "；原因：" + string.Join("；", notes);
        return null;
    }

    /// <summary>程序集是谁、从哪来的——拒载信息里没有定位线索就等于没说。</summary>
    private static string Where(Assembly a)
    {
        var n = a.GetName();
        string src = string.IsNullOrEmpty(a.Location) ? "内存/单文件发布" : a.Location;
        return $"{n.Name} v{n.Version} ← {src}";
    }

    private static Assembly ResolveAssembly(PluginManifest manifest, IReadOnlyList<Assembly>? known, string dir)
    {
        // 1) 显式提供的程序集（内置插件 / 测试）
        if (known is not null)
        {
            var hit = known.FirstOrDefault(a => string.Equals(a.GetName().Name, manifest.Id, StringComparison.Ordinal));
            if (hit is not null) return hit;
        }
        // 2) 当前进程已加载的同名程序集：CLI 直接引用了 core.basic，走这条路。
        //    只要名字不要上下文会造成「同一份代码的两个拷贝」，所以优先宿主上下文里的那一个。
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => string.Equals(a.GetName().Name, manifest.Id, StringComparison.Ordinal))
            .FirstOrDefault(a => (AssemblyLoadContext.GetLoadContext(a) ?? AssemblyLoadContext.Default) == HostContext)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, manifest.Id, StringComparison.Ordinal));
        if (loaded is not null) return loaded;
        // 3) 插件目录里的 dll
        string dllName = manifest.EffectiveDll;
        if (TryLoadPlugin(dir, dllName, out Assembly? fromDir)) return fromDir!;
        // 4) 应用基目录
        if (TryLoadPlugin(AppContext.BaseDirectory, dllName, out Assembly? fromBase)) return fromBase!;
        throw new SimPluginException(manifest.Id, $"找不到程序集 {dllName}（已加载 / 插件目录 {dir} / 基目录都没有）");
    }

    private static bool TryLoadPlugin(string dir, string dllName, out Assembly? asm)
    {
        string path = Path.GetFullPath(Path.Combine(dir, dllName));
        if (!File.Exists(path)) { asm = null; return false; }
        asm = PluginLoadContext.For(path).LoadFromAssemblyPath(path);
        return asm is not null;
    }

    /// <summary>
    /// 插件专用加载上下文（§10.3 外部插件 / 创意工坊的地基）。
    ///
    /// 为什么不用 <see cref="Assembly.LoadFrom(string)"/>：它把程序集塞进<b>默认</b>上下文，而宿主不一定在
    /// 默认上下文里（Godot 4 的 C# 脚本跑在自己的 AssemblyLoadContext 中）。结果是插件实现的 IPlugin
    /// 属于「第二份 SimCore」，宿主 cast 不过去。CLI 碰巧没事，因为它的宿主就在默认上下文——
    /// 「这台机器能跑」从来不是加载策略的证明。
    ///
    /// 规则两条：依赖先在<b>宿主上下文</b>按简单名找（SimCore 等共享类型必须只有一份），
    /// 找不到才按插件自己的 .deps.json 与同目录解析。所以插件目录里就算多塞一份 SimCore.dll 也不会被装载，
    /// 这与 core.basic.csproj 里 <c>Private="false"</c> 的注释是同一件事的两半。
    /// </summary>
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly ConcurrentDictionary<string, PluginLoadContext> ByPath = new(StringComparer.OrdinalIgnoreCase);

        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _directory;

        private PluginLoadContext(string dllPath) : base(isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(dllPath);
            _directory = Path.GetDirectoryName(dllPath)!;
        }

        /// <summary>同一个 dll 复用同一个上下文，避免同名程序集在一个进程里被装载两遍。</summary>
        public static PluginLoadContext For(string dllPath) =>
            ByPath.GetOrAdd(dllPath, static p => new PluginLoadContext(p));

        protected override Assembly? Load(AssemblyName name)
        {
            foreach (var a in HostContext.Assemblies)
                if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) return a;

            string? path = _resolver.ResolveAssemblyToPath(name);
            if (path is null)
            {
                string candidate = Path.Combine(_directory, (name.Name ?? "") + ".dll");
                if (File.Exists(candidate)) path = candidate;
            }
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    public IReadOnlyList<string> DescribeLoaded() =>
        _loaded.Select(p => $"{p.Id} {p.Manifest.Version} ← {p.Assembly.GetName().Name}").ToList();
}

/// <summary>由 <see cref="Simulation"/> 实现：给插件一个受限的注册面，并在失败时回滚。</summary>
public interface IPluginRegistrar
{
    IPluginApi BeginPlugin(PluginManifest manifest);
    void EndPlugin();
    void AbandonPlugin(string pluginId);
}

namespace SimCore;

/// <summary>
/// 插件注册面（GDD §10.2 职责 8 的另一半：<see cref="PluginHost"/> 负责发现，这里负责接收））。
///
/// 注册期规则：
/// - 一个插件 OnLoad 期间，注册都记在它名下；失败可整体回滚（不允许半加载）；
/// - 注册期禁止读写 World——OnLoad 只声明「我会在什么时候做什么」，不做任何事实。
/// </summary>
public sealed partial class Simulation : IPluginRegistrar
{
    IPluginApi IPluginRegistrar.BeginPlugin(PluginManifest manifest)
    {
        LoadingPluginId = manifest.Id;
        return new PluginApi(this, manifest);
    }

    void IPluginRegistrar.EndPlugin()
    {
        LoadingPluginId = null;
        // 注册期结束，World 不该被碰过：这条断言把「OnLoad 只注册」变成可检查的事实
        if (CurrentJournal.Diffs.Count != 0)
            throw new SimPluginException("?", "OnLoad 阶段产生了状态变更；注册期只允许注册");
    }

    void IPluginRegistrar.AbandonPlugin(string pluginId)
    {
        Hooks.UnregisterAll(pluginId);
        Bootstraps.RemoveAll(b => b.Plugin == pluginId);
        LayerList.RemoveAll(l => l.Owner == pluginId);
        foreach (var type in CommandDocs.Where(kv => kv.Value == pluginId).Select(kv => kv.Key).ToList())
        {
            CommandDocs.Remove(type);
            Executors.Remove(type);
        }
        foreach (var rule in Invariants.Rules.Where(r => r.Owner == pluginId).Select(r => r.Name).ToList())
            Invariants.Remove(rule);
        LoadingPluginId = null;
        StartupLogLines.Add($"已回滚插件 {pluginId} 的全部注册");
    }

    /// <summary>插件可见的全部能力。想加新能力 = 改这个接口 = 必须升 CORE_API 版本号。</summary>
    private sealed class PluginApi : IPluginApi
    {
        private readonly Simulation _sim;
        private readonly PluginManifest _manifest;
        private readonly List<LayerInfo> _layers = new();

        public PluginApi(Simulation sim, PluginManifest manifest)
        {
            _sim = sim;
            _manifest = manifest;
        }

        public string PluginId => _manifest.Id;
        public PluginManifest Manifest => _manifest;
        public DataRegistry Data => _sim.Data;
        public IReadOnlyList<LayerInfo> Layers => _layers;

        public void Require(HookPoint point, int priority, HookHandler handler)
        {
            _sim.Hooks.Register(point, PluginId, priority, handler);
            _sim.StartupLogLines.Add($"  {PluginId} 挂 {point}（全局，priority {priority}）");
        }

        public void RegisterCommand(string type, CommandExecutor executor, string argDocs = "")
        {
            if (string.IsNullOrEmpty(type)) throw new SimPluginException(PluginId, "命令类型为空");
            if (_sim.Executors.ContainsKey(type))
                throw new SimPluginException(PluginId, $"命令 {type} 已由 {_sim.CommandDocs[type]} 注册");
            _sim.Executors[type] = executor;
            _sim.CommandDocs[type] = PluginId;
            _sim.StartupLogLines.Add($"  {PluginId} 受理命令 {type}{(argDocs.Length > 0 ? "（" + argDocs + "）" : "")}");
        }

        public void RegisterBootstrap(BootstrapInitializer init)
        {
            _sim.Bootstraps.Add((PluginId, _sim.Bootstraps.Count, init));
        }

        public void RegisterInvariant(string name, IReadOnlyList<int> steps, InvariantCheck check, bool fatal = true)
        {
            _sim.Invariants.Register(name, PluginId, steps, check, fatal);
            _sim.StartupLogLines.Add($"  {PluginId} 声明不变量 {name}（步 {(steps.Count == 0 ? "全部" : string.Join(",", steps))}）");
        }

        public void RegisterLayer(string id, string title, int order = 100)
        {
            if (_sim.LayerList.Any(l => l.Id == id))
                throw new SimPluginException(PluginId, $"图层 {id} 已存在");
            var info = new LayerInfo(id, title, PluginId, order);
            _sim.LayerList.Add(info);
            _layers.Add(info);
        }

        public void Log(string message) => _sim.StartupLogLines.Add($"  {PluginId}：{message}");
    }
}

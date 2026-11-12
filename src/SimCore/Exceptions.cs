namespace SimCore;

/// <summary>Core 抛出的所有异常基类。UI/壳层捕获这一类即可，不必逐个 catch。</summary>
public abstract class SimException : Exception
{
    protected SimException(string message) : base(message) { }
}

/// <summary>数据包 / schema / id 冲突等：属于「数据写错了」，开发期应尽早失败。</summary>
public sealed class SimDataException : SimException
{
    public SimDataException(string message) : base(message) { }
}

/// <summary>状态访问越界、字段类型不符、实体不存在：属于「代码写错了」。</summary>
public sealed class SimStateException : SimException
{
    public SimStateException(string message) : base(message) { }
}

/// <summary>插件加载失败（GDD §10.5：任一检查不过就拒绝加载并写日志，不允许半加载）。</summary>
public sealed class SimPluginException : SimException
{
    public string PluginId { get; }
    public SimPluginException(string pluginId, string message) : base($"插件 {pluginId} 加载失败：{message}")
        => PluginId = pluginId;
}

/// <summary>不变量被破坏（GDD §5.9）：不是静默扩散，而是立刻报错。</summary>
public sealed class SimInvariantException : SimException
{
    public IReadOnlyList<string> Violations { get; }
    public SimInvariantException(IReadOnlyList<string> violations)
        : base("不变量违反 " + violations.Count + " 条：\n  - " + string.Join("\n  - ", violations))
        => Violations = violations;
}

/// <summary>存档版本 / 迁移问题（GDD §10.6）。</summary>
public sealed class SimSaveException : SimException
{
    public SimSaveException(string message) : base(message) { }
}

/// <summary>命令非法（身份、回合、资源、目标、规则）——联机侧主机校验用（§9.2）。</summary>
public sealed class SimCommandRejectException : SimException
{
    public SimCommandRejectException(string message) : base(message) { }
}

namespace SimCore;

/// <summary>同步模式（GDD §9.1）。三种模式一套模拟，差别只在命令从哪来。</summary>
public enum SyncMode
{
    /// <summary>单人：本机提交，本机结算。</summary>
    Solo = 0,
    /// <summary>本地热座：多人轮流在本机提交，统一结算。</summary>
    Hotseat = 1,
    /// <summary>在线：房主权威，客户端只提交命令与渲染。</summary>
    Online = 2,
}

/// <summary>
/// 传输层抽象（GDD §10.2 职责 9）。
///
/// 只搬三样东西：命令、回合号、状态哈希 —— 不同步画面对象（§9.2）。
/// sim 代码只认这个接口，所以 M10 加联机时不用改模拟（§14 风险「联机返工」的预案）。
/// </summary>
public interface ITransport
{
    SyncMode Mode { get; }
    /// <summary>客户端 → 主机提交命令（本地模式直接进队列）。</summary>
    void Submit(Command command);
    /// <summary>主机侧取出一条待处理命令；没有则 false。</summary>
    bool TryDequeueReceived(out Command command);
    /// <summary>回合末广播分块状态哈希（§9.3 校验）。</summary>
    void BroadcastHash(int turn, StateHash hash);
    /// <summary>诊断文本，进日志与「同步」面板。</summary>
    IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>
/// 本地环回：单机与热座用。命令在进程内转一圈再进总线，
/// 路径与联机完全一致，因此「单机跑通」这件事对联机也有意义。
/// </summary>
public sealed class LoopbackTransport : ITransport
{
    private readonly Queue<Command> _queue = new();
    private readonly List<string> _diag = new();
    public SyncMode Mode { get; }
    public IReadOnlyList<string> Diagnostics => _diag;
    /// <summary>最近一次广播的哈希，供本地校验（回放一致性测试直接读它）。</summary>
    public StateHash? LastHash { get; private set; }

    public LoopbackTransport(SyncMode mode = SyncMode.Solo)
    {
        Mode = mode;
        _diag.Add($"环回传输就绪（{(mode == SyncMode.Hotseat ? "热座" : "单人")}）");
    }

    public void Submit(Command command) => _queue.Enqueue(command);

    public bool TryDequeueReceived(out Command command) => _queue.TryDequeue(out command!);

    public void BroadcastHash(int turn, StateHash hash)
    {
        LastHash = hash;
        _diag.Add($"T{turn:000} 哈希 {hash.Total}（{hash.Chunks.Count} 分块 / {hash.FieldCount} 字段）");
    }
}

/// <summary>
/// 命令流录制器：把「谁在什么时候提交了什么」原样存下来。
/// 回放定位（§9.3「客户端上传命令流 → 服务端回放定位出问题的回合」）与
/// 确定性测试都用它，联机时它就是网络上的唯一真相。
/// </summary>
public sealed class CommandStream
{
    private readonly List<Command> _all = new();
    public IReadOnlyList<Command> All => _all;

    public void Record(Command c) => _all.Add(c);
    public IReadOnlyList<Command> OfTurn(int turn) => _all.Where(c => c.Turn == turn).ToList();
    public string Text() => string.Join("\n", _all.Select(c => c.Text));
    public string Hash()
    {
        ulong h = StableHash.FnvOffset;
        foreach (var c in _all) h = StableHash.Mix(h, StableHash.Of(c.Canonical()));
        return StableHash.Hex(h);
    }
}

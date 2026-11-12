namespace SimCore;

/// <summary>
/// 一条命令 = 玩家 / AI / 官员在「意图段」提交的一次意志（GDD §2.2、§9.2、§10.2 职责 5）。
///
/// 为什么要分「命令」和「变更」两层：
/// - 命令是外部输入，是玩家能负责的东西，也是回放的唯一变量；
/// - 变更（<see cref="Diff"/>）是结算的中间结果，由命令 + 世界状态 + 种子推导出来。
/// 「同种子 + 同命令流 ⇒ 位级相同」（§8.3 规则 4）成立的前提，就是回放只需要命令。
/// </summary>
public sealed class Command
{
    /// <summary>提交时所属回合（意图段属于第 Turn 回合，实际在第 Turn 回合的 1 步应用）。</summary>
    public int Turn { get; set; }
    /// <summary>同回合内的提交序号，用于打破平序（联机时序不确定时靠它定序）。</summary>
    public int Seq { get; set; }
    /// <summary>提交者：nation.03 / hero.0042 / "host" / "system"。</summary>
    public string Issuer { get; init; } = "host";
    /// <summary>命令类型，点分小写，例 <c>town.found</c>、<c>policy.adopt</c>、<c>hero.recruit</c>。</summary>
    public string Type { get; init; } = "";
    /// <summary>参数：一律扁平标量，键排序后参与规范化（不允许依赖字典顺序）。</summary>
    public IReadOnlyDictionary<string, SimValue> Args { get; init; } = new SortedDictionary<string, SimValue>(StringComparer.Ordinal);
    /// <summary>玩家给这条命令的理由 / 来源，进因果链。<c>auto</c> 表示由 AI 托管提交。</summary>
    public string Reason { get; init; } = "";
    /// <summary>联机：这条命令在主机侧被接受时的回合号；-1 表示尚未被接受。</summary>
    public int AcceptedTurn { get; set; } = -1;

    public SimValue Arg(string key) => Args.TryGetValue(key, out var v) ? v : SimValue.Null;
    public string ArgStr(string key, string fallback = "") => Arg(key).Kind switch { ValueKind.None => fallback, _ => Arg(key).AsString() };
    public Fix ArgFix(string key, Fix fallback = default) => Arg(key).Kind switch { ValueKind.None => fallback, _ => Arg(key).AsFix() } ;
    public long ArgInt(string key, long fallback = 0) => Arg(key).Kind switch { ValueKind.None => fallback, _ => Arg(key).AsInt() };

    /// <summary>规范化字符串：哈希、存档、命令日志共用同一份序列化结果。</summary>
    public string Canonical()
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append(Turn).Append('|').Append(Seq).Append('|').Append(Issuer).Append('|').Append(Type);
        foreach (var kv in Args.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value.Canonical);
        return sb.ToString();
    }

    public string Text
    {
        get
        {
            var args = string.Join(" ", Args.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value.Display}"));
            return $"T{Turn:000}.{Seq} {Issuer} → {Type}{(args.Length == 0 ? "" : " " + args)}";
        }
    }

    public override string ToString() => Text;

    public static Command Build(int turn, int seq, string issuer, string type, string reason = "", params (string Key, SimValue Value)[] args)
    {
        var dict = new SortedDictionary<string, SimValue>(StringComparer.Ordinal);
        foreach (var a in args) dict[a.Key] = a.Value;
        return new Command { Turn = turn, Seq = seq, Issuer = issuer, Type = type, Reason = reason, Args = dict };
    }
}

/// <summary>
/// 命令受理结果。<see cref="ExecutionRate"/> 是「上令不一定下达」（§7.5）的落点。
///
/// 两个文本各有用途：<see cref="CommandText"/> 是人读的（年报的「政令执行」栏），
/// <see cref="CanonicalText"/> 是机器读的（回放对账、联机校验时报「哪一条命令被驳了」）。
/// 早先只留 Canonical，年报里就出现了 <c>name=s魏</c> 这种带类型前缀的字——那是哈希格式，不该给人看。
/// </summary>
public sealed record CommandResult(
    bool Accepted,
    string CommandText,
    string CanonicalText,
    string Reason,
    Fix ExecutionRate,
    IReadOnlyList<string> ExplainTerms)
{
    public static CommandResult Accept(Command c, string why, Fix rate, IReadOnlyList<string>? terms = null) =>
        new(true, c.Text, c.Canonical(), why, rate, terms ?? Array.Empty<string>());
    public static CommandResult Reject(Command c, string why) =>
        new(false, c.Text, c.Canonical(), why, Fix.Zero, Array.Empty<string>());
}

/// <summary>
/// 命令总线（GDD §10.2 职责 5）。
/// 校验顺序：格式 → 身份 → 回合 → 规则（OnActionValidate hook）→ 资源与 AP。
/// 任何一层不过都拒绝并留原因，主机侧同样走这条链（§9.2 客户端不能指定金钱/人口/战斗结果）。
/// </summary>
public sealed class CommandBus
{
    private readonly List<Command> _pending = new();
    private readonly List<Command> _log = new();
    private readonly Dictionary<string, int> _seqByIssuerTurn = new(StringComparer.Ordinal);
    /// <summary>
    /// 命令日志的滚动哈希。初值必须是 <see cref="StableHash.FnvOffset"/>，和
    /// <c>Restore</c> 里的重置值一致——否则「一口气跑到第 N 回合」和「读第 N 回合的档」
    /// 算出的 LogHash 天生就差一个初值，§10.6 的等价性自检永远过不了。
    /// </summary>
    private ulong _logHash = StableHash.FnvOffset;

    public IReadOnlyList<Command> Pending => _pending;
    /// <summary>完整命令日志：回放的输入，存档的一部分（§10.6）。</summary>
    public IReadOnlyList<Command> Log => _log;
    public int Turn { get; set; }
    /// <summary>严格模式：拒绝重复与越序提交（联机主机应开）。</summary>
    public bool StrictSeq { get; init; } = true;

    public string LogHash => StableHash.Hex(_logHash);

    /// <summary>入队（意图段）。不校验业务合法性——那要在结算第 1 步按当时状态判定。</summary>
    public Command Submit(string issuer, string type, string reason = "", params (string Key, SimValue Value)[] args)
    {
        int seq = NextSeq(issuer);
        var cmd = Command.Build(Turn, seq, issuer, type, reason, args);
        _pending.Add(cmd);
        return cmd;
    }

    public Command Submit(Command cmd)
    {
        cmd.Seq = NextSeq(cmd.Issuer);
        cmd.Turn = Turn;
        _pending.Add(cmd);
        return cmd;
    }

    private int NextSeq(string issuer)
    {
        string key = $"{issuer}@{Turn}";
        _seqByIssuerTurn.TryGetValue(key, out int n);
        _seqByIssuerTurn[key] = ++n;
        return n;
    }

    /// <summary>
    /// 排空意图段队列，按 (Turn, Seq, Issuer, Type) 全序入日志。
    /// 联机时各玩家的提交到达顺序不确定，所以定序靠这个全序而不是到达序（§8.3 规则 2）。
    /// </summary>
    public IReadOnlyList<Command> DrainPending()
    {
        var ordered = _pending
            .OrderBy(c => c.Turn).ThenBy(c => c.Seq)
            .ThenBy(c => c.Issuer, StringComparer.Ordinal)
            .ThenBy(c => c.Type, StringComparer.Ordinal)
            .ToList();
        _pending.Clear();
        foreach (var c in ordered)
        {
            c.AcceptedTurn = Turn;
            _log.Add(c);
            _logHash = StableHash.Mix(_logHash, StableHash.Of(c.Canonical()));
        }
        return ordered;
    }

    /// <summary>回放到指定日志长度后需要的命令序列（存档恢复 / 联机追赶）。</summary>
    internal IReadOnlyList<Command> LogSnapshot() => _log.ToList();

    internal void Restore(int turn, IReadOnlyList<Command> log, IReadOnlyList<Command> pending)
    {
        _log.Clear();
        _logHash = StableHash.FnvOffset;
        foreach (var c in log)
        {
            _log.Add(c);
            _logHash = StableHash.Mix(_logHash, StableHash.Of(c.Canonical()));
            string key = $"{c.Issuer}@{c.Turn}";
            if (!_seqByIssuerTurn.TryGetValue(key, out int n) || c.Seq > n) _seqByIssuerTurn[key] = c.Seq;
        }
        _pending.Clear();
        foreach (var c in pending) _pending.Add(c);
        Turn = turn;
    }
}

using SimCore;

namespace HexaCiv.Cli;

/// <summary>脚本里的一行命令。<see cref="Turn"/> 是「第几回合的意图段提交」，从 1 开始。</summary>
public sealed record ScriptLine(int Turn, string Type, string Issuer, string Reason, IReadOnlyList<(string Key, string Value)> Args);

/// <summary>
/// 命令脚本（GDD §10.8：标定与回归都要「可复现的输入」，而输入只有种子 + 命令流）。
///
/// 格式，一行一条，<c>#</c> 到行尾是注释：
/// <code>
///   回合  命令类型  key=value  key=value  …   as=nation.0001  reason=为什么下这道令
/// </code>
/// 例：
/// <code>
///   1 nation.found  name=魏
///   2 town.found    hex=auto nation=auto name=初城
///   3 building.build town=auto building=def:building.farm hex=auto
/// </code>
///
/// <c>hex=auto</c> / <c>town=auto</c> 由 core.basic 的 <c>Siting</c> 解释（规则在插件里，不在这里，§10.1），
/// 于是同一份脚本换种子照样能跑——这是 <c>sim hash</c> 与 <c>sim replay</c> 成立的前提。
///
/// 约束：以空白分隔，所以<b>值里不能有空格</b>（<c>reason=起粮仓压损耗</c> 可以，
/// <c>reason=人口到 4 个百人单位</c> 会被拆成三个参数并报错）。真需要空格留到 M2 加引号解析。
/// </summary>
public static class Script
{
    /// <summary>脚本默认路径；不存在时退到内置的演示序列，保证 <c>sim run</c> 开箱就有东西可看。</summary>
    public static string PathFor(Options o) => o.Script;

    public static List<ScriptLine> Load(string path)
    {
        string? resolved = FirstExisting(path);
        if (resolved is null) return Builtin;

        var list = new List<ScriptLine>();
        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(resolved))
        {
            lineNo++;
            string line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            var tok = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tok.Length < 2) throw new CommandLineException($"{resolved}:{lineNo} 至少要有「回合 命令类型」");
            if (!int.TryParse(tok[0], out int turn) || turn < 1)
                throw new CommandLineException($"{resolved}:{lineNo} 回合号要是不小于 1 的整数，写的是 '{tok[0]}'");

            string issuer = "host", reason = "脚本";
            var args = new List<(string, string)>();
            for (int i = 2; i < tok.Length; i++)
            {
                int eq = tok[i].IndexOf('=');
                if (eq <= 0) throw new CommandLineException($"{resolved}:{lineNo} 参数 '{tok[i]}' 不是 key=value");
                string k = tok[i][..eq], v = tok[i][(eq + 1)..];
                switch (k)
                {
                    case "as": issuer = v; break;
                    case "reason": reason = v; break;
                    default: args.Add((k, v)); break;
                }
            }
            list.Add(new ScriptLine(turn, tok[1], issuer, reason, args));
        }
        return list;
    }

    /// <summary>提交属于本回合的命令。返回提交条数（0 很正常——多数年份玩家什么都不做）。</summary>
    public static int SubmitFor(Simulation sim, List<ScriptLine> lines, int turn)
    {
        int n = 0;
        foreach (var l in lines.OrderBy(x => x.Turn).ThenBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal))
        {
            if (l.Turn != turn) continue;
            sim.SubmitTarget(l.Issuer, l.Type, l.Reason, l.Args.ToArray());
            n++;
        }
        return n;
    }

    private static string? FirstExisting(string configured)
    {
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 9 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, configured);
            if (File.Exists(candidate)) return candidate;
        }
        string local = Path.Combine(Directory.GetCurrentDirectory(), configured);
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// 内置演示序列：建国 → 选址开城 → 连年盖田 → 攒够人口盖粮仓。
    /// 只当脚本文件缺失时的兜底，内容全部走命令面，跟真脚本没有任何区别。
    /// </summary>
    private static readonly List<ScriptLine> Builtin =
    [
        new(1, "nation.found", "host", "内置演示", [("name", "魏")]),
        new(2, "town.found", "host", "内置演示", [("hex", "auto"), ("nation", "auto"), ("name", "初城")]),
        new(3, "building.build", "host", "内置演示", [("town", "auto"), ("building", "def:building.farm"), ("hex", "auto")]),
        new(5, "building.build", "host", "内置演示", [("town", "auto"), ("building", "def:building.farm"), ("hex", "auto")]),
        new(7, "building.build", "host", "内置演示", [("town", "auto"), ("building", "def:building.granary")]),
        new(9, "town.found", "host", "内置演示", [("hex", "auto"), ("nation", "auto")]),
    ];
}

using System.Globalization;
using System.Text;
using SimCore;

namespace HexaCiv.Cli;

/// <summary>
/// 命令行入口。整个壳层只做三件事：解析参数、把脚本变成命令、把 <see cref="TurnReport"/> 打成人读的东西。
///
/// 这里<b>不允许出现任何规则</b>（GDD §10.1：壳层不含规则）。如果你在 CLI 里写出了
/// 「粮不够就减人口」这类判断，那它属于 core.basic 或 econ.*，请搬回去。
/// </summary>
public static class Program
{
    private const string UsageText = """
        群邦纪元 HexaCiv · 无头模拟器 sim（M1）

        用法：sim <动词> [选项]

        动词
          run      跑一局，输出年度报告（默认）
          check    只校验数据包与插件清单，不开局
          hash     同种子跑两遍比哈希：确定性自检（§10.8）
          map      打印地图（ASCII），验证 §3.7 的生成结果
          peek     打印某实体（或某 kind 全部实体）的字段：sim peek town.0000 town.growth.peasant
          replay   自带存盘的等价性自检：一口气跑满 N 回合 vs 第 K 回合存盘续跑（§10.6）
          help     本帮助

        选项
          --seed <n>          世界种子，默认 1
          --turns <n>         回合数（1 回合 = 1 年），默认 12
          --at <n>            replay 在第几回合存盘，默认回合数的一半
          --headless          只打机器可读的输出（配合 --out 做标定）
          --out <path.csv>    每回合写一行 CSV
          --save <path>       结束时写存档（replay：中途快照路径，默认 saves/replay.bin）
          --load <path>       从存档继续
          --script <path>     命令脚本，默认 scripts/demo.txt
          --data <dir>        数据包目录，默认 data
          --plugins <dir>     插件目录，默认 build/plugins
          --no-plugins        禁用全部插件：验证「Core 无玩法也能跑」（§0.2 判定 5）
          --no-data           不加载数据包
          --steps             打印 15 步的分步耗时
          --explain           打印每条变更的因果链（§8.4）
          --lenient           不变量只记录不抛异常，用于抢救坏档
          --quiet             不打印年报

        例子
          sim run --seed 7 --turns 30 --steps
          sim run --seed 42 --turns 200 --headless --out analysis/out/demo.csv
          sim hash --seed 99 --turns 50
          sim map --seed 3
          sim replay --seed 5 --turns 20 --at 12
          sim run --seed 5 --turns 8 --save saves/demo.bin && sim run --load saves/demo.bin --turns 4
        """;

    public static int Main(string[] argv)
    {
        var o = Options.Parse(argv);
        if (o.Verb is "help" or "" && argv.Length == 0) { Console.WriteLine(UsageText); return 0; }

        try
        {
            return o.Verb switch
            {
                "run" => Runner.Run(o),
                "check" => Runner.Check(o),
                "hash" => Runner.HashCheck(o),
                "map" => Runner.PrintMap(o),
                "peek" => Runner.Peek(o, o.PeekTarget, o.PeekField),
                "replay" => Runner.Replay(o),
                _ => Unknown(o.Verb),
            };
        }
        catch (CommandLineException e)
        {
            // 参数与脚本写错：给一行原因 + 一行用法提示，不甩堆栈
            Console.Error.WriteLine($"✗ {e.Message}");
            Console.Error.WriteLine("  （选项看 sim help；脚本格式看 scripts/demo.txt 的开头注释，值里不能有空格）");
            return 64;
        }
        catch (SimException e)
        {
            // 模拟自己的异常都是「说人话」的：直接把 Message 打出来，不甩堆栈吓玩家
            Console.Error.WriteLine($"✗ {e.GetType().Name["Sim".Length..]}：{e.Message}");
            if (e is SimInvariantException inv)
                foreach (var line in inv.Violations) Console.Error.WriteLine($"    · {line}");
            Console.Error.WriteLine("    （要完整堆栈请设环境变量 SIM_STACK=1）");
            if (Environment.GetEnvironmentVariable("SIM_STACK") == "1") Console.Error.WriteLine(e);
            return 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"✗ 非预期异常 {e.GetType().Name}: {e.Message}");
            Console.Error.WriteLine(e);
            return 3;
        }
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"未知动词 '{verb}'。\n\n{UsageText}");
        return 64;
    }
}

/// <summary>解析出来的命令行选项。全部是壳层参数，没有一个进得到规则（§10.1）。</summary>
public sealed class Options
{
    public string Verb = "run";
    public ulong Seed = 1;
    public int Turns = 12;
    /// <summary><c>replay</c> 的存盘回合；负数 = 自动取回合数的一半。</summary>
    public int At = -1;
    public bool Headless, Steps, Explain, Quiet, Lenient, NoPlugins, NoData;
    public string? Out, Save, Load;
    public string Script = "scripts/demo.txt";
    public string DataDir = "data";
    public string PluginDir = "build/plugins";
    /// <summary>位置参数：peek 用 <c>sim peek &lt;实体id&gt; [字段]</c>。</summary>
    public readonly List<string> Extra = new();
    public string PeekTarget => Extra.Count > 0 ? Extra[0] : "*";
    public string? PeekField => Extra.Count > 1 ? Extra[1] : null;

    public static Options Parse(string[] argv)
    {
        var o = new Options();
        int i = 0;
        if (argv.Length > 0 && !argv[0].StartsWith("-", StringComparison.Ordinal)) { o.Verb = argv[0].ToLowerInvariant(); i = 1; }

        string Next(string flag)
        {
            if (++i >= argv.Length) throw new CommandLineException($"{flag} 后面要跟一个值");
            return argv[i];
        }
        long Num(string flag) =>
            long.TryParse(Next(flag), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
                ? v : throw new CommandLineException($"{flag} 要的是整数");

        for (; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--seed": o.Seed = unchecked((ulong)Num(argv[i])); break;
                case "--turns": o.Turns = (int)Num(argv[i]); break;
                case "--at": o.At = (int)Num(argv[i]); break;
                case "--out": o.Out = Next(argv[i]); break;
                case "--save": o.Save = Next(argv[i]); break;
                case "--load": o.Load = Next(argv[i]); break;
                case "--script": o.Script = Next(argv[i]); break;
                case "--data": o.DataDir = Next(argv[i]); break;
                case "--plugins": o.PluginDir = Next(argv[i]); break;
                case "--headless": o.Headless = true; break;
                case "--steps": o.Steps = true; break;
                case "--explain": o.Explain = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--lenient": o.Lenient = true; break;
                case "--no-plugins": o.NoPlugins = true; break;
                case "--no-data": o.NoData = true; break;
                case "--help": case "-h": o.Verb = "help"; break;
                default:
                    // 不以 - 开头的算位置参数（peek 的实体 id / 字段名）；其余才是写错的选项
                    if (argv[i].StartsWith("-", StringComparison.Ordinal))
                        throw new CommandLineException($"不认识的选项 '{argv[i]}'（sim help 看全部）");
                    o.Extra.Add(argv[i]);
                    break;
            }
        }
        if (o.Turns < 0) throw new CommandLineException("--turns 不能为负");
        if (o.At < -1) throw new CommandLineException("--at 要 ≥ 0（省略则由 replay 自动取回合数的一半）");
        return o;
    }

    public SimConfig ToConfig() => new()
    {
        Seed = Seed,
        DataDir = DataDir,
        PluginDir = PluginDir,
        FatalInvariants = !Lenient,
        NoPlugins = NoPlugins,
        StrictData = true,
    };
}

/// <summary>参数错误：不是模拟故障，单独一个异常类型好让 Main 给出「用法提示」而不是错误码 2。</summary>
public sealed class CommandLineException(string message) : Exception(message);

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SimCore;

namespace HexaCiv.GodotShell;

/// <summary>
/// 窗口壳层的主循环（GDD §10.1：壳层不含规则）。
///
/// 这里只做四件事：
///   1. 装配 SimConfig 并 <c>Bootstrap()</c>；
///   2. 把世界状态读成画面（<see cref="HexView"/>）和文字（国势栏 / 选中详情 / 年报）；
///   3. 把点击翻译成命令 <c>SubmitTarget(...)</c>——<b>包括翻译成被驳回的那一类</b>：
///      「这块地能不能建城」由 core.basic 回答，壳层只负责把它的理由显示出来；
///   4. 回合推进与存读档。
///
/// 想在按钮回调里写「粮不够就减人口」？那行字属于 core.basic，请搬回去（§10.1 纪律）。
/// </summary>
public partial class Main : Node2D
{
    private const string PlayerId = "host";
    private const string DefaultSave = "saves/godot-shell.bin";

    private ulong _seed = 7;
    private int _layerIndex;
    private Simulation _sim = null!;
    private TurnReport? _last;
    private HexView _view = null!;

    private string _selectedHex = "";
    private string _selectedTown = "";
    private string _lastRuling = "点一格：看它是什么；按「建城」：把命令交出去，成不成都会说理由。";

    private Label _clock = null!;
    private Label _hint = null!;
    private RichTextLabel _state = null!;
    private RichTextLabel _log = null!;
    private LineEdit _nationName = null!;

    public override void _Ready()
    {
        _view = new HexView { Name = "HexView" };
        AddChild(_view);
        _view.HexClicked += OnHexClicked;
        _view.HexHovered += OnHexHovered;

        BuildHud();

        if (OS.GetCmdlineUserArgs().Contains("--smoke")) { Smoke(); return; }
        if (OS.GetCmdlineUserArgs().Contains("--shot")) { _ = Shot(); return; }

        StartGame(_seed, banner: true);
    }

    /// <summary>
    /// 截一帧真窗口画面（README 与验收用的那张图）。
    /// <c>godot --path src/GodotShell -- --shot docs/screenshots/godot-shell.png</c>
    /// 必须在<b>有显示</b>的窗口模式跑：--headless 不渲染，截出来是空的。
    /// </summary>
    private async System.Threading.Tasks.Task Shot()
    {
        string outPath = "docs/screenshots/godot-shell.png";
        string[] args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--shot");
        if (i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--")) outPath = args[i + 1];

        StartGame(_seed, banner: true);
        if (_sim is null) { GD.PrintErr("SHOT FAIL：起局就没成功"); GetTree().Quit(); return; }

        // 让画面里有东西可看：建国 → 开城 → 两年农事 → 选中城址格
        Send("nation.found", ("name", "魏"));
        Send("town.found", ("hex", "auto"), ("nation", "auto"));
        EndTurn();
        Send("building.build", ("town", "auto"), ("building", "def:building.farm"), ("hex", "auto"));
        EndTurn();
        Send("building.build", ("town", "auto"), ("building", "def:building.farm"), ("hex", "auto"));
        EndTurn();
        var site = _sim.World.OfKind("hex").FirstOrDefault(h => h.GetStr("hex.town").Length > 0);
        if (site is not null) { _selectedHex = site.Id.Value; _selectedTown = site.GetStr("hex.town"); _view.Selected = site.Id; }
        EndTurn();
        Refresh();

        // 等三帧：_Draw 与 HUD 排版都落定之后再取纹理
        for (int f = 0; f < 3; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(RepoRoot(), outPath));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        Image img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(full);
        GD.Print(err == Error.Ok ? $"SHOT OK {full}（{img.GetSize().X}×{img.GetSize().Y}）" : $"SHOT FAIL {err}");
        GetTree().Quit();
    }

    // ================= 起局 / 回合 =================

    /// <summary>开一局新的。数据与插件目录按仓库根解析，与 CLI 同一套路径约定。</summary>
    private void StartGame(ulong seed, bool banner)
    {
        var (dataDir, pluginDir, _) = RepoPaths();
        if (banner) AppendLog($"[color=#8fbf5a]群邦纪元 HexaCiv · 窗口壳层[/color] seed={seed} · 数据 {dataDir} · 插件 {pluginDir}");
        if (!Directory.Exists(pluginDir))
            AppendLog($"[color=#e0a060]! 插件目录不存在：{pluginDir}[/color]\n  先在仓库根跑一次 [b]dotnet build[/b]，否则这里只能跑「空世界」（§0.2 判定 5）。");

        _seed = seed;
        try
        {
            _sim = new Simulation(new SimConfig
            {
                Seed = seed,
                DataDir = dataDir,
                PluginDir = pluginDir,
                FatalInvariants = true,
                StrictData = true,
            }).Bootstrap();
            foreach (string line in _sim.StartupLog) AppendLog("  " + line);
            foreach (string w in _sim.DataWarnings) AppendLog($"[color=#e0a060]  ! {w}[/color]");
        }
        catch (SimException e)
        {
            AppendLog($"[color=#e07070]✗ {e.Message}[/color]");
            GD.PrintErr($"壳层起局失败：{e}");                  // headless 跑没有画面，日志必须落到 stdout
            _sim = null!;
            return;
        }
        catch (Exception e)
        {
            AppendLog($"[color=#e07070]✗ 非预期异常 {e.GetType().Name}：{e.Message}[/color]");
            GD.PrintErr($"壳层起局失败：{e}");
            _sim = null!;
            return;
        }

        _last = null;
        _selectedHex = "";
        _selectedTown = "";
        _layerIndex = 0;
        _view.Layer = _sim.Layers.Count > 0 ? _sim.Layers[0].Id : "terrain";
        _view.Bind(_sim);
        Refresh();
    }

    private void EndTurn()
    {
        if (_sim is null) return;
        try
        {
            _last = _sim.AdvanceTurn();
        }
        catch (SimException e)
        {
            AppendLog($"[color=#e07070]✗ 结算中断：{e.Message}[/color]");
            return;
        }
        AppendLog(_last.AnnualReport());
        Refresh();
    }

    /// <summary>
    /// 无头冒烟：给 CI 与「编辑器打不开窗口」的机器一条验证路径。
    /// <c>godot --headless --path src/GodotShell -- --smoke</c>
    /// </summary>
    private void Smoke()
    {
        StartGame(7, banner: false);
        if (_sim is null) { GD.PrintErr("SMOKE FAIL：起局就没成功"); GetTree().Quit(); return; }

        var found = _sim.SubmitTarget(PlayerId, "nation.found", "冒烟", ("name", "魏"));
        var town = _sim.SubmitTarget(PlayerId, "town.found", "冒烟", ("hex", "auto"), ("nation", "auto"));
        GD.Print($"smoke 提交：{found.Type} / {town.Type}（意图段受理，裁决在结算）");

        for (int turn = 1; turn <= 6; turn++)
        {
            var r = _sim.AdvanceTurn();
            // 国势栏的键就是 report.* 字段路径本身（Simulation 只收前缀，不改名）
            string Get(string k) => r.Headline.GetValueOrDefault("report." + k, "—");
            string rulings = string.Join(" | ", r.Commands.Select(c => $"{(c.Accepted ? "✓" : "✗")} {c.CommandText}：{c.Reason}"));
            GD.Print($"smoke 第 {r.Turn} 回合 hash={r.Hash.Total} 城={Get("towns")} 人口={Get("pop")} " +
                     $"粮出={Get("food.out")} 仓={Get("food.stock")} 最低满意={Get("minSat")} 不安={Get("unrest")}");
            if (rulings.Length > 0) GD.Print($"      裁决：{rulings}");
        }

        int towns = _sim.World.OfKind("town").Count;
        int nations = _sim.World.OfKind("nation").Count;
        // 字体探测：headless 里 ThemeDB 的兜底字体常常是空的，壳层据此决定要不要走 DrawString 那条路
        GD.Print($"smoke 字体：FallbackFont={(ThemeDB.FallbackFont is null ? "null" : ThemeDB.FallbackFont.GetType().Name)} " +
                 $"FallbackFontSize={ThemeDB.FallbackFontSize} DisplayServer={DisplayServer.GetName()}");

        // 点击命中自检：把每格中心换算成屏幕坐标再拾取回来，必须落在同一格。
        // 「点一格建城」是壳层唯一的交互，Pick 的取整错了就等于按钮全废，而窗口里看不出来。
        int bad = 0, total = 0;
        foreach (var hex in _sim.World.OfKind("hex"))
        {
            int q = (int)hex.GetInt("hex.q"), r = (int)hex.GetInt("hex.r");
            // 偏移写在「格内局部坐标」里（按 HexSize 取比例），这样地图缩放到 32×20 后仍然落在同一格
            foreach (var off in new[]
                     {
                         Vector2.Zero,
                         new Vector2(0.24f, -0.20f) * HexView.HexSize,
                         new Vector2(-0.28f, 0.15f) * HexView.HexSize,
                     })
            {
                total++;
                if (_view.Pick(_view.ToGlobal(HexView.Pixel(q, r) + off)) != hex.Id) bad++;
            }
        }
        GD.Print($"smoke 拾取：{total - bad}/{total} 命中" + (bad == 0 ? "" : $"（{bad} 处偏格）"));

        int shapeTiles = _sim.World.OfKind("hex").Count(h => h.GetStr("hex.shape").Length > 0);
        int resourceTiles = _sim.World.OfKind("hex").Count(h => h.GetStr("hex.resource").Length > 0);
        GD.Print($"smoke 图层：{string.Join(",", _sim.Layers.Select(l => l.Id))} / " +
                 $"形胜 {shapeTiles} 格 / 资源 {resourceTiles} 格（§3.4 / §3.5）");
        GD.Print($"smoke 终局：{nations} 国 / {towns} 城 / 地块 {_sim.World.OfKind("hex").Count}");
        bool ok = towns > 0 && nations > 0 && bad == 0;
        // 失败要落到 stderr：窗口模式下 stdout 可能被吞，CI 只看得到退出码与错误流
        if (ok) GD.Print("SMOKE PASS");
        else GD.PrintErr("SMOKE FAIL：跑了一年既没国也没城（插件没接上），或拾取偏格");
        GetTree().Quit();
    }

    // ================= 输入 =================

    private void OnHexClicked(string hexId)
    {
        if (_sim is null) return;
        _selectedHex = hexId;
        var hex = _sim.World.Get(new EntityId(hexId));
        _selectedTown = hex is null || hex.GetStr("hex.town").Length == 0
            ? FindOwnerTown(hexId)
            : hex.GetStr("hex.town");
        Refresh();
    }

    private void OnHexHovered(string hexId)
    {
        if (_sim is null || hexId.Length == 0) return;
        var hex = _sim.World.Get(new EntityId(hexId));
        if (hex is null) { _hint.Text = ""; return; }
        string land = _sim.Data.TryGet(hex.GetStr("hex.landform"), out var lf) ? lf.GetStr("title") : "?";
        string waterId = hex.GetStr("hex.water");
        string water = waterId.Length > 0 && _sim.Data.TryGet(waterId, out var wb) ? $"（{wb.GetStr("title")}）" : "";
        string river = hex.GetStr("hex.river.down").Length > 0
            ? $" · 河 {hex.GetInt("hex.river.flow", 0)} 格集水 / 宽 {hex.GetInt("hex.river.width", 0)} 档"
            : "";
        string shape = DefTitle(hex.GetStr("hex.shape"), " · 形胜 ");
        string resource = DefTitle(hex.GetStr("hex.resource"), " · 资源 ");
        _hint.Text = $"{hexId} {land}{water}{shape}{resource} · 高 {hex.GetInt("hex.height", 0)} · 温 {hex.GetInt("hex.temp", 0)} · 降水 {hex.GetInt("hex.rain", 0)} · 粮潜力 {hex.GetFix("hex.yield.food", Fix.Zero).Text}{river}";
    }

    /// <summary>把字段里的数据 id 读成标题；空串返回空，壳层不猜规则。</summary>
    private string DefTitle(string defId, string prefix) =>
        defId.Length > 0 && _sim.Data.TryGet(defId, out var def) ? prefix + def.GetStr("title") : "";

    /// <summary>辖地格属于哪座城（只读字段，不判规则）。</summary>
    private string FindOwnerTown(string hexId) =>
        _sim.World.GetStr(new EntityId(hexId), "hex.owner", "");

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;
        switch (k.Keycode)
        {
            case Key.Space: EndTurn(); break;
            case Key.B: SubmitFound(); break;
            case Key.F: SubmitBuild("building.farm"); break;
            case Key.G: SubmitBuild("building.granary"); break;
            case Key.N: SubmitFoundNation(); break;
            case Key.T: CycleLayer(); break;
            case Key.R: StartGame(_seed + 1, banner: true); break;
            case Key.S: DoSave(); break;
            case Key.L: DoLoad(); break;
            case Key.Escape: _selectedHex = ""; _selectedTown = ""; Refresh(); break;
            default: return;
        }
    }

    // ---- 命令：只有提交，没有裁决 ----

    /// <summary>§3.9 图层切换：只换壳层配色，世界状态一个字段都不改。</summary>
    private void CycleLayer()
    {
        if (_sim is null || _sim.Layers.Count == 0) return;
        _layerIndex = (_layerIndex + 1) % _sim.Layers.Count;
        _view.Layer = _sim.Layers[_layerIndex].Id;
        _lastRuling = $"图层：{_sim.Layers[_layerIndex].Title}（{_view.Layer}，§3.9 纯查询）";
        _view.QueueRedraw();
        Refresh();
    }

    private void SubmitFoundNation()
    {
        Send("nation.found", ("name", _nationName.Text.Length > 0 ? _nationName.Text : "无名邦"));
    }

    private void SubmitFound()
    {
        if (_selectedHex.Length == 0) { _lastRuling = "先点一格再建城（壳层不替你选地方）。"; Refresh(); return; }
        var args = new List<(string, string)> { ("hex", _selectedHex) };
        if (_sim.World.OfKind("nation").Count > 0) args.Add(("nation", "auto"));
        Send("town.found", args.ToArray());
    }

    private void SubmitBuild(string buildingDefId)
    {
        if (_selectedTown.Length == 0) { _lastRuling = "营造要选中一座城（点城址格，或点它辖地里的一格）。"; Refresh(); return; }
        Send("building.build",
            ("town", _selectedTown),
            ("building", "def:" + buildingDefId),
            ("hex", "auto"));
    }

    private void Send(string type, params (string Key, string Value)[] args)
    {
        if (_sim is null) return;
        var cmd = _sim.SubmitTarget(PlayerId, type, "UI", args);
        _lastRuling = $"已提交 {cmd.Type}，等下一次结算裁决（意图段，§2.3 第 1 步）";
        var pretty = string.Join(" ", args.Select(a => $"{a.Key}={a.Value}"));
        AppendLog($"[color=#7fb2d6]→ {type}{{{pretty}}}[/color]");
        Refresh();
    }

    // ---- 存读档（§10.6：档 = 快照 + 命令流，落盘由 Core 管）----

    private void DoSave()
    {
        if (_sim is null) return;
        string path = Path.Combine(RepoRoot(), DefaultSave);
        try
        {
            _sim.Save(path);
            AppendLog($"[color=#8fbf5a]✓ 存档 → {path}[/color]");
        }
        catch (SimException e) { AppendLog($"[color=#e07070]✗ 存盘失败：{e.Message}[/color]"); }
    }

    private void DoLoad()
    {
        if (_sim is null) return;
        string path = Path.Combine(RepoRoot(), DefaultSave);
        if (!File.Exists(path)) { AppendLog($"[color=#e0a060]还没有档：{path}[/color]"); return; }
        try
        {
            _sim.Restore(path);
            _last = null;
            _view.Bind(_sim);
            AppendLog($"[color=#8fbf5a]✓ 读档 ← {path}（第 {_sim.Time.Turn} 回合，哈希 {_sim.Hash().Total}）[/color]");
            Refresh();
        }
        catch (SimException e) { AppendLog($"[color=#e07070]✗ 读档失败：{e.Message}[/color]"); }
    }

    // ================= 面板文字 =================

    private void Refresh()
    {
        if (_sim is null)
        {
            _clock.Text = "未起局";
            _state.Text = "起局失败，看右下日志。";
            return;
        }

        var sb = new System.Text.StringBuilder();
        void Add(string line) => sb.AppendLine(line);

        string layerTitle = _sim.Layers.Count > 0
            ? _sim.Layers[Math.Clamp(_layerIndex, 0, _sim.Layers.Count - 1)].Title
            : "—";
        _clock.Text = $"{_sim.Time.Text}　·　seed {_seed}　·　哈希 {_sim.Hash().Total}　·　图层 {layerTitle}";

        Add(_lastRuling);
        Add("");

        if (_last is not null && _last.Headline.Count > 0)
        {
            Add("[b]— 国势（§1.3 第一屏）—[/b]");
            foreach (var kv in _last.Headline) Add($"  {kv.Key}：{kv.Value}");
            Add("");
        }
        else Add("还没结算过这一年：按「结束回合」。");
        Add("");

        if (_selectedHex.Length > 0 && _sim.World.Get(new EntityId(_selectedHex)) is { } hex)
        {
            Add($"[b]— {_selectedHex} —[/b]");
            string land = _sim.Data.TryGet(hex.GetStr("hex.landform"), out var lf) ? lf.GetStr("title") : "?";
            Add($"  地表：{land}（可建城 {(hex.Get("hex.buildable").AsBool() ? "是" : "否")}）");
            string waterId = hex.GetStr("hex.water");
            if (waterId.Length > 0 && _sim.Data.TryGet(waterId, out var wb))
                Add($"  水体：{wb.GetStr("title")}（§3.1；水域不可建城）");
            string down = hex.GetStr("hex.river.down");
            if (down.Length > 0)
                Add($"  河流：→ {down}（集水 {hex.GetInt("hex.river.flow", 0)} 格，宽 {hex.GetInt("hex.river.width", 1)} 档）");
            string shapeId = hex.GetStr("hex.shape");
            if (shapeId.Length > 0 && _sim.Data.TryGet(shapeId, out var shapeDef))
            {
                string shapeNote = shapeDef.GetStr("note");
                Add(shapeNote.Length > 0
                    ? $"  形胜：{shapeDef.GetStr("title")}（§3.4：{shapeNote}）"
                    : $"  形胜：{shapeDef.GetStr("title")}（§3.4）");
            }
            string resourceId = hex.GetStr("hex.resource");
            if (resourceId.Length > 0 && _sim.Data.TryGet(resourceId, out var resDef))
            {
                string resNote = resDef.GetStr("note");
                Add(resNote.Length > 0
                    ? $"  资源：{resDef.GetStr("title")}（§3.5：{resNote}）"
                    : $"  资源：{resDef.GetStr("title")}（§3.5）");
            }
            Add($"  高度 {hex.GetInt("hex.height", 0)} · 温度 {hex.GetInt("hex.temp", 0)} · 降水 {hex.GetInt("hex.rain", 0)} · 粮潜力 {hex.GetFix("hex.yield.food", Fix.Zero).Text} · 料潜力 {hex.GetFix("hex.yield.mat", Fix.Zero).Text} · 行军 {hex.GetInt("hex.moveCost", 0)} · 防御 {hex.GetInt("hex.defense", 0)}");
            int farm = (int)hex.GetInt("hex.building.farm", 0);
            if (farm > 0) Add($"  农田 {farm} 级");
            Add("");
        }

        foreach (var nation in _sim.World.OfKind("nation"))
        {
            Add($"[b]{nation.GetStr("nation.name")}[/b]（{nation.Id}）");
            Add($"  国库 {nation.GetFix("nation.stock.food", Fix.Zero).Text} 石粮");
        }
        if (_sim.World.OfKind("nation").Count == 0)
            Add("[color=#8a94a6]还没有国家：建国前建的城是独立城镇（§4.1 的 owner 可为 null）。[/color]");

        if (_selectedTown.Length > 0 && _sim.World.Get(new EntityId(_selectedTown)) is { } town)
        {
            Add("");
            Add($"[b]{town.GetStr("town.name")}[/b]（{town.Id} · {town.GetStr("town.sovereignty")} · {town.GetStr("town.tier")}）");
            Add($"  人口 {town.GetInt("town.pop.peasant", 0)} 百 · 零头 {town.GetFix("town.growth.peasant", Fix.Zero).Text}");
            Add($"  仓 {town.GetFix("town.stock.food", Fix.Zero).Text} / 容 {town.GetFix("town.stockCap.food", Fix.Zero).Text} · 产出 {town.GetFix("town.output.food", Fix.Zero).Text} · 损耗率 {town.GetFix("town.spoilage.food", Fix.Zero).Text}");
            Add($"  满意 {town.GetFix("town.sat.peasant", Fix.Zero).Text}（{town.GetStr("town.status.peasant")}）· 设施 {town.GetFix("town.facility", Fix.One).Text} · 辖地 {town.GetInt("town.tiles", 0)} 格");
            Add($"[color=#7fb2d6]因果链 town.stock.food：[/color]");
            Add(Mono(_sim.CausalChain(town.Id, "town.stock.food")));
        }

        _state.Text = sb.ToString();
        _view.RefreshLabels();                      // 城名是节点，跟着世界状态走（见 HexView.RefreshLabels）
    }

    private static string Mono(string s) =>
        string.Join("\n", s.Split('\n').Select(l => "    " + l.TrimEnd()));

    // ================= HUD 搭建（全代码，免 .tscn 里对齐像素）=================

    private void BuildHud()
    {
        var hud = new CanvasLayer { Name = "HUD" };
        AddChild(hud);

        var root = new Control { Name = "Root" };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        hud.AddChild(root);

        // 顶栏
        var top = new PanelContainer();
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        top.OffsetBottom = 66;
        root.AddChild(top);

        var topBox = new VBoxContainer();
        topBox.AddThemeConstantOverride("separation", 2);
        top.AddChild(topBox);

        _clock = new Label { Text = "—" };
        _clock.AddThemeFontSizeOverride("font_size", 17);
        topBox.AddChild(_clock);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 6);
        topBox.AddChild(bar);
        void AddButton(string text, Action onPressed)
        {
            var b = new Button { Text = text };
            b.Pressed += onPressed;
            bar.AddChild(b);
        }
        AddButton("建国 N", SubmitFoundNation);
        _nationName = new LineEdit { Text = "魏", PlaceholderText = "国名", CustomMinimumSize = new Vector2(96, 0) };
        bar.AddChild(_nationName);
        AddButton("建城 B", SubmitFound);
        AddButton("农田 F", () => SubmitBuild("building.farm"));
        AddButton("粮仓 G", () => SubmitBuild("building.granary"));
        AddButton("结束回合 Space", EndTurn);
        AddButton("存盘 S", DoSave);
        AddButton("读档 L", DoLoad);
        AddButton("图层 T", CycleLayer);
        AddButton("换种子 R", () => StartGame(_seed + 1, banner: true));
        _hint = new Label { Text = "" };
        _hint.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f));
        bar.AddChild(_hint);

        // 左侧状态栏：国势 + 选中详情（§1.3 第一屏要的那几行数）
        var left = new PanelContainer();
        left.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        left.OffsetLeft = 8;
        left.OffsetTop = 78;
        left.OffsetRight = 470;
        left.OffsetBottom = 470;
        root.AddChild(left);

        _state = new RichTextLabel
        {
            BbcodeEnabled = true,
            SelectionEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(450, 380),
        };
        _state.AddThemeFontSizeOverride("normal_font_size", 14);
        left.AddChild(_state);

        // 右下：年报日志
        var logPanel = new PanelContainer();
        logPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        logPanel.OffsetLeft = -990;
        logPanel.OffsetTop = -420;
        logPanel.OffsetRight = -8;
        logPanel.OffsetBottom = -8;
        root.AddChild(logPanel);

        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(970, 400),
        };
        _log.AddThemeFontSizeOverride("normal_font_size", 13);
        logPanel.AddChild(_log);
    }

    private void AppendLog(string bbcode)
    {
        _log.AppendText(bbcode + "\n");
        if (_log.GetLineCount() > 4000) _log.Text = "";            // 长局别把内存吃光，日志只留最近一段
    }

    // ================= 路径 =================

    /// <summary>仓库根 = 本 Godot 工程目录（src/GodotShell/）往上两级。数据与插件都按根解析。</summary>
    private static string RepoRoot()
    {
        string projectDir = ProjectSettings.GlobalizePath("res://");
        return Path.GetFullPath(Path.Combine(projectDir, "..", ".."));
    }

    private static (string DataDir, string PluginDir, string RepoRoot) RepoPaths()
    {
        string root = RepoRoot();
        return (Path.Combine(root, "data"), Path.Combine(root, "build", "plugins"), root);
    }
}

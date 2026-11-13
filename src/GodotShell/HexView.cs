using System.Globalization;
using Godot;
using SimCore;

namespace HexaCiv.GodotShell;

/// <summary>
/// 六边形地图渲染（GDD §3.1 pointy-top 轴坐标）。
///
/// 它是<b>纯读</b>的一层：颜色取自数据包 <c>landform.color</c>，
/// 辖地、城址、田级一律读世界字段，画面里没有任何一条规则（§10.1）。
/// 「这块地能不能建城」不由这里回答——壳层照样把命令提交上去，
/// 由 core.basic 给出驳回理由（<see cref="Main"/> 把那行字显示出来）。
/// </summary>
public partial class HexView : Node2D
{
    /// <summary>外接圆半径（像素）。改这一个数就整体缩放地图。</summary>
    public const float HexSize = 46f;

    [Signal]
    public delegate void HexClickedEventHandler(string hexId);

    [Signal]
    public delegate void HexHoveredEventHandler(string hexId);

    private Simulation? _sim;
    private EntityId _hover;
    private EntityId _selected;
    private Node2D? _labels;

    /// <summary>辖地着色用的城色板：UI 资产，不进模拟（同一座城在任何一台机器上都是这个颜色，因为它按城序取）。</summary>
    private static readonly Color[] TownTints =
    [
        new(0.86f, 0.72f, 0.30f), new(0.55f, 0.78f, 0.92f), new(0.80f, 0.58f, 0.86f),
        new(0.62f, 0.86f, 0.62f), new(0.92f, 0.60f, 0.52f), new(0.72f, 0.66f, 0.90f)
    ];

    public override void _Ready()
    {
        // 地图要跟着窗口中心走，否则拉大窗口后画面偏在一角
        GetViewport().SizeChanged += () => QueueRedraw();
    }

    public void Bind(Simulation sim)
    {
        _sim = sim;
        _hover = default;
        _selected = default;
        QueueRedraw();
        RefreshLabels();
    }

    /// <summary>
    /// 城名用 <see cref="Label"/> 子节点画，不用 <c>DrawString</c>。理由不是审美，是当场量到的：
    /// 原开发机的 Godot 4.6.1(.NET) 里 <c>ThemeDB.FallbackFont</c> 非空、<c>FallbackFontSize=16</c>，
    /// 但 <c>DrawString(font, pos, text, align, fontSize: 15, …)</c> 每格都报
    /// <c>Condition "p_size &lt;= 0"</c>（headless 与窗口模式都报，与传进去的 15 无关）；
    /// 同一个 TextServer 画 HUD 的 Label / RichTextLabel 却一直是好的——出问题的是 DrawString 这条取字号的路径。
    /// 壳层为了「地图上有名字」去赌引擎的字体回退不值得，节点这条走得住，还白送描边。
    /// （<c>--smoke</c> 会打印这三个数，换台机器如果结论变了，先看那行。）
    /// </summary>
    public void RefreshLabels()
    {
        if (_sim is null) return;
        if (_labels is null) _labels = new Node2D { Name = "TownLabels" };
        var host = _labels;
        if (!host.IsNodeReady()) AddChild(host);                  // Bind 可能早于 _Ready，这里补挂
        foreach (var child in host.GetChildren()) child.QueueFree();

        foreach (var hex in _sim.World.OfKind("hex"))
        {
            string townId = hex.GetStr("hex.town");
            if (townId.Length == 0) continue;
            var (q, r) = AxialOf(hex);
            string name = _sim.World.GetStr(new EntityId(townId), "town.name", townId);

            var label = new Label
            {
                Text = name,
                Position = Pixel(q, r) - new Vector2(70, HexSize * 0.92f),
                CustomMinimumSize = new Vector2(140, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,     // 名字不能挡点击
            };
            label.AddThemeFontSizeOverride("font_size", 15);
            label.AddThemeColorOverride("font_color", new Color(0.97f, 0.97f, 0.99f));
            label.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.05f, 0.07f, 0.9f));
            label.AddThemeConstantOverride("outline_size", 4);
            _labels.AddChild(label);
        }
    }

    public EntityId Selected
    {
        get => _selected;
        set { _selected = value; QueueRedraw(); }
    }

    public override void _Draw()
    {
        if (_sim is null) return;
        var world = _sim.World;
        if (world.Get(World.GlobalId) is null) return;

        Position = GetViewportRect().Size / 2f;          // 轴坐标 (0,0) 落在视口中心

        // 城色按 id 升序取色板，保证「同一个存档、同一座城、同一个颜色」
        var tint = new Dictionary<string, Color>(StringComparer.Ordinal);
        int i = 0;
        foreach (var town in world.OfKind("town"))
            tint[town.Id.Value] = TownTints[i++ % TownTints.Length];

        foreach (var hex in world.OfKind("hex"))
        {
            var (q, r) = AxialOf(hex);
            var center = Pixel(q, r);
            var poly = Corners(center);

            Color baseColor = ColorOf(hex);
            string owner = hex.GetStr("hex.owner");
            if (owner.Length > 0 && tint.TryGetValue(owner, out var c))
                baseColor = baseColor.Lerp(c, 0.34f);

            DrawColoredPolygon(poly, baseColor);
            DrawPolyline(poly, new Color(0f, 0f, 0f, 0.35f), 1f);

            // §3.2：山地不可建城。画一个峰形而不是「▲」字符——壳层不解释规则，但也不该依赖字体能显示什么符号
            if (!hex.Get("hex.buildable").AsBool())
                DrawColoredPolygon(
                    [center + new Vector2(0, -11), center + new Vector2(10, 8), center + new Vector2(-10, 8)],
                    new Color(0.16f, 0.16f, 0.19f, 0.80f));

            int farm = (int)hex.GetInt("hex.building.farm", 0);
            for (int f = 0; f < farm; f++)
                DrawCircle(center + new Vector2(-14 + 14 * f, HexSize * 0.52f), 4f, new Color(0.98f, 0.90f, 0.42f));

            string townId = hex.GetStr("hex.town");
            if (townId.Length > 0)
            {
                DrawCircle(center, HexSize * 0.30f, new Color(0.12f, 0.12f, 0.14f, 0.90f));
                DrawCircle(center, HexSize * 0.24f, tint.TryGetValue(townId, out var tc) ? tc : Colors.White);
            }

            if (hex.Id == _hover)
                DrawPolyline(poly, new Color(1f, 1f, 1f, 0.55f), 2f);
            if (hex.Id == _selected)
                DrawPolyline(poly, new Color(0.35f, 0.95f, 1f, 0.95f), 3f);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_sim is null) return;

        if (@event is InputEventMouseMotion m)
        {
            var id = Pick(m.GlobalPosition);
            if (id != _hover)
            {
                _hover = id;
                QueueRedraw();
                EmitSignal(SignalName.HexHovered, id.Value);
            }
            return;
        }

        if (@event is InputEventMouseButton b && b.Pressed && b.ButtonIndex == MouseButton.Left)
        {
            var id = Pick(b.GlobalPosition);
            if (id.IsEmpty) return;
            _selected = id;
            QueueRedraw();
            EmitSignal(SignalName.HexClicked, id.Value);
        }
    }

    // ---- 坐标换算（§3.1：pointy-top，x = √3·size·(q + r/2)，y = 1.5·size·r）----

    public static Vector2 Pixel(int q, int r) =>
        new(HexSize * 1.7320508f * (q + r / 2f), HexSize * 1.5f * r);

    /// <summary>屏幕点 → 轴坐标（先 fractional axial，再立方坐标取整）。</summary>
    public EntityId Pick(Vector2 globalPos)
    {
        if (_sim is null) return default;
        var local = ToLocal(globalPos);
        float fq = (local.X * 0.57735027f - local.Y / 3f) / HexSize;
        float fr = (local.Y * 2f / 3f) / HexSize;
        var (q, r) = CubeRound(fq, fr);
        var id = new EntityId($"hex.{q:000}.{r:000}");
        return _sim.World.Has(id) ? id : default;
    }

    private static (int q, int r) CubeRound(float fq, float fr)
    {
        float fq2 = fq, fr2 = fr, fs = -fq - fr;
        int rq = Mathf.RoundToInt(fq2), rr = Mathf.RoundToInt(fr2), rs = Mathf.RoundToInt(fs);
        float dq = Mathf.Abs(rq - fq2), dr = Mathf.Abs(rr - fr2), ds = Mathf.Abs(rs - fs);
        if (dq > dr && dq > ds) rq = -rr - rs;
        else if (dr > ds) rr = -rq - rs;
        return (rq, rr);
    }

    private static (int q, int r) AxialOf(Entity hex) => ((int)hex.GetInt("hex.q"), (int)hex.GetInt("hex.r"));

    private static Vector2[] Corners(Vector2 center)
    {
        var pts = new Vector2[6];
        for (int k = 0; k < 6; k++)
        {
            double angle = Mathf.DegToRad(60 * k - 30);        // pointy-top 的第一支角在 −30°
            pts[k] = center + new Vector2((float)(HexSize * Mathf.Cos(angle)), (float)(HexSize * Mathf.Sin(angle)));
        }
        return pts;
    }

    private Color ColorOf(Entity hex)
    {
        string defId = hex.GetStr("hex.landform");
        if (_sim is not null && _sim.Data.TryGet(defId, out var lf))
        {
            Color parsed = HtmlColor(lf.GetStr("color"), new Color(0.5f, 0.5f, 0.52f));
            // 高度越高越亮一点：让 §3.7 第 1 步的高度图在画面上看得出来，但不引入任何规则
            int h = (int)hex.GetInt("hex.height", 50);
            return parsed.Lightened((h - 50) / 400f);
        }
        return new Color(0.5f, 0.5f, 0.52f);
    }

    /// <summary>
    /// 自己解 #rrggbb，不用 Godot 的颜色解析重载：
    /// 配色写在数据包里（改颜色不用改代码），但跨引擎版本的 API 名字不值得壳层去赌。
    /// </summary>
    public static Color HtmlColor(string html, Color fallback)
    {
        if (string.IsNullOrEmpty(html)) return fallback;
        string s = html.StartsWith('#') ? html[1..] : html;
        if (s.Length != 6) return fallback;
        try
        {
            return new Color(
                int.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f,
                int.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f,
                int.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f);
        }
        catch (FormatException) { return fallback; }
    }
}

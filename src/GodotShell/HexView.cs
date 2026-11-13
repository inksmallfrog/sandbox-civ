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
    private float _fitScale = 1f;
    private Vector2 _mapCenter;

    /// <summary>辖地着色用的城色板：UI 资产，不进模拟（同一座城在任何一台机器上都是这个颜色，因为它按城序取）。</summary>
    private static readonly Color[] TownTints =
    [
        new(0.86f, 0.72f, 0.30f), new(0.55f, 0.78f, 0.92f), new(0.80f, 0.58f, 0.86f),
        new(0.62f, 0.86f, 0.62f), new(0.92f, 0.60f, 0.52f), new(0.72f, 0.66f, 0.90f)
    ];

    public override void _Ready()
    {
        // 地图要跟着窗口中心走，否则拉大窗口后画面偏在一角
        GetViewport().SizeChanged += () => { LayoutMap(); QueueRedraw(); };
    }

    public void Bind(Simulation sim)
    {
        _sim = sim;
        _hover = default;
        _selected = default;
        LayoutMap();
        QueueRedraw();
        RefreshLabels();
    }

    /// <summary>
    /// 把整张矩形地图（§3.9 的 32 × 20）缩放进窗口可用区域。
    /// 拾取走 <c>ToLocal</c>，会自带这里的 Position/Scale，所以点击不会和画面错位。
    /// 左侧状态栏与顶部按钮条要让出来——数值与 <c>Main.BuildHud</c> 的布局一致，改那边要同步这里。
    /// </summary>
    private void LayoutMap()
    {
        if (_sim is null) return;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var hex in _sim.World.OfKind("hex"))
        {
            var (q, r) = AxialOf(hex);
            Vector2 p = Pixel(q, r);
            minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
            minY = Mathf.Min(minY, p.Y); maxY = Mathf.Max(maxY, p.Y);
        }
        if (minX > maxX) { _fitScale = 1f; _mapCenter = Vector2.Zero; return; }

        // 六边形的外接框：横向半宽 √3/2·size，纵向半高 size
        const float halfWidth = 0.8660254f * HexSize;
        minX -= halfWidth; maxX += halfWidth; minY -= HexSize; maxY += HexSize;

        const float hudLeft = 480f, hudTop = 74f, margin = 12f;
        Vector2 vp = GetViewportRect().Size;
        float availW = Mathf.Max(320f, vp.X - hudLeft - margin);
        float availH = Mathf.Max(240f, vp.Y - hudTop - margin);
        float mapW = Mathf.Max(1f, maxX - minX);
        float mapH = Mathf.Max(1f, maxY - minY);

        _fitScale = Mathf.Min(1.25f, Mathf.Min(availW / mapW, availH / mapH));
        _mapCenter = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
        Scale = new Vector2(_fitScale, _fitScale);
        Position = new Vector2(hudLeft, hudTop) + new Vector2(availW, availH) / 2f - _mapCenter * _fitScale;
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
        LayoutMap();
        if (_labels is null) _labels = new Node2D { Name = "TownLabels" };
        var host = _labels;
        if (!host.IsNodeReady()) AddChild(host);                  // Bind 可能早于 _Ready，这里补挂
        foreach (var child in host.GetChildren()) child.QueueFree();

        // 标签节点跟着 HexView 一起缩放，字号要反着放大，屏幕上才是稳定大小
        int fontSize = Mathf.Clamp(Mathf.RoundToInt(15f / Mathf.Max(_fitScale, 0.05f)), 10, 60);
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
            label.AddThemeFontSizeOverride("font_size", fontSize);
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

        LayoutMap();                                     // Position/Scale 由它按视口与地图尺寸算（32×20 放不进窗口）

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
        }

        // 第二遍：河段。§3.3 的边存在上游格上（hex.river.down），两端中心连一条线；
        // 必须等所有多边形画完，否则后画的格子会把半条河盖掉。
        foreach (var hex in world.OfKind("hex"))
        {
            string down = hex.GetStr("hex.river.down");
            if (down.Length == 0) continue;
            var to = world.Get(new EntityId(down));
            if (to is null) continue;
            var (q, r) = AxialOf(hex);
            var (tq, tr) = AxialOf(to);
            int width = (int)hex.GetInt("hex.river.width", 1);
            DrawLine(Pixel(q, r), Pixel(tq, tr), new Color(0.20f, 0.55f, 0.85f, 0.92f), 1.5f + width * 1.8f);
        }

        // 第三遍：不可建城标记、农田、城、悬停/选中描边。
        foreach (var hex in world.OfKind("hex"))
        {
            var (q, r) = AxialOf(hex);
            var center = Pixel(q, r);
            var poly = Corners(center);

            // §3.2：山地不可建城。水域同样不可建城（§3.8），但水格上不必再画峰形。
            if (!hex.Get("hex.buildable").AsBool() && hex.GetStr("hex.water").Length == 0)
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
        // §3.1 水体层优先于地表：湖/海的颜色来自 water.* 数据，改配色不用改代码。
        string waterId = hex.GetStr("hex.water");
        if (_sim is not null && waterId.Length > 0 && _sim.Data.TryGet(waterId, out var wb))
            return HtmlColor(wb.GetStr("color"), new Color(0.20f, 0.40f, 0.60f));

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

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace KeyMouseCounter.UI;

internal static class UiText
{
    /// <summary>千分位整数（§7.2.3、§5.9）。</summary>
    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>挂件用：万以上缩写成 "1.5万"，其余千分位（§5.9）。</summary>
    public static string Compact(long v)
    {
        if (v >= 100_000_000) return (v / 100_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "亿";
        if (v >= 10_000) return (v / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "万";
        return N(v);
    }

    /// <summary>秒 → "5h 58m" / "58m" / "0m"（§7.1 卡片）。</summary>
    public static string Duration(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = (seconds % 3600) / 60;
        if (h > 0) return $"{h}h {m}m";
        return $"{m}m";
    }

    /// <summary>秒 → "5小时58分"，挂件次要行用（§5.9）。</summary>
    public static string DurationCjk(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = (seconds % 3600) / 60;
        if (h > 0) return $"{h}h {m}m";
        if (m > 0) return $"{m}m";
        return $"{seconds}s";
    }

    /// <summary>同口径百分比变化；昨日为 0 时返回 "—"（§7.1）。</summary>
    public static (string Text, int Sign) Delta(long today, long yesterday)
    {
        if (yesterday <= 0) return ("—", 0);
        if (today == yesterday) return ("0.0%", 0);
        double pct = (today - yesterday) * 100.0 / yesterday;
        string text = Math.Abs(pct).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        return (text, pct > 0 ? 1 : -1);
    }

    public static string Weekday(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    public static string MinuteLabel(int minuteOfDay)
        => $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";
}

/// <summary>圆角矩形工具 + 圆角自绘面板。</summary>
internal static class UiGfx
{
    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void EnableSmoothing(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }
}

/// <summary>圆角面板，带 1px 描边（§7 卡片风格）。</summary>
internal class CardPanel : Panel
{
    private int _radius = 10;

    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }

    public int Radius
    {
        get => _radius;
        set { _radius = value; Invalidate(); }
    }

    public bool ShowBorder { get; set; } = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        UiGfx.EnableSmoothing(g);
        var p = Theme.Current;
        g.Clear(p.Window);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiGfx.RoundRect(r, Radius);
        using var fill = new SolidBrush(p.Surface);
        g.FillPath(fill, path);
        if (ShowBorder)
        {
            using var pen = new Pen(p.Border, 1f);
            g.DrawPath(pen, path);
        }

        base.OnPaint(e);
    }
}

/// <summary>扁平按钮：主按钮（强调色）/ 次按钮（描边）。</summary>
internal sealed class FlatButton : Button
{
    public bool Primary { get; set; }

    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        DoubleBuffered = true;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Font = Theme.Font(9.5f);
        Height = 32;
        SizeChanged += (_, _) => Apply();
        Theme.Changed += Apply;
        Apply();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Apply;
        base.Dispose(disposing);
    }

    private void Apply()
    {
        var p = Theme.Current;
        if (Primary)
        {
            BackColor = p.Accent;
            ForeColor = p.Kind == ThemeKind.Dark ? Color.White : Color.White;
            FlatAppearance.MouseOverBackColor = ControlPaint.Light(p.Accent, 0.15f);
            FlatAppearance.MouseDownBackColor = ControlPaint.Dark(p.Accent, 0.1f);
        }
        else
        {
            BackColor = p.SurfaceAlt;
            ForeColor = p.Fore;
            FlatAppearance.MouseOverBackColor = ControlPaint.Light(p.SurfaceAlt, 0.06f);
            FlatAppearance.MouseDownBackColor = p.Surface;
        }
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) ForeColor = Theme.Current.Muted;
        else Apply();
    }
}

/// <summary>顶部分段标签：[今日·按小时] [今日·按分钟] [多日·按天]。</summary>
internal sealed class SegmentedTabs : Control
{
    private string[] _items = Array.Empty<string>();
    private int _selected;
    private int _hover = -1;

    public event EventHandler? SelectedIndexChanged;

    public SegmentedTabs()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Font(9.5f);
        Height = 30;
        Cursor = Cursors.Hand;
    }

    public void SetItems(params string[] items)
    {
        _items = items;
        if (_selected >= items.Length) _selected = Math.Max(0, items.Length - 1);
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Length || value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private RectangleF ItemRect(int i, Graphics g)
    {
        if (_items.Length == 0) return RectangleF.Empty;
        float total = 0;
        var widths = new float[_items.Length];
        for (int k = 0; k < _items.Length; k++)
        {
            widths[k] = g.MeasureString(_items[k], Font).Width + 26;
            total += widths[k];
        }
        float start = Math.Max(0, (Width - total) / 2);
        for (int k = 0; k < i; k++) start += widths[k];
        return new RectangleF(start + 3, 2, widths[i] - 6, Height - 5);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        UiGfx.EnableSmoothing(g);
        var p = Theme.Current;
        g.Clear(p.Window);
        if (_items.Length == 0) return;

        using var trackFill = new SolidBrush(p.SurfaceAlt);
        for (int i = 0; i < _items.Length; i++)
        {
            var r = ItemRect(i, g);
            using var rr = UiGfx.RoundRect(Rectangle.Round(r), 6);
            g.FillPath(trackFill, rr);
        }

        for (int i = 0; i < _items.Length; i++)
        {
            var r = ItemRect(i, g);
            using var rr = UiGfx.RoundRect(Rectangle.Round(r), 6);
            if (i == _selected)
            {
                using var sel = new SolidBrush(p.Accent);
                g.FillPath(sel, rr);
            }
            else if (i == _hover)
            {
                using var hov = new SolidBrush(Color.FromArgb(p.Kind == ThemeKind.Dark ? 22 : 14, p.Accent));
                g.FillPath(hov, rr);
            }

            var color = i == _selected ? Color.White : p.Muted;
            using var brush = new SolidBrush(color);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_items[i], i == _selected ? Theme.Font(9.5f, FontStyle.Bold) : Font, brush, r, sf);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = -1;
        using (var g = CreateGraphics())
        {
            for (int i = 0; i < _items.Length; i++)
                if (ItemRect(i, g).Contains(e.Location)) { hit = i; break; }
        }
        if (hit != _hover) { _hover = hit; Invalidate(); Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default; }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        using var g = CreateGraphics();
        for (int i = 0; i < _items.Length; i++)
            if (ItemRect(i, g).Contains(e.Location)) { SelectedIndex = i; return; }
    }
}

/// <summary>下拉选择按钮（"最近 7 天 ▾" / "指标：键盘按键 ▾"）。</summary>
internal sealed class DropDownButton : Button
{
    private ToolStripDropDown? _menu;

    public DropDownButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        DoubleBuffered = true;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Font = Theme.Font(9.5f);
        Height = 30;
        TextAlign = ContentAlignment.MiddleCenter;
        Theme.Changed += Apply;
        Apply();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= Apply; _menu?.Dispose(); }
        base.Dispose(disposing);
    }

    private void Apply()
    {
        var p = Theme.Current;
        BackColor = p.SurfaceAlt;
        ForeColor = p.Fore;
        FlatAppearance.MouseOverBackColor = ControlPaint.Light(p.SurfaceAlt, 0.06f);
        FlatAppearance.MouseDownBackColor = p.AccentSoft;
        Invalidate();
    }

    /// <summary>重建下拉项。</summary>
    public void SetItems(IEnumerable<(string Text, Action OnClick, bool Checked)> items)
    {
        _menu?.Dispose();
        _menu = new ToolStripDropDown { Renderer = new DarkMenuRenderer() };
        foreach (var (text, onClick, isChecked) in items)
        {
            var it = new ToolStripMenuItem(text) { Checked = isChecked };
            it.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Diagnostics.Log.Error("下拉项执行失败", ex); } };
            _menu.Items.Add(it);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (_menu is null || _menu.Items.Count == 0) return;
        _menu.Show(this, new Point(0, Height + 2), ToolStripDropDownDirection.BelowRight);
    }
}

/// <summary>深色主题的菜单/上下文菜单渲染器。</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item!.Enabled ? Theme.Current.Fore : Theme.Current.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var p = Theme.Current;
        var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        using var b = new SolidBrush(e.Item.Selected ? p.AccentSoft : p.Surface);
        e.Graphics.FillRectangle(b, r);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Current.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Current.Border);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        UiGfx.EnableSmoothing(g);
        var c = Theme.Current.Accent;
        using var pen = new Pen(c, 1.8f);
        var r = e.ImageRectangle;
        g.DrawLines(pen, new[]
        {
            new PointF(r.Left + 2, r.Top + r.Height / 2f),
            new PointF(r.Left + r.Width * 0.42f, r.Bottom - 3),
            new PointF(r.Right - 2, r.Top + 3)
        });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Current.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Current.Surface;
    public override Color MenuItemSelected => Theme.Current.AccentSoft;
    public override Color MenuItemBorder => Theme.Current.Accent;
    public override Color ImageMarginGradientBegin => Theme.Current.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Current.Surface;
    public override Color ImageMarginGradientEnd => Theme.Current.Surface;
    public override Color SeparatorDark => Theme.Current.Border;
    public override Color SeparatorLight => Theme.Current.Border;
}

/// <summary>标题 + 大数字 + 副行的统计卡片（§7.1）。</summary>
internal sealed class StatCard : CardPanel
{
    private readonly Label _title;
    private readonly Label _value;
    private readonly Label _sub;

    public StatCard(string title)
    {
        Padding = new Padding(16, 12, 16, 12);
        Margin = new Padding(0);

        _title = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 20,
            Font = Theme.Font(9.5f),
            ForeColor = Theme.Current.Muted,
            BackColor = Color.Transparent
        };
        _value = new Label
        {
            Text = "0",
            Dock = DockStyle.Fill,
            Font = Theme.Font(24f, FontStyle.Bold),
            ForeColor = Theme.Current.Fore,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };
        _sub = new Label
        {
            Text = "较昨日 —",
            Dock = DockStyle.Bottom,
            Height = 18,
            Font = Theme.Font(8.5f),
            ForeColor = Theme.Current.Muted,
            BackColor = Color.Transparent
        };

        Controls.Add(_value);
        Controls.Add(_sub);
        Controls.Add(_title);
        Theme.Changed += ApplyTheme;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    private void ApplyTheme()
    {
        var p = Theme.Current;
        _title.ForeColor = p.Muted;
        _value.ForeColor = p.Fore;
        _sub.ForeColor = p.Muted;
        Invalidate(true);
    }

    public string Value
    {
        get => _value.Text;
        set { if (_value.Text != value) _value.Text = value; }
    }

    /// <summary>副行："较昨日 +2.3%" 或 "滚轮 810 格"。</summary>
    public void SetSub(string text, int sign, bool colorize = true)
    {
        if (_sub.Text == text)
        {
            // 文本没变也要保证颜色对（主题切换后）
            if (colorize && sign != 0) _sub.ForeColor = sign > 0 ? Theme.Current.Accent : Theme.Current.Danger;
            return;
        }
        _sub.Text = text;
        if (!colorize || sign == 0) _sub.ForeColor = Theme.Current.Muted;
        else _sub.ForeColor = sign > 0 ? Theme.Current.Accent : Theme.Current.Danger;
    }

    public void SetTitle(string title) => _title.Text = title;

    /// <summary>暂停时数字变灰（§5.9 挂件同理，主窗口保持一致）。</summary>
    public void SetPaused(bool paused)
    {
        _value.ForeColor = paused ? Theme.Current.Muted : Theme.Current.Fore;
    }
}

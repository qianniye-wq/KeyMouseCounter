using System.Drawing;
using System.Drawing.Drawing2D;
using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Native;

namespace KeyMouseCounter.UI;

/// <summary>挂件上显示的一组数字（由 AppHost 提供，挂件自己不算数）。</summary>
internal readonly record struct WidgetData(
    long Key, long Click, long Wheel, long ActiveSeconds, bool Paused);

/// <summary>
/// 桌面挂件（§5.9 / §7.3）：无边框、置顶、不进任务栏、默认不抢焦点；
/// WM_EX_NOACTIVATE + ShowWithoutActivation 保证点它不会打断当前输入焦点。
/// </summary>
internal sealed class WidgetForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private readonly AppConfig _cfg;
    private readonly ConfigWatcher _watch;
    private readonly CounterEngine _engine;
    private readonly Func<WidgetData> _provider;
    private readonly Action _openMain;
    private readonly Action _openSettings;
    private readonly Action _exitApp;
    private readonly Action _togglePause;
    private readonly Func<bool> _isPaused;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _savePos = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 40 };

    private WidgetData _last;
    private Point _dragStart;
    private bool _ctrWheel;
    private bool _fading;
    private double _fadeStep;

    public WidgetForm(AppConfig cfg, ConfigWatcher watch, CounterEngine engine, Func<WidgetData> provider,
                      Action openMain, Action openSettings, Action exitApp, Action togglePause, Func<bool> isPaused)
    {
        _cfg = cfg;
        _watch = watch;
        _engine = engine;
        _provider = provider;
        _openMain = openMain;
        _openSettings = openSettings;
        _exitApp = exitApp;
        _togglePause = togglePause;
        _isPaused = isPaused;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "KeyMouseCounter 挂件";
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        ApplySizeAndFont();
        ApplyPosition(cfg.Widget.X, cfg.Widget.Y, firstRun: true);
        ApplyOpacity(cfg.Widget.Opacity, save: false);
        ApplyClickThrough(cfg.Widget.ClickThrough);

        _tick.Tick += (_, _) => Refresh1s();
        _savePos.Tick += (_, _) => { _savePos.Stop(); SavePosition(); };
        _fade.Tick += (_, _) => FadeStep();

        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) _openMain(); };
        MouseWheel += OnMouseWheel;
        MouseClick += OnMouseClick;
    }

    /// <summary>不抢焦点（§5.9）。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            // 注意：Control 基类构造函数里就会读一次 CreateParams，那时 _cfg 还没赋值，
            // 所以这里必须容忍 null（否则启动即 NullReferenceException）。
            if (_cfg is not null && _cfg.Widget.ClickThrough) cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void StartTicking() => _tick.Start();

    public void StopTicking() => _tick.Stop();

    // ── 外观 ────────────────────────────────────────────────────────────

    private float BigSize => (float)(_cfg.Widget.FontSize * 2.4);
    private float LabelSize => (float)(_cfg.Widget.FontSize * 0.9);
    private float SmallSize => (float)(_cfg.Widget.FontSize * 0.85);

    private void ApplySizeAndFont()
    {
        double scale = Math.Clamp(_cfg.Widget.FontSize / 10.0, 0.7, 2.0);
        var size = new Size((int)Math.Round(240 * scale), (int)Math.Round(96 * scale));
        MinimumSize = size;
        MaximumSize = size;
        ClientSize = size;
        ApplyRoundedCorners();
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            // Win11 原生圆角（§5.9 / §15.4 圆角观感）
            int pref = 2;
            if (NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0)
                return;
        }
        catch { }
        try
        {
            using var rgn = System.Drawing.Region.FromHrgn(NativeMethods.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 14, 14));
            Region = rgn;
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        UiGfx.EnableSmoothing(g);

        bool paused = _last.Paused;
        bool warn = !string.IsNullOrEmpty(_warning);
        var accent = paused ? Theme.Current.Muted : AccentColor();
        var fore = paused ? Theme.Current.Muted : Theme.Current.Fore;
        var muted = Theme.Current.Muted;

        // 背景：半透明卡片 + 1px 描边（§5.9）
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = UiGfx.RoundRect(rect, 12))
        using (var bg = new SolidBrush(Color.FromArgb(0xF2, 0x14, 0x18, 0x20)))
        {
            g.FillPath(bg, path);
            using var pen = new Pen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            g.DrawPath(pen, path);
        }

        float pad = Width * 0.055f;
        float colW = (Width - pad * 2) / 2f;

        using (var big = Theme.Font(BigSize, FontStyle.Bold))
        using (var label = Theme.Font(LabelSize))
        using (var small = Theme.Font(SmallSize))
        {
            // 第一行：键盘 / 点击（9pt 灰标签 + 24pt 大数字，§5.9）
            float lh = label.GetHeight(g) + 1f;
            DrawMetric(g, "键盘", UiText.N(_last.Key), pad, Height * 0.08f, big, label, fore, muted);
            DrawMetric(g, "点击", UiText.N(_last.Click), pad + colW, Height * 0.08f, big, label, fore, muted);

            // 分隔线
            float sepY = Height * 0.715f;
            using (var pen = new Pen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)))
                g.DrawLine(pen, pad, sepY, Width - pad, sepY);

            // 次要信息：活跃 xh ym · 滚轮 n 格
            string second = _cfg.Widget.ShowActiveTime
                ? $"活跃 {UiText.DurationCjk(_last.ActiveSeconds)} · 滚轮 {UiText.N(_last.Wheel)} 格"
                : $"滚轮 {UiText.N(_last.Wheel)} 格";
            using (var sb = new SolidBrush(muted))
                g.DrawString(second, small, sb, pad, sepY + Height * 0.035f);

            float right = Width - pad;
            if (warn)
            {
                using var wb = new SolidBrush(Theme.Current.Warning);
                string tip = _warning!;
                var size = g.MeasureString("⚠", label);
                g.DrawString("⚠", label, wb, right - size.Width, Height * 0.045f);
                right -= size.Width + 4;
            }
            if (paused)
            {
                using var pb = new SolidBrush(accent);
                var size = g.MeasureString("Ⅱ", label);
                g.DrawString("Ⅱ", label, pb, right - size.Width, Height * 0.045f);
            }
            _ = lh;
        }
    }

    private static void DrawMetric(Graphics g, string title, string value, float x, float y,
                                   Font big, Font label, Color fore, Color muted)
    {
        float lh = label.GetHeight(g);
        using var lb = new SolidBrush(muted);
        g.DrawString(title, label, lb, x, y);
        using var vb = new SolidBrush(fore);
        g.DrawString(value, big, vb, x, y + lh + 1f);
    }

    private Color AccentColor()
    {
        try
        {
            var c = ColorTranslator.FromHtml(_cfg.Widget.AccentColor);
            return c.IsEmpty ? Theme.Current.Accent : c;
        }
        catch { return Theme.Current.Accent; }
    }

    // ── 交互 ────────────────────────────────────────────────────────────

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragStart = Cursor.Position;
            // 点挂件产生的鼠标事件不该计入统计（§5.7）
            _engine.SuppressFor(TimeSpan.FromMilliseconds(50));
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _dragStart != Point.Empty)
        {
            if (Cursor.Position != _dragStart)
            {
                var pos = Cursor.Position;
                _dragStart = Point.Empty;
                ApplyPosition(pos.X, pos.Y, firstRun: false);
            }
            _dragStart = Point.Empty;
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _dragStart != Point.Empty)
        {
            // 用系统级拖动，避免自己算偏移在 DPI 缩放下漂移（§5.9）
            try
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                _dragStart = Point.Empty;
            }
            catch (Exception ex) { Log.Warn($"挂件拖动失败：{ex.Message}"); }
        }
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (WindowState == FormWindowState.Normal)
        {
            _savePos.Stop();
            _savePos.Start();
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        _engine.SuppressFor(TimeSpan.FromMilliseconds(50));
        if (e.Button != MouseButtons.Right) return;
        ShowContextMenu(new Point(e.X, e.Y));
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0) return;
        _ctrWheel = true;
        double delta = e.Delta > 0 ? 0.05 : -0.05;
        ApplyOpacity(_cfg.Widget.Opacity + delta, save: true);
        _ctrWheel = false;
    }

    private void ShowContextMenu(Point at)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };
        var hide = new ToolStripMenuItem("隐藏挂件");
        hide.Click += (_, _) => HideWidget();
        var top = new ToolStripMenuItem("穿透点击") { Checked = _cfg.Widget.ClickThrough, CheckOnClick = false };
        top.Click += (_, _) => ToggleClickThrough();
        var opacity = new ToolStripMenuItem("透明度");
        foreach (double v in new[] { 1.0, 0.88, 0.7, 0.55, 0.4 })
        {
            double copy = v;
            var it = new ToolStripMenuItem($"{copy * 100:0}%") { Checked = Math.Abs(_cfg.Widget.Opacity - copy) < 0.01 };
            it.Click += (_, _) => ApplyOpacity(copy, save: true);
            opacity.DropDownItems.Add(it);
        }
        var pause = new ToolStripMenuItem(_isPaused() ? "恢复统计" : "暂停统计");
        pause.Click += (_, _) => _togglePause();
        var settings = new ToolStripMenuItem("设置…");
        settings.Click += (_, _) => _openSettings();
        var open = new ToolStripMenuItem("打开主界面");
        open.Click += (_, _) => _openMain();
        var exit = new ToolStripMenuItem("退出 KeyMouseCounter");
        exit.Click += (_, _) => _exitApp();

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(hide);
        menu.Items.Add(top);
        menu.Items.Add(opacity);
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        menu.Show(this, at);
    }

    public void ToggleClickThrough()
    {
        _cfg.Widget.ClickThrough = !_cfg.Widget.ClickThrough;
        ApplyClickThrough(_cfg.Widget.ClickThrough);
        try { _watch.Save(); } catch (Exception ex) { Log.Warn($"保存穿透设置失败：{ex.Message}"); }
    }

    private void ApplyClickThrough(bool on)
    {
        // ExStyle 只在创建时生效，改完必须销毁重建（§15.5 坑 12）
        if (IsHandleCreated) RecreateHandle();
        Invalidate();
    }

    public void HideWidget()
    {
        _cfg.Widget.Enabled = false;
        Hide();
        _tick.Stop();
        try { _watch.Save(); } catch { }
    }

    public void ShowWidget(bool firstFade)
    {
        _cfg.Widget.Enabled = true;
        ApplySizeAndFont();
        ApplyPosition(_cfg.Widget.X, _cfg.Widget.Y, firstRun: firstFade);
        ApplyOpacity(_cfg.Widget.Opacity, save: false);
        Show();
        _tick.Start();
        Refresh1s();
        if (firstFade) StartFadeIn();
    }

    public void ApplyOpacity(double value, bool save)
    {
        double v = Math.Clamp(value, 0.4, 1.0);
        _cfg.Widget.Opacity = v;
        Opacity = v;
        if (save && !_ctrWheel)
        {
            try { _watch.Save(); } catch (Exception ex) { Log.Warn($"保存透明度失败：{ex.Message}"); }
        }
        else if (save)
        {
            try { _watch.Save(); } catch { }
        }
    }

    /// <summary>启动越界校正：不在任何屏幕可见区内就归位主屏（§5.9 / §15.5 坑 14）。</summary>
    private void ApplyPosition(int x, int y, bool firstRun)
    {
        var rect = new Rectangle(x, y, Width, Height);
        var areas = new List<Rectangle>();
        foreach (var s in Screen.AllScreens) areas.Add(s.WorkingArea);
        var primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

        var p = WidgetGeometry.EnsureVisible(rect, areas, primary, firstRun);
        _cfg.Widget.X = p.X;
        _cfg.Widget.Y = p.Y;
        Location = p;
        if (firstRun)
        {
            try { _watch.Save(); } catch { }
        }
    }

    private void SavePosition()
    {
        _cfg.Widget.X = Location.X;
        _cfg.Widget.Y = Location.Y;
        try { _watch.Save(); } catch (Exception ex) { Log.Warn($"保存挂件位置失败：{ex.Message}"); }
    }

    private void StartFadeIn()
    {
        _fading = true;
        _fadeStep = 0;
        Opacity = 0;
        _fade.Start();
    }

    private void FadeStep()
    {
        if (!_fading) { _fade.Stop(); return; }
        _fadeStep += 40.0 / 3000.0;
        if (_fadeStep >= 1)
        {
            _fadeStep = 1;
            _fading = false;
            _fade.Stop();
        }
        Opacity = Math.Clamp(_cfg.Widget.Opacity * _fadeStep, 0.02, 1.0);
    }

    private void Refresh1s()
    {
        if (!Visible && _cfg.Widget.Enabled) return;
        try
        {
            var data = _provider();
            if (!data.Equals(_last))
            {
                _last = data;
                Invalidate();
            }
            else if (_isPaused() != _last.Paused)
            {
                _last = data with { Paused = _isPaused() };
                Invalidate();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"挂件刷新失败：{ex.Message}");
        }
    }

    /// <summary>外部（口径/字号/主题变化）调用，重新取一次配置。</summary>
    public void ApplyConfig()
    {
        ApplySizeAndFont();
        ApplyOpacity(_cfg.Widget.Opacity, save: false);
        if (IsHandleCreated) RecreateHandle();
        Refresh1s();
        Invalidate();
    }

    /// <summary>立即重取一次数字（暂停/恢复、数据变化时调用）。</summary>
    public void RefreshNow()
    {
        if (!Visible) return;
        Refresh1s();
        Invalidate();
    }

    /// <summary>主题变化时重绘。</summary>
    public void ApplyTheme() => Invalidate();

    /// <summary>写盘受阻等异常状态的提示（§9.2 挂件显示 ⚠）。</summary>
    public void SetWarning(string? text)
    {
        if (string.Equals(_warning, text, StringComparison.Ordinal)) return;
        _warning = text;
        Invalidate();
    }

    /// <summary>把挂件挪回主屏 (40,40)（设置页"重置位置"）。</summary>
    public void ResetPosition()
    {
        ApplyPosition(40, 40, firstRun: true);
        _watch.Save();
    }

    /// <summary>真正关闭（退出流程用），不再走"隐藏到托盘"。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        try { _tick.Stop(); _savePos.Stop(); _fade.Stop(); } catch { }
        try { Close(); } catch { }
    }

    private string? _warning;
    private bool _forceClose;

    /// <summary>挂件是常驻窗口：用户 Alt+F4/关闭只隐藏，只有退出流程的 ForceClose 才真正关闭。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideWidget();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (IsHandleCreated) ApplyClickThroughStyle();
    }

    private void ApplyClickThroughStyle()
    {
        try
        {
            if (!_cfg.Widget.ClickThrough) return;
            // CreateParams 已经带上 ExStyle，这里只兜底设置一次
            var style = NativeMethods.GetWindowLong(Handle, (-20));
            NativeMethods.SetWindowLong(Handle, (-20),
                style | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _savePos.Dispose();
            _fade.Dispose();
        }
        base.Dispose(disposing);
    }
}

using System.Drawing;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Native;

namespace KeyMouseCounter.UI;

/// <summary>托盘图标与菜单（§7.1 关闭 = 隐藏到托盘；§8.4 退出走托盘）。</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly ContextMenuStrip _menu = new() { Renderer = new DarkMenuRenderer() };
    private readonly Action _openMain;
    private readonly Action _toggleWidget;
    private readonly Action _togglePause;
    private readonly Action _openDataDir;
    private readonly Action _export;
    private readonly Action _openSettings;
    private readonly Action _purgeToday;
    private readonly Action _about;
    private readonly Action _exit;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _widgetVisible;

    private ToolStripMenuItem? _pauseItem;
    private ToolStripMenuItem? _widgetItem;

    public TrayIcon(Action openMain, Action toggleWidget, Action togglePause, Action openDataDir,
                    Action export, Action openSettings, Action purgeToday, Action about, Action exit,
                    Func<bool> isPaused, Func<bool> widgetVisible)
    {
        _openMain = openMain;
        _toggleWidget = toggleWidget;
        _togglePause = togglePause;
        _openDataDir = openDataDir;
        _export = export;
        _openSettings = openSettings;
        _purgeToday = purgeToday;
        _about = about;
        _exit = exit;
        _isPaused = isPaused;
        _widgetVisible = widgetVisible;

        _icon.Icon = AppIcon.Create();
        _icon.Text = "KeyMouseCounter";
        _icon.Visible = true;
        _icon.DoubleClick += (_, _) => _openMain();

        _menu.Opening += (_, _) => Rebuild();
        _icon.ContextMenuStrip = _menu;
        Rebuild();
    }

    private void Rebuild()
    {
        _menu.Items.Clear();

        var open = new ToolStripMenuItem("打开主界面");
        open.Click += (_, _) => Guard(_openMain);
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());

        _pauseItem = new ToolStripMenuItem(_isPaused() ? "恢复统计" : "暂停统计");
        _pauseItem.Click += (_, _) => Guard(_togglePause);
        _menu.Items.Add(_pauseItem);

        _widgetItem = new ToolStripMenuItem("显示挂件") { Checked = _widgetVisible() };
        _widgetItem.Click += (_, _) => Guard(_toggleWidget);
        _menu.Items.Add(_widgetItem);

        var export = new ToolStripMenuItem("导出 CSV…");
        export.Click += (_, _) => Guard(_export);
        _menu.Items.Add(export);

        var purge = new ToolStripMenuItem("清空今日数据");
        purge.Click += (_, _) => Guard(_purgeToday);
        _menu.Items.Add(purge);

        _menu.Items.Add(new ToolStripSeparator());

        var dir = new ToolStripMenuItem("打开数据目录");
        dir.Click += (_, _) => Guard(_openDataDir);
        _menu.Items.Add(dir);

        var settings = new ToolStripMenuItem("设置…");
        settings.Click += (_, _) => Guard(_openSettings);
        _menu.Items.Add(settings);

        var about = new ToolStripMenuItem("关于");
        about.Click += (_, _) => Guard(_about);
        _menu.Items.Add(about);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => Guard(_exit);
        _menu.Items.Add(exit);
    }

    private static void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("托盘菜单动作失败", ex); }
    }

    /// <summary>热键注册失败等场景的气泡提示（§5.8）。</summary>
    public void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = icon;
            _icon.ShowBalloonTip(5000);
        }
        catch { }
    }

    public void SetTooltip(string text)
    {
        try { _icon.Text = text.Length > 63 ? text[..63] : text; } catch { }
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
        catch { }
    }
}

/// <summary>程序图标：运行时画一个（不依赖外部 ico 资源，单文件发布更省事）。</summary>
internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Create()
    {
        if (_cached is not null) return _cached;
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var bg = new SolidBrush(Color.FromArgb(0xFF, 0x1E, 0x3A, 0x8A));
                g.FillEllipse(bg, 0, 0, 31, 31);
                using var fg = new SolidBrush(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
                g.FillRectangle(fg, 8, 12, 16, 3);
                g.FillRectangle(fg, 8, 18, 16, 3);
                g.FillRectangle(fg, 15, 7, 3, 16);
            }
            // 关键：HICON 的生命周期独立于 Bitmap。之前直接 Icon.FromHandle(bmp.GetHicon()) 后
            // bmp 被 using 释放，托盘/标题栏再画这个 HICON 就可能踩到已释放的位图（§15.5 坑）。
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(hIcon);
                _cached = (Icon)tmp.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
            return _cached;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}

using System.Diagnostics;
using System.Drawing;
using KeyMouseCounter.Config;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.UI;

/// <summary>设置窗口（§7.4）：左侧导航 + 右侧内容，五页全部即时生效。</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppHost _host;
    private readonly AppConfig _cfg;
    private readonly Panel _nav = new();
    private readonly Panel _body = new();
    private readonly List<FlatButton> _navButtons = new();
    private readonly List<Control> _pages = new();

    private static readonly (string Key, string Title)[] Pages =
    {
        ("general", "常规"),
        ("widget", "挂件"),
        ("counting", "计数口径"),
        ("data", "数据"),
        ("about", "关于")
    };

    public SettingsForm(AppHost host, string? initialPage)
    {
        _host = host;
        _cfg = host.Config;

        Text = "设置 · KeyMouseCounter";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        HandleCreated += (_, _) => UiGfx.ApplyDarkTitleBar(this);
        ClientSize = new Size(660, 470);
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Font = Theme.Font(9f);

        _nav.Dock = DockStyle.Left;
        _nav.Width = 132;
        _nav.BackColor = Theme.Current.Window;
        _nav.Padding = new Padding(12, 14, 8, 8);

        _body.Dock = DockStyle.Fill;
        _body.BackColor = Theme.Current.Window;
        _body.Padding = new Padding(14, 12, 14, 10);
        _body.AutoScroll = true;

        Controls.Add(_body);
        Controls.Add(_nav);

        BuildNav();
        BuildPages();

        int index = Array.FindIndex(Pages, p => string.Equals(p.Key, initialPage, StringComparison.OrdinalIgnoreCase));
        SelectPage(index < 0 ? 0 : index);
    }

    private void BuildNav()
    {
        int y = 0;
        for (int i = 0; i < Pages.Length; i++)
        {
            int index = i;
            var b = new FlatButton
            {
                Text = Pages[i].Title,
                Width = 108,
                Height = 34,
                Left = 0,
                Top = y
            };
            b.Click += (_, _) => SelectPage(index);
            _nav.Controls.Add(b);
            _navButtons.Add(b);
            y += 40;
        }
    }

    private void SelectPage(int index)
    {
        for (int i = 0; i < _navButtons.Count; i++)
        {
            var b = _navButtons[i];
            b.BackColor = i == index ? Theme.Current.SurfaceAlt : Theme.Current.Window;
            b.ForeColor = i == index ? Theme.Current.Accent : Theme.Current.Muted;
            b.Invalidate();
        }
        _body.SuspendLayout();
        foreach (var p in _pages) p.Visible = false;
        if (index >= 0 && index < _pages.Count) _pages[index].Visible = true;
        _body.ResumeLayout();
    }

    private void BuildPages()
    {
        _pages.Clear();
        _pages.Add(BuildGeneralPage());
        _pages.Add(BuildWidgetPage());
        _pages.Add(BuildCountingPage());
        _pages.Add(BuildDataPage());
        _pages.Add(BuildAboutPage());
        foreach (var p in _pages)
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            _body.Controls.Add(p);
        }

        var close = new FlatButton { Text = "关闭", Width = 86, Height = 30 };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.Location = new Point(ClientSize.Width - 86 - 14, ClientSize.Height - 30 - 12);
        close.BringToFront();
        AcceptButton = null;
        CancelButton = close;
    }

    // ─────────────────────────── 常规 ───────────────────────────

    private Control BuildGeneralPage()
    {
        var panel = NewPage("常规");

        bool autostart = false;
        bool autostartKnown = true;
        try { autostart = _host.IsAutostartEnabled(out _); }
        catch { autostartKnown = false; }

        var cbAuto = AddCheck(panel, "开机自启（登录后自动开始统计）", autostart, ref _rowY);
        cbAuto.Enabled = autostartKnown;
        cbAuto.CheckedChanged += (_, _) =>
        {
            string? err = _host.SetAutostart(cbAuto.Checked);
            if (err is not null)
            {
                cbAuto.Checked = !cbAuto.Checked;
                Info(panel, "设置开机自启失败：" + err, warn: true);
            }
            else
            {
                Info(panel, cbAuto.Checked ? "已注册计划任务 KeyMouseCounter。" : "已取消开机自启。");
            }
        };

        var cbMinimized = AddCheck(panel, "启动时最小化到托盘", _cfg.Ui.StartMinimized, ref _rowY);
        cbMinimized.CheckedChanged += (_, _) =>
        {
            _cfg.Ui.StartMinimized = cbMinimized.Checked;
            Save();
        };

        var cbClose = AddCheck(panel, "关闭主窗口时最小化到托盘（取消勾选 = 关闭即退出）", _cfg.Ui.CloseToTray, ref _rowY);
        cbClose.CheckedChanged += (_, _) =>
        {
            _cfg.Ui.CloseToTray = cbClose.Checked;
            Save();
        };

        AddLabel(panel, "主题", ref _rowY);
        var theme = AddCombo(panel, new[] { "深色", "浅色" },
            string.Equals(_cfg.Ui.Theme, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0, ref _rowY, 150);
        theme.SelectedIndexChanged += (_, _) =>
        {
            _host.ApplyTheme(theme.SelectedIndex == 1 ? "light" : "dark");
        };

        AddLabel(panel, "语言", ref _rowY);
        var lang = AddCombo(panel, new[] { "简体中文（当前仅支持）" }, 0, ref _rowY, 220);
        lang.Enabled = false;

        Info(panel, "提示：开机自启用 Windows 计划任务实现，不需要管理员权限。");
        return panel;
    }

    // ─────────────────────────── 挂件 ───────────────────────────

    private Control BuildWidgetPage()
    {
        var panel = NewPage("挂件");

        var cbEnabled = AddCheck(panel, "启用桌面挂件", _cfg.Widget.Enabled, ref _rowY);
        cbEnabled.CheckedChanged += (_, _) =>
        {
            _cfg.Widget.Enabled = cbEnabled.Checked;
            Save();
            _host.ApplyWidgetConfig();
        };

        var cbClickThrough = AddCheck(panel, "穿透点击（鼠标事件透过挂件，不影响下层窗口）", _cfg.Widget.ClickThrough, ref _rowY);
        cbClickThrough.CheckedChanged += (_, _) =>
        {
            _cfg.Widget.ClickThrough = cbClickThrough.Checked;
            Save();
            _host.ApplyWidgetConfig();
        };

        AddLabel(panel, "透明度", ref _rowY);
        var slider = new TrackBar
        {
            Minimum = 40,
            Maximum = 100,
            TickFrequency = 10,
            Value = (int)Math.Round(Math.Clamp(_cfg.Widget.Opacity, 0.4, 1.0) * 100),
            Width = 240,
            Height = 30,
            BackColor = Theme.Current.Window
        };
        var opacityText = new Label
        {
            Left = 260,
            Top = _rowY + 6,
            Width = 60,
            ForeColor = Theme.Current.Muted,
            Text = slider.Value + "%"
        };
        slider.Left = 96;
        slider.Top = _rowY;
        panel.Controls.Add(slider);
        panel.Controls.Add(opacityText);
        _rowY += 36;
        slider.ValueChanged += (_, _) =>
        {
            _cfg.Widget.Opacity = slider.Value / 100.0;
            opacityText.Text = slider.Value + "%";
            Save();
            _host.ApplyWidgetConfig();
        };

        AddLabel(panel, "字号", ref _rowY);
        var font = AddNumeric(panel, (decimal)_cfg.Widget.FontSize, 8, 18, ref _rowY, 80);
        font.ValueChanged += (_, _) =>
        {
            _cfg.Widget.FontSize = (int)font.Value;
            Save();
            _host.ApplyWidgetConfig();
        };

        var cbActive = AddCheck(panel, "在挂件上显示活跃时长", _cfg.Widget.ShowActiveTime, ref _rowY);
        cbActive.CheckedChanged += (_, _) =>
        {
            _cfg.Widget.ShowActiveTime = cbActive.Checked;
            Save();
            _host.ApplyWidgetConfig();
        };

        AddLabel(panel, "全局热键", ref _rowY);
        var hotkeyBox = new TextBox
        {
            Left = 96,
            Top = _rowY,
            Width = 150,
            Text = _cfg.Widget.Hotkey,
            BackColor = Theme.Current.SurfaceAlt,
            ForeColor = Theme.Current.Fore,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(hotkeyBox);
        var applyHotkey = new FlatButton { Text = "应用", Width = 56, Height = 26, Left = 252, Top = _rowY - 1 };
        panel.Controls.Add(applyHotkey);
        var hotkeyState = new Label { Left = 318, Top = _rowY + 3, Width = 220, ForeColor = Theme.Current.Muted };
        panel.Controls.Add(hotkeyState);
        _rowY += 34;
        applyHotkey.Click += (_, _) =>
        {
            string spec = hotkeyBox.Text.Trim();
            if (string.IsNullOrEmpty(spec))
            {
                _host.SetHotkey("");
                hotkeyState.Text = "已禁用热键";
                return;
            }
            if (!Native.HotkeyParser.TryParse(spec, out _, out _, out string? parseErr))
            {
                hotkeyState.Text = parseErr ?? "格式不正确";
                return;
            }
            string? err = _host.SetHotkey(spec);
            hotkeyState.Text = err is null ? "已生效" : err;
        };
        hotkeyBox.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(hotkeyBox.Text)) { hotkeyState.Text = ""; return; }
            bool ok = Native.HotkeyParser.TryParse(hotkeyBox.Text, out _, out _, out string? e1);
            if (!ok) { hotkeyState.Text = e1 ?? "格式不正确"; return; }
            hotkeyState.Text = AppHost.IsHotkeyAvailable(hotkeyBox.Text, out string? e2) ? "" : (e2 ?? "已被占用");
        };

        var reset = new FlatButton { Text = "重置挂件位置", Width = 120, Height = 28, Left = 96, Top = _rowY };
        reset.Click += (_, _) => { _host.ResetWidgetPosition(); hotkeyState.Text = ""; };
        panel.Controls.Add(reset);
        _rowY += 38;

        Info(panel, "热键格式示例：Ctrl+Alt+K、Ctrl+Shift+F9。挂件上也可右键打开菜单调整透明度。");
        return panel;
    }

    // ─────────────────────────── 计数口径 ───────────────────────────

    private Control BuildCountingPage()
    {
        var panel = NewPage("计数口径");

        var cbRepeat = AddCheck(panel, "统计长按自动重复（默认不统计）", _cfg.Counting.CountAutoRepeat, ref _rowY);
        cbRepeat.CheckedChanged += (_, _) => { _cfg.Counting.CountAutoRepeat = cbRepeat.Checked; Save(); };

        var cbModifier = AddCheck(panel, "统计修饰键（Shift/Ctrl/Alt/Win）", _cfg.Counting.CountModifierKeys, ref _rowY);
        cbModifier.CheckedChanged += (_, _) => { _cfg.Counting.CountModifierKeys = cbModifier.Checked; Save(); };

        var cbInjected = AddCheck(panel, "忽略软件模拟的输入（推荐勾选）", _cfg.Counting.IgnoreInjected, ref _rowY);
        cbInjected.CheckedChanged += (_, _) => { _cfg.Counting.IgnoreInjected = cbInjected.Checked; Save(); };

        var cbWheel = AddCheck(panel, "统计滚轮", _cfg.Counting.CountWheel, ref _rowY);
        cbWheel.CheckedChanged += (_, _) => { _cfg.Counting.CountWheel = cbWheel.Checked; Save(); };

        var cbSelf = AddCheck(panel, "排除本程序自身的窗口操作", _cfg.Counting.ExcludeSelf, ref _rowY);
        cbSelf.CheckedChanged += (_, _) => { _cfg.Counting.ExcludeSelf = cbSelf.Checked; Save(); };

        AddLabel(panel, "空闲阈值(秒)", ref _rowY);
        var idle = AddNumeric(panel, _cfg.Counting.IdleThresholdSec, 5, 600, ref _rowY, 90);
        idle.ValueChanged += (_, _) => { _cfg.Counting.IdleThresholdSec = (int)idle.Value; Save(); };

        Info(panel, "相邻两次输入的间隔 ≤ 空闲阈值时，该间隔计入活跃时长。", warn: false);
        Info(panel, "口径变更只影响之后的数据，已记录的历史不会被重算。", warn: true);
        return panel;
    }

    // ─────────────────────────── 数据 ───────────────────────────

    private Control BuildDataPage()
    {
        var panel = NewPage("数据");

        AddLabel(panel, "数据目录", ref _rowY);
        var dirBox = new TextBox
        {
            Left = 96,
            Top = _rowY,
            Width = 330,
            Text = AppPaths.DataDir,
            ReadOnly = true,
            BackColor = Theme.Current.SurfaceAlt,
            ForeColor = Theme.Current.Muted,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(dirBox);
        var openDir = new FlatButton { Text = "打开", Width = 60, Height = 26, Left = 434, Top = _rowY - 1 };
        openDir.Click += (_, _) => _host.OpenDataDir();
        panel.Controls.Add(openDir);
        _rowY += 34;

        AddLabel(panel, "保留天数", ref _rowY);
        var retain = AddNumeric(panel, _cfg.Storage.DataRetentionDays, 0, 3650, ref _rowY, 90);
        var retainHint = new Label
        {
            Left = 196,
            Top = _rowY - 22,
            Width = 380,
            ForeColor = Theme.Current.Muted,
            Text = _cfg.Storage.DataRetentionDays == 0 ? "0 = 永久保留" : $"自动删除 {_cfg.Storage.DataRetentionDays} 天前的数据"
        };
        panel.Controls.Add(retainHint);
        retain.ValueChanged += (_, _) =>
        {
            _cfg.Storage.DataRetentionDays = (int)retain.Value;
            retainHint.Text = retain.Value == 0 ? "0 = 永久保留" : $"自动删除 {retain.Value:0} 天前的数据";
            Save();
        };

        AddLabel(panel, "导出", ref _rowY);
        var export = new FlatButton { Text = "导出 CSV…", Width = 110, Height = 28, Left = 96, Top = _rowY };
        export.Click += (_, _) => { Close(); _host.ShowMain(openExport: true); };
        panel.Controls.Add(export);
        _rowY += 38;

        AddLabel(panel, "清空", ref _rowY);
        int y = _rowY;
        var purgeToday = Danger(new FlatButton { Text = "清空今日", Width = 100, Height = 28, Left = 96, Top = y });
        purgeToday.Click += (_, _) =>
        {
            if (!ConfirmDialog.Ask(this, "清空今日数据",
                "将删除今天的全部统计数据（内存 + 数据库 + 今日日志文件），此操作不可撤销。\r\n请输入 DELETE 确认。"))
                return;
            var r = _host.PurgeDayNow(DateOnly.FromDateTime(DateTime.Today));
            Info(panel, $"已清空今日：删除 {r.FilesRemoved} 个日志文件。");
        };
        var purgeRange = Danger(new FlatButton { Text = "清空区间…", Width = 100, Height = 28, Left = 204, Top = y });
        purgeRange.Click += (_, _) => PurgeRangeDialog();
        var purgeAll = Danger(new FlatButton { Text = "清空全部…", Width = 100, Height = 28, Left = 312, Top = y });
        purgeAll.Click += (_, _) => PurgeAllDialog();
        panel.Controls.Add(purgeToday);
        panel.Controls.Add(purgeRange);
        panel.Controls.Add(purgeAll);
        _rowY += 38;

        AddLabel(panel, "维护", ref _rowY);
        var rebuild = new FlatButton { Text = "从日志重建数据库", Width = 150, Height = 28, Left = 96, Top = _rowY };
        rebuild.Click += (_, _) =>
        {
            if (!ConfirmDialog.Ask(this, "从日志重建数据库",
                "将按 JSONL 日志重新计算数据库里的分钟统计（日志是权威数据源）。\r\n" +
                "数据量较大时可能需要几十秒，期间统计继续进行。\r\n请输入 DELETE 确认。", requireWord: false))
                return;
            try
            {
                var report = _host.RebuildFromLogs();
                Info(panel, $"重建完成：扫描 {report.DaysScanned} 天，全量 {report.DaysFullImported}，" +
                            $"增量 {report.DaysIncremental}，跳过 {report.DaysSkippedGap}，失败 {report.DaysFailed}。");
            }
            catch (Exception ex)
            {
                Log.Error("从日志重建失败", ex);
                Info(panel, "重建失败：" + ex.Message, warn: true);
            }
        };
        panel.Controls.Add(rebuild);
        _rowY += 38;

        Info(panel, "数据只保存在本机，不联网；删除后无法恢复，请先导出需要保留的数据。");
        return panel;
    }

    private void PurgeRangeDialog()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        using var dlg = new RangeDialog(today.AddDays(-6), today);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var (from, to) = (dlg.FromDate, dlg.ToDate);
        if (!ConfirmDialog.Ask(this, "清空区间数据",
            $"将删除 {from:yyyy-MM-dd} ~ {to:yyyy-MM-dd} 的全部统计数据，此操作不可撤销。\r\n请输入 DELETE 确认。"))
            return;
        var r = _host.PurgeRangeNow(from, to);
        Info(CurrentPage(), $"已清空区间：删除 {r.DaysRemoved} 天、{r.FilesRemoved} 个文件。");
    }

    private void PurgeAllDialog()
    {
        if (!ConfirmDialog.Ask(this, "清空全部数据",
            "这会删除所有统计数据（数据库全部行 + 所有日志文件），此操作不可撤销。\r\n请输入 DELETE 确认。"))
            return;
        var r = _host.PurgeAllNow();
        Info(CurrentPage(), $"已清空全部：删除 {r.DaysRemoved} 天、{r.FilesRemoved} 个文件。");
    }

    private Control CurrentPage()
        => _pages.Count > 3 ? _pages[3] : this;

    // ─────────────────────────── 关于 ───────────────────────────

    private Control BuildAboutPage()
    {
        var panel = NewPage("关于");

        var title = new Label
        {
            Text = "KeyMouseCounter  " + AppPaths.Version,
            Font = Theme.Font(14f, FontStyle.Bold),
            ForeColor = Theme.Current.Fore,
            AutoSize = true,
            Left = 0,
            Top = _rowY
        };
        panel.Controls.Add(title);
        _rowY += 36;

        var uptime = _host.Uptime;
        string up = uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours} 小时 {uptime.Minutes} 分钟"
            : $"{uptime.Minutes} 分钟";
        int lines = _host.TodayLogLines;

        foreach (var (k, v) in new[]
        {
            ("运行时长", up),
            ("今日日志行数", UiText.N(lines) + " 行"),
            ("日志路径", AppPaths.LogDir),
            ("数据路径", AppPaths.DataDir),
            ("配置文件", AppPaths.ConfigPath),
            ("运行时", $".NET {Environment.Version}"),
            ("系统", Environment.OSVersion.VersionString)
        })
        {
            AddLabel(panel, k, ref _rowY);
            var val = new Label
            {
                Text = v,
                Left = 96,
                Top = _rowY - 18,
                Width = 400,
                ForeColor = Theme.Current.Muted
            };
            panel.Controls.Add(val);
            _rowY += 26;
        }

        _rowY += 6;
        Info(panel, "隐私说明：本程序只统计按键和点击的「次数」，不记录任何按键内容、窗口标题或输入文本；");
        Info(panel, "不联网、不上传任何数据；所有数据保存在本机，可随时清空或随程序一并删除。");
        return panel;
    }

    // ─────────────────────────── 小工具 ───────────────────────────

    private int _rowY;

    private Panel NewPage(string title)
    {
        _rowY = 0;
        var page = new Panel { BackColor = Theme.Current.Window, AutoScroll = false };
        var head = new Label
        {
            Text = title,
            Font = Theme.Font(12f, FontStyle.Bold),
            ForeColor = Theme.Current.Fore,
            AutoSize = true,
            Left = 0,
            Top = _rowY
        };
        page.Controls.Add(head);
        _rowY += 36;
        return page;
    }

    private static void AddLabel(Control panel, string text, ref int y)
    {
        panel.Controls.Add(new Label
        {
            Text = text,
            Left = 0,
            Top = y + 4,
            Width = 92,
            Height = 20,
            ForeColor = Theme.Current.Muted
        });
    }

    private static CheckBox AddCheck(Control panel, string text, bool value, ref int y)
    {
        var cb = new CheckBox
        {
            Text = text,
            Checked = value,
            Left = 0,
            Top = y,
            AutoSize = true,
            ForeColor = Theme.Current.Fore,
            BackColor = Color.Transparent
        };
        panel.Controls.Add(cb);
        y += 32;
        return cb;
    }

    private static ComboBox AddCombo(Control panel, string[] items, int selected, ref int y, int width)
    {
        var cb = new ComboBox
        {
            Left = 96,
            Top = y,
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Theme.Current.SurfaceAlt,
            ForeColor = Theme.Current.Fore,
            FlatStyle = FlatStyle.Flat
        };
        cb.Items.AddRange(items);
        cb.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        panel.Controls.Add(cb);
        y += 34;
        return cb;
    }

    private static NumericUpDown AddNumeric(Control panel, decimal value, int min, int max, ref int y, int width)
    {
        var n = new NumericUpDown
        {
            Left = 96,
            Top = y,
            Width = width,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            BackColor = Theme.Current.SurfaceAlt,
            ForeColor = Theme.Current.Fore,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(n);
        y += 34;
        return n;
    }

    private Label? _lastInfo;

    private void Info(Control panel, string text, bool warn = false)
    {
        var lb = new Label
        {
            Text = text,
            Left = 0,
            Top = _rowY,
            Width = 470,
            ForeColor = warn ? Theme.Current.Warning : Theme.Current.Muted
        };
        panel.Controls.Add(lb);
        _lastInfo = lb;
        _rowY += 22;
    }

    private static FlatButton Danger(FlatButton b)
    {
        b.ForeColor = Theme.Current.Danger;
        return b;
    }

    private void Save()
    {
        try
        {
            _cfg.Normalize();
            _host.SaveConfig();
        }
        catch (Exception ex) { Log.Error("保存设置失败", ex); }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _ = _lastInfo;
    }
}

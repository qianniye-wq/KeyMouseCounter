using System.Diagnostics;
using System.Drawing;
using KeyMouseCounter.Charts;
using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Services;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.UI;

/// <summary>主窗口（§7.1）：顶栏 + 三张卡 + 三标签 + 三视图 + 底部摘要。</summary>
internal sealed class MainForm : Form
{
    private static readonly int[] QuickDays = { 1, 7, 14, 30, 90 };

    private readonly AppConfig _cfg;
    private readonly ConfigWatcher _watch;
    private readonly FlushService _flush;
    private readonly StatsQuery _query;
    private readonly CounterEngine _engine;
    private readonly AppHost? _host;

    // 顶栏
    private readonly Label _title = new();
    private readonly SegmentedTabs _tabs = new();
    private readonly DropDownButton _dateBtn = new();
    private readonly Label _pausedBadge = new();
    private readonly FlatButton _pauseBtn = new();
    private readonly FlatButton _exportBtn = new();
    private readonly FlatButton _refreshBtn = new();
    private readonly FlatButton _settingsBtn = new();

    // 卡片
    private readonly StatCard _cardKey = new("键盘按键");
    private readonly StatCard _cardClick = new("鼠标点击");
    private readonly StatCard _cardActive = new("活跃时长");

    // 工具栏
    private readonly Label _rangeLabel = new();
    private readonly DropDownButton _rangeBtn = new();
    private readonly DropDownButton _metricBtn = new();
    private readonly SegmentedTabs _viewTabs = new();
    private readonly CheckBox _showAll = new();
    private readonly CheckBox _asBar = new();
    private readonly DateTimePicker _fromPicker = new();
    private readonly DateTimePicker _toPicker = new();
    private readonly Label _customRangeHint = new();
    private readonly Label _rowCount = new();

    // 图表区
    private readonly Panel _chartHost = new();
    private readonly LineChartView _lineView = new();
    private readonly HeatmapView _heatView = new();
    private readonly TableChartView _tableView = new();

    // 底部
    private readonly Label _summary = new();
    private readonly Label _status = new();

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly CancellationTokenSource _cts = new();

    private DateOnly _day;
    private int _rangeDays;
    private DateOnly _from, _to;
    private Metric _metric = Metric.Key;
    private ViewKind _view = ViewKind.Line;
    private bool _suppressRefresh;
    private bool _busy;
    private bool _dirty = true;
    private TabKind _startTab = TabKind.TodayHour;

    private enum ViewKind { Line = 0, Heat = 1, Table = 2 }

    private enum TabKind { TodayHour = 0, TodayMinute = 1, MultiDay = 2 }

    public MainForm(AppHost host)
    {
        _cfg = host.Config;
        _watch = host.ConfigWatcher;
        _flush = host.Flusher;
        _query = host.Query;
        _engine = host.Engine;
        _host = host;


        var today = DateOnly.FromDateTime(DateTime.Today);
        _day = today;
        _rangeDays = Math.Clamp(_cfg.Ui.DefaultRangeDays, 1, 366);
        _from = _day.AddDays(-(_rangeDays - 1));
        _to = _day;
        _metric = Metric.Key;
        _view = _cfg.Ui.DefaultChart?.ToLowerInvariant() switch
        {
            "heat" or "heatmap" => ViewKind.Heat,
            "table" => ViewKind.Table,
            _ => ViewKind.Line
        };
        _startTab = _cfg.Ui.DefaultTab?.ToLowerInvariant() switch
        {
            "todayminute" => TabKind.TodayMinute,
            "multiday" => TabKind.MultiDay,
            _ => TabKind.TodayHour
        };

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "KeyMouseCounter";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        ClientSize = new Size(Math.Max(820, _cfg.Ui.LastMainWindowSize?[0] ?? 1000),
                              Math.Max(560, _cfg.Ui.LastMainWindowSize?[1] ?? 700));
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Font = Theme.Font(9f);
        KeyPreview = true;

        BuildLayout();

        ResumeLayout(true);

        Theme.Changed += OnThemeChanged;
        _timer.Tick += (_, _) =>
        {
            if (!Visible) return;
            // 今日视图固定 5s 刷新（§7.1）；多日视图只有数据变动时才重查，省一次全表扫描
            if (_tabs.SelectedIndex == (int)TabKind.MultiDay && !_dirty) return;
            RefreshData(flushFirst: false);
        };
        Load += OnLoaded;
        ResizeEnd += (_, _) => PersistSize();
    }

    private void OnThemeChanged()
    {
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        ApplyControlColors(this);
        _tableView.ApplyTheme();
        Invalidate(true);
        _dirty = true;
    }

    private static void ApplyControlColors(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case StatCard or SegmentedTabs or DropDownButton or FlatButton or CardPanel:
                    break;
                case CheckBox cb:
                    cb.ForeColor = Theme.Current.Fore;
                    cb.BackColor = Color.Transparent;
                    break;
                case Label l:
                    if (l.BackColor != Color.Transparent && l.Parent is not null && l.Parent.BackColor != Color.Transparent)
                        l.BackColor = Theme.Current.Window;
                    break;
                case DateTimePicker dtp:
                    dtp.CalendarMonthBackground = Theme.Current.Surface;
                    break;
            }
            if (c.HasChildren) ApplyControlColors(c);
        }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14, 10, 14, 10),
            BackColor = Color.Transparent
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 顶栏
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));  // 卡片
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // 工具栏
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 图表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));   // 摘要

        root.Controls.Add(BuildTopBar(), 0, 0);
        root.Controls.Add(BuildCards(), 0, 1);
        root.Controls.Add(BuildToolbar(), 0, 2);
        root.Controls.Add(BuildChartArea(), 0, 3);
        root.Controls.Add(BuildFooter(), 0, 4);
        Controls.Add(root);
    }

    private Control BuildTopBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        _title.Text = "KeyMouseCounter";
        _title.Font = Theme.Font(13f, FontStyle.Bold);
        _title.ForeColor = Theme.Current.Fore;
        _title.AutoSize = false;
        _title.Dock = DockStyle.Left;
        _title.Width = 148;
        _title.TextAlign = ContentAlignment.MiddleLeft;

        _tabs.SetItems("今日·按小时", "今日·按分钟", "多日");
        _tabs.Dock = DockStyle.Left;
        _tabs.Width = 306;
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            _suppressRefresh = true;
            try
            {
                switch ((TabKind)_tabs.SelectedIndex)
                {
                    case TabKind.TodayHour:
                        _day = MinDay(_day, DateOnly.FromDateTime(DateTime.Today));
                        break;
                    case TabKind.TodayMinute:
                        _day = MinDay(_day, DateOnly.FromDateTime(DateTime.Today));
                        break;
                    case TabKind.MultiDay:
                        _to = _day;
                        _from = _to.AddDays(-(_rangeDays - 1));
                        break;
                }
                SyncToolbarToTab();
            }
            finally
            {
                _suppressRefresh = false;
            }
            RefreshData(flushFirst: true);
        };

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 0)
        };

        ConfigureIconButton(_settingsBtn, "⚙", 36, "设置");
        _settingsBtn.Click += (_, _) => _host?.OpenSettings(null);

        ConfigureTextButton(_exportBtn, "导出", 62, "导出 CSV");
        _exportBtn.Click += (_, _) => RunExport();

        ConfigureTextButton(_pauseBtn, "⏸ 暂停", 86, "暂停/恢复统计（Ctrl+Alt+K）");
        _pauseBtn.Click += (_, _) => _host?.TogglePausePublic();

        ConfigureTextButton(_refreshBtn, "刷新", 56, "立即刷盘并重绘");
        _refreshBtn.Click += (_, _) => RefreshData(flushFirst: true);

        _dateBtn.Width = 168;
        _dateBtn.Height = 30;
        _dateBtn.Margin = new Padding(8, 0, 8, 0);
        _dateBtn.Click += (_, _) => ShowDateMenu();

        _pausedBadge.Text = "Ⅱ";
        _pausedBadge.ForeColor = Theme.Current.Danger;
        _pausedBadge.AutoSize = false;
        _pausedBadge.Width = 52;
        _pausedBadge.Height = 30;
        _pausedBadge.TextAlign = ContentAlignment.MiddleCenter;
        _pausedBadge.Font = Theme.Font(9f, FontStyle.Bold);
        _pausedBadge.Margin = new Padding(6, 0, 0, 0);

        right.Controls.Add(_settingsBtn);
        right.Controls.Add(_refreshBtn);
        right.Controls.Add(_exportBtn);
        right.Controls.Add(_pauseBtn);
        right.Controls.Add(_pausedBadge);
        right.Controls.Add(_dateBtn);

        bar.Controls.Add(right);
        bar.Controls.Add(_tabs);
        bar.Controls.Add(_title);
        return bar;
    }

    private static void ConfigureIconButton(FlatButton b, string text, int width, string tip)
    {
        b.Text = text;
        b.Width = width;
        b.Height = 30;
        b.Font = Theme.Font(12f);
        b.Margin = new Padding(6, 0, 0, 0);
        new ToolTip().SetToolTip(b, tip);
    }

    private static void ConfigureTextButton(FlatButton b, string text, int width, string tip)
    {
        b.Text = text;
        b.Width = width;
        b.Height = 30;
        b.Margin = new Padding(6, 0, 0, 0);
        new ToolTip().SetToolTip(b, tip);
    }

    private Control BuildCards()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 10)
        };
        for (int i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        foreach (var (card, margin) in new[]
                 {
                     (_cardKey, new Padding(0, 0, 8, 0)),
                     (_cardClick, new Padding(4, 0, 4, 0)),
                     (_cardActive, new Padding(8, 0, 0, 0))
                 })
        {
            card.Dock = DockStyle.Fill;
            card.Margin = margin;
        }

        row.Controls.Add(_cardKey, 0, 0);
        row.Controls.Add(_cardClick, 1, 0);
        row.Controls.Add(_cardActive, 2, 0);
        return row;
    }

    private Control BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        _rangeLabel.Text = "范围";
        _rangeLabel.ForeColor = Theme.Current.Muted;
        _rangeLabel.AutoSize = false;
        _rangeLabel.Dock = DockStyle.Left;
        _rangeLabel.Width = 38;
        _rangeLabel.TextAlign = ContentAlignment.MiddleLeft;

        _rangeBtn.Width = 96;
        _rangeBtn.Dock = DockStyle.Left;
        _rangeBtn.Click += (_, _) => ShowRangeMenu();

        _fromPicker.Format = DateTimePickerFormat.Custom;
        _fromPicker.CustomFormat = "yyyy-MM-dd";
        _fromPicker.Width = 120;
        _fromPicker.Dock = DockStyle.Left;
        _fromPicker.ValueChanged += (_, _) => OnCustomDateChanged();

        _toPicker.Format = DateTimePickerFormat.Custom;
        _toPicker.CustomFormat = "yyyy-MM-dd";
        _toPicker.Width = 120;
        _toPicker.Dock = DockStyle.Left;
        _toPicker.ValueChanged += (_, _) => OnCustomDateChanged();

        _customRangeHint.Text = "—";
        _customRangeHint.ForeColor = Theme.Current.Muted;
        _customRangeHint.Dock = DockStyle.Left;
        _customRangeHint.Width = 20;
        _customRangeHint.TextAlign = ContentAlignment.MiddleCenter;

        _metricBtn.Width = 138;
        _metricBtn.Dock = DockStyle.Left;
        _metricBtn.Click += (_, _) => ShowMetricMenu();

        _showAll.Text = "显示全部";
        _showAll.AutoSize = false;
        _showAll.Width = 92;
        _showAll.Dock = DockStyle.Left;
        _showAll.ForeColor = Theme.Current.Fore;
        _showAll.BackColor = Color.Transparent;
        _showAll.Visible = false;
        _showAll.CheckedChanged += (_, _) => { if (!_suppressRefresh) RefreshData(flushFirst: false); };

        _asBar.Text = "柱状";
        _asBar.AutoSize = false;
        _asBar.Width = 66;
        _asBar.Dock = DockStyle.Left;
        _asBar.ForeColor = Theme.Current.Fore;
        _asBar.BackColor = Color.Transparent;
        _asBar.Visible = false;
        _asBar.CheckedChanged += (_, _) => { _dirty = true; if (!_suppressRefresh) RefreshData(flushFirst: false); };

        _rowCount.Text = "";
        _rowCount.ForeColor = Theme.Current.Muted;
        _rowCount.Dock = DockStyle.Left;
        _rowCount.Width = 120;
        _rowCount.TextAlign = ContentAlignment.MiddleLeft;

        _viewTabs.SetItems("曲线", "热力图", "表格");
        _viewTabs.Dock = DockStyle.Right;
        _viewTabs.Width = 260;
        _viewTabs.SelectedIndexChanged += (_, _) =>
        {
            _view = (ViewKind)_viewTabs.SelectedIndex;
            if (!_suppressRefresh) RefreshData(flushFirst: false);
        };

        bar.Controls.Add(_viewTabs);
        bar.Controls.Add(_rowCount);
        bar.Controls.Add(_asBar);
        bar.Controls.Add(_showAll);
        bar.Controls.Add(_metricBtn);
        bar.Controls.Add(_customRangeHint);
        bar.Controls.Add(_toPicker);
        bar.Controls.Add(_fromPicker);
        bar.Controls.Add(_rangeBtn);
        bar.Controls.Add(_rangeLabel);
        return bar;
    }

    private Control BuildChartArea()
    {
        _chartHost.Dock = DockStyle.Fill;
        _chartHost.BackColor = Theme.Current.ChartBack;
        _chartHost.Padding = new Padding(1);
        _chartHost.Resize += (_, _) => { if (Visible) _dirty = true; };
        foreach (var host in new[] { _lineView.Host, _heatView.Host, _tableView.Host })
        {
            host.Dock = DockStyle.None;
            host.Bounds = new Rectangle(6, 6, Math.Max(1, _chartHost.Width - 12), Math.Max(1, _chartHost.Height - 12));
            host.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            host.Visible = false;
            _chartHost.Controls.Add(host);
        }
        var card = new CardPanel { Dock = DockStyle.Fill, Radius = 10, Padding = new Padding(6) };
        card.Controls.Add(_chartHost);
        return card;
    }

    private Control BuildFooter()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(2, 8, 2, 0) };

        _summary.Text = "指标：键盘按键    合计 — · 峰值 — · 日均 —";
        _summary.ForeColor = Theme.Current.Fore;
        _summary.Dock = DockStyle.Top;
        _summary.Height = 20;
        _summary.Font = Theme.Font(9f);
        _summary.TextAlign = ContentAlignment.MiddleLeft;

        _status.Text = "";
        _status.ForeColor = Theme.Current.Muted;
        _status.Dock = DockStyle.Fill;
        _status.Font = Theme.Font(8.5f);
        _status.TextAlign = ContentAlignment.MiddleLeft;

        panel.Controls.Add(_status);
        panel.Controls.Add(_summary);
        return panel;
    }

    private static DateOnly MinDay(DateOnly a, DateOnly b) => a < b ? a : b;

    private void OnLoaded(object? sender, EventArgs e)
    {
        _day = MinDay(_day, DateOnly.FromDateTime(DateTime.Today));
        _tabs.SelectedIndex = (int)_startTab;
        _viewTabs.SelectedIndex = (int)_view;
        SyncToolbarToTab();
        _timer.Start();
        RefreshData(flushFirst: true);
    }

    /// <summary>三个标签各自的工具栏形态（§7.1：多日视图把日期选择换成起止日期 + 快捷天数）。</summary>
    private void SyncToolbarToTab()
    {
        var tab = (TabKind)_tabs.SelectedIndex;
        bool multi = tab == TabKind.MultiDay;
        _suppressRefresh = true;
        try
        {
            _rangeLabel.Visible = multi;
            _rangeBtn.Visible = multi;
            _fromPicker.Visible = multi;
            _toPicker.Visible = multi;
            _customRangeHint.Visible = multi;

            _showAll.Visible = tab == TabKind.TodayMinute && _view == ViewKind.Table;
            _asBar.Visible = _view == ViewKind.Line && !_showAll.Visible;

            if (multi)
            {
                _fromPicker.Value = _from.ToDateTime(TimeOnly.MinValue);
                _toPicker.Value = _to.ToDateTime(TimeOnly.MinValue);
                _customRangeHint.Text = "/";
                _customRangeHint.Width = 14;
            }

            _viewTabs.Enabled = true;
            if (tab == TabKind.TodayHour && _view == ViewKind.Heat)
            {
                // §7.2.2：TodayHour 不做热力图，固定用柱状图
                _view = ViewKind.Line;
                _asBar.Checked = true;
                _viewTabs.SelectedIndex = 0;
            }
            UpdateDateButton();
            UpdateMetricButton();
            UpdateRangeButton();
        }
        finally
        {
            _suppressRefresh = false;
        }
        _dirty = true;
    }

    private static string TabName(TabKind tab) => tab switch
    {
        TabKind.TodayMinute => "今日·按分钟",
        TabKind.MultiDay => "多日·按天",
        _ => "今日·按小时"
    };

    private void UpdateDateButton()
    {
        var tab = (TabKind)_tabs.SelectedIndex;
        var today = DateOnly.FromDateTime(DateTime.Today);
        string text;
        if (tab == TabKind.MultiDay)
        {
            text = $"{_from:MM-dd} ~ {_to:MM-dd}";
        }
        else
        {
            string suffix = _day == today ? "今日" : _day == today.AddDays(-1) ? "昨日" : UiText.Weekday(_day);
            text = $"{_day:yyyy-MM-dd} {suffix} ▾";
        }
        if (_dateBtn.Text != text) _dateBtn.Text = text;
    }

    private void UpdateMetricButton()
    {
        string text = $"指标：{_metric.DisplayName()} ▾";
        if (_metricBtn.Text != text) _metricBtn.Text = text;
    }

    private void UpdateRangeButton()
    {
        bool quick = _rangeDays is 1 or 7 or 14 or 30 or 90;
        string text = quick ? $"最近 {_rangeDays} 天 ▾" : $"{_rangeDays} 天 ▾";
        if (_rangeBtn.Text != text) _rangeBtn.Text = text;
    }

    private void ShowDateMenu()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var items = new List<(string, Action, bool)>
        {
            ($"今日 {today:yyyy-MM-dd} {UiText.Weekday(today)}", () =>
            {
                _day = today;
                _to = today;
                _from = _to.AddDays(-(_rangeDays - 1));
                UpdateDateButton();
                RefreshData(flushFirst: true);
            }, _day == today),
            ($"昨日 {today.AddDays(-1):yyyy-MM-dd} {UiText.Weekday(today.AddDays(-1))}", () =>
            {
                _day = today.AddDays(-1);
                _to = _day;
                _from = _to.AddDays(-(_rangeDays - 1));
                UpdateDateButton();
                RefreshData(flushFirst: true);
            }, _day == today.AddDays(-1))
        };

        using var dlg = new DatePickerDialog(_day, today);
        items.Add(("指定日期…", () =>
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _day = MinDay(dlg.SelectedDate, today);
                _to = _day;
                _from = _to.AddDays(-(_rangeDays - 1));
                UpdateDateButton();
                RefreshData(flushFirst: true);
            }
        }, false));

        var btn = _dateBtn;
        btn.SetItems(items);
        btn.PerformClick();
    }

    private void ShowRangeMenu()
    {
        var items = new List<(string, Action, bool)>();
        foreach (int d in QuickDays)
        {
            int copy = d;
            items.Add(($"最近 {copy} 天", () =>
            {
                _rangeDays = copy;
                _to = _day;
                _from = _to.AddDays(-(copy - 1));
                _suppressRefresh = true;
                _fromPicker.Value = _from.ToDateTime(TimeOnly.MinValue);
                _toPicker.Value = _to.ToDateTime(TimeOnly.MinValue);
                _suppressRefresh = false;
                UpdateRangeButton();
                UpdateDateButton();
                RefreshData(flushFirst: true);
            }, _rangeDays == copy));
        }

        using var dlg = new RangeDialog(_from, _to);
        items.Add(("自定义…", () =>
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _from = dlg.FromDate;
            _to = dlg.ToDate;
            _rangeDays = Math.Min(366, _to.DayNumber - _from.DayNumber + 1);
            _day = _to;
            _suppressRefresh = true;
            _fromPicker.Value = _from.ToDateTime(TimeOnly.MinValue);
            _toPicker.Value = _to.ToDateTime(TimeOnly.MinValue);
            _suppressRefresh = false;
            UpdateRangeButton();
            UpdateDateButton();
            RefreshData(flushFirst: true);
        }, !(_rangeDays is 1 or 7 or 14 or 30 or 90)));

        var btn = _rangeBtn;
        btn.SetItems(items);
        btn.PerformClick();
    }

    private void ShowMetricMenu()
    {
        var items = new List<(string, Action, bool)>();
        foreach (var m in new[] { Metric.Key, Metric.Click, Metric.ActiveSeconds })
        {
            var copy = m;
            items.Add((copy.DisplayName(), () =>
            {
                _metric = copy;
                UpdateMetricButton();
                RefreshData(flushFirst: false);
            }, _metric == copy));
        }
        var btn = _metricBtn;
        btn.SetItems(items);
        btn.PerformClick();
    }

    private void OnCustomDateChanged()
    {
        if (_suppressRefresh) return;
        var from = DateOnly.FromDateTime(_fromPicker.Value);
        var to = DateOnly.FromDateTime(_toPicker.Value);
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber + 1 > 366)
            from = to.AddDays(-365);
        if (from == _from && to == _to) return;
        _from = from;
        _to = to;
        _rangeDays = Math.Min(366, to.DayNumber - from.DayNumber + 1);
        _day = to;
        UpdateRangeButton();
        UpdateDateButton();
        RefreshData(flushFirst: true);
    }

    /// <summary>切到指定日期的今日视图（托盘菜单/挂件用）。</summary>
    public void ShowDay(DateOnly day)
    {
        _day = MinDay(day, DateOnly.FromDateTime(DateTime.Today));
        _to = _day;
        _from = _to.AddDays(-(_rangeDays - 1));
        if (_tabs.SelectedIndex == (int)TabKind.MultiDay) _tabs.SelectedIndex = 0;
        _suppressRefresh = true;
        try
        {
            _fromPicker.Value = _from.ToDateTime(TimeOnly.MinValue);
            _toPicker.Value = _to.ToDateTime(TimeOnly.MinValue);
        }
        finally { _suppressRefresh = false; }
        UpdateDateButton();
        RefreshData(flushFirst: true);
    }

    public void TriggerRefresh() => RefreshData(flushFirst: true);

    public void UpdatePausedUi(bool paused)
    {
        _pauseBtn.Text = paused ? "▶ 恢复" : "⏸ 暂停";
        _pausedBadge.Visible = paused;
        _cardKey.SetPaused(paused);
        _cardClick.SetPaused(paused);
        _cardActive.SetPaused(paused);
        Text = paused ? "KeyMouseCounter（已暂停）" : "KeyMouseCounter";
    }

    /// <summary>刷新卡片 + 图表（§7.1：先 FlushNow 再查库）。</summary>
    private async void RefreshData(bool flushFirst)
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        var sw = Stopwatch.StartNew();
        try
        {
            if (flushFirst)
            {
                try { _flush.FlushNow(); }
                catch (Exception ex) { Log.Warn($"刷盘失败：{ex.Message}"); }
            }

            var today = DateOnly.FromDateTime(DateTime.Today);
            var yesterday = today.AddDays(-1);
            var tab = (TabKind)_tabs.SelectedIndex;

            await RefreshCardsAsync(today, yesterday);

            var ct = _cts.Token;
            switch (tab)
            {
                case TabKind.TodayHour:
                    await RenderTodayHourAsync(ct);
                    break;
                case TabKind.TodayMinute:
                    await RenderTodayMinuteAsync(ct);
                    break;
                default:
                    await RenderMultiDayAsync(ct);
                    break;
            }

            _dirty = false;
            sw.Stop();
            SetStatus($"{DateTime.Now:HH:mm:ss} 刷新 · {sw.ElapsedMilliseconds} ms · " +
                      $"数据目录 {AppPaths.Root}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("刷新界面失败", ex);
            SetStatus("刷新失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshCardsAsync(DateOnly today, DateOnly yesterday)
    {
        var t = await _query.GetDayAsync(today, _cts.Token);
        var y = await _query.GetDayAsync(yesterday, _cts.Token);

        long keyT = t.Key, clickT = t.Click, activeT = t.ActiveSeconds, wheelT = t.Wheel;
        long keyY = y.Key, clickY = y.Click, activeY = y.ActiveSeconds;

        _cardKey.Value = UiText.N(keyT);
        var (kt, ks) = UiText.Delta(keyT, keyY);
        _cardKey.SetSub("较昨日 " + Prefix(ks) + kt, ks);

        _cardClick.Value = UiText.N(clickT);
        var (ct, cs) = UiText.Delta(clickT, clickY);
        _cardClick.SetSub("较昨日 " + Prefix(cs) + ct, cs);

        _cardActive.Value = UiText.Duration(activeT);
        var (at, asg) = UiText.Delta(activeT, activeY);
        _cardActive.SetSub($"滚轮 {UiText.N(wheelT)} 格 · 较昨日 {Prefix(asg)}{at}", 0, colorize: false);

        UpdatePausedUi(_engine.IsPaused);
    }

    private static string Prefix(int sign) => sign > 0 ? "+" : sign < 0 ? "−" : "";

    private async Task RenderTodayHourAsync(CancellationToken ct)
    {
        var hours = await _query.GetHoursAsync(_day, ct);
        var summary = ChartDataBuilder.SummarizeHours(hours, _metric);
        int peakHour = -1;
        long best = 0;
        for (int h = 0; h < hours.Count; h++)
        {
            long v = _metric.Value(hours[h].Key, hours[h].Click, hours[h].Wheel, hours[h].ActiveSeconds);
            if (v > best) { best = v; peakHour = hours[h].Hour; }
        }
        var data = ChartDataBuilder.HourSeries(hours, _metric, peakHour < 0 ? null : peakHour, asBar: _asBar.Checked);
        ShowView(data);
        SetSummary(summary);
        _rowCount.Text = _day == DateOnly.FromDateTime(DateTime.Today) ? "今日 24 小时" : $"{_day:MM-dd} 24 小时";
    }

    private async Task RenderTodayMinuteAsync(CancellationToken ct)
    {
        var minutes = await _query.GetMinutesAsync(_day, ct);
        var summary = ChartDataBuilder.SummarizeMinutes(minutes, _metric);
        SetSummary(summary);

        switch (_view)
        {
            case ViewKind.Heat:
                ShowView(ChartDataBuilder.MinuteHeat(minutes, _metric, _day));
                break;
            case ViewKind.Table:
                var table = ChartDataBuilder.MinuteTable(minutes, _metric, includeZero: _showAll.Checked);
                ShowView(table);
                _rowCount.Text = $"{table.Rows.Count} 行";
                break;
            default:
                ShowView(ChartDataBuilder.MinuteSeries(minutes, _metric, asBar: _asBar.Checked));
                break;
        }
        if (_view != ViewKind.Table)
            _rowCount.Text = $"非零分钟 {minutes.Count(m => _metric.Value(m.Key, m.Click, m.Wheel, m.ActiveSeconds) > 0)}";
    }

    private async Task RenderMultiDayAsync(CancellationToken ct)
    {
        var days = await _query.GetDaysAsync(_from, _to, ct);
        var summary = ChartDataBuilder.Summarize(days, _metric);
        SetSummary(summary);

        switch (_view)
        {
            case ViewKind.Heat:
                var matrix = await _query.GetHeatMatrixAsync(_from, _to, _metric, ct);
                ShowView(ChartDataBuilder.DayHeat(matrix));
                break;
            case ViewKind.Table:
                var table = ChartDataBuilder.DayTable(days, _metric);
                ShowView(table);
                _rowCount.Text = $"{table.Rows.Count} 天";
                break;
            default:
                ShowView(ChartDataBuilder.DaySeries(days, _metric, asBar: _asBar.Checked));
                break;
        }
        if (_view != ViewKind.Table)
            _rowCount.Text = $"{_from:MM-dd} ~ {_to:MM-dd}";
    }

    private IChartView CurrentView => _view switch
    {
        ViewKind.Heat => _heatView,
        ViewKind.Table => _tableView,
        _ => _lineView
    };

    private void ShowView(ChartData data)
    {
        var view = CurrentView;
        var target = view.Host;
        foreach (var host in new[] { _lineView.Host, _heatView.Host, _tableView.Host })
            host.Visible = ReferenceEquals(host, target);
        target.BringToFront();
        _showAll.Visible = _tabs.SelectedIndex == (int)TabKind.TodayMinute && _view == ViewKind.Table;
        _asBar.Visible = _view == ViewKind.Line;
        view.Render(data);
    }

    private void SetSummary(PeriodSummary s)
    {
        bool compact = false;
        string avgLabel = _tabs.SelectedIndex == (int)TabKind.MultiDay ? "日均" : "均值";
        string line = $"指标：{_metric.DisplayName()}    合计 {s.TotalText(compact)} · " +
                      $"峰值 {s.PeakText(compact)} · {avgLabel} {s.AverageText(compact)}";
        if (s.Metric != Metric.ActiveSeconds) line += $" · 活跃 {ValueFormat.DurationShort(s.ActiveSeconds)}";
        if (_summary.Text != line) _summary.Text = line;
    }

    private void SetStatus(string text)
    {
        if (_status.Text != text) _status.Text = text;
    }

    private void PersistSize()
    {
        if (WindowState != FormWindowState.Normal) return;
        int w = Math.Max(640, ClientSize.Width), h = Math.Max(420, ClientSize.Height);
        var saved = _cfg.Ui.LastMainWindowSize;
        if (saved is { Length: 2 } && saved[0] == w && saved[1] == h) return;
        _cfg.Ui.LastMainWindowSize = new[] { w, h };
        try { _watch.Save(); } catch (Exception ex) { Log.Warn($"保存窗口尺寸失败：{ex.Message}"); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        PersistSize();
        if (_forceClose) { base.OnFormClosing(e); return; }
        if (e.CloseReason != CloseReason.UserClosing) return;
        // §7.1：默认关闭 = 隐藏到托盘；Ui.CloseToTray=false 时"关闭即退出"
        if (!_cfg.Ui.CloseToTray)
        {
            e.Cancel = true;
            _host?.ExitApplication();
            return;
        }
        e.Cancel = true;
        Hide();
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdatePausedUi(_engine.IsPaused);
        RefreshData(flushFirst: true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F5) RefreshData(flushFirst: true);
        else if (e.KeyCode == Keys.Escape) Hide();
        else if (e.Control && e.KeyCode == Keys.D) ShowDateMenu();
        else if (e.Control && e.KeyCode == Keys.R) RefreshData(flushFirst: true);
    }

    // ─────────────────── 给 AppHost / 托盘用的入口 ───────────────────

    /// <summary>重读配置（热加载后调用）。</summary>
    public void ReloadConfig()
    {
        _rangeDays = Math.Clamp(_cfg.Ui.DefaultRangeDays, 1, 366);
        ApplyControlColors(this);
        UpdatePausedUi(_engine.IsPaused);
        _dirty = true;
    }

    /// <summary>主题切换后重绘。</summary>
    public void ApplyTheme()
    {
        OnThemeChanged();
    }

    /// <summary>立即刷盘并重绘（数据变化、暂停恢复、清空之后调用）。</summary>
    public void RefreshDashboard()
    {
        if (IsDisposed) return;
        UpdatePausedUi(_engine.IsPaused);
        RefreshData(flushFirst: true);
    }

    /// <summary>打开导出对话框（托盘"导出"也走这里）。</summary>
    public void RunExport()
    {
        try
        {
            using var dlg = new ExportDialog(_query, _day);
            dlg.ShowDialog(this);
        }
        catch (Exception ex) { Log.Error("打开导出对话框失败", ex); }
    }

    public void OpenSettings(string? page)
    {
        try
        {
            using var dlg = new SettingsForm(_host!, page);
            dlg.ShowDialog(this);
            RefreshDashboard();
        }
        catch (Exception ex) { Log.Error("打开设置失败", ex); }
    }

    public void ShowAboutPage() => OpenSettings("about");

    /// <summary>清空今日（托盘/设置页共用；confirmInUi=true 时先弹二次确认）。</summary>
    public void PurgeToday(bool confirmInUi)
    {
        try
        {
            if (confirmInUi)
            {
                var ok = ConfirmDialog.Ask(this, "清空今日数据",
                    "将删除今天的统计数据（内存计数 + 数据库行 + 今日日志文件）。\r\n" +
                    "此操作不可撤销，请输入 DELETE 确认。");
                if (!ok) return;
            }
            if (_host is null) return;
            var r = _host.PurgeDayNow(DateOnly.FromDateTime(DateTime.Today));
            Log.Info($"已清空今日数据：删除 {r.DaysRemoved} 天、{r.FilesRemoved} 个文件。");
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            Log.Error("清空今日失败", ex);
            MessageBox.Show(this, "清空失败：\r\n" + ex.Message, "KeyMouseCounter",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>退出流程调用：不再"隐藏到托盘"。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        try { Close(); } catch { }
    }

    private bool _forceClose;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            Theme.Changed -= OnThemeChanged;
        }
        base.Dispose(disposing);
    }
}

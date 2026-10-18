using System.Diagnostics;
using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Input;
using KeyMouseCounter.Native;
using KeyMouseCounter.Services;
using KeyMouseCounter.Storage;
using KeyMouseCounter.UI;

namespace KeyMouseCounter.UI;

/// <summary>
/// 装配层（§4.2 / §14.6.6）：把配置、引擎、钩子、存储、图表、窗口全部接起来，
/// 并负责启动顺序（单实例锁 → 日志 → 配置 → 数据库 → 重建 → 保留期 → 钩子 → 窗口）。
/// </summary>
internal sealed class AppHost : ApplicationContext
{
    private static readonly TimeSpan ReconcileBudget = TimeSpan.FromSeconds(20);

    private readonly CommandLineOptions _options;
    private readonly System.Threading.Timer _flushWatchdog;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private AppConfig _config = new();
    private SingleInstance? _single;
    private CounterEngine? _engine;
    private SqliteStore? _store;
    private StatsQuery? _query;
    private DailyLogWriter? _writer;
    private FlushService? _flusher;
    private DataPurger? _purger;
    private RetentionService? _retention;
    private LogDbReconciler? _reconciler;
    private LowLevelHookSource? _hooks;
    private HotKeyWindow? _hotkeys;
    private TrayIcon? _tray;
    private MainForm? _main;
    private WidgetForm? _widget;
    private ConfigWatcher? _configWatcher;
    private System.Threading.Timer? _retentionTimer;
    private System.Threading.Timer? _hookWatchdog;

    private long _lastFlushCheckTick;
    private string? _widgetWarning;
    private bool _shuttingDown;
    private bool _hookFailureNotified;

    public AppHost(CommandLineOptions options)
    {
        _options = options;
        _flushWatchdog = new System.Threading.Timer(_ => SafeWatch(WatchFlush), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ContextMenuStrip? TrayMenu => null;

    /// <summary>配置热加载器（窗口保存窗口尺寸/挂件位置时要用它抑制回环）。</summary>
    public ConfigWatcher ConfigWatcher => _configWatcher!;

    /// <summary>主窗口/托盘/挂件共用的暂停开关。</summary>
    public void TogglePausePublic() => TogglePause();

    /// <summary>托盘用的主窗口（懒创建）。</summary>
    public MainForm MainWindow => _main ?? throw new InvalidOperationException("主窗口尚未创建");

    public TimeSpan Uptime => _uptime.Elapsed;

    // ─────────────────────────── 装配 ───────────────────────────

    public static AppHost Create(CommandLineOptions options)
    {
        AppPaths.EnsureCreated();

        var host = new AppHost(options);
        host.BuildServices();
        return host;
    }

    private void BuildServices()
    {
        // 1) 单实例（§5.10 用 Local\ 而不是 Global\，多用户各自一份）
        _single = SingleInstance.TryAcquire(AppPaths.AppName);
        if (_single is null)
        {
            // 已经通知旧实例把窗口弹出来，本进程安静退出（§15.5 坑 8）
            Log.Info("已有实例在运行，本次启动退出。");
            throw new Services.AlreadyRunningException();
        }
        _single.Activated += OnSecondInstance;

        // 2) 日志
        Log.Configure(LogLevel.Info);
        Log.Info($"启动 v{AppPaths.Version} pid={Environment.ProcessId} os={Environment.OSVersion.VersionString}");

        // 3) 配置
        _config = AppConfig.Load(AppPaths.ConfigPath, out string? warning);
        if (!string.IsNullOrEmpty(warning)) Log.Warn("配置：" + warning);
        if (_options.CountInjected)
        {
            // §14.4：自动化验证时临时统计注入输入（只在内存里生效，不写回配置）
            _config.Debug.CountInjected = true;
            Log.Warn("--count-injected：本次运行将统计注入/合成输入（仅供自动化验证）");
        }
        Theme.Apply(_config.Ui.Theme);
        Log.Configure(_config.MinLogLevel);

        // 4) 引擎 + 时钟
        _engine = new CounterEngine(_config);

        // 5) 数据库（含损坏自愈：备份 + 从日志重建，见 §9.2）
        _store = OpenStoreWithSelfHeal();

        // 6) 日志写入器 + 刷盘
        _writer = new DailyLogWriter(AppPaths.DataDir, _store.UserSid);
        _flusher = new FlushService(_engine, _config, _store, _writer);
        _purger = new DataPurger(_store, AppPaths.DataDir, _engine, _writer);
        _retention = new RetentionService(_config, _store, _purger, AppPaths.DataDir);
        _query = new StatsQuery(_store, _engine);
        _reconciler = new LogDbReconciler(_store, AppPaths.DataDir);
    }

    private SqliteStore OpenStoreWithSelfHeal()
    {
        var store = new SqliteStore(AppPaths.DbPath);
        try
        {
            store.Open();
            if (store.IntegrityCheck() != 0)
            {
                Log.Error("数据库完整性检查失败，准备从日志重建。");
                store.Dispose();
                throw new SqliteCorruptException("integrity_check 失败");
            }
            return store;
        }
        catch (Exception ex) when (ex is SqliteCorruptException || ex is Microsoft.Data.Sqlite.SqliteException || ex is InvalidOperationException)
        {
            try { store.Dispose(); } catch { }
            return RebuildDatabaseFromScratch(ex);
        }
    }

    private SqliteStore RebuildDatabaseFromScratch(Exception cause)
    {
        Log.Error("数据库无法打开，备份后从日志重建", cause);
        try
        {
            if (File.Exists(AppPaths.DbPath))
            {
                string backup = AppPaths.DbPath + ".corrupt";
                try { File.Delete(backup); } catch { }
                File.Move(AppPaths.DbPath, backup);
                Log.Warn($"损坏的数据库已备份为 {Path.GetFileName(backup)}");
            }
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                string f = AppPaths.DbPath + suffix;
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch (Exception ex)
        {
            Log.Error("备份损坏数据库失败", ex);
        }

        var store = new SqliteStore(AppPaths.DbPath);
        store.Open();
        return store;
    }

    // ─────────────────────────── 启动 ───────────────────────────

    public void Start()
    {
        // 7) 从日志重建（必须在钩子启动之前，避免和实时刷盘抢同一批数据）
        try
        {
            var report = _reconciler!.Reconcile(ReconcileBudget);
            Log.Info($"日志重建：扫描 {report.DaysScanned} 天，全量 {report.DaysFullImported}，" +
                     $"增量 {report.DaysIncremental}，跳过 {report.DaysSkippedGap}，" +
                     $"失败 {report.DaysFailed}，孤立日期 {report.OrphanDbDays}，耗时 {report.ElapsedMs}ms");
            if (report.BudgetExceeded) Log.Warn("日志重建超出时间预算，剩余部分将在后台继续。");
            foreach (var note in report.Notes) Log.Warn("重建：" + note);
        }
        catch (Exception ex)
        {
            Log.Error("日志重建失败（不影响实时统计）", ex);
        }

        // 8) 保留期清理
        try
        {
            var purged = _retention!.RunIfNeeded();
            if (purged is not null && purged.DaysRemoved > 0)
                Log.Info($"保留期清理：删除 {purged.DaysRemoved} 天（{purged.From:yyyy-MM-dd} ~ {purged.To:yyyy-MM-dd}）");
        }
        catch (Exception ex)
        {
            Log.Error("保留期清理失败", ex);
        }

        // 9) 刷盘启动 + 看门狗
        _flusher!.Start();
        _lastFlushCheckTick = _engine!.Clock.Ticks;
        _flushWatchdog.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        // 10) 钩子
        StartHooks();

        // 11) 全局热键
        _hotkeys = new HotKeyWindow();
        _hotkeys.HotKeyPressed += OnHotkey;
        if (!string.IsNullOrWhiteSpace(_config.Widget.Hotkey))
        {
            if (!_hotkeys.TryRegister(_config.Widget.Hotkey, out string? hkErr))
                Log.Warn($"全局热键注册失败：{hkErr}");
        }

        // 12) 电源/会话事件：唤醒后立刻落盘（§5.4）
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        // 13) 配置热加载
        _configWatcher = new ConfigWatcher(AppPaths.ConfigPath, _config, OnConfigReloaded);

        // 14) 托盘
        _tray = new TrayIcon(
            openMain: () => Dispatch(() => ShowMain()),
            toggleWidget: () => Dispatch(ToggleWidget),
            togglePause: () => Dispatch(TogglePause),
            openDataDir: OpenDataDir,
            export: () => Dispatch(() => ShowMain(openExport: true)),
            openSettings: () => Dispatch(() => OpenSettings(null)),
            purgeToday: () => Dispatch(() => PurgeTodayGuarded()),
            about: () => Dispatch(() => ShowAbout()),
            exit: () => Dispatch(ExitApplication),
            isPaused: () => _engine?.IsPaused ?? false,
            widgetVisible: () => _widget?.Visible ?? false);

        if (!_options.RegisterAutostart && !_options.UnregisterAutostart) { /* 命令已在入口处理 */ }

        // 15) 后台巡检：钩子自愈（§9.2）+ 保留期
        _hookWatchdog = new System.Threading.Timer(_ => SafeWatch(WatchHooks), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        _retentionTimer = new System.Threading.Timer(_ => SafeWatch(WatchRetention), null,
            TimeSpan.FromHours(6), TimeSpan.FromHours(6));

        TryRegisterHotkey();

        // 16) 挂件
        if (_config.Widget.Enabled && !_options.NoWidget) ShowWidget(firstShow: true);

        // 17) 主窗口
        ShowMain();

        if (_config.Ui.StartMinimized) _main!.Hide();
        Log.Info("初始化完成，开始统计。");
    }

    private void StartHooks()
    {
        try
        {
            _hooks ??= new LowLevelHookSource(_engine!, _config);
            _hooks.Start();
            if (_hooks.IsRunning) Log.Info("键盘/鼠标低级钩子已安装。");
            else Log.Error("钩子安装失败：" + (_hooks.LastError ?? "未知原因"));
        }
        catch (Exception ex)
        {
            Log.Error("钩子启动异常", ex);
        }
    }

    // ─────────────────────────── 关闭 ───────────────────────────

    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Log.Info("正在退出…");

        try { _config.Save(AppPaths.ConfigPath); } catch (Exception ex) { Log.Error("保存配置失败", ex); }

        try { _flushWatchdog.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _hookWatchdog?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _retentionTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }

        try { _hooks?.Stop(); } catch (Exception ex) { Log.Error("停钩子失败", ex); }
        try { _hotkeys?.Dispose(); } catch { }
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }

        try { _flusher?.Dispose(); } catch (Exception ex) { Log.Error("刷盘收尾失败", ex); }
        try { _configWatcher?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _store?.Checkpoint(); _store?.Dispose(); } catch (Exception ex) { Log.Error("关闭数据库失败", ex); }
        try { _single?.Dispose(); } catch { }

        Log.Info("已退出。");
    }

    public void ExitApplication()
    {
        try { Shutdown(); } catch { }
        try { _main?.ForceClose(); } catch { }
        try { _widget?.ForceClose(); } catch { }
        ExitThread();
    }

    private void OnSecondInstance()
    {
        Dispatch(() =>
        {
            ShowMain();
            if (_main is not null) _main.Activate();
        });
    }

    // ─────────────────────────── 托盘/窗口动作 ───────────────────────────

    public void ShowMain(bool openExport = false)
    {
        _main ??= new MainForm(this);
        _main.ReloadConfig();
        _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        _main.BringToFront();
        if (openExport) _main.RunExport();
    }

    private void ToggleWidget()
    {
        _config.Widget.Enabled = !_config.Widget.Enabled;
        _configWatcher?.NotifySelfSaved();
        PersistConfig();
        if (_config.Widget.Enabled) ShowWidget(firstShow: true);
        else HideWidget();
    }

    private void ShowWidget(bool firstShow)
    {
        try
        {
            if (_widget is null || _widget.IsDisposed)
            {
                _widget = new WidgetForm(
                    cfg: _config,
                    watch: _configWatcher!,
                    engine: _engine!,
                    provider: BuildWidgetData,
                    openMain: () => Dispatch(() => ShowMain()),
                    openSettings: () => Dispatch(() => OpenSettings("widget")),
                    exitApp: () => Dispatch(ExitApplication),
                    togglePause: () => Dispatch(TogglePause),
                    isPaused: () => _engine?.IsPaused ?? false);
            }
            _widget.ShowWidget(firstShow);
            _config.Widget.Enabled = true;
        }
        catch (Exception ex)
        {
            // 挂件失败不能拖垮主程序：计数与落库照常运行（§9.2）
            Log.Error("挂件创建失败（主程序继续运行）", ex);
            _widgetWarning = "挂件不可用";
        }
    }

    private WidgetData BuildWidgetData()
    {
        if (_engine is null) return default;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var (k, c, w, a) = _engine.TodayPendingTotals();
        try
        {
            var row = _store?.GetDay(today);
            if (row is not null)
            {
                k += row.Total.K;
                c += row.Total.Clicks;
                w += row.Total.Wheels;
                a += row.Total.AS;
            }
        }
        catch (Exception ex) { Log.Warn("挂件取数失败：" + ex.Message); }
        return new WidgetData(k, c, w, a, _engine.IsPaused);
    }

    private void HideWidget()
    {
        if (_widget is not null && !_widget.IsDisposed)
            _widget.HideWidget();
        _config.Widget.Enabled = false;
        PersistConfig();
    }

    private void TogglePause()
    {
        if (_engine is null) return;
        _engine.SetPaused(!_engine.IsPaused);
        Log.Info(_engine.IsPaused ? "已暂停统计。" : "已恢复统计。");
        _widget?.RefreshNow();
        if (_main is not null && !_main.IsDisposed) _main.RefreshDashboard();
    }

    private void OnHotkey()
    {
        Log.Info("热键触发：显示/隐藏挂件。");
        if (_config.Widget.Enabled) HideWidget();
        else
        {
            _config.Widget.Enabled = true;
            PersistConfig();
            ShowWidget(firstShow: true);
        }
    }

    public void OpenDataDir()
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("打开数据目录失败", ex); }
    }

    // ─────────────────── 设置页用的操作入口 ───────────────────

    public void ApplyWidgetConfig()
    {
        if (_config.Widget.Enabled)
        {
            if (_widget is null || _widget.IsDisposed) ShowWidget(firstShow: false);
            else
            {
                _widget.ApplyConfig();
                if (!_widget.Visible) _widget.ShowWidget(firstFade: false);
            }
        }
        else if (_widget is { Visible: true })
        {
            _widget.HideWidget();
        }
        _main?.RefreshDashboard();
    }

    public void ResetWidgetPosition()
    {
        if (_widget is null || _widget.IsDisposed) ShowWidget(firstShow: false);
        _widget?.ResetPosition();
    }

    public bool IsAutostartEnabled(out string? error) => AutostartService.IsEnabled(out error);

    public string? SetAutostart(bool enabled)
        => enabled ? AutostartService.Register() : AutostartService.Unregister();

    /// <summary>切换全局热键；返回 null 表示成功。</summary>
    public string? SetHotkey(string spec)
    {
        if (_hotkeys is null) return "热键窗口尚未创建";
        if (string.IsNullOrWhiteSpace(spec))
        {
            _hotkeys.Unregister();
            _config.Widget.Hotkey = "";
            PersistConfig();
            return null;
        }
        if (!_hotkeys.TryRegister(spec, out string? error)) return error;
        _config.Widget.Hotkey = spec;
        PersistConfig();
        return null;
    }

    /// <summary>检测热键是否可用（试注册再注销，§7.4）。</summary>
    public static bool IsHotkeyAvailable(string spec, out string? error)
        => HotKeyWindow.IsAvailable(spec, out error);

    public PurgeResult PurgeDayNow(DateOnly day)
    {
        var r = _purger!.PurgeDay(day);
        RefreshAfterDataChange();
        return r;
    }

    public PurgeResult PurgeRangeNow(DateOnly from, DateOnly to)
    {
        var r = _purger!.PurgeRange(from, to);
        RefreshAfterDataChange();
        return r;
    }

    /// <summary>全部清空：停钩子 → 落盘 → 删表删日志 → 重启钩子（§6.7）。</summary>
    public PurgeResult PurgeAllNow()
    {
        try { _hooks?.Stop(); } catch { }
        SafeFlush();
        var r = _purger!.PurgeAll();
        try { StartHooks(); } catch { }
        RefreshAfterDataChange();
        return r;
    }

    public ReconcileReport RebuildFromLogs(TimeSpan? budget = null)
    {
        SafeFlush();
        var report = _reconciler!.Reconcile(budget ?? TimeSpan.FromMinutes(5));
        RefreshAfterDataChange();
        return report;
    }

    private void PurgeTodayGuarded()
    {
        ShowMain();
        _main!.PurgeToday(confirmInUi: true);
    }

    private void ShowAbout()
    {
        ShowMain();
        _main!.ShowAboutPage();
    }

    public void OpenSettings(string? page)
    {
        ShowMain();
        _main!.OpenSettings(page);
    }

    // ─────────────────────────── 配置 ───────────────────────────

    public AppConfig Config => _config;

    private void PersistConfig()
    {
        try
        {
            _config.Save(AppPaths.ConfigPath);
            _configWatcher?.NotifySelfSaved();
        }
        catch (Exception ex) { Log.Error("保存配置失败", ex); }
    }

    public void SaveConfig()
    {
        _config.Normalize();
        PersistConfig();
    }

    public void ApplyTheme(string theme)
    {
        _config.Ui.Theme = theme;
        Theme.Apply(theme);
        PersistConfig();
        _main?.ApplyTheme();
        _widget?.ApplyTheme();
    }

    private void OnConfigReloaded(AppConfig fresh)
    {
        if (_shuttingDown) return;
        Log.Info("检测到配置文件变化，已热加载。");
        var oldWidget = _config.Widget;
        var oldHotkey = oldWidget.Hotkey;

        _config = fresh;
        Theme.Apply(_config.Ui.Theme);
        _engine!.Config = _config;
        Log.Configure(_config.MinLogLevel);

        if (!string.Equals(oldHotkey, _config.Widget.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            if (_hotkeys is not null)
            {
                if (string.IsNullOrWhiteSpace(_config.Widget.Hotkey)) _hotkeys.Unregister();
                else if (!_hotkeys.TryRegister(_config.Widget.Hotkey, out string? err))
                    Log.Warn("热键切换失败：" + err);
            }
        }

        if (_config.Widget.Enabled && (_widget is null || _widget.IsDisposed || !_widget.Visible)) ShowWidget(firstShow: false);
        else if (!_config.Widget.Enabled && _widget is { Visible: true }) HideWidget();
        else _widget?.ApplyConfig();

        _main?.ReloadConfig();
    }

    private void TryRegisterHotkey()
    {
        if (_hotkeys is null || string.IsNullOrWhiteSpace(_config.Widget.Hotkey)) return;
        if (!_hotkeys.TryRegister(_config.Widget.Hotkey, out string? error))
        {
            Log.Warn($"全局热键 {_config.Widget.Hotkey} 注册失败：{error}");
            _tray?.Balloon("全局热键不可用", $"{_config.Widget.Hotkey} 已被其他程序占用，可在「设置 → 挂件」里改。");
        }
    }

    // ─────────────────────────── 巡检 ───────────────────────────

    private void SafeWatch(Action action)
    {
        if (_shuttingDown) return;
        try { action(); }
        catch (Exception ex) { Log.Error("后台巡检出错", ex); }
    }

    /// <summary>刷盘看门狗：有积压但长时间没写出去就告警（§6.5）。</summary>
    private void WatchFlush()
    {
        if (_flusher is null || _engine is null) return;
        long now = _engine.Clock.Ticks;
        double sinceLast = (now - _lastFlushCheckTick) / (double)_engine.Clock.Frequency;
        if (_flusher.PendingBatches > 0 && sinceLast > 120)
        {
            Log.Warn($"刷盘积压 {_flusher.PendingBatches} 批，已 {sinceLast:F0} 秒未成功写入。");
            _tray?.SetTooltip("KeyMouseCounter（数据写入受阻）");
        }
    }

    /// <summary>钩子自愈（§9.2）：系统近 2 分钟有输入而引擎计数为 0 → 重装钩子。</summary>
    private void WatchHooks()
    {
        if (_hooks is null || _engine is null) return;
        if (!_hooks.IsRunning)
        {
            Log.Warn("检测到钩子未运行，尝试重新安装。");
            StartHooks();
            return;
        }
        if (HookHealth.ShouldReinstall(_engine)) { 
            Log.Warn("检测到系统有输入活动但统计为 0，判定钩子已被摘除，重新安装。");
            try { _hooks.Stop(); } catch { }
            _hooks = null;
            StartHooks();
            if (!_hookFailureNotified)
            {
                _hookFailureNotified = true;
                _tray?.Balloon("统计可能中断", "检测到键盘钩子被系统或安全软件摘除，已自动重装。");
            }
        }
    }

    private void WatchRetention()
    {
        var result = _retention?.RunIfNeeded();
        if (result is not null && result.DaysRemoved > 0)
            Log.Info($"保留期清理：删除 {result.DaysRemoved} 天。");
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        Log.Info("系统从休眠/待机恢复，立即落盘。");
        SafeFlush();
        RestartWidgetTimerIfNeeded();
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        Log.Info("会话已解锁，立即落盘。");
        SafeFlush();
    }

    private void SafeFlush()
    {
        try
        {
            _flusher?.FlushNow();
            _lastFlushCheckTick = _engine?.Clock.Ticks ?? _lastFlushCheckTick;
        }
        catch (Exception ex) { Log.Error("立即落盘失败", ex); }
    }

    private void RestartWidgetTimerIfNeeded() => _widget?.RefreshNow();

    // ─────────────────────────── 给 UI 用的查询/服务入口 ───────────────────────────

    public StatsQuery Query => _query!;
    public FlushService Flusher => _flusher!;
    public DataPurger Purger => _purger!;
    public SqliteStore Store => _store!;
    public CounterEngine Engine => _engine!;
    public LogDbReconciler Reconciler => _reconciler!;
    public MainForm? ExistingMain => _main;
    public string? WidgetWarning => _widgetWarning;

    public void SetWidgetWarning(string? text)
    {
        _widgetWarning = text;
        _widget?.SetWarning(text);
    }

    public void RefreshAfterDataChange()
    {
        _main?.RefreshDashboard();
        _widget?.RefreshNow();
    }

    /// <summary>日志写入行数统计（设置-关于页）。</summary>
    public int TodayLogLines
    {
        get
        {
            try
            {
                var day = DateOnly.FromDateTime(DateTime.Now);
                var path = AppPaths.DailyLogPath(day);
                if (!File.Exists(path)) return 0;
                int lines = 0;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                while (sr.ReadLine() is { } line) if (line.Contains("\"v\":", StringComparison.Ordinal)) lines++;
                return lines;
            }
            catch { return 0; }
        }
    }

    /// <summary>切到 UI 线程（主窗口句柄还没建出来时直接执行）。</summary>
    private void Dispatch(Action action)
    {
        var form = _main;
        if (form is null || form.IsDisposed || !form.IsHandleCreated) action();
        else
        {
            try { form.BeginInvoke(action); }
            catch { try { action(); } catch (Exception ex) { Log.Error("UI 调度失败", ex); } }
        }
    }
}

/// <summary>数据库损坏标记（用于触发自愈流程）。</summary>
public sealed class SqliteCorruptException : Exception
{
    public SqliteCorruptException(string message) : base(message) { }
}

/// <summary>
/// 钩子健康判定（纯函数，便于单测）。系统近 2 分钟有输入而引擎计数为 0 → 判定被摘钩。
/// </summary>
internal static class HookHealth
{
    public static bool ShouldReinstall(CounterEngine engine)
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            if (!NativeMethods.GetLastInputInfo(ref lii)) return false;
            uint idleMs = NativeMethods.GetTickCount() - lii.dwTime;
            if (idleMs > 120_000) return false; // 系统本身就没输入
            // 引擎自上次巡检以来有没有计过数？用"今日待写 + 已写"的近似：直接看 LastInputTick
            long now = engine.Clock.Ticks;
            long last = engine.LastCountedTick;
            if (last == 0) return false;
            double sec = (now - last) / (double)engine.Clock.Frequency;
            return sec > 120;
        }
        catch
        {
            return false;
        }
    }
}

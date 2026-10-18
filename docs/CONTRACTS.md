# KeyMouseCounter 模块接口契约（v2.3）

本文件是模块之间的**冻结接口**：谁改签名，谁负责同步所有调用方与单测。
分层：`UI / Charts → Services → Storage → Core`，`Native / Input` 只被 `AppHost` 与引擎使用。

命名约定：`★` = 契约红线（改动需同步文档），`internal` 类型对 `KeyMouseCounter.Tests` 可见（`InternalsVisibleTo`）。

---

## 1. `AppPaths`（static，根命名空间）

```csharp
public static class AppPaths
{
    public const string AppName = "KeyMouseCounter";
    public const string Version = "2.3.0";
    public static string? RootOverride { get; set; }   // 测试重定向根目录
    public static string Root { get; }                 // %LOCALAPPDATA%\KeyMouseCounter
    public static string DataDir { get; }              // Root\data
    public static string LogDir { get; }               // Root\logs
    public static string ExportDir { get; }            // Root\exports
    public static string DbPath { get; }               // Root\data.db
    public static string ConfigPath { get; }           // Root\config.json
    public static string ConfigBadPath { get; }        // Root\config.bad.json
    public static void EnsureCreated();
    public static string DailyLogPath(DateOnly day);   // DataDir\yyyy-MM-dd.jsonl
    public static string DailySummaryPath(DateOnly day);
}
```

---

## 2. `Core`：计数引擎与模型

### 2.1 `IClock` / `SystemClock`

```csharp
public interface IClock
{
    DateTimeOffset Now { get; }   // 墙钟（分钟归属用）
    long Ticks { get; }           // Stopwatch.GetTimestamp()，单调（活跃时长用）
    long Frequency { get; }       // Stopwatch.Frequency
}
public sealed class SystemClock : IClock { public static readonly SystemClock Instance; }
```

### 2.2 `MinuteBuckets`（值类型，9 个计数器）

```csharp
public struct MinuteBuckets
{
    public long K, ML, MR, MM, WU, WD, WL, WR, AS;   // 键/左/右/中/滚上/滚下/横左/横右/活跃秒
    public readonly bool IsZero { get; }
    public readonly long Clicks { get; }             // ML+MR+MM
    public readonly long Wheels { get; }             // WU+WD+WL+WR
    public void AddFrom(in MinuteBuckets other);
    public static MinuteBuckets From(long k, long ml, long mr, long mm,
                                     long wu, long wd, long wl, long wr, long activeSeconds);
}
```

### 2.3 `CounterEngine` ★

```csharp
public sealed class CounterEngine
{
    public CounterEngine(AppConfig cfg, IClock? clock = null, bool cacheClock = true);
    public event Action<DateOnly>? DayRollover;
    public IClock Clock { get; }
    public AppConfig Config { get; set; }      // 引用替换，钩子回调每次都读最新
    public bool IsPaused { get; }
    public DateOnly CurrentDay { get; }
    public long LastCountedTick { get; }       // 钩子自愈判据

    public void SetPaused(bool paused);
    public void SuppressFor(TimeSpan duration);          // 自身输入排除（挂件点击）
    public void OnKey(int vk, bool down, bool injected);
    public void OnMouseButton(MouseButton btn, bool down, bool injected);
    public void OnWheel(int delta, bool horizontal);     // 120 一格，余量跨事件累积

    public List<(DateOnly Day, int Minute, MinuteBuckets Data)> TakeSnapshot();  // 锁内拷贝+清零
    public MinuteBuckets PendingFor(DateOnly day);                              // 锁内拷贝，不清零
    public List<(int Minute, MinuteBuckets Data)> PendingMinutes(DateOnly day);
    public void ClearDay(DateOnly day);
    public void ResetAll();
    public (long Key, long Click, long Wheel, long ActiveSeconds) TodayPendingTotals();
}
```

**契约约束**：
- 全部写入路径必须经过 `OnKey/OnMouseButton/OnWheel`，不允许外部直接改桶；
- 钩子回调只调这三个方法，且**不得**在回调里 `new`/LINQ/日志/DB；
- `TakeSnapshot` 返回后引擎内对应数据已清零，调用方必须负责落日志与落库（`FlushService`）。

### 2.4 统计模型

```csharp
public readonly record struct DayPoint(DateOnly Day, long Key, long Click, long Wheel,
                                       long ActiveSeconds, string? FirstTs, string? LastTs);
public readonly record struct HourPoint(int Hour, long Key, long Click, long Wheel, long ActiveSeconds);
public readonly record struct MinutePoint(int Minute, long Key, long Click, long Wheel, long ActiveSeconds);
public readonly record struct HeatCell(int Row, int Col, long Value);
public enum HeatShape { HoursByMinutes, HoursByDays, Calendar }
public enum Metric { Key = 0, Click = 1, Wheel = 2, ActiveSeconds = 3 }
public static class MetricExtensions
{
    public static string DisplayName(this Metric m);   // 键盘按键 / 鼠标点击 / 滚轮 / 活跃时长
    public static long Value(this Metric m, long key, long click, long wheel, long activeSeconds);
}
public sealed class HeatMatrix { /* Row/Rows/Cols/Values(long[,])/RowLabels/ColumnLabels/Metric/From/To/IsEmpty */ }

public interface IStatsQuery
{
    Task<IReadOnlyList<DayPoint>>   GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<HourPoint>>  GetHoursAsync(DateOnly day, CancellationToken ct);
    Task<IReadOnlyList<MinutePoint>> GetMinutesAsync(DateOnly day, CancellationToken ct);
    Task<IReadOnlyList<HeatCell>>   GetHeatAsync(DateOnly from, DateOnly to, Metric metric, CancellationToken ct);
    Task<HeatMatrix>                GetHeatMatrixAsync(DateOnly from, DateOnly to, Metric metric, CancellationToken ct);
    Task<DayPoint>                  GetDayAsync(DateOnly day, CancellationToken ct);
}
```

---

## 3. `Input`：输入源

```csharp
public enum InputKind { KeyDown, KeyUp, MouseDown, MouseUp, Wheel }
public enum MouseButton { Left = 1, Right = 2, Middle = 3 }
public readonly record struct InputEvent(InputKind Kind, int Code, bool Injected,
                                         int Delta = 0, bool Horizontal = false);

public interface IInputSource : IDisposable
{
    event Action<InputEvent>? EventReceived;
    void Start();
    void Stop();
    bool IsRunning { get; }
    string? LastError { get; }
}

public sealed class LowLevelHookSource : IInputSource
{
    public LowLevelHookSource(CounterEngine engine, AppConfig cfg);
    // Start(): 起专用线程装 WH_KEYBOARD_LL + WH_MOUSE_LL，_ready.Wait(5s)
    // Stop(): PostThreadMessage(WM_QUIT) + Join(3s)；Unhook 必须在同一线程
}
```

**契约约束（§4.1 三条硬规则）**：
1. 回调只允许「读结构体 → 过滤 → 一次极短 lock 自增 → `CallNextHookEx`」，禁止 I/O、`DateTime.Now`、UI、跨线程等待；
2. 钩子线程与 UI 线程分离；
3. 委托实例必须存字段（否则 `CallbackOnCollectedDelegate` 崩溃）；回调恒返回 `CallNextHookEx`。

---

## 4. `Storage`：唯一数据访问层

### 4.1 `SqliteStore` ★

```csharp
public sealed class SqliteStore : IDisposable
{
    public const int SchemaVersion = 2;
    public string DatabasePath { get; }
    public string UserSid { get; }              // WindowsIdentity 的 SID，失败退 "unknown"
    public SqliteStore(string dbPath);

    public void Open();                          // WAL / synchronous=NORMAL / busy_timeout=3000 + 迁移
    public string? GetMeta(string key);  public void SetMeta(string key, string value);
    public int IntegrityCheck();                 // 0 = ok

    // 写入（事务内，幂等增量合并）
    public void SaveBatch(DateOnly day, IReadOnlyList<(int Minute, MinuteBuckets Data)> items,
                          int utcOffsetMin, string? firstTs, string? lastTs);
    public void ReplaceDay(DateOnly day, IReadOnlyList<(int Minute, MinuteBuckets Data)> items,
                           int utcOffsetMin);    // 先删该日 minute_stat + day_stat 再插（重建用）

    // 查询
    public long CountMinuteRows(DateOnly day);
    public bool HasDayRow(DateOnly day);
    public DayRow? GetDay(DateOnly day);
    public List<DayRow> GetDays(DateOnly from, DateOnly to);
    public List<DateOnly> GetDayDates();
    public List<HourPoint> GetHours(DateOnly day);
    public List<MinutePoint> GetMinutes(DateOnly day);
    public List<HeatCell> GetHeat(DateOnly from, DateOnly to, Metric metric = Metric.Key);
    public static string MetricColumn(Metric metric);   // SQL 聚合列表达式

    // 清空
    public void DeleteDay(DateOnly day);
    public int  DeleteRange(DateOnly from, DateOnly to);
    public void DeleteAll();
    public void WritePurgeLog(string scope, DateOnly from, DateOnly to);

    // 重建账本
    public bool HasLogGap(DateOnly day);
    public void AddLogGap(DateOnly day, string reason);
    public void RemoveLogGap(DateOnly day);
    public LogCursor? GetCursor(DateOnly day);
    public void SetCursor(DateOnly day, LogCursor cursor);
    public void DeleteCursor(DateOnly day);

    public void Checkpoint();
    public void Dispose();
}

public readonly record struct LogCursor(long ByteOffset, int LineCount, long TotalKey, long TotalClick);
public sealed record DayRow(DateOnly Day, MinuteBuckets Total, string? FirstTs, string? LastTs,
                            int UpdatedAtOffsetMin);
```

**契约约束**：这是进程内**唯一**的 DB 访问点；内部单连接 + `lock(_gate)` 串行；调用方不得自行开连接。

### 4.2 JSONL 日志 ★

```csharp
public sealed record LogHeader(string type, int v, string app, string host, string sid,
                               int utcOffset, string createdAt);
public sealed record MinuteLine(int v, string date, int min, long k, long ml, long mr, long mm,
                                long wu, long wd, long wl, long wr, long @as, string t);
public sealed class LogParseResult
{
    public DateOnly Day { get; init; }
    public SortedDictionary<int, MinuteBuckets> Minutes { get; }
    public int LinesRead/LinesImported/LinesSkipped { get; set; }
    public bool TruncatedTail { get; set; }
    public long ValidBytes { get; set; }
    public int LineCount { get; set; }
    public List<string> Warnings { get; }
    public int FirstMinute/LastMinute { get; }
}

public sealed class DailyLogWriter : IDisposable
{
    public DailyLogWriter(string dir, string sid);
    public static string PathFor(string dir, DateOnly day);
    public long Append(DateOnly day, IReadOnlyList<(int Minute, MinuteBuckets Data)> items); // 返回写后长度
    public static void TruncateTo(string dir, DateOnly day, long length);
    public static void WriteSummary(string dir, DateOnly day, in MinuteBuckets total, int nonZeroMinutes);
    public void Close();
    public void Dispose();
}

public static class DailyLogReader
{
    public static LogParseResult ParseFile(string path, DateOnly? dayHint = null);
    public static LogParseResult ParseFrom(string path, long byteOffset, DateOnly day);
    public static long RepairTail(string path);                       // 截断半行，返回有效长度
    public static DateOnly DayFromFileName(string path);
    public static List<DateOnly> ListLogDays(string dir);
    public static int CountDateMismatches(string path, DateOnly fileDay);
}
```

**契约约束**：日志是**权威源**。任何写入顺序都必须是「先 `Append` 成功 → 再 `SaveBatch`」；
`Append` 失败必须 `AddLogGap(day)` 且数据留在 `FlushService` 队列重试（§6.5）。

### 4.3 重建与清空

```csharp
public sealed class ReconcileReport
{
    public int DaysScanned/DaysFullImported/DaysIncremental/DaysSkippedGap/DaysFailed/OrphanDbDays { get; set; }
    public bool BudgetExceeded { get; set; }
    public long ElapsedMs { get; set; }
    public List<string> Notes { get; }
}
public sealed class LogDbReconciler
{
    public LogDbReconciler(SqliteStore store, string dataDir, IClock? clock = null);
    public ReconcileReport Reconcile(TimeSpan budget, CancellationToken ct = default);
}

public sealed class PurgeResult { int DaysRemoved; int FilesRemoved; string Scope; DateOnly From; DateOnly To; }
public sealed class DataPurger
{
    public DataPurger(SqliteStore store, string dataDir, CounterEngine engine, DailyLogWriter writer);
    public PurgeResult PurgeDay(DateOnly day);
    public PurgeResult PurgeRange(DateOnly from, DateOnly to);   // 只统计确有数据的日期/文件
    public PurgeResult PurgeAll();
}
```

---

## 5. `Services`

```csharp
public sealed class FlushService : IDisposable
{
    public FlushService(CounterEngine engine, AppConfig cfg, SqliteStore store,
                        DailyLogWriter writer, IClock? clock = null);
    public event Action? Flushed;
    public long LastFlushMs { get; }
    public void Start();       // Timer 每 max(5, flushIntervalSec) 秒
    public void Stop();
    public void FlushNow();    // 快照 → 日志 → DB；UI 打开/切标签前调用
    public void Dispose();     // 最后一次刷盘 + writer.Close()
}

public sealed class StatsQuery : IStatsQuery
{
    public StatsQuery(SqliteStore store, CounterEngine engine, IClock? clock = null);
    // 同步版：GetDays / GetHours / GetMinutes / GetHeat / GetHeatMatrix / GetDay
    public List<DayRow> GetDayRows(DateOnly from, DateOnly to);   // 按天导出的分项数据
    // “今天”的查询都会把 engine.PendingMinutes(today) 合并进结果
}

public sealed class RetentionService
{
    public RetentionService(AppConfig cfg, SqliteStore store, DataPurger purger, string dataDir, IClock? clock = null);
    public bool Enabled { get; }
    public DateOnly? LastCutoff { get; }
    public PurgeResult? RunIfNeeded();
}

public static class SingleInstance
{
    public static SingleInstanceGuard? TryAcquire(string appName);   // Local\ 前缀 Mutex
}
public static class AutostartService
{
    public const string TaskName = "KeyMouseCounter";
    public const string NeedElevation;              // 未提权时的中文提示（含操作指引）
    public static string ExecutablePath { get; }
    public static bool IsElevated();                // 计划任务注册/注销要求管理员权限
    public static bool IsEnabled(out string? error);
    public static string? Register();      // null = 成功；未提权返回 NeedElevation
    public static string? Unregister();
}
```

**契约约束**：`FlushService` 的刷盘顺序不可颠倒；`Flushed` 事件在 UI 线程外触发，订阅方需自行 marshal。

---

## 6. `Export`

```csharp
public static class CsvExporter
{
    public static void ExportDays(string path, IEnumerable<DayRow> rows);
    public static void ExportHours(string path, DateOnly day, IEnumerable<HourPoint> rows);
    public static void ExportMinutes(string path, DateOnly day, IEnumerable<MinutePoint> rows,
                                     bool includeZero = false);
    public static string SuggestFileName(string kind, DateOnly from, DateOnly? to = null);
}
```

**契约约束**：编码必须是 `new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)`（**with BOM**，Excel 才不乱码）；
行尾 CRLF；表头逐字对照文档 §6.9。

---

## 7. `Charts`

```csharp
public abstract class ChartData { public string EmptyText = "暂无数据"; public abstract bool IsEmpty { get; } }
public sealed class SeriesChartData : ChartData { Xs, Ys, Labels, AsBar, XLabel, YLabel,
                                                  SeriesName, HighlightIndex, HighlightText, YFormat }
public sealed class HeatmapChartData : ChartData { Rows, Cols, Values(long[,]), RowLabels,
                                                   ColumnLabels, RowAxisTitle, ColumnAxisTitle,
                                                   MetricName, Tooltip, Levels }
public sealed class TableChartData : ChartData { Headers, Rows(List<object?[]>), Format, ColumnWidths }

public interface IChartView { Control Host { get; } void Render(ChartData data); }
// 实现：LineChartView / HeatmapView / TableChartView（ScottPlot 5.0.56），
// 或按 §7.5 的 GDI+ 降级实现，主窗口只认接口。

public static class ChartDataBuilder   // 纯函数，便于单测
{
    public static SeriesChartData HourSeries(IReadOnlyList<HourPoint> hours, Metric metric,
                                             int? highlightHour = null, bool asBar = false);
    public static SeriesChartData MinuteSeries(...);
    public static SeriesChartData DaySeries(IReadOnlyList<DayPoint> days, Metric metric, bool asBar = false);
    public static HeatmapChartData MinuteHeat(IReadOnlyList<MinutePoint> minutes, Metric metric, DateOnly day);
    public static HeatmapChartData DayHeat(HeatMatrix matrix);
    public static TableChartData MinuteTable(IReadOnlyList<MinutePoint> minutes, Metric metric, bool includeZero = false);
    public static TableChartData DayTable(IReadOnlyList<DayPoint> days, Metric metric);
    public static PeriodSummary Summarize(IReadOnlyList<DayPoint> days, Metric metric);
    public static PeriodSummary SummarizeHours(IReadOnlyList<HourPoint> hours, Metric metric);
    public static PeriodSummary SummarizeMinutes(IReadOnlyList<MinutePoint> minutes, Metric metric);
}
public static class ColorRamp   // §15.4：Stops + BuildLevels(P50/P75/P90/P99) + Pick（注意 Stops[i+1]）
```

---

## 8. `UI`：装配与窗口

```csharp
internal sealed class AppHost : ApplicationContext
{
    public static AppHost Create(CommandLineOptions options);
    public void Start();                 // 重建 → 保留期 → 刷盘 → 钩子 → 热键 → 托盘 → 挂件 → 主窗口
    public void Shutdown();              // 逆序收尾
    public void ExitApplication();
    public void ShowMain(bool openExport = false);
    public void OpenSettings(string? page);
    public void OpenDataDir();
    public void TogglePausePublic();
    public void ApplyWidgetConfig();
    public void ResetWidgetPosition();
    public bool IsAutostartEnabled(out string? error);
    public string? SetAutostart(bool enabled);
    public string? SetHotkey(string spec);
    public static bool IsHotkeyAvailable(string spec, out string? error);
    public PurgeResult PurgeDayNow(DateOnly day);
    public PurgeResult PurgeRangeNow(DateOnly from, DateOnly to);
    public PurgeResult PurgeAllNow();
    public ReconcileReport RebuildFromLogs(TimeSpan? budget = null);
    public void RefreshAfterDataChange();
    public void SetWidgetWarning(string? text);
    public void SaveConfig();
    public void ApplyTheme(string theme);
    // 只读门面：Config / Query / Flusher / Purger / Store / Engine / Reconciler / Uptime / TodayLogLines
}

internal sealed class MainForm : Form
{
    public MainForm(AppHost host);
    public void ShowDay(DateOnly day);
    public void TriggerRefresh();
    public void UpdatePausedUi(bool paused);
    public void ReloadConfig();
    public void ApplyTheme();
    public void RefreshDashboard();
    public void RunExport();
    public void OpenSettings(string? page);
    public void ShowAboutPage();
    public void PurgeToday(bool confirmInUi);
    public void ForceClose();
}

internal sealed class WidgetForm : Form
{
    internal readonly record struct WidgetData(long Key, long Click, long Wheel, long ActiveSeconds, bool Paused);
    public void StartTicking(); public void StopTicking(); public void RefreshNow();
    public void ShowWidget(bool firstFade); public void HideWidget();
    public void ToggleClickThrough(); public void ApplyOpacity(double value, bool save);
    public void ApplyConfig(); public void ApplyTheme(); public void ResetPosition();
    public void SetWarning(string? text); public void ForceClose();
}

internal sealed class SettingsForm : Form { public SettingsForm(AppHost host, string? initialPage); }

internal static class Dialogs
{
    public sealed class DatePickerDialog : Form { DateOnly SelectedDate; }
    public sealed class RangeDialog : Form { DateOnly FromDate, ToDate; }
    public static class ConfirmDialog { public static bool Ask(IWin32Window owner, string title, string message, bool requireWord = true); }
    public static class InputDialog   { public static string? Ask(IWin32Window owner, string title, string label, string initial); }
}
```

---

## 9. 变更流程

1. 改任何 `★` 契约 → 同步更新本文件 + `docs/DESIGN.md` + 相关单测；
2. 改 `CounterEngine` 口径 → 先改/加 T01–T15 单测，红了不许合；
3. 改 JSONL 或 DDL → 必须新增 T16–T21 往返/重建用例并保持旧日志可读；
4. 全量验证：`dotnet build -c Release && dotnet test -c Release`（当前 31/31 绿）。

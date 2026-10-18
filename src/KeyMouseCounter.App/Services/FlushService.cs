using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.Services;

/// <summary>
/// 刷盘服务（§5.6、§6.5）。顺序永远是：内存快照 → 日志 → 数据库。
///
/// 队列拆成两段保证"日志是事实来源"且不会重复写增量：
/// - <c>_logQueue</c>：还没进日志的增量；
/// - <c>_dbQueue</c>：已经进日志、还没进库的增量。
/// 日志写失败 → 记 log_gap，数据留在 _logQueue 待重试（不写库，避免库里出现日志里没有的数据）。
/// </summary>
public sealed class FlushService : IDisposable
{
    private sealed class Batch
    {
        public DateOnly Day;
        public readonly SortedDictionary<int, MinuteBuckets> Items = new();
        public DateTimeOffset FirstFailureUtc;
        public int FailCount;

        public MinuteBuckets Total
        {
            get
            {
                var t = default(MinuteBuckets);
                foreach (var b in Items.Values) t.AddFrom(b);
                return t;
            }
        }
    }

    private readonly CounterEngine _engine;
    private readonly AppConfig _cfg;
    private readonly SqliteStore _store;
    private readonly DailyLogWriter _writer;
    private readonly IClock _clock;
    private readonly object _gate = new();

    private readonly List<Batch> _logQueue = new();
    private readonly List<Batch> _dbQueue = new();

    private System.Threading.Timer? _timer;
    private volatile bool _disposed;

    /// <summary>每次成功刷盘后触发（UI 用来刷新卡片/图表）。</summary>
    public event Action? Flushed;

    public FlushService(CounterEngine engine, AppConfig cfg, SqliteStore store,
                        DailyLogWriter writer, IClock? clock = null)
    {
        _engine = engine;
        _cfg = cfg;
        _store = store;
        _writer = writer;
        _clock = clock ?? SystemClock.Instance;
    }

    public long LastFlushMs { get; private set; }
    public int PendingBatches
    {
        get { lock (_gate) return _logQueue.Count + _dbQueue.Count; }
    }

    public void Start()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _cfg.Storage.FlushIntervalSec));
        _timer = new System.Threading.Timer(_ => SafeFlush(), null, interval, interval);
        Log.Info($"flush service started (every {interval.TotalSeconds:0}s)");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void SafeFlush()
    {
        try { FlushNow(); }
        catch (Exception ex) { Log.Error("flush failed", ex); }
    }

    /// <summary>立即刷盘（UI 打开/切标签/关闭前都调用它，§7.1 刷新）。</summary>
    public void FlushNow()
    {
        if (_disposed) return;

        long start = _clock.Ticks;
        bool any = false;

        lock (_gate)
        {
            MergeSnapshotLocked();

            // ① 日志段
            if (_cfg.Storage.WriteDailyLog)
            {
                for (int i = _logQueue.Count - 1; i >= 0; i--)
                {
                    var batch = _logQueue[i];
                    try
                    {
                        AppendToLogLocked(batch);
                        _dbQueue.Add(batch);
                        _logQueue.RemoveAt(i);
                        any = true;
                    }
                    catch (Exception ex)
                    {
                        batch.FailCount++;
                        if (batch.FirstFailureUtc == default) batch.FirstFailureUtc = DateTimeOffset.UtcNow;
                        var stuck = DateTimeOffset.UtcNow - batch.FirstFailureUtc;
                        if (stuck > TimeSpan.FromHours(1))
                        {
                            // 内存队列最多扛 1 小时：超时后宁可入库（日志留缺口），也不能丢数据
                            Log.Warn($"日志写入持续失败 {stuck.TotalMinutes:0}min，改为只写数据库（{batch.Day:yyyy-MM-dd}）");
                            try
                            {
                                _store.AddLogGap(batch.Day, "log write failed > 1h");
                                _dbQueue.Add(batch);
                                _logQueue.RemoveAt(i);
                                any = true;
                            }
                            catch { /* 下一轮再试 */ }
                        }
                        else
                        {
                            Log.Warn($"日志写入失败（{batch.Day:yyyy-MM-dd}，第 {batch.FailCount} 次）：{ex.Message}");
                            try { _store.AddLogGap(batch.Day, ex.Message); } catch { }
                        }
                    }
                }
            }
            else
            {
                // 关闭日志时直接进库
                _dbQueue.AddRange(_logQueue);
                _logQueue.Clear();
            }

            // ② 数据库段
            for (int i = _dbQueue.Count - 1; i >= 0; i--)
            {
                var batch = _dbQueue[i];
                try
                {
                    PersistToDbLocked(batch);
                    _dbQueue.RemoveAt(i);
                    any = true;
                }
                catch (Exception ex)
                {
                    batch.FailCount++;
                    Log.Warn($"数据库写入失败（{batch.Day:yyyy-MM-dd}，第 {batch.FailCount} 次）：{ex.Message}");
                }
            }
        }

        LastFlushMs = (_clock.Ticks - start) * 1000 / _clock.Frequency;

        if (any)
        {
            try { Flushed?.Invoke(); } catch (Exception ex) { Log.Warn($"Flushed handler failed: {ex.Message}"); }
        }
    }

    private void MergeSnapshotLocked()
    {
        var snapshot = _engine.TakeSnapshot();
        if (snapshot.Count == 0) return;

        foreach (var (day, minute, data) in snapshot)
        {
            if (data.IsZero) continue;

            // 只在 _logQueue 里找批次：已经进过日志的批次不能再吸收新数据，
            // 否则这些新数据只会进库、不进日志，重建时就会凭空消失。
            var batch = _cfg.Storage.WriteDailyLog
                ? _logQueue.Find(b => b.Day == day)
                : _dbQueue.Find(b => b.Day == day);

            if (batch is null)
            {
                batch = new Batch { Day = day };
                if (_cfg.Storage.WriteDailyLog) _logQueue.Add(batch);
                else _dbQueue.Add(batch);
            }

            batch.Items.TryGetValue(minute, out var existing);
            existing.AddFrom(data);
            batch.Items[minute] = existing;
        }
    }

    private void AppendToLogLocked(Batch batch)
    {
        var items = ToList(batch.Items);
        long newLength = _writer.Append(batch.Day, items);

        var prev = _store.GetCursor(batch.Day) ?? new LogCursor(0, 0, 0, 0);
        var total = batch.Total;
        _store.SetCursor(batch.Day, new LogCursor(
            newLength,
            prev.LineCount + items.Count,
            prev.TotalKey + total.K,
            prev.TotalClick + total.Clicks));
        _store.RemoveLogGap(batch.Day);

        // 非当天的日期顺手写一份人读的 summary
        if (batch.Day != _engine.CurrentDay)
        {
            DailyLogWriter.WriteSummary(_dataDir, batch.Day, total, batch.Items.Count);
        }
    }

    private string _dataDir = AppPaths.DataDir;
    /// <summary>summary 文件目录，默认 data 目录；测试可覆盖。</summary>
    public string DataDir
    {
        get => _dataDir;
        set => _dataDir = value;
    }

    private void PersistToDbLocked(Batch batch)
    {
        var items = ToList(batch.Items);
        if (items.Count == 0) return;

        int first = int.MaxValue, last = int.MinValue;
        foreach (var (m, _) in items)
        {
            if (m < first) first = m;
            if (m > last) last = m;
        }
        int offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
        _store.SaveBatch(batch.Day, items, offset, HhMm(first), HhMm(last));
    }

    private static string HhMm(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private static List<(int Minute, MinuteBuckets Data)> ToList(SortedDictionary<int, MinuteBuckets> map)
    {
        var list = new List<(int, MinuteBuckets)>(map.Count);
        foreach (var kv in map) list.Add((kv.Key, kv.Value));
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { FlushNow(); } catch (Exception ex) { Log.Error("final flush failed", ex); }
        _writer.Close();
    }
}

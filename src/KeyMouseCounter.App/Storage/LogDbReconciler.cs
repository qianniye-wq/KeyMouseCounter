using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Storage;

public sealed class ReconcileReport
{
    public int DaysScanned { get; set; }
    public int DaysFullImported { get; set; }
    public int DaysIncremental { get; set; }
    public int DaysSkippedGap { get; set; }
    public int DaysFailed { get; set; }
    public int OrphanDbDays { get; set; }
    public bool BudgetExceeded { get; set; }
    public long ElapsedMs { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// 日志 → SQLite 重建（§6.6）。日志是事实来源，DB 是派生数据。
///
/// 幂等性靠 token 保证：每个日期用 (line_count, total_key, total_click) 标记"已导入到哪一行"，
/// 重复执行同一天不会重复累加。
/// </summary>
public sealed class LogDbReconciler
{
    private readonly SqliteStore _store;
    private readonly string _dataDir;
    private readonly IClock _clock;

    public LogDbReconciler(SqliteStore store, string dataDir, IClock? clock = null)
    {
        _store = store;
        _dataDir = dataDir;
        _clock = clock ?? SystemClock.Instance;
    }

    public ReconcileReport Reconcile(TimeSpan budget, CancellationToken ct = default)
    {
        var report = new ReconcileReport();
        long start = _clock.Ticks;

        var logDays = DailyLogReader.ListLogDays(_dataDir);
        var dbDays = _store.GetDayDates();
        var dbSet = new HashSet<DateOnly>(dbDays);

        double ElapsedMs() => (_clock.Ticks - start) * 1000.0 / _clock.Frequency;

        foreach (var day in logDays)
        {
            if (ct.IsCancellationRequested) break;
            if (ElapsedMs() > budget.TotalMilliseconds)
            {
                report.BudgetExceeded = true;
                report.Notes.Add($"重建超出预算 {budget.TotalSeconds:0}s，剩余日期留到后台继续");
                break;
            }

            report.DaysScanned++;
            dbSet.Remove(day);

            // 4) 有 log_gap 的日期跳过（那次写入失败的数据无法还原）
            if (_store.HasLogGap(day))
            {
                report.DaysSkippedGap++;
                report.Notes.Add($"{day:yyyy-MM-dd} 存在 log_gap，跳过重建");
                continue;
            }

            var path = DailyLogWriter.PathFor(_dataDir, day);
            try
            {
                long repaired = DailyLogReader.RepairTail(path);
                var mismatches = DailyLogReader.CountDateMismatches(path, day);
                if (mismatches > 0)
                {
                    report.Notes.Add($"{day:yyyy-MM-dd} 有 {mismatches} 行 date 与文件名不一致，以文件名为准");
                }

                long rows = _store.CountMinuteRows(day);
                var cursor = _store.GetCursor(day);

                if (rows == 0 && cursor is null)
                {
                    // 全量导入
                    var parsed = DailyLogReader.ParseFile(path, day);
                    _store.ReplaceDay(day, ToItems(parsed), Offset());
                    _store.SetCursor(day, new LogCursor(repaired, parsed.LineCount, parsed.Total.K, parsed.Total.Clicks));
                    report.DaysFullImported++;
                }
                else if (cursor.HasValue && cursor.Value.ByteOffset > 0 && cursor.Value.ByteOffset <= repaired)
                {
                    // 增量导入：只读 cursor.byte_offset 之后的部分，避免 O(全天) 重扫
                    var cur = cursor.Value;
                    if (cur.ByteOffset < repaired)
                    {
                        var tail = DailyLogReader.ParseFrom(path, cur.ByteOffset, day);
                        var items = ToItems(tail);
                        if (items.Count > 0)
                        {
                            _store.SaveBatch(day, items, Offset(), null, null);
                        }
                        _store.SetCursor(day, new LogCursor(repaired,
                            cur.LineCount + tail.LineCount,
                            cur.TotalKey + tail.Total.K,
                            cur.TotalClick + tail.Total.Clicks));
                        report.DaysIncremental++;
                    }
                }
                else
                {
                    // 有 DB 数据但没有游标（v1 升级/游标丢失）：全量重算后整体替换，天然幂等
                    var parsed = DailyLogReader.ParseFile(path, day);
                    _store.ReplaceDay(day, ToItems(parsed), Offset());
                    _store.SetCursor(day, new LogCursor(repaired, parsed.LineCount, parsed.Total.K, parsed.Total.Clicks));
                    report.DaysFullImported++;
                }

                if (_store.CountMinuteRows(day) == 0 && _store.GetCursor(day) is null)
                    report.Notes.Add($"{day:yyyy-MM-dd} 日志为空");
            }
            catch (Exception ex)
            {
                report.DaysFailed++;
                report.Notes.Add($"{day:yyyy-MM-dd} 重建失败：{ex.Message}");
                Log.Error($"reconcile failed for {day:yyyy-MM-dd}", ex);
            }
        }

        // 6) 库里有、日志没有的日期
        foreach (var day in dbSet)
        {
            if (day == DateOnly.FromDateTime(DateTime.Now))
            {
                report.Notes.Add("今日日志尚未建立，属正常情况");
                continue;
            }
            report.OrphanDbDays++;
            report.Notes.Add($"{day:yyyy-MM-dd} 仅有数据库数据、无日志文件");
        }

        report.ElapsedMs = (long)ElapsedMs();
        Log.Info($"reconcile done days={report.DaysScanned} full={report.DaysFullImported} " +
                 $"inc={report.DaysIncremental} gap={report.DaysSkippedGap} failed={report.DaysFailed} " +
                 $"elapsed={report.ElapsedMs}ms");
        return report;
    }

    private static List<(int Minute, MinuteBuckets Data)> ToItems(LogParseResult parsed)
    {
        var items = new List<(int, MinuteBuckets)>(parsed.Minutes.Count);
        foreach (var kv in parsed.Minutes)
        {
            if (kv.Value.IsZero) continue;
            items.Add((kv.Key, kv.Value));
        }
        return items;
    }

    private static int Offset() => (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
}

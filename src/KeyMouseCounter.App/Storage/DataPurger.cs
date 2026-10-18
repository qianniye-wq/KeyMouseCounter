using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Storage;

public sealed class PurgeResult
{
    public int DaysRemoved { get; set; }
    public int FilesRemoved { get; set; }
    public string Scope { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}

/// <summary>
/// 数据删除（§6.7）。删除必须同时覆盖：内存桶、JSONL、SQLite、log_cursor，
/// 否则"清空今日后重启"会被日志复活（§15.5 坑 10）。
/// </summary>
public sealed class DataPurger
{
    private readonly SqliteStore _store;
    private readonly string _dataDir;
    private readonly CounterEngine _engine;
    private readonly DailyLogWriter _writer;

    public DataPurger(SqliteStore store, string dataDir, CounterEngine engine, DailyLogWriter writer)
    {
        _store = store;
        _dataDir = dataDir;
        _engine = engine;
        _writer = writer;
    }

    /// <summary>清空指定日期（默认今日）。</summary>
    public PurgeResult PurgeDay(DateOnly day)
    {
        var result = new PurgeResult { Scope = "day", From = day, To = day };

        _engine.ClearDay(day);
        _store.DeleteDay(day);
        _store.DeleteCursor(day);
        _store.RemoveLogGap(day);

        // 先关句柄再删文件，否则 Windows 会拒绝删除
        _writer.Close();
        var path = DailyLogWriter.PathFor(_dataDir, day);
        if (File.Exists(path))
        {
            try { File.Delete(path); result.FilesRemoved++; }
            catch (Exception ex) { Log.Warn($"日志删除失败 {Path.GetFileName(path)}: {ex.Message}"); }
        }
        var summary = Path.Combine(_dataDir, $"{day:yyyy-MM-dd}.summary.json");
        if (File.Exists(summary))
        {
            try { File.Delete(summary); result.FilesRemoved++; } catch { }
        }

        _store.WritePurgeLog("day", day, day);
        result.DaysRemoved = 1;
        Log.Info($"purged day {day:yyyy-MM-dd}");
        return result;
    }

    /// <summary>清空区间内的所有日期。</summary>
    public PurgeResult PurgeRange(DateOnly from, DateOnly to)
    {
        var result = new PurgeResult { Scope = "range", From = from, To = to };

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var logPath = DailyLogWriter.PathFor(_dataDir, d);
            bool existed = _store.HasDayRow(d) || File.Exists(logPath);

            _engine.ClearDay(d);
            _store.DeleteCursor(d);
            _store.RemoveLogGap(d);
            _writer.Close();

            if (File.Exists(logPath))
            {
                try { File.Delete(logPath); result.FilesRemoved++; } catch { }
            }
            var summary = Path.Combine(_dataDir, $"{d:yyyy-MM-dd}.summary.json");
            if (File.Exists(summary))
            {
                try { File.Delete(summary); } catch { }
            }
            if (existed) result.DaysRemoved++;    // 只统计真的有数据的日期，区间里没数据的空日不计
        }

        _store.DeleteRange(from, to);
        _store.WritePurgeLog("range", from, to);
        Log.Info($"purged range {from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
        return result;
    }

    /// <summary>清空全部数据（§6.7 清空全部）。调用方负责先停钩子、后重启。</summary>
    public PurgeResult PurgeAll()
    {
        var result = new PurgeResult { Scope = "all", From = DateOnly.MinValue, To = DateOnly.MaxValue };

        _engine.ResetAll();
        _writer.Close();
        _store.DeleteAll();

        foreach (var f in Directory.EnumerateFiles(_dataDir, "*.jsonl"))
        {
            try { File.Delete(f); result.FilesRemoved++; } catch { }
        }
        foreach (var f in Directory.EnumerateFiles(_dataDir, "*.summary.json"))
        {
            try { File.Delete(f); } catch { }
        }

        _store.WritePurgeLog("all", DateOnly.FromDateTime(DateTime.Now), DateOnly.FromDateTime(DateTime.Now));
        Log.Info("purged all data");
        return result;
    }
}

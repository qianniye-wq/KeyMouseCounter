using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.Services;

/// <summary>
/// 保留天数（§6.8）。dataRetentionDays &gt; 0 时，启动与每天跨天执行一次，
/// 删除早于 today - N 的 DB 行与日志文件，并写 purge_log（scope='range'）。
/// </summary>
public sealed class RetentionService
{
    private readonly AppConfig _cfg;
    private readonly SqliteStore _store;
    private readonly DataPurger _purger;
    private readonly string _dataDir;
    private readonly IClock _clock;

    public RetentionService(AppConfig cfg, SqliteStore store, DataPurger purger, string dataDir, IClock? clock = null)
    {
        _cfg = cfg;
        _store = store;
        _purger = purger;
        _dataDir = dataDir;
        _clock = clock ?? SystemClock.Instance;
        Enabled = cfg.Storage.DataRetentionDays > 0;
    }

    public bool Enabled { get; private set; }
    public DateOnly? LastCutoff { get; private set; }

    public PurgeResult? RunIfNeeded()
    {
        int days = _cfg.Storage.DataRetentionDays;
        if (days <= 0)
        {
            Enabled = false;
            return null;
        }
        Enabled = true;

        var today = DateOnly.FromDateTime(_clock.Now.LocalDateTime);
        var cutoff = today.AddDays(-days);
        if (cutoff <= DateOnly.MinValue) return null;
        if (LastCutoff is { } last && last == cutoff) return null;

        var oldest = _store.GetDayDates().FirstOrDefault();
        var logDays = DailyLogReader.ListLogDays(_dataDir);
        var oldestLog = logDays.Count > 0 ? logDays[0] : today;

        var from = oldest < oldestLog ? oldest : oldestLog;
        if (from == default || from >= cutoff)
        {
            LastCutoff = cutoff;
            return null;
        }

        var to = cutoff.AddDays(-1);
        if (from > to)
        {
            LastCutoff = cutoff;
            return null;
        }

        Log.Info($"retention: removing {from:yyyy-MM-dd}..{to:yyyy-MM-dd} (keep {days} days)");
        var result = _purger.PurgeRange(from, to);
        LastCutoff = cutoff;
        return result;
    }
}

using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Services;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.Tests;

/// <summary>§11.1 T16–T22：日志往返、截断、重建、清空、保留期。</summary>
public sealed class StorageTests
{
    private static readonly DateOnly Day1 = new(2025, 3, 1);
    private static readonly DateOnly Day2 = new(2025, 3, 2);

    private static List<(int Minute, MinuteBuckets Data)> Samples(int minute)
        => new()
        {
            (minute, MinuteBuckets.From(k: 3, ml: 1, mr: 0, mm: 0, wu: 1, wd: 0, wl: 0, wr: 0, activeSeconds: 10)),
            (minute + 1, MinuteBuckets.From(k: 5, ml: 2, mr: 1, mm: 0, wu: 0, wd: 2, wl: 0, wr: 0, activeSeconds: 20)),
            (minute + 2, MinuteBuckets.From(k: 2, ml: 0, mr: 0, mm: 1, wu: 3, wd: 0, wl: 0, wr: 0, activeSeconds: 5))
        };

    // T16 日志往返：写 5 行增量 → 解析后求和与写入值一致
    [Fact]
    public void T16_LogRoundTrip_SumMatches()
    {
        using var root = new TempRoot();
        var day = Day1;
        var items = new List<(int Minute, MinuteBuckets Data)>();
        for (int i = 0; i < 5; i++)
            items.Add((600 + i, MinuteBuckets.From(k: 2, ml: 1, mr: 1, mm: 0, wu: 1, wd: 0, wl: 0, wr: 0, activeSeconds: 3)));

        using (var writer = new DailyLogWriter(AppPaths.DataDir, "S-1-5-21-test"))
        {
            writer.Append(day, items);
        }

        var parsed = DailyLogReader.ParseFile(AppPaths.DailyLogPath(day), day);

        Assert.Equal(5, parsed.LinesImported);
        Assert.Equal(5, parsed.LineCount);
        Assert.Equal(10, parsed.Total.K);
        Assert.Equal(5, parsed.Total.ML);
        Assert.Equal(5, parsed.Total.MR);
        Assert.Equal(5, parsed.Total.WU);
        Assert.Equal(15, parsed.Total.AS);
    }

    // T17 同一文件两次追加同一分钟 → 解析求和为两段之和
    [Fact]
    public void T17_TwoAppendsSameMinute_Summed()
    {
        using var root = new TempRoot();
        var day = Day1;
        var first = new List<(int, MinuteBuckets)> { (720, MinuteBuckets.From(4, 1, 0, 0, 0, 0, 0, 0, 6)) };
        var second = new List<(int, MinuteBuckets)> { (720, MinuteBuckets.From(6, 2, 0, 0, 0, 0, 0, 0, 9)) };

        using (var writer = new DailyLogWriter(AppPaths.DataDir, "S-1-5-21-test"))
        {
            writer.Append(day, first);
            writer.Append(day, second);
        }

        var parsed = DailyLogReader.ParseFile(AppPaths.DailyLogPath(day), day);
        Assert.Equal(2, parsed.LinesImported);
        Assert.Equal(10, parsed.Minutes[720].K);
        Assert.Equal(3, parsed.Minutes[720].ML);
        Assert.Equal(15, parsed.Minutes[720].AS);
    }

    // T18 末行截断 → 只丢末行，其余正常解析并报告 TruncatedTail
    [Fact]
    public void T18_TruncatedTail_IgnoredOthersKept()
    {
        using var root = new TempRoot();
        var day = Day1;
        var items = new List<(int, MinuteBuckets)>
        {
            (100, MinuteBuckets.From(1, 0, 0, 0, 0, 0, 0, 0, 0)),
            (101, MinuteBuckets.From(2, 0, 0, 0, 0, 0, 0, 0, 0)),
            (102, MinuteBuckets.From(3, 0, 0, 0, 0, 0, 0, 0, 0))
        };
        using (var writer = new DailyLogWriter(AppPaths.DataDir, "S-1-5-21-test"))
        {
            writer.Append(day, items);
        }

        // 手工追加一个没有换行结尾的半行，模拟断电
        var path = AppPaths.DailyLogPath(day);
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var partial = System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"date\":\"2025-03-01\",\"min\":103,\"k\":");
            fs.Write(partial, 0, partial.Length);
        }

        var parsed = DailyLogReader.ParseFile(path, day);

        Assert.Equal(3, parsed.LinesImported);
        Assert.True(parsed.TruncatedTail);
        Assert.Equal(6, parsed.Total.K);
        Assert.False(parsed.Minutes.ContainsKey(103));
    }

    // T19 清空数据库后从日志重建 → 数值与重建前一致
    [Fact]
    public void T19_RebuildFromLog_RestoresSameValues()
    {
        using var root = new TempRoot();
        var day = Day1;
        using (var writer = new DailyLogWriter(AppPaths.DataDir, "S-1-5-21-test"))
        {
            writer.Append(day, Samples(600));
        }

        using var store = new SqliteStore(AppPaths.DbPath);
        store.Open();
        var reconciler = new LogDbReconciler(store, AppPaths.DataDir);

        var first = reconciler.Reconcile(TimeSpan.FromSeconds(30));
        Assert.Equal(1, first.DaysFullImported);

        var before = store.GetMinutes(day);
        Assert.Equal(3, before.Count);

        // 模拟"数据库丢了、日志还在"
        store.DeleteAll();
        store.DeleteCursor(day);
        Assert.Equal(0, store.CountMinuteRows(day));

        var second = reconciler.Reconcile(TimeSpan.FromSeconds(30));
        Assert.Equal(1, second.DaysFullImported);

        var after = store.GetMinutes(day);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Minute, after[i].Minute);
            Assert.Equal(before[i].Key, after[i].Key);
            Assert.Equal(before[i].Click, after[i].Click);
            Assert.Equal(before[i].Wheel, after[i].Wheel);
            Assert.Equal(before[i].ActiveSeconds, after[i].ActiveSeconds);
        }
    }

    // T20 log_gap 里的日期 → 重建直接跳过，不导入也不报错
    [Fact]
    public void T20_LogGapDay_SkippedByReconcile()
    {
        using var root = new TempRoot();
        var day = Day1;
        using (var writer = new DailyLogWriter(AppPaths.DataDir, "S-1-5-21-test"))
        {
            writer.Append(day, Samples(300));
        }

        using var store = new SqliteStore(AppPaths.DbPath);
        store.Open();
        store.AddLogGap(day, "磁盘满期间日志缺失");

        var report = new LogDbReconciler(store, AppPaths.DataDir).Reconcile(TimeSpan.FromSeconds(30));

        Assert.Equal(1, report.DaysSkippedGap);
        Assert.Equal(0, report.DaysFullImported);
        Assert.Equal(0, store.CountMinuteRows(day));
    }

    // T21 清空今日（DB + 日志）后重启重建 → 数据不会复活
    [Fact]
    public void T21_PurgeToday_DoesNotResurrect()
    {
        using var root = new TempRoot();
        var day = Day1;
        using (var writer = new DailyLogWriter(AppPaths.DataDir, "S-1-5-21-test"))
        {
            writer.Append(day, Samples(900));
        }

        using var store = new SqliteStore(AppPaths.DbPath);
        store.Open();
        new LogDbReconciler(store, AppPaths.DataDir).Reconcile(TimeSpan.FromSeconds(30));
        Assert.Equal(3, store.CountMinuteRows(day));

        var cfg = new AppConfig();
        var engine = new CounterEngine(cfg);
        using var logWriter = new DailyLogWriter(AppPaths.DataDir, store.UserSid);
        var purger = new DataPurger(store, AppPaths.DataDir, engine, logWriter);
        var result = purger.PurgeDay(day);

        Assert.Equal(1, result.DaysRemoved);
        Assert.Equal(0, store.CountMinuteRows(day));
        Assert.False(File.Exists(AppPaths.DailyLogPath(day)));

        // 重启后重建，依然没有数据
        var report = new LogDbReconciler(store, AppPaths.DataDir).Reconcile(TimeSpan.FromSeconds(30));
        Assert.Equal(0, report.DaysFullImported);
        Assert.Equal(0, report.DaysIncremental);
        Assert.Equal(0, store.CountMinuteRows(day));
    }

    // T22 保留 30 天 → 只删 30 天前的整日，近期数据保留
    [Fact]
    public void T22_Retention_DeletesOnlyExpiredDays()
    {
        using var root = new TempRoot();
        var today = new DateOnly(2025, 3, 31);
        var oldDay = new DateOnly(2025, 2, 1);     // 58 天前 → 删
        var recentDay = new DateOnly(2025, 3, 20); // 11 天前 → 留

        using var store = new SqliteStore(AppPaths.DbPath);
        store.Open();
        store.SaveBatch(oldDay, Samples(60), 480, null, null);
        store.SaveBatch(recentDay, Samples(60), 480, null, null);

        var cfg = new AppConfig();
        cfg.Storage.DataRetentionDays = 30;
        var engine = new CounterEngine(cfg);
        using var logWriter = new DailyLogWriter(AppPaths.DataDir, store.UserSid);
        var purger = new DataPurger(store, AppPaths.DataDir, engine, logWriter);
        var clock = new FakeClock(new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(8)));

        var retention = new RetentionService(cfg, store, purger, AppPaths.DataDir, clock);
        var result = retention.RunIfNeeded();

        Assert.NotNull(result);
        Assert.Equal(1, result!.DaysRemoved);
        Assert.Equal(0, store.CountMinuteRows(oldDay));
        Assert.True(store.CountMinuteRows(recentDay) > 0);
    }
}

using KeyMouseCounter.Core;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.Services;

/// <summary>
/// 统计查询实现（§4.3 IStatsQuery）：数据库是历史，内存是尚未刷盘的当日增量，
/// 任何"今日"视图都要把两者合并，否则用户看到的是最多 30 秒前的旧数据（§5.10）。
/// </summary>
public sealed class StatsQuery : IStatsQuery
{
    private readonly SqliteStore _store;
    private readonly CounterEngine _engine;
    private readonly IClock _clock;

    public StatsQuery(SqliteStore store, CounterEngine engine, IClock? clock = null)
    {
        _store = store;
        _engine = engine;
        _clock = clock ?? SystemClock.Instance;
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.Now.LocalDateTime);

    /// <summary>把内存增量并进"今天"，其余日期本来就是库里的事实。</summary>
    private void MergePending(DateOnly day, Action<int, MinuteBuckets> apply)
    {
        if (day != Today) return;
        foreach (var (minute, data) in _engine.PendingMinutes(day))
            apply(minute, data);
    }

    public Task<IReadOnlyList<DayPoint>> GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
        => Task.Run<IReadOnlyList<DayPoint>>(() => GetDays(from, to), ct);

    public List<DayPoint> GetDays(DateOnly from, DateOnly to)
    {
        var rows = _store.GetDays(from, to);
        var byDay = new Dictionary<DateOnly, DayPoint>(rows.Count);
        foreach (var r in rows)
        {
            byDay[r.Day] = new DayPoint(r.Day, r.Total.K, r.Total.Clicks, r.Total.Wheels, r.Total.AS,
                                        r.FirstTs, r.LastTs);
        }

        var today = Today;
        if (today >= from && today <= to)
        {
            var pending = _engine.PendingMinutes(today);
            if (pending.Count > 0)
            {
                byDay.TryGetValue(today, out var cur);
                long k = cur.Key, c = cur.Click, w = cur.Wheel, a = cur.ActiveSeconds;
                int first = int.MaxValue, last = int.MinValue;
                foreach (var (minute, b) in pending)
                {
                    k += b.K; c += b.Clicks; w += b.Wheels; a += b.AS;
                    if (minute < first) first = minute;
                    if (minute > last) last = minute;
                }
                // 内存里的分钟一定比库里更新（库里是更早刷盘的），所以 last 直接用内存的
                var firstTs = cur.FirstTs;
                int storedFirst = ParseMinute(firstTs);
                if (storedFirst >= 0 && first < storedFirst) firstTs = HhMm(first);
                else if (firstTs is null && first != int.MaxValue) firstTs = HhMm(first);
                var lastTs = last != int.MinValue ? HhMm(last) : cur.LastTs;

                byDay[today] = new DayPoint(today, k, c, w, a, firstTs, lastTs);
            }
        }

        var list = new List<DayPoint>(byDay.Count);
        for (var d = from; d <= to; d = d.AddDays(1))
            if (byDay.TryGetValue(d, out var p)) list.Add(p);
        return list;
    }

    /// <summary>
    /// 按天导出用：返回带完整分项（左/右/中键、滚轮上下左右）的 DayRow，
    /// 并把今天未刷盘的内存增量并进去（CSV 与界面看到的一致）。
    /// </summary>
    public List<DayRow> GetDayRows(DateOnly from, DateOnly to)
    {
        var totals = new Dictionary<DateOnly, MinuteBuckets>();
        var firsts = new Dictionary<DateOnly, string?>();
        var lasts = new Dictionary<DateOnly, string?>();
        var offsets = new Dictionary<DateOnly, int>();
        foreach (var r in _store.GetDays(from, to))
        {
            totals[r.Day] = r.Total;
            firsts[r.Day] = r.FirstTs;
            lasts[r.Day] = r.LastTs;
            offsets[r.Day] = r.UpdatedAtOffsetMin;
        }

        var today = Today;
        if (today >= from && today <= to)
        {
            var pending = _engine.PendingMinutes(today);
            if (pending.Count > 0)
            {
                totals.TryGetValue(today, out var bucket);
                firsts.TryGetValue(today, out var curFirst);
                lasts.TryGetValue(today, out var curLast);
                int first = int.MaxValue, last = int.MinValue;
                foreach (var (minute, b) in pending)
                {
                    bucket.AddFrom(b);
                    if (minute < first) first = minute;
                    if (minute > last) last = minute;
                }
                totals[today] = bucket;

                int storedFirst = ParseMinute(curFirst);
                if (storedFirst >= 0 && first < storedFirst) curFirst = HhMm(first);
                else if (curFirst is null && first != int.MaxValue) curFirst = HhMm(first);
                if (last != int.MinValue) curLast = HhMm(last);
                firsts[today] = curFirst;
                lasts[today] = curLast;
                offsets[today] = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
            }
        }

        var list = new List<DayRow>(totals.Count);
        for (var d = from; d <= to; d = d.AddDays(1))
            if (totals.TryGetValue(d, out var total))
                list.Add(new DayRow(d, total, firsts[d], lasts[d], offsets[d]));
        return list;
    }

    public Task<IReadOnlyList<HourPoint>> GetHoursAsync(DateOnly day, CancellationToken ct)
        => Task.Run<IReadOnlyList<HourPoint>>(() => GetHours(day), ct);

    public List<HourPoint> GetHours(DateOnly day)
    {
        var map = _store.GetHours(day).ToDictionary(h => h.Hour);
        MergePending(day, (minute, b) =>
        {
            int h = minute / 60;
            map.TryGetValue(h, out var cur);
            map[h] = new HourPoint(h, cur.Key + b.K, cur.Click + b.Clicks,
                                   cur.Wheel + b.Wheels, cur.ActiveSeconds + b.AS);
        });
        var list = new List<HourPoint>(24);
        for (int h = 0; h < 24; h++)
            list.Add(map.TryGetValue(h, out var p) ? p : new HourPoint(h, 0, 0, 0, 0));
        return list;
    }

    public Task<IReadOnlyList<MinutePoint>> GetMinutesAsync(DateOnly day, CancellationToken ct)
        => Task.Run<IReadOnlyList<MinutePoint>>(() => GetMinutes(day), ct);

    public List<MinutePoint> GetMinutes(DateOnly day)
    {
        var dict = new SortedDictionary<int, MinutePoint>();
        foreach (var m in _store.GetMinutes(day)) dict[m.Minute] = m;
        MergePending(day, (minute, b) =>
        {
            dict.TryGetValue(minute, out var cur);
            dict[minute] = new MinutePoint(minute, cur.Key + b.K, cur.Click + b.Clicks,
                                           cur.Wheel + b.Wheels, cur.ActiveSeconds + b.AS);
        });
        return dict.Values.ToList();
    }

    public Task<IReadOnlyList<HeatCell>> GetHeatAsync(DateOnly from, DateOnly to, Metric metric, CancellationToken ct)
        => Task.Run<IReadOnlyList<HeatCell>>(() => GetHeat(from, to, metric), ct);

    public List<HeatCell> GetHeat(DateOnly from, DateOnly to, Metric metric = Metric.Key)
    {
        var dict = new Dictionary<(int Row, int Col), long>();
        foreach (var c in _store.GetHeat(from, to, metric))
            dict[(c.Row, c.Col)] = c.Value;

        var today = Today;
        if (today >= from && today <= to)
        {
            int col = today.DayNumber - from.DayNumber;
            MergePending(today, (minute, b) =>
            {
                int h = minute / 60;
                var key = (h, col);
                dict.TryGetValue(key, out long v);
                dict[key] = v + metric.Value(b.K, b.Clicks, b.Wheels, b.AS);
            });
        }

        return dict.Select(kv => new HeatCell(kv.Key.Row, kv.Key.Col, kv.Value)).ToList();
    }

    public Task<HeatMatrix> GetHeatMatrixAsync(DateOnly from, DateOnly to, Metric metric, CancellationToken ct)
        => Task.Run(() => GetHeatMatrix(from, to, metric), ct);

    /// <summary>多日热力图：24 行（小时）× N 列（日期）。列标签超过 14 天时只写日号。</summary>
    public HeatMatrix GetHeatMatrix(DateOnly from, DateOnly to, Metric metric = Metric.Key)
    {
        int days = to.DayNumber - from.DayNumber + 1;
        if (days < 1) days = 1;

        var values = new long[24, days];
        foreach (var c in GetHeat(from, to, metric))
        {
            if (c.Row is < 0 or > 23 || c.Col < 0 || c.Col >= days) continue;
            values[c.Row, c.Col] = c.Value;
        }

        var rowLabels = new string[24];
        for (int h = 0; h < 24; h++) rowLabels[h] = $"{h:00}";

        bool dense = days <= 14;
        var colLabels = new string[days];
        for (int i = 0; i < days; i++)
        {
            var d = from.AddDays(i);
            colLabels[i] = dense ? $"{d.Month:00}-{d.Day:00}" : $"{d.Day}";
        }

        return new HeatMatrix
        {
            Shape = HeatShape.HoursByDays,
            Rows = 24,
            Cols = days,
            Values = values,
            RowLabels = rowLabels,
            ColumnLabels = colLabels,
            Metric = metric,
            From = from,
            To = to
        };
    }

    public Task<DayPoint> GetDayAsync(DateOnly day, CancellationToken ct)
        => Task.Run(() => GetDay(day), ct);

    public DayPoint GetDay(DateOnly day)
    {
        var list = GetDays(day, day);
        return list.Count > 0 ? list[0] : new DayPoint(day, 0, 0, 0, 0, null, null);
    }

    private static string HhMm(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private static int ParseMinute(string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return -1;
        var parts = hhmm.Split(':');
        if (parts.Length != 2) return -1;
        if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return -1;
        return h * 60 + m;
    }
}

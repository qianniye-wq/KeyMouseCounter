using KeyMouseCounter.Core;

namespace KeyMouseCounter.Charts;

/// <summary>周期汇总（底部摘要条 §7.1）。</summary>
public sealed class PeriodSummary
{
    public long Total { get; init; }
    public long Peak { get; init; }
    public string PeakLabel { get; init; } = "—";
    public double Average { get; init; }
    public long ActiveSeconds { get; init; }
    public int Days { get; init; }
    public Metric Metric { get; init; }

    public string TotalText(bool compact) => ValueFormat.Metric(Total, Metric, compact);
    public string PeakText(bool compact) => Peak <= 0 ? "—" : $"{ValueFormat.Metric(Peak, Metric, compact)} ({PeakLabel})";
    public string AverageText(bool compact) => Days <= 0 ? "—" : ValueFormat.Metric((long)Math.Round(Average), Metric, compact);
    public string ActiveText() => ValueFormat.DurationShort(ActiveSeconds);
}

/// <summary>
/// 查询结果 → 图表数据（纯函数，便于单测 T25/T26；不碰数据库、不碰控件）。
/// </summary>
public static class ChartDataBuilder
{
    // ─────────────────────────── 折线 / 柱状 ───────────────────────────

    /// <summary>今日·按小时：24 个点，全部保留（0 也要画出来，曲线才不会断开）。</summary>
    public static SeriesChartData HourSeries(IReadOnlyList<HourPoint> hours, Metric metric,
                                             int? highlightHour = null, bool asBar = false)
    {
        int n = 24;
        var xs = new double[n];
        var ys = new double[n];
        var labels = new string[n];
        for (int h = 0; h < n; h++)
        {
            xs[h] = h;
            labels[h] = $"{h:00}";
            if (h < hours.Count)
            {
                var p = hours[h];
                ys[h] = metric.Value(p.Key, p.Click, p.Wheel, p.ActiveSeconds);
            }
        }
        return new SeriesChartData
        {
            Xs = xs,
            Ys = ys,
            Labels = labels,
            AsBar = asBar,
            XLabel = "小时",
            YLabel = metric.DisplayName(),
            SeriesName = metric.DisplayName(),
            HighlightIndex = highlightHour,
            HighlightText = "当前",
            YFormat = v => ValueFormat.Metric(v, metric),
            EmptyText = "今天还没有记录"
        };
    }

    /// <summary>今日·按分钟：只画有数据的分钟，但 X 轴仍是分钟序号，方便看出空档。</summary>
    public static SeriesChartData MinuteSeries(IReadOnlyList<MinutePoint> minutes, Metric metric,
                                               int? highlightMinute = null, bool asBar = false)
    {
        var dict = new Dictionary<int, long>();
        foreach (var p in minutes)
            dict[p.Minute] = metric.Value(p.Key, p.Click, p.Wheel, p.ActiveSeconds);

        if (dict.Count == 0)
            return new SeriesChartData { EmptyText = "今天还没有记录", YFormat = v => ValueFormat.Metric(v, metric) };

        int first = dict.Keys.Min();
        int last = dict.Keys.Max();
        int n = last - first + 1;
        var xs = new double[n];
        var ys = new double[n];
        var labels = new string[n];
        for (int i = 0; i < n; i++)
        {
            int m = first + i;
            xs[i] = m;
            labels[i] = $"{m / 60:00}:{m % 60:00}";
            ys[i] = dict.TryGetValue(m, out long v) ? v : 0;
        }
        return new SeriesChartData
        {
            Xs = xs,
            Ys = ys,
            Labels = labels,
            AsBar = asBar,
            XLabel = "时间",
            YLabel = metric.DisplayName(),
            SeriesName = metric.DisplayName(),
            HighlightIndex = highlightMinute,
            HighlightText = "当前",
            YFormat = v => ValueFormat.Metric(v, metric),
            EmptyText = "今天还没有记录"
        };
    }

    /// <summary>多日：每天一个点。</summary>
    public static SeriesChartData DaySeries(IReadOnlyList<DayPoint> days, Metric metric, bool asBar = false)
    {
        if (days.Count == 0)
            return new SeriesChartData { EmptyText = "这段时间没有记录", YFormat = v => ValueFormat.Metric(v, metric) };

        var xs = new double[days.Count];
        var ys = new double[days.Count];
        var labels = new string[days.Count];
        for (int i = 0; i < days.Count; i++)
        {
            xs[i] = i;
            labels[i] = $"{days[i].Day.Month:00}-{days[i].Day.Day:00}";
            ys[i] = metric.Value(days[i].Key, days[i].Click, days[i].Wheel, days[i].ActiveSeconds);
        }
        return new SeriesChartData
        {
            Xs = xs,
            Ys = ys,
            Labels = labels,
            AsBar = asBar,
            XLabel = "日期",
            YLabel = metric.DisplayName(),
            SeriesName = metric.DisplayName(),
            YFormat = v => ValueFormat.Metric(v, metric),
            EmptyText = "这段时间没有记录"
        };
    }

    // ─────────────────────────── 热力图 ───────────────────────────

    /// <summary>今日·按分钟热力图：24 行（小时）× 60 列（分钟）。</summary>
    public static HeatmapChartData MinuteHeat(IReadOnlyList<MinutePoint> minutes, Metric metric, DateOnly day)
    {
        var values = new long[24, 60];
        var flat = new List<long>();
        foreach (var p in minutes)
        {
            int h = p.Minute / 60, m = p.Minute % 60;
            if (h is < 0 or > 23 || m is < 0 or > 59) continue;
            long v = metric.Value(p.Key, p.Click, p.Wheel, p.ActiveSeconds);
            values[h, m] = v;
            if (v > 0) flat.Add(v);
        }

        var rowLabels = new string[24];
        for (int h = 0; h < 24; h++) rowLabels[h] = $"{h:00}";
        var colLabels = new string[60];
        for (int m = 0; m < 60; m++) colLabels[m] = m % 10 == 0 ? $"{m:00}" : "";

        var ramp = ColorRamp.FromValues(flat);
        return new HeatmapChartData
        {
            Rows = 24,
            Cols = 60,
            Values = values,
            RowLabels = rowLabels,
            ColumnLabels = colLabels,
            RowAxisTitle = "小时",
            ColumnAxisTitle = "分钟",
            MetricName = metric.DisplayName(),
            Levels = ramp.Levels.ToArray(),
            Tooltip = (r, c, v) => $"{r:00}:{c:00}  {ValueFormat.Metric(v, metric)}",
            EmptyText = "今天还没有记录"
        };
    }

    /// <summary>多日热力图：24 行（小时）× N 列（日期）。</summary>
    public static HeatmapChartData DayHeat(HeatMatrix matrix)
    {
        var flat = new List<long>();
        for (int r = 0; r < matrix.Rows; r++)
            for (int c = 0; c < matrix.Cols; c++)
            {
                long v = matrix.Values[r, c];
                if (v > 0) flat.Add(v);
            }

        var metric = matrix.Metric;
        var ramp = ColorRamp.FromValues(flat);
        return new HeatmapChartData
        {
            Rows = matrix.Rows,
            Cols = matrix.Cols,
            Values = matrix.Values,
            RowLabels = matrix.RowLabels,
            ColumnLabels = matrix.ColumnLabels,
            RowAxisTitle = "小时",
            ColumnAxisTitle = "日期",
            MetricName = metric.DisplayName(),
            Levels = ramp.Levels.ToArray(),
            Tooltip = (r, c, v) =>
            {
                var d = matrix.From.AddDays(c);
                return $"{d:yyyy-MM-dd} {r:00}:00  {ValueFormat.Metric(v, metric)}";
            },
            EmptyText = "这段时间没有记录"
        };
    }

    // ─────────────────────────── 表格 ───────────────────────────

    /// <summary>今日·按分钟明细（默认只显示非零分钟，符合 §7.2.3）。</summary>
    public static TableChartData MinuteTable(IReadOnlyList<MinutePoint> minutes, Metric metric,
                                             bool includeZero = false)
    {
        var rows = new List<object?[]>();
        foreach (var p in minutes)
        {
            long v = metric.Value(p.Key, p.Click, p.Wheel, p.ActiveSeconds);
            if (!includeZero && v == 0 && p.ActiveSeconds == 0) continue;
            rows.Add(new object?[] { p.Minute, p.Key, p.Click, p.Wheel, p.ActiveSeconds });
        }
        return new TableChartData
        {
            Headers = new[] { "时间", "键盘按键", "鼠标点击", "滚轮", "活跃时长" },
            Rows = rows,
            ColumnWidths = new[] { 90, 120, 120, 100, 120 },
            Format = (col, val) => col switch
            {
                0 => FormatMinute((int)val!),
                4 => ValueFormat.Duration((long)val!),
                _ => ValueFormat.Count((long)val!)
            },
            EmptyText = "今天还没有记录"
        };
    }

    /// <summary>多日明细：每天一行，带"较前日"。</summary>
    public static TableChartData DayTable(IReadOnlyList<DayPoint> days, Metric metric)
    {
        var rows = new List<object?[]>();
        long prev = -1;
        foreach (var d in days)
        {
            long v = metric.Value(d.Key, d.Click, d.Wheel, d.ActiveSeconds);
            string delta = prev < 0 ? "—" : ValueFormat.Delta(v, prev);
            rows.Add(new object?[]
            {
                d.Day, d.Key, d.Click, d.Wheel, d.ActiveSeconds,
                d.FirstTs ?? "—", d.LastTs ?? "—", delta
            });
            prev = v;
        }
        return new TableChartData
        {
            Headers = new[] { "日期", "键盘按键", "鼠标点击", "滚轮", "活跃时长", "首次", "末次", "较前日" },
            Rows = rows,
            ColumnWidths = new[] { 110, 110, 110, 90, 110, 80, 80, 90 },
            Format = (col, val) => col switch
            {
                0 => ((DateOnly)val!).ToString("yyyy-MM-dd"),
                4 => ValueFormat.Duration((long)val!),
                5 or 6 or 7 => val?.ToString() ?? "—",
                _ => ValueFormat.Count((long)val!)
            },
            EmptyText = "这段时间没有记录"
        };
    }

    /// <summary>分钟序号 → "08:35"。</summary>
    public static string FormatMinute(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    // ─────────────────────────── 汇总 ───────────────────────────
    public static PeriodSummary Summarize(IReadOnlyList<DayPoint> days, Metric metric)
    {
        long total = 0, peak = 0, active = 0;
        string peakLabel = "—";
        foreach (var d in days)
        {
            long v = metric.Value(d.Key, d.Click, d.Wheel, d.ActiveSeconds);
            total += v;
            active += d.ActiveSeconds;
            if (v > peak)
            {
                peak = v;
                peakLabel = d.Day.ToString("MM-dd");
            }
        }
        return new PeriodSummary
        {
            Total = total,
            Peak = peak,
            PeakLabel = peakLabel,
            Average = days.Count == 0 ? 0 : (double)total / days.Count,
            ActiveSeconds = active,
            Days = days.Count,
            Metric = metric
        };
    }

    /// <summary>今日·按分钟：峰值带时刻，"均值"用非零分钟算（1440 分母没有意义）。</summary>
    public static PeriodSummary SummarizeMinutes(IReadOnlyList<MinutePoint> minutes, Metric metric)
    {
        long total = 0, peak = 0, active = 0;
        int peakMinute = -1, nonZero = 0;
        foreach (var m in minutes)
        {
            long v = metric.Value(m.Key, m.Click, m.Wheel, m.ActiveSeconds);
            total += v;
            active += m.ActiveSeconds;
            if (v > 0) nonZero++;
            if (v > peak) { peak = v; peakMinute = m.Minute; }
        }
        return new PeriodSummary
        {
            Total = total,
            Peak = peak,
            PeakLabel = peakMinute < 0 ? "—" : FormatMinute(peakMinute),
            Average = nonZero == 0 ? 0 : (double)total / nonZero,
            ActiveSeconds = active,
            Days = 1,
            Metric = metric
        };
    }

    public static PeriodSummary SummarizeHours(IReadOnlyList<HourPoint> hours, Metric metric)
    {
        long total = 0, peak = 0, active = 0;
        int peakHour = -1;
        foreach (var h in hours)
        {
            long v = metric.Value(h.Key, h.Click, h.Wheel, h.ActiveSeconds);
            total += v;
            active += h.ActiveSeconds;
            if (v > peak) { peak = v; peakHour = h.Hour; }
        }
        return new PeriodSummary
        {
            Total = total,
            Peak = peak,
            PeakLabel = peakHour < 0 ? "—" : $"{peakHour:00}:00",
            Average = hours.Count == 0 ? 0 : (double)total / hours.Count,
            ActiveSeconds = active,
            Days = 1,
            Metric = metric
        };
    }
}

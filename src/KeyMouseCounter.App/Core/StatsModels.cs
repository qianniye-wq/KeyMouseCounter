namespace KeyMouseCounter.Core;

/// <summary>多日视图的一行（按天聚合）。</summary>
public readonly record struct DayPoint(
    DateOnly Day, long Key, long Click, long Wheel, long ActiveSeconds, string? FirstTs, string? LastTs);

/// <summary>今日·按小时视图的一行。</summary>
public readonly record struct HourPoint(
    int Hour, long Key, long Click, long Wheel, long ActiveSeconds);

/// <summary>今日·按分钟视图的一行。</summary>
public readonly record struct MinutePoint(
    int Minute, long Key, long Click, long Wheel, long ActiveSeconds);

/// <summary>热力图单元格：Row/Col 的含义由 MatrixShape 决定。</summary>
public readonly record struct HeatCell(int Row, int Col, long Value);

/// <summary>热力图矩阵尺寸与行列含义（§7.2.2）。</summary>
public enum HeatShape
{
    /// <summary>24 行（小时）× 60 列（分钟）——今日·按分钟。</summary>
    HoursByMinutes,
    /// <summary>24 行（小时）× N 列（日期）——多日。</summary>
    HoursByDays,
    /// <summary>7 行（星期）× ⌈N/7⌉ 列——日历式，优先级最低。</summary>
    Calendar
}

/// <summary>底部摘要与图表共用的指标选择（§7.1 指标选择）。</summary>
public enum Metric
{
    Key = 0,
    Click = 1,
    Wheel = 2,
    ActiveSeconds = 3
}

public static class MetricExtensions
{
    public static string DisplayName(this Metric m) => m switch
    {
        Metric.Key => "键盘按键",
        Metric.Click => "鼠标点击",
        Metric.Wheel => "滚轮",
        _ => "活跃时长"
    };

    /// <summary>从聚合行上取指标值。AS 的单位是秒，其余是次数。</summary>
    public static long Value(this Metric m, long key, long click, long wheel, long activeSeconds) => m switch
    {
        Metric.Key => key,
        Metric.Click => click,
        Metric.Wheel => wheel,
        _ => activeSeconds
    };
}

/// <summary>热力图矩阵（含尺寸与列标签），供 HeatmapView 使用。</summary>
public sealed class HeatMatrix
{
    public HeatShape Shape { get; init; }
    public int Rows { get; init; }
    public int Cols { get; init; }
    /// <summary>Values[row, col]；缺数据的格子是 0。</summary>
    public long[,] Values { get; init; } = new long[0, 0];
    public string[] RowLabels { get; init; } = Array.Empty<string>();
    public string[] ColumnLabels { get; init; } = Array.Empty<string>();
    public Metric Metric { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    public bool IsEmpty => Values.Length == 0 || Rows == 0 || Cols == 0;

    /// <summary>按行取索引，用于填充非零单元格。</summary>
    public void Apply(IEnumerable<HeatCell> cells)
    {
        foreach (var c in cells)
        {
            if (c.Row < 0 || c.Row >= Rows || c.Col < 0 || c.Col >= Cols) continue;
            Values[c.Row, c.Col] = c.Value;
        }
    }
}

/// <summary>UI 只通过这个接口查数据（§4.3），图表与界面都不直接碰 SQLite。</summary>
public interface IStatsQuery
{
    Task<IReadOnlyList<DayPoint>> GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<HourPoint>> GetHoursAsync(DateOnly day, CancellationToken ct);
    Task<IReadOnlyList<MinutePoint>> GetMinutesAsync(DateOnly day, CancellationToken ct);

    /// <summary>多日 × 小时热力图数据：Row=小时(0-23)，Col=日期在 [from,to] 中的序号。</summary>
    Task<IReadOnlyList<HeatCell>> GetHeatAsync(DateOnly from, DateOnly to, Metric metric, CancellationToken ct);

    /// <summary>多日热力图矩阵（含尺寸与列标签），供 HeatmapView 使用。</summary>
    Task<HeatMatrix> GetHeatMatrixAsync(DateOnly from, DateOnly to, Metric metric, CancellationToken ct);

    /// <summary>单日合计。</summary>
    Task<DayPoint> GetDayAsync(DateOnly day, CancellationToken ct);
}

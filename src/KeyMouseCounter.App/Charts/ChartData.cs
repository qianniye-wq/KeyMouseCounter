namespace KeyMouseCounter.Charts;

/// <summary>
/// 图表数据（§7.2）。UI 只负责把查询结果交给 <see cref="ChartDataBuilder"/>，
/// 由这里产生的 DTO 交给 <see cref="IChartView"/> 渲染，图表实现不碰 SQLite。
/// </summary>
public abstract class ChartData
{
    public string EmptyText { get; init; } = "暂无数据";
    public abstract bool IsEmpty { get; }
}

/// <summary>折线 / 柱状的通用数据。X 用 0..n-1 的下标，标签交给刻度生成器。</summary>
public sealed class SeriesChartData : ChartData
{
    public double[] Xs { get; init; } = Array.Empty<double>();
    public double[] Ys { get; init; } = Array.Empty<double>();
    /// <summary>与 <see cref="Xs"/> 一一对应的刻度标签。</summary>
    public string[] Labels { get; init; } = Array.Empty<string>();
    /// <summary>柱状（true）还是折线（false）。</summary>
    public bool AsBar { get; init; }
    public string XLabel { get; init; } = "";
    public string YLabel { get; init; } = "";
    public string SeriesName { get; init; } = "";
    /// <summary>需要竖线标注的位置（下标，通常是"当前小时"）；null 表示不标。</summary>
    public int? HighlightIndex { get; init; }
    public string HighlightText { get; init; } = "";
    /// <summary>Y 轴数值的格式化方式（活跃时长要显示成 5h 58m）。</summary>
    public Func<long, string>? YFormat { get; init; }
    public override bool IsEmpty => Xs.Length == 0;
}

/// <summary>热力图数据。行为横轴标签、列为纵轴标签，Value 按行优先填充。</summary>
public sealed class HeatmapChartData : ChartData
{
    public int Rows { get; init; }
    public int Cols { get; init; }
    public long[,] Values { get; init; } = new long[0, 0];
    public string[] RowLabels { get; init; } = Array.Empty<string>();
    public string[] ColumnLabels { get; init; } = Array.Empty<string>();
    public string RowAxisTitle { get; init; } = "";
    public string ColumnAxisTitle { get; init; } = "";
    public string MetricName { get; init; } = "";
    /// <summary>悬停提示：参数是 (row, col, value)。</summary>
    public Func<int, int, long, string>? Tooltip { get; init; }
    /// <summary>已知的分级边界（P50/P75/P90/P99），为空时由渲染层按数据现算。</summary>
    public double[] Levels { get; init; } = Array.Empty<double>();
    public override bool IsEmpty => Rows == 0 || Cols == 0 || Values.Length == 0;
}

/// <summary>表格数据。行是 object[]，第 0 列一律是可排序的主键列。</summary>
public sealed class TableChartData : ChartData
{
    public string[] Headers { get; init; } = Array.Empty<string>();
    public List<object?[]> Rows { get; init; } = new();
    /// <summary>按列格式化（列号 → 显示文本）；null 表示用默认 ToString。</summary>
    public Func<int, object?, string>? Format { get; init; }
    /// <summary>各列相对宽度（不填则平均分配）。</summary>
    public int[]? ColumnWidths { get; init; }
    public override bool IsEmpty => Rows.Count == 0;
}

/// <summary>图表实现（§7.5 允许 ScottPlot / GDI+ 两套实现互换）。</summary>
public interface IChartView
{
    /// <summary>放进界面的控件。</summary>
    Control Host { get; }

    /// <summary>渲染一份数据；空数据必须显示"暂无数据"占位而不是异常。</summary>
    void Render(ChartData data);
}

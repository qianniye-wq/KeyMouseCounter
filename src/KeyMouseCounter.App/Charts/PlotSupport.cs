using ScottPlot;
using SkiaSharp;

namespace KeyMouseCounter.Charts;

/// <summary>
/// 把界面主题色套到 ScottPlot 上。5.0.56 的 <c>Plot.Style</c> 已过时（并且是编译期错误），
/// 只能用 <c>Plot.SetStyle(new PlotStyle{...})</c>（§7.2 暗色主题）。
/// </summary>
internal static class PlotTheme
{
    private static ScottPlot.Color ToSp(System.Drawing.Color c) => ScottPlot.Color.FromColor(c);

    public static void Apply(Plot plot)
    {
        try
        {
            var t = Theme.Current;
            plot.SetStyle(new PlotStyle
            {
                FigureBackgroundColor = ToSp(t.ChartBack),
                DataBackgroundColor = ToSp(t.ChartBack),
                AxisColor = ToSp(t.Muted),
                GridMajorLineColor = ToSp(t.Grid),
                LegendBackgroundColor = ToSp(t.SurfaceAlt),
                LegendFontColor = ToSp(t.Fore),
                LegendOutlineColor = ToSp(t.Border)
            });

            // 5.0.56 默认字体不含中文字形（截图里中文刻度全是方框），必须显式指定雅黑。
            // Set 的后三个参数有默认值，省略即可。
            plot.Font.Set(CjkFontName);
        }
        catch
        {
            // 主题失败不影响主图
        }
    }

    /// <summary>中文字体候选：取系统里第一个可用的。</summary>
    private static string? _cjkFont;

    private static string CjkFontName
    {
        get
        {
            if (_cjkFont is not null) return _cjkFont;
            foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI" })
            {
                try
                {
                    using var f = new System.Drawing.Font(name, 9f);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        _cjkFont = name;
                        break;
                    }
                }
                catch
                {
                    // 换下一个候选
                }
            }
            _cjkFont ??= "Segoe UI";
            return _cjkFont;
        }
    }
}

/// <summary>屏蔽 ScottPlot 自带的右键菜单，让右键落到主窗口的上下文菜单（§7.1）。</summary>
internal sealed class NoopPlotMenu : IPlotMenu
{
    public void Reset() { }
    public void Clear() { }
    public void Add(string label, Action<Plot> action) { }
    public void AddSeparator() { }
    public void ShowContextMenu(Pixel pixel) { }
}

/// <summary>
/// 把 0..n-1 的下标映射成"08:00 / 01-05"这类刻度文字。
/// ScottPlot 只认数值轴，标签必须自己给（§7.2.1）。
/// </summary>
internal sealed class CategoryTickGenerator : ITickGenerator
{
    private readonly double[] _xs;
    private readonly string[] _labels;

    public CategoryTickGenerator(double[] xs, string[] labels)
    {
        _xs = xs;
        _labels = labels;
    }

    public int MaxTickCount { get; set; } = 12;

    /// <summary>ScottPlot 5 要求实现，但刻度由 <see cref="Ticks"/> 直接给，这里不用做事。</summary>
    public void Regenerate(CoordinateRange range, Edge edge, PixelLength size, SKPaint paint, LabelStyle style) { }

    public Tick[] Ticks
    {
        get
        {
            try
            {
                int n = Math.Min(_xs.Length, _labels.Length);
                if (n <= 0) return Array.Empty<Tick>();

                int max = Math.Max(2, MaxTickCount);
                int step = (int)Math.Ceiling(n / (double)max);
                if (step < 1) step = 1;

                var list = new List<Tick>(max + 2);
                // 空标签的刻度不提交：热力图 60 列里只有 0/10/20.. 有文字，
                // 把空刻度也交给 ScottPlot 会让它按"有 60 个刻度"做抽稀，结果只剩 00 和 30。
                for (int i = 0; i < n; i += step)
                    if (!string.IsNullOrEmpty(_labels[i]))
                        list.Add(new Tick(_xs[i], _labels[i]));
                return list.ToArray();
            }
            catch
            {
                return Array.Empty<Tick>();
            }
        }
    }
}

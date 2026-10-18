using System.Drawing;
using KeyMouseCounter.Diagnostics;
using ScottPlot.WinForms;

namespace KeyMouseCounter.Charts;

/// <summary>
/// 把分级色阶接到 ScottPlot 的 Heatmap 上：ScottPlot 只认 0..1 的 position，
/// 这里反算回原始数值再交给 <see cref="ColorRamp.Pick"/>，保证图例和格子用同一套规则。
/// </summary>
internal sealed class RampColormap : ScottPlot.IColormap
{
    private readonly ColorRamp _ramp;
    private readonly double _max;

    public RampColormap(ColorRamp ramp, double max)
    {
        _ramp = ramp;
        _max = max > 0 ? max : 1;
    }

    public string Name => "KeyMouseCounter";

    public ScottPlot.Color GetColor(double position)
    {
        if (double.IsNaN(position)) return ToSp(_ramp.Pick(0));
        double v = position * _max;
        return ToSp(_ramp.Pick(v));
    }

    private static ScottPlot.Color ToSp(Color c) => new(c.R, c.G, c.B, c.A);
}

/// <summary>色阶图例：5 个色块 + 档位文字。</summary>
internal sealed class RampLegend : Control
{
    private ColorRamp _ramp = new(Array.Empty<double>());
    private string _metric = "";

    public RampLegend()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 26;
        Dock = DockStyle.Bottom;
    }

    public void SetRamp(ColorRamp ramp, string metric)
    {
        _ramp = ramp;
        _metric = metric;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Current.ChartBack);

        int swatch = 16, gap = 4;
        int x = 6;
        int y = (Height - swatch) / 2;
        var text = _ramp.LegendText();

        // 色块：由浅到深
        for (int i = 0; i < 5; i++)
        {
            using var b = new SolidBrush(_ramp.StopAt(i));
            g.FillRectangle(b, x, y, swatch, swatch);
            x += swatch + gap;
        }

        using var fg = new SolidBrush(Theme.Current.Muted);
        using var font = new Font("Microsoft YaHei UI", 8.5f);
        string label = string.IsNullOrEmpty(_metric) ? text : $"{_metric}  {text}";
        g.DrawString(label, font, fg, x + 6, y);
    }
}

/// <summary>热力图（§7.2.2）：分级配色、悬停提示、色阶图例。</summary>
public sealed class HeatmapView : IChartView
{
    private static Color Surface => Theme.Current.ChartBack;
    private static Color Muted => Theme.Current.Muted;

    private readonly FormsPlot _plot = new() { Dock = DockStyle.Fill };
    private readonly Label _tip = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
        ForeColor = Muted,
        BackColor = Surface,
        Font = new Font("Microsoft YaHei UI", 8.5f)
    };
    private readonly RampLegend _legend = new();
    private readonly Label _empty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Muted,
        BackColor = Surface,
        Font = new Font("Microsoft YaHei UI", 10f)
    };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Surface };

    private HeatmapChartData? _data;
    private ColorRamp _ramp = new(Array.Empty<double>());

    public HeatmapView()
    {
        _plot.Menu = new NoopPlotMenu();
        ApplyTheme();
        _host.Controls.Add(_plot);
        _host.Controls.Add(_empty);
        _host.Controls.Add(_legend);
        _host.Controls.Add(_tip);
        _empty.BringToFront();
        _plot.MouseMove += OnMouseMove;
        _plot.MouseLeave += (_, _) => _tip.Text = "";
    }

    public Control Host => _host;

    public void Render(ChartData data)
    {
        if (data is not HeatmapChartData d)
            throw new ArgumentException($"HeatmapView 需要 HeatmapChartData，收到 {data.GetType().Name}", nameof(data));

        _data = d;
        if (d.IsEmpty)
        {
            ShowEmpty(d.EmptyText);
            return;
        }

        try
        {
            var p = _plot.Plot;
            p.Clear();
            try { PlotTheme.Apply(p); } catch { }
            ApplyTheme();

            // 行 0（00 点）要画在最上面：把数据上下翻过来，坐标就保持常规方向，悬停也好算
            int rows = d.Rows, cols = d.Cols;
            var values = new double[rows, cols];
            long max = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    long v = d.Values[r, c];
                    values[rows - 1 - r, c] = v;
                    if (v > max) max = v;
                }
            }

            _ramp = d.Levels.Length > 0 ? new ColorRamp(d.Levels) : ColorRamp.FromValues(
                d.Values.Cast<long>());

            var hm = p.Add.Heatmap(values);
            hm.Colormap = new RampColormap(_ramp, max);

            p.Axes.Bottom.TickGenerator = new CategoryTickGenerator(
                Enumerable.Range(0, cols).Select(c => c + 0.5).ToArray(), d.ColumnLabels)
            {
                MaxTickCount = Math.Clamp(_host.Width / 46, 4, 30)
            };
            p.Axes.Left.TickGenerator = new CategoryTickGenerator(
                Enumerable.Range(0, rows).Select(r => rows - r - 0.5).ToArray(), d.RowLabels)
            {
                MaxTickCount = rows
            };

            p.Axes.SetLimits(0, cols, 0, rows);
            if (!string.IsNullOrEmpty(d.ColumnAxisTitle)) p.XLabel(d.ColumnAxisTitle);
            if (!string.IsNullOrEmpty(d.RowAxisTitle)) p.YLabel(d.RowAxisTitle);

            _legend.SetRamp(_ramp, d.MetricName);
            _empty.Visible = false;
            _plot.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warn($"热力图渲染失败：{ex.Message}");
            ShowEmpty("图表渲染失败");
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_data is null || _data.IsEmpty) return;
        try
        {
            var coords = _plot.Plot.GetCoordinates(e.X, e.Y);
            int c = (int)Math.Floor(coords.X);
            int i = (int)Math.Floor(coords.Y);
            if (c < 0 || c >= _data.Cols || i < 0 || i >= _data.Rows)
            {
                _tip.Text = "";
                return;
            }

            int r = _data.Rows - 1 - i;
            long v = _data.Values[r, c];
            _tip.Text = _data.Tooltip?.Invoke(r, c, v) ?? $"({r},{c}) = {v}";
        }
        catch
        {
            _tip.Text = "";
        }
    }

    private void ApplyTheme()
    {
        _host.BackColor = Surface;
        _tip.BackColor = Surface;
        _tip.ForeColor = Muted;
        _empty.BackColor = Surface;
        _empty.ForeColor = Muted;
        _plot.BackColor = Surface;
        _legend.Invalidate();
    }

    private void ShowEmpty(string text)
    {        try { _plot.Plot.Clear(); _plot.Refresh(); } catch { }
        _empty.Text = text;
        _empty.Visible = true;
        _empty.BringToFront();
        _legend.SetRamp(new ColorRamp(Array.Empty<double>()), "");
        _tip.Text = "";
    }
}

using System.Drawing;
using KeyMouseCounter.Diagnostics;
using ScottPlot.WinForms;

namespace KeyMouseCounter.Charts;

/// <summary>折线 / 柱状图（ScottPlot 实现，§7.2.1）。</summary>
public sealed class LineChartView : IChartView
{
    private static Color Accent => Theme.Current.Accent;
    private static Color Surface => Theme.Current.ChartBack;
    private static Color Muted => Theme.Current.Muted;
    private static Color Fore => Theme.Current.ChartFore;

    private readonly FormsPlot _plot = new() { Dock = DockStyle.Fill };
    private readonly Label _empty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Muted,
        BackColor = Surface,
        Font = new Font("Microsoft YaHei UI", 10f)
    };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Surface };

    public LineChartView()
    {
        _plot.Menu = new NoopPlotMenu();
        _host.Controls.Add(_plot);
        _host.Controls.Add(_empty);
        _empty.BringToFront();
    }

    public Control Host => _host;

    public void Render(ChartData data)
    {
        if (data is not SeriesChartData d)
            throw new ArgumentException($"LineChartView 需要 SeriesChartData，收到 {data.GetType().Name}", nameof(data));

        if (d.IsEmpty)
        {
            ShowEmpty(d.EmptyText);
            return;
        }

        try
        {
            var p = _plot.Plot;
            p.Clear();
            ApplyTheme();

            var color = new ScottPlot.Color(Accent.R, Accent.G, Accent.B, Accent.A);

            if (d.AsBar)
            {
                var bars = p.Add.Bars(d.Xs, d.Ys);
                bars.Color = color;
                bars.LegendText = d.SeriesName;
            }
            else
            {
                var sc = p.Add.Scatter(d.Xs, d.Ys);
                sc.Color = color;
                sc.LineWidth = 1.8f;
                sc.MarkerSize = d.Xs.Length <= 96 ? 4 : 0;
                sc.FillY = d.Xs.Length <= 600;
                sc.FillYColor = color.WithAlpha(0.18);
                sc.LegendText = d.SeriesName;
            }

            if (d.HighlightIndex is int hi && hi >= 0 && hi < d.Xs.Length)
                p.Add.VerticalLine(d.Xs[hi], 1.5f, ScottPlot.Color.FromHex("#FBBF24"), ScottPlot.LinePattern.Dashed);

            // 刻度密度随窗口宽度变化；按分钟全天视图要留够整点标签，所以下限 6。
            p.Axes.Bottom.TickGenerator = new CategoryTickGenerator(d.Xs, d.Labels)
            {
                MaxTickCount = Math.Clamp(_host.Width / 90, 6, 24)
            };

            double yMax = 0;
            foreach (var v in d.Ys) if (v > yMax) yMax = v;
            if (yMax <= 0) yMax = 1;

            double x0 = d.Xs[0], x1 = d.Xs[^1];
            if (x1 <= x0) { x0 -= 0.5; x1 += 0.5; }
            // §7.2.1 要求按小时固定 0-23、按分钟固定 0-1439；MultiDay 才按数据范围。
        double limitLeft = d.XMin ?? (x0 - 0.5);
        double limitRight = d.XMax ?? (x1 + 0.5);
        p.Axes.SetLimits(limitLeft, limitRight, 0, yMax * 1.15);

            if (!string.IsNullOrEmpty(d.XLabel)) p.XLabel(d.XLabel);
            if (!string.IsNullOrEmpty(d.YLabel)) p.YLabel(d.YLabel);

            try
            {
                p.Legend.IsVisible = !string.IsNullOrEmpty(d.SeriesName);
                if (p.Legend.IsVisible) p.ShowLegend(ScottPlot.Alignment.UpperRight);
            }
            catch { /* 图例失败不影响主图 */ }

            _empty.Visible = false;
            _plot.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warn($"折线图渲染失败：{ex.Message}");
            ShowEmpty("图表渲染失败");
        }
    }

    private void ShowEmpty(string text)
    {
        try { _plot.Plot.Clear(); _plot.Refresh(); } catch { }
        _empty.Text = text;
        _empty.Visible = true;
        _empty.BringToFront();
    }

    private void ApplyTheme()
    {
        try
        {
            PlotTheme.Apply(_plot.Plot);
        }
        catch { /* 主题失败不影响主图 */ }
        _host.BackColor = Surface;
        _empty.BackColor = Surface;
        _empty.ForeColor = Muted;
        _plot.BackColor = Surface;
    }
}

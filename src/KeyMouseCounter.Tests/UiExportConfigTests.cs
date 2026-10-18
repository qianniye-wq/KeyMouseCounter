using System.Drawing;
using System.Text;
using KeyMouseCounter.Charts;
using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Export;
using KeyMouseCounter.Storage;
using KeyMouseCounter.UI;

namespace KeyMouseCounter.Tests;

/// <summary>§11.1 T23–T26：挂件坐标、热力图分位数、CSV 格式、配置损坏兜底。</summary>
public sealed class UiExportConfigTests
{
    // ─────────────── T23 挂件坐标越界 → 归位主屏 ───────────────

    [Fact]
    public void T23_WidgetOffscreen_RestoredToPrimary()
    {
        var screens = new List<Rectangle> { new(0, 0, 1920, 1040) };
        var primary = new Rectangle(0, 0, 1920, 1040);
        var widget = new Rectangle(5000, 5000, 240, 96);

        var p = WidgetGeometry.EnsureVisible(widget, screens, primary, firstRun: false);
        Assert.Equal(new Point(100, 100), p);
    }

    [Fact]
    public void T23_WidgetFirstRun_PlacedAtDefaultCorner()
    {
        var screens = new List<Rectangle> { new(0, 0, 1920, 1040) };
        var primary = new Rectangle(0, 0, 1920, 1040);
        var widget = new Rectangle(10, 10, 240, 96);

        var p = WidgetGeometry.EnsureVisible(widget, screens, primary, firstRun: true);
        Assert.Equal(new Point(40, 40), p);
    }

    [Fact]
    public void T23_WidgetVisible_KeepsPosition()
    {
        var screens = new List<Rectangle> { new(0, 0, 1920, 1040), new(1920, 0, 1920, 1040) };
        var primary = new Rectangle(0, 0, 1920, 1040);
        var widget = new Rectangle(2200, 300, 240, 96);

        var p = WidgetGeometry.EnsureVisible(widget, screens, primary, firstRun: false);
        Assert.Equal(new Point(2200, 300), p);
    }

    // ─────────────── T24 热力图分位数 ───────────────

    [Fact]
    public void T24_AllZeroValues_DoesNotThrow()
    {
        var ramp = ColorRamp.FromValues(new long[24 * 60]);

        Assert.Empty(ramp.Levels);
        Assert.Equal(ColorRamp.DefaultStops[0], ramp.Pick(0));
        Assert.Equal(ColorRamp.DefaultStops[0], ramp.Pick(123));   // 无分级时统一最浅色
        Assert.Equal("无数据", ramp.LegendText());
    }

    [Fact]
    public void T24_Levels_MapToExpectedStops()
    {
        // 1..100 的分位数：P50≈50.5 / P75≈75.25 / P90≈90.1 / P99≈99.01
        var ramp = ColorRamp.FromValues(Enumerable.Range(1, 100).Select(i => (long)i));

        Assert.Equal(4, ramp.Levels.Count);
        Assert.Equal(ColorRamp.DefaultStops[0], ramp.Pick(0));      // 0 → 背景色
        Assert.Equal(ColorRamp.DefaultStops[1], ramp.Pick(1));      // ≤P50 → 第 1 档
        Assert.Equal(ColorRamp.DefaultStops[1], ramp.Pick(50));
        Assert.Equal(ColorRamp.DefaultStops[2], ramp.Pick(75.25));  // ≤P75
        Assert.Equal(ColorRamp.DefaultStops[3], ramp.Pick(90.1));   // ≤P90
        Assert.Equal(ColorRamp.DefaultStops[4], ramp.Pick(99.01));  // ≤P99
        Assert.Equal(ColorRamp.DefaultStops[4], ramp.Pick(1000));   // 超过 P99 → 最热
    }

    // ─────────────── T25 CSV 三粒度 ───────────────

    private static string[] ReadCsvLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());   // UTF-8 with BOM

        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void T25_ExportDays_HeaderAndRows()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "days.csv");
        var day = new DateOnly(2025, 3, 1);
        var total = MinuteBuckets.From(35, 8, 1, 0, 2, 0, 0, 0, 30);

        CsvExporter.ExportDays(path, new[] { new DayRow(day, total, "08:35", "09:10", 480) });

        var lines = ReadCsvLines(path);
        Assert.Equal("日期,键盘按键,鼠标左键,鼠标右键,鼠标中键,滚轮上,滚轮下,活跃时长(秒),首次输入,最后输入", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2025-03-01,35,8,1,0,2,0,30,08:35,09:10", lines[1]);
    }

    [Fact]
    public void T25_ExportHours_HeaderAndRowCount()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "hours.csv");
        var day = new DateOnly(2025, 3, 1);
        var rows = new List<HourPoint>();
        for (int h = 0; h < 24; h++)
            rows.Add(new HourPoint(h, h, h * 2, h, h * 3));

        CsvExporter.ExportHours(path, day, rows);

        var lines = ReadCsvLines(path);
        Assert.Equal("日期,小时,键盘按键,鼠标点击,滚轮,活跃时长(秒)", lines[0]);
        Assert.Equal(25, lines.Length);   // 表头 + 24 小时
        Assert.Equal("2025-03-01,8,8,16,8,24", lines[9]);
    }

    [Fact]
    public void T25_ExportMinutes_SkipsZeroByDefault()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "minutes.csv");
        var day = new DateOnly(2025, 3, 1);

        var rows = new List<MinutePoint>
        {
            new(0, 0, 0, 0, 0),
            new(515, 35, 8, 2, 30),   // 08:35
            new(516, 0, 0, 0, 0),
            new(517, 1, 0, 0, 0)
        };

        CsvExporter.ExportMinutes(path, day, rows);

        var lines = ReadCsvLines(path);
        Assert.Equal("日期,时间,键盘按键,鼠标点击,滚轮,活跃时长(秒)", lines[0]);
        Assert.Equal(3, lines.Length);   // 表头 + 两条非零
        Assert.Equal("2025-03-01,08:35,35,8,2,30", lines[1]);
        Assert.Equal("2025-03-01,08:37,1,0,0,0", lines[2]);
    }

    // ─────────────── T26 配置损坏 ───────────────

    [Fact]
    public void T26_CorruptConfig_FallsBackWithBackup()
    {
        using var root = new TempRoot();
        File.WriteAllText(AppPaths.ConfigPath, "{ 这不是合法 JSON ");

        var cfg = AppConfig.Load(AppPaths.ConfigPath, out string? warning);

        Assert.NotNull(warning);
        Assert.True(File.Exists(AppPaths.ConfigBadPath));          // 损坏文件已备份
        Assert.Equal(60, cfg.Counting.IdleThresholdSec);           // 回落到默认值
        Assert.Equal("Ctrl+Alt+K", cfg.Widget.Hotkey);
        Assert.Equal(2, cfg.SchemaVersion);
        Assert.True(File.Exists(AppPaths.ConfigPath));             // 新配置已写回
    }
}

/// <summary>按小时标签下的表格（§7.2.3 的补充形态）：24 行、时刻格式化。</summary>
public sealed class HourTableTests
{
    [Fact]
    public void HourTable_Has24Rows_AndFormatsHourLabels()
    {
        var hours = Enumerable.Range(0, 24)
            .Select(h => new HourPoint(h, h * 10, h, h * 2, h * 60))
            .ToList();

        var table = ChartDataBuilder.HourTable(hours, Metric.Key);

        Assert.Equal(24, table.Rows.Count);
        Assert.Equal(new[] { "时间", "键盘按键", "鼠标点击", "滚轮", "活跃时长" }, table.Headers);
        Assert.Equal("08:00", table.Format!(0, table.Rows[8][0]));
        Assert.Equal("80", table.Format!(1, table.Rows[8][1]));
        Assert.Equal("1,800", table.Format!(1, 1800L));
        Assert.Equal("30m", table.Format!(4, 1800L));
    }
}
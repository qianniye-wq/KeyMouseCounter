using System.Globalization;
using System.Text;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.Export;

/// <summary>
/// CSV 导出（§6.9）。UTF-8 **with BOM**，否则 Excel 打开中文/数字排列会乱码。
/// </summary>
public static class CsvExporter
{
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string s) => s.Contains(',') || s.Contains('"')
        ? "\"" + s.Replace("\"", "\"\"") + "\""
        : s;

    /// <summary>按天导出。</summary>
    public static void ExportDays(string path, IEnumerable<DayRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("日期,键盘按键,鼠标左键,鼠标右键,鼠标中键,滚轮上,滚轮下,活跃时长(秒),首次输入,最后输入");
        foreach (var r in rows)
        {
            sb.Append(r.Day.ToString("yyyy-MM-dd")).Append(',')
              .Append(Num(r.Total.K)).Append(',')
              .Append(Num(r.Total.ML)).Append(',')
              .Append(Num(r.Total.MR)).Append(',')
              .Append(Num(r.Total.MM)).Append(',')
              .Append(Num(r.Total.WU)).Append(',')
              .Append(Num(r.Total.WD)).Append(',')
              .Append(Num(r.Total.AS)).Append(',')
              .Append(r.FirstTs ?? "").Append(',')
              .Append(r.LastTs ?? "")
              .Append("\r\n");
        }
        WriteBom(path, sb.ToString());
    }

    /// <summary>按小时导出。</summary>
    public static void ExportHours(string path, DateOnly day, IEnumerable<HourPoint> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("日期,小时,键盘按键,鼠标点击,滚轮,活跃时长(秒)");
        foreach (var r in rows)
        {
            sb.Append(day.ToString("yyyy-MM-dd")).Append(',')
              .Append(r.Hour.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Num(r.Key)).Append(',')
              .Append(Num(r.Click)).Append(',')
              .Append(Num(r.Wheel)).Append(',')
              .Append(Num(r.ActiveSeconds))
              .Append("\r\n");
        }
        WriteBom(path, sb.ToString());
    }

    /// <summary>按分钟导出，时间列 HH:mm，默认只导非零分钟。</summary>
    public static void ExportMinutes(string path, DateOnly day, IEnumerable<MinutePoint> rows, bool includeZero = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("日期,时间,键盘按键,鼠标点击,滚轮,活跃时长(秒)");
        foreach (var r in rows)
        {
            if (!includeZero && r.Key == 0 && r.Click == 0 && r.Wheel == 0 && r.ActiveSeconds == 0) continue;
            sb.Append(day.ToString("yyyy-MM-dd")).Append(',')
              .Append($"{r.Minute / 60:00}:{r.Minute % 60:00}").Append(',')
              .Append(Num(r.Key)).Append(',')
              .Append(Num(r.Click)).Append(',')
              .Append(Num(r.Wheel)).Append(',')
              .Append(Num(r.ActiveSeconds))
              .Append("\r\n");
        }
        WriteBom(path, sb.ToString());
    }

    private static void WriteBom(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, Utf8Bom);
        Log.Info($"csv exported: {Path.GetFileName(path)}");
    }

    public static string SuggestFileName(string kind, DateOnly from, DateOnly? to = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return to is null || to == from
            ? $"KeyMouseCounter-{kind}-{from:yyyyMMdd}-{stamp}.csv"
            : $"KeyMouseCounter-{kind}-{from:yyyyMMdd}_{to:yyyyMMdd}-{stamp}.csv";
    }
}

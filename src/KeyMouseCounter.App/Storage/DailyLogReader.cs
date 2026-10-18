using System.Text;
using System.Text.Json;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Storage;

/// <summary>一次日志解析的结果。</summary>
public sealed class LogParseResult
{
    public DateOnly Day { get; init; }
    /// <summary>按分钟求和后的增量（分钟 → 桶）。</summary>
    public SortedDictionary<int, MinuteBuckets> Minutes { get; } = new();
    public int LinesRead { get; set; }
    public int LinesImported { get; set; }
    public int LinesSkipped { get; set; }
    /// <summary>末尾存在解析不了的半行（断电），调用方应把它截掉再追加。</summary>
    public bool TruncatedTail { get; set; }
    /// <summary>以完整换行结尾的字节位置——安全的追加/游标位置。</summary>
    public long ValidBytes { get; set; }
    public int LineCount { get; set; }
    public List<string> Warnings { get; } = new();

    public MinuteBuckets Total
    {
        get
        {
            var t = default(MinuteBuckets);
            foreach (var v in Minutes.Values) t.AddFrom(v);
            return t;
        }
    }

    public int FirstMinute => Minutes.Count == 0 ? -1 : Minutes.Keys.First();
    public int LastMinute => Minutes.Count == 0 ? -1 : Minutes.Keys.Last();
}

/// <summary>
/// 每日日志解析与聚合（§6.4、§6.6 重建用）。
///
/// 关键点：
/// - 所有行是增量，同一分钟可多行 → **求和**；
/// - 末行无法解析（断电半行）→ 忽略并告警，最多丢 30 秒；
/// - 旧日志的 `mp`（鼠标移动）与一切未知字段直接忽略；
/// - 字节偏移按原始字节精确计算，供 log_cursor / 截断修复使用。
/// </summary>
public static class DailyLogReader
{
    public static LogParseResult ParseFile(string path, DateOnly? dayHint = null)
    {
        var result = new LogParseResult { Day = dayHint ?? DayFromFileName(path) };
        if (!File.Exists(path))
        {
            result.Warnings.Add($"日志文件不存在：{Path.GetFileName(path)}");
            return result;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ParseStream(fs, result, 0);
        return result;
    }

    /// <summary>从指定字节偏移开始做增量解析（log_cursor 优化，§6.6 步骤 3）。</summary>
    public static LogParseResult ParseFrom(string path, long byteOffset, DateOnly day)
    {
        var result = new LogParseResult { Day = day };
        if (!File.Exists(path))
        {
            result.Warnings.Add($"日志文件不存在：{Path.GetFileName(path)}");
            return result;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (byteOffset > fs.Length) byteOffset = 0;    // 文件被外部截断过 → 退回全量
        ParseStream(fs, result, byteOffset);
        return result;
    }

    private static void ParseStream(FileStream fs, LogParseResult result, long startOffset)
    {
        fs.Seek(startOffset, SeekOrigin.Begin);
        long remain = fs.Length - startOffset;
        if (remain <= 0) { result.ValidBytes = startOffset; return; }

        var buf = new byte[remain];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }

        result.ValidBytes = startOffset;
        int i = 0;
        while (i < read)
        {
            int nl = Array.IndexOf(buf, (byte)'\n', i, read - i);
            int contentEnd = nl < 0 ? read : nl;
            int len = contentEnd - i;
            if (len > 0 && buf[contentEnd - 1] == (byte)'\r') len--;

            var line = Encoding.UTF8.GetString(buf, i, len);
            int next = nl < 0 ? read : nl + 1;

            if (!string.IsNullOrWhiteSpace(line))
            {
                result.LinesRead++;
                var parsed = TryParseLine(line, out bool isHeader, out string? warn);
                if (warn is not null) result.Warnings.Add(warn);

                if (isHeader)
                {
                    // 首行，跳过
                }
                else if (parsed is null)
                {
                    result.LinesSkipped++;
                    result.TruncatedTail = true;      // 只有末行坏是正常情况，中间坏行也能被跳过
                }
                else if (parsed.Min is < 0 or > 1439)
                {
                    result.LinesSkipped++;
                    result.Warnings.Add($"非法 min={parsed.Min}，已跳过");
                }
                else
                {
                    AddMinutes(result.Minutes, parsed);
                    result.LinesImported++;
                    result.LineCount++;      // 只数数据行（header 不计），与 log_cursor.line_count 口径一致
                }
            }

            if (nl >= 0) result.ValidBytes = startOffset + next;   // 只有以 \n 结尾的行才是安全边界
            i = next;
        }
    }

    private static void AddMinutes(SortedDictionary<int, MinuteBuckets> map, in MinuteLine l)
    {
        map.TryGetValue(l.Min, out var b);
        b.K += l.K;
        b.ML += l.Ml;
        b.MR += l.Mr;
        b.MM += l.Mm;
        b.WU += l.Wu;
        b.WD += l.Wd;
        b.WL += l.Wl;
        b.WR += l.Wr;
        b.AS += l.As;
        map[l.Min] = b;
    }

    /// <summary>把文件末尾无法解析的半行截掉（§6.4 约定 4），返回截断后的长度。</summary>
    public static long RepairTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var probe = ParseFile(path);
            if (!probe.TruncatedTail) return probe.ValidBytes;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            if (fs.Length > probe.ValidBytes)
            {
                fs.SetLength(probe.ValidBytes);
                Log.Warn($"daily log tail truncated: {Path.GetFileName(path)} kept={probe.ValidBytes}B");
            }
            return probe.ValidBytes;
        }
        catch (Exception ex)
        {
            Log.Warn($"daily log tail repair failed: {ex.Message}");
            return 0;
        }
    }

    private static MinuteLine? TryParseLine(string line, out bool isHeader, out string? warning)
    {
        isHeader = false;
        warning = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warning = "非对象行，已跳过";
                return null;
            }

            if (root.TryGetProperty("type", out var typeEl) &&
                typeEl.ValueKind == JsonValueKind.String &&
                typeEl.GetString() == "header")
            {
                isHeader = true;
                return null;
            }

            // 未知字段（含旧版 mp）一律忽略：只从白名单取值
            int min = GetInt(root, "min");
            int v = GetInt(root, "v");
            if (v <= 0) v = MinuteLineFactory.FormatVersion;
            if (v > MinuteLineFactory.FormatVersion)
                warning = $"未知格式版本 v={v}，按 v{MinuteLineFactory.FormatVersion} 尽力解析";

            return new MinuteLine(
                v,
                GetString(root, "date"),
                min,
                GetInt(root, "k"),
                GetInt(root, "ml"),
                GetInt(root, "mr"),
                GetInt(root, "mm"),
                GetInt(root, "wu"),
                GetInt(root, "wd"),
                GetInt(root, "wl"),
                GetInt(root, "wr"),
                GetInt(root, "as"),
                GetString(root, "t"));
        }
        catch (JsonException)
        {
            warning = "无法解析的行（可能是写入过程中断电），已忽略";
            return null;
        }
    }

    private static int GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out long l) ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : 0,
            JsonValueKind.String => int.TryParse(el.GetString(), out int i) ? i : 0,
            _ => 0
        };
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    public static DateOnly DayFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return DateOnly.TryParseExact(name, "yyyy-MM-dd", out var d) ? d : DateOnly.MinValue;
    }

    /// <summary>扫描 data 目录下所有 *.jsonl 的日期集合（§6.6 步骤 2）。</summary>
    public static List<DateOnly> ListLogDays(string dir)
    {
        var days = new List<DateOnly>();
        if (!Directory.Exists(dir)) return days;
        foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            var d = DayFromFileName(f);
            if (d != DateOnly.MinValue) days.Add(d);
        }
        days.Sort();
        return days;
    }

    /// <summary>校验每行 date 与文件名是否一致（§6.4：以文件名为准并告警）。</summary>
    public static int CountDateMismatches(string path, DateOnly fileDay)
    {
        int n = 0;
        try
        {
            var expected = fileDay.ToString("yyyy-MM-dd");
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.Contains("\"type\"", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("date", out var el)) continue;
                if (el.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(el.GetString(), expected, StringComparison.Ordinal)) n++;
            }
        }
        catch { }
        return n;
    }
}

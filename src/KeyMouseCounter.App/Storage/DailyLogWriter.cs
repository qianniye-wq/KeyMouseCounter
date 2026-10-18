using System.Text;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Storage;

/// <summary>
/// JSONL 追加写入器（§6.4、§15.3）。UTF-8 无 BOM、\n 换行、追加写、每次 flush 后落盘。
///
/// 写失败时会把文件截断回本次写入之前的位置，保证重试不会写出重复的增量行
/// （日志是 delta，重复行会在重建时被求和成双倍）。
/// </summary>
public sealed class DailyLogWriter : IDisposable
{
    private readonly string _dir;
    private readonly string _sid;
    private FileStream? _stream;
    private DateOnly _openDay = DateOnly.MinValue;

    public DailyLogWriter(string dir, string sid)
    {
        _dir = dir;
        _sid = sid;
        Directory.CreateDirectory(_dir);
    }

    public static string PathFor(string dir, DateOnly day) => Path.Combine(dir, $"{day:yyyy-MM-dd}.jsonl");

    /// <summary>追加一天的增量行，返回写入后的文件字节长度（供 log_cursor 使用）。</summary>
    public long Append(DateOnly day, IReadOnlyList<(int Minute, MinuteBuckets Data)> items)
    {
        if (items.Count == 0) return CurrentLength(day);

        EnsureOpen(day);

        var sb = new StringBuilder(items.Count * 150);
        foreach (var (minute, data) in items)
            sb.Append(MinuteLineFactory.Serialize(MinuteLineFactory.Create(day, minute, data))).Append('\n');

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        long before = _stream!.Length;

        try
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();          // 每次 flush 都落盘，最多丢 30s
            return _stream.Length;
        }
        catch
        {
            // 截断回写前位置，避免半行 + 重试造成重复增量
            try
            {
                _stream.SetLength(before);
                _stream.Seek(0, SeekOrigin.End);
                _stream.Flush();
            }
            catch { /* 连截断都失败就关闭，下次重新打开 */ CloseInternal(); }
            throw;
        }
    }

    /// <summary>把文件截断到指定长度（修复断电留下的半行，§6.4 约定 4）。</summary>
    public static void TruncateTo(string dir, DateOnly day, long length)
    {
        var path = PathFor(dir, day);
        if (!File.Exists(path)) return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        if (fs.Length > length) fs.SetLength(length);
    }

    private long CurrentLength(DateOnly day)
    {
        if (_openDay == day && _stream is not null) return _stream.Length;
        var path = PathFor(_dir, day);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private void EnsureOpen(DateOnly day)
    {
        if (_openDay == day && _stream is not null) return;

        CloseInternal();

        var path = PathFor(_dir, day);
        bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _openDay = day;

        if (isNew)
        {
            var header = MinuteLineFactory.Serialize(MinuteLineFactory.CreateHeader(_sid, GetUtcOffsetMinutes()));
            var bytes = Encoding.UTF8.GetBytes(header + "\n");
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    private static int GetUtcOffsetMinutes() => (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;

    private void CloseInternal()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _openDay = DateOnly.MinValue;
    }

    public void Close() => CloseInternal();

    public void Dispose() => CloseInternal();

    /// <summary>生成当日汇总快照（§6.4 可选文件，导入时忽略）。</summary>
    public static void WriteSummary(string dir, DateOnly day, in MinuteBuckets total, int nonZeroMinutes)
    {
        try
        {
            var summary = new DaySummary(
                day.ToString("yyyy-MM-dd"),
                total.K,
                total.Clicks,
                total.Wheels,
                total.AS,
                nonZeroMinutes,
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));

            var json = System.Text.Json.JsonSerializer.Serialize(summary, JsonCtx.Default.DaySummary);
            var path = Path.Combine(dir, $"{day:yyyy-MM-dd}.summary.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"summary write failed: {ex.Message}");
        }
    }
}

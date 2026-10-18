using System.Text.Json;
using System.Text.Json.Serialization;
using KeyMouseCounter.Core;

namespace KeyMouseCounter.Storage;

/// <summary>每日日志首行（§6.4）。可缺省，导入时忽略。</summary>
public sealed record LogHeader(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("sid")] string Sid,
    [property: JsonPropertyName("utcOffset")] int UtcOffset,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

/// <summary>
/// 每日日志的数据行（§6.4）。**所有行都是增量（delta）**，导入时按 min 求和。
/// 字段名是契约，不得改动（红线）。
/// </summary>
public sealed record MinuteLine(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("k")] int K,
    [property: JsonPropertyName("ml")] int Ml,
    [property: JsonPropertyName("mr")] int Mr,
    [property: JsonPropertyName("mm")] int Mm,
    [property: JsonPropertyName("wu")] int Wu,
    [property: JsonPropertyName("wd")] int Wd,
    [property: JsonPropertyName("wl")] int Wl,
    [property: JsonPropertyName("wr")] int Wr,
    [property: JsonPropertyName("as")] int As,
    [property: JsonPropertyName("t")] string T);

/// <summary>当日汇总快照（§6.4），人读用，导入时忽略。</summary>
public sealed record DaySummary(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("key")] long Key,
    [property: JsonPropertyName("click")] long Click,
    [property: JsonPropertyName("wheel")] long Wheel,
    [property: JsonPropertyName("activeSec")] long ActiveSec,
    [property: JsonPropertyName("minutes")] int Minutes,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt);

/// <summary>源生成的序列化上下文，避免反射开销与单文件裁剪问题（§15.3）。</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(MinuteLine))]
[JsonSerializable(typeof(LogHeader))]
[JsonSerializable(typeof(DaySummary))]
internal sealed partial class JsonCtx : JsonSerializerContext
{
}

public static class MinuteLineFactory
{
    public const int FormatVersion = 1;

    /// <summary>
    /// 源生成上下文 + 不转义 '+' 等字符。默认编码器会把 "T00:52:29+08:00" 写成 "\u002B"，
    /// 日志要人能直接读，所以这里给源生成元数据套一份自己的 options。
    /// </summary>
    private static readonly JsonSerializerOptions LogJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = JsonCtx.Default
    };

    private static int Clamp(long v) => v <= 0 ? 0 : (v > int.MaxValue ? int.MaxValue : (int)v);

    public static MinuteLine Create(DateOnly day, int minute, in MinuteBuckets b) => new(
        FormatVersion,
        day.ToString("yyyy-MM-dd"),
        minute,
        Clamp(b.K), Clamp(b.ML), Clamp(b.MR), Clamp(b.MM),
        Clamp(b.WU), Clamp(b.WD), Clamp(b.WL), Clamp(b.WR),
        Clamp(b.AS),
        $"{minute / 60:00}:{minute % 60:00}");

    public static string Serialize(MinuteLine line) => JsonSerializer.Serialize(line, LogJson);

    public static string Serialize(LogHeader header) => JsonSerializer.Serialize(header, LogJson);

    public static LogHeader CreateHeader(string sid, int utcOffsetMin) => new(
        "header",
        FormatVersion,
        AppPaths.Version,
        SafeHostName(),
        sid,
        utcOffsetMin,
        DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));

    private static string SafeHostName()
    {
        try { return Environment.MachineName; } catch { return "unknown"; }
    }
}

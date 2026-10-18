using System.Globalization;
using KeyMouseCounter.Core;

namespace KeyMouseCounter.Charts;

/// <summary>统一的数值呈现（§7.1 卡片/图表/表格共用，保证同一个数字到哪都长一样）。</summary>
public static class ValueFormat
{
    /// <summary>次数：大于等于 10000 且开启紧凑时写成 1.2w / 3.4k。</summary>
    public static string Count(long v, bool compact = false)
    {
        if (!compact) return v.ToString("N0", CultureInfo.InvariantCulture);
        if (v >= 100_000_000) return (v / 100_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "亿";
        if (v >= 10_000) return (v / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "w";
        return v.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>活跃时长：90 → "1m 30s"，3590 → "59m 50s"，3600 → "1h 0m"。</summary>
    public static string Duration(long seconds)
    {
        if (seconds <= 0) return "0m";
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600)
        {
            long m = seconds / 60, s = seconds % 60;
            return s == 0 ? $"{m}m" : $"{m}m {s}s";
        }
        long h = seconds / 3600, mm = (seconds % 3600) / 60;
        return $"{h}h {mm}m";
    }

    /// <summary>仪表盘用的短时长：只到分钟，不写秒。</summary>
    public static string DurationShort(long seconds)
    {
        if (seconds <= 0) return "0 分钟";
        if (seconds < 60) return "不足 1 分钟";
        long h = seconds / 3600, m = (seconds % 3600) / 60;
        if (h == 0) return $"{m} 分钟";
        return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
    }

    public static string Metric(long v, KeyMouseCounter.Core.Metric metric, bool compact = false)
        => metric == KeyMouseCounter.Core.Metric.ActiveSeconds ? Duration(v) : Count(v, compact);

    /// <summary>同比/环比增量：+12.5% / -3.1% / 新增。</summary>
    public static string Delta(double current, double previous)
    {
        if (previous <= 0) return current > 0 ? "新增" : "—";
        double pct = (current - previous) / previous * 100.0;
        if (Math.Abs(pct) < 0.05) return "持平";
        return (pct > 0 ? "+" : "") + pct.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>每天的活跃占比（活跃秒 / 86400）。</summary>
    public static string ActiveRatio(long activeSeconds)
        => (activeSeconds / 86400.0 * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

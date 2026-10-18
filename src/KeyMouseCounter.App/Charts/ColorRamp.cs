using System.Drawing;

namespace KeyMouseCounter.Charts;

/// <summary>
/// 分级色阶（§7.2.2）：按 P50 / P75 / P90 / P99 把数值分成 5 档，
/// Stops 比 Levels 多一个 —— 第 i 档（i = 0..4）对应 <c>Stops[i]</c>，
/// 边界值取"落在哪一档"由 <see cref="Pick"/> 统一决定，图表实现不许自己再算一遍。
/// </summary>
public sealed class ColorRamp
{
    /// <summary>#111827 → #1E3A8A → #2563EB → #60A5FA → #FBBF24（空 → 最热）。</summary>
    public static readonly Color[] DefaultStops =
    {
        Color.FromArgb(0x11, 0x18, 0x27),
        Color.FromArgb(0x1E, 0x3A, 0x8A),
        Color.FromArgb(0x25, 0x63, 0xEB),
        Color.FromArgb(0x60, 0xA5, 0xFA),
        Color.FromArgb(0xFB, 0xBF, 0x24)
    };

    private readonly double[] _levels;
    private readonly Color[] _stops;

    public ColorRamp(IEnumerable<double> levels, IEnumerable<Color>? stops = null)
    {
        _levels = levels.ToArray();
        _stops = (stops ?? DefaultStops).ToArray();
        if (_stops.Length == 0) _stops = DefaultStops;
        if (_stops.Length != _levels.Length + 1)
        {
            // 调用方给的档位不匹配时补/裁成 Levels.Length + 1，避免 Pick 越界
            var fixedStops = new Color[_levels.Length + 1];
            for (int i = 0; i < fixedStops.Length; i++)
                fixedStops[i] = _stops[Math.Min(i, _stops.Length - 1)];
            _stops = fixedStops;
        }
    }

    public IReadOnlyList<double> Levels => _levels;

    /// <summary>第 i 档的颜色（i = 0..Levels.Count），图例画色块用。</summary>
    public Color StopAt(int index)
    {
        if (_stops.Length == 0) return Color.Empty;
        if (index < 0) index = 0;
        if (index >= _stops.Length) index = _stops.Length - 1;
        return _stops[index];
    }

    /// <summary>用一组数值算分位边界；全零/空集合返回空 Levels（Pick 恒返回最浅色）。</summary>
    public static ColorRamp FromValues(IEnumerable<long> values, IEnumerable<Color>? stops = null)
    {
        var nonZero = values.Where(v => v > 0).ToList();
        if (nonZero.Count == 0) return new ColorRamp(Array.Empty<double>(), stops);
        nonZero.Sort();
        return new ColorRamp(Percentiles(nonZero, 50, 75, 90, 99), stops);
    }

    /// <summary>
    /// 线性插值分位数（与 Excel PERCENTILE.INC 同义）：
    /// 位置 = (n-1) * p/100，落在两个样本之间时按比例插值。
    /// </summary>
    public static double[] Percentiles(IReadOnlyList<long> sortedAscending, params double[] ps)
    {
        var result = new double[ps.Length];
        int n = sortedAscending.Count;
        if (n == 0) return result;
        for (int k = 0; k < ps.Length; k++)
        {
            double pos = (n - 1) * (ps[k] / 100.0);
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            if (lo < 0) lo = 0;
            if (hi > n - 1) hi = n - 1;
            double frac = pos - lo;
            result[k] = sortedAscending[lo] + (sortedAscending[hi] - sortedAscending[lo]) * frac;
        }
        return result;
    }

    /// <summary>
    /// 取色（§15.4 必须 +1）：v ≤ 0 → Stops[0]（背景/无数据）；
    /// 否则落在第 i 个分位边界内 → Stops[i+1]；超过 P99 → 最后一档。
    /// </summary>
    public Color Pick(double v)
    {
        if (_stops.Length == 0) return Color.Empty;
        if (v <= 0 || _levels.Length == 0) return _stops[0];
        for (int i = 0; i < _levels.Length; i++)
            if (v <= _levels[i]) return _stops[Math.Min(i + 1, _stops.Length - 1)];
        return _stops[_stops.Length - 1];
    }

    /// <summary>图例文字："P50 34 · P75 88 · P90 150 · P99 260"。</summary>
    public string LegendText(Func<double, string>? format = null)
    {
        if (_levels.Length == 0) return "无数据";
        format ??= d => ((long)Math.Round(d)).ToString();
        return $"P50 {format(_levels[0])} · P75 {format(_levels[1])} · P90 {format(_levels[2])} · P99 {format(_levels[3])}";
    }
}

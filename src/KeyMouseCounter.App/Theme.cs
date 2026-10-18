using System.Drawing;

namespace KeyMouseCounter;

/// <summary>
/// 主题配色（§7.4）。界面和图表都从这里取色，切换主题只需要改 <see cref="Kind"/>，
/// 不允许在某个控件里写死颜色。
/// </summary>
public enum ThemeKind { Dark, Light }

public sealed class ThemePalette
{
    public Color Window { get; init; }
    public Color Surface { get; init; }
    public Color SurfaceAlt { get; init; }
    public Color Border { get; init; }
    public Color Fore { get; init; }
    public Color Muted { get; init; }
    public Color Accent { get; init; }
    public Color AccentSoft { get; init; }
    public Color Danger { get; init; }
    public Color Warning { get; init; }
    public Color Grid { get; init; }
    public Color ChartBack { get; init; }
    public Color ChartFore { get; init; }
    public Color Head { get; init; }
    public ThemeKind Kind { get; init; }
}

public static class Theme
{
    public static readonly ThemePalette Dark = new()
    {
        Kind = ThemeKind.Dark,
        Window = Color.FromArgb(0x0B, 0x0E, 0x14),
        Surface = Color.FromArgb(0x0F, 0x13, 0x1A),
        SurfaceAlt = Color.FromArgb(0x16, 0x1B, 0x24),
        Border = Color.FromArgb(0x1F, 0x26, 0x33),
        Fore = Color.FromArgb(0xE5, 0xE7, 0xEB),
        Muted = Color.FromArgb(0x8A, 0x93, 0xA5),
        Accent = Color.FromArgb(0x4F, 0x8D, 0xF7),
        AccentSoft = Color.FromArgb(0x1E, 0x3A, 0x8A),
        Danger = Color.FromArgb(0xE5, 0x53, 0x53),
        Warning = Color.FromArgb(0xFB, 0xBF, 0x24),
        Grid = Color.FromArgb(0x1F, 0x26, 0x33),
        ChartBack = Color.FromArgb(0x0F, 0x13, 0x1A),
        ChartFore = Color.FromArgb(0xB4, 0xBC, 0xCA),
        Head = Color.FromArgb(0x18, 0x1D, 0x27)
    };

    public static readonly ThemePalette Light = new()
    {
        Kind = ThemeKind.Light,
        Window = Color.FromArgb(0xF6, 0xF7, 0xFA),
        Surface = Color.FromArgb(0xFF, 0xFF, 0xFF),
        SurfaceAlt = Color.FromArgb(0xF1, 0xF3, 0xF7),
        Border = Color.FromArgb(0xD8, 0xDE, 0xE8),
        Fore = Color.FromArgb(0x12, 0x18, 0x22),
        Muted = Color.FromArgb(0x64, 0x6E, 0x7E),
        Accent = Color.FromArgb(0x25, 0x63, 0xEB),
        AccentSoft = Color.FromArgb(0xDB, 0xE6, 0xFE),
        Danger = Color.FromArgb(0xDC, 0x26, 0x26),
        Warning = Color.FromArgb(0xB4, 0x53, 0x09),
        Grid = Color.FromArgb(0xE5, 0xE9, 0xEF),
        ChartBack = Color.FromArgb(0xFF, 0xFF, 0xFF),
        ChartFore = Color.FromArgb(0x3F, 0x46, 0x53),
        Head = Color.FromArgb(0xEE, 0xF1, 0xF6)
    };

    private static ThemePalette _current = Dark;

    public static ThemePalette Current => _current;
    public static ThemeKind Kind => _current.Kind;

    /// <summary>主题换色时触发（图表/卡片在自己的 Render 里重新取色）。</summary>
    public static event Action? Changed;

    public static void Apply(string? name)
    {
        var next = string.Equals(name, "light", StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        if (ReferenceEquals(next, _current)) return;
        _current = next;
        Changed?.Invoke();
    }

    /// <summary>主字体：中文优先用微软雅黑，缺了就退回系统默认。</summary>
    public static Font Font(float size, FontStyle style = FontStyle.Regular)
    {
        try { return new Font("Microsoft YaHei UI", size, style); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style); }
    }
}

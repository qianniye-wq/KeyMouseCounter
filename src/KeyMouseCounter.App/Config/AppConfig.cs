using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Config;

public sealed class CountingConfig
{
    /// <summary>长按自动重复是否计数。默认 false（§2.3）。</summary>
    public bool CountAutoRepeat { get; set; } = false;
    /// <summary>Shift/Ctrl/Alt/Win 是否计数。默认 true。</summary>
    public bool CountModifierKeys { get; set; } = true;
    /// <summary>注入/合成按键是否忽略。默认 true。</summary>
    public bool IgnoreInjected { get; set; } = true;
    /// <summary>本程序自身窗口/挂件的输入是否排除。默认 true。</summary>
    public bool ExcludeSelf { get; set; } = true;
    /// <summary>是否统计滚轮。</summary>
    public bool CountWheel { get; set; } = true;
    /// <summary>活跃时长的空闲阈值（秒）。</summary>
    public int IdleThresholdSec { get; set; } = 60;
}

public sealed class StorageConfig
{
    public bool WriteDailyLog { get; set; } = true;
    public int FlushIntervalSec { get; set; } = 30;
    /// <summary>0 = 永久保留。</summary>
    public int DataRetentionDays { get; set; } = 0;
}

public sealed class WidgetConfig
{
    public bool Enabled { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public double Opacity { get; set; } = 0.88;
    public int X { get; set; } = 40;
    public int Y { get; set; } = 40;
    public double FontSize { get; set; } = 10;
    public bool ShowActiveTime { get; set; } = true;
    public string AccentColor { get; set; } = "#4F8DF7";
    public string Hotkey { get; set; } = "Ctrl+Alt+K";
    public bool HideOnFullscreen { get; set; } = false;
    /// <summary>万以上是否缩写为 1.5万。</summary>
    public bool CompactNumbers { get; set; } = false;
}

public sealed class UiConfig
{
    public string DefaultTab { get; set; } = "TodayHour";
    public string DefaultChart { get; set; } = "Line";
    public int DefaultRangeDays { get; set; } = 7;
    public bool StartMinimized { get; set; } = true;
    public int[] LastMainWindowSize { get; set; } = { 1000, 700 };
    /// <summary>关闭主窗口的行为：true = 隐藏到托盘，false = 退出。</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>界面主题：dark / light。</summary>
    public string Theme { get; set; } = "dark";
}

public sealed class DebugConfig
{
    /// <summary>调试：允许统计注入输入（自动化测试用）。</summary>
    public bool CountInjected { get; set; } = false;
    public string LogLevel { get; set; } = "Info";
}

/// <summary>
/// config.json 的全部配置（§2.4）。损坏时回落默认值 + 备份为 config.bad.json，绝不启动失败。
/// </summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;
    public CountingConfig Counting { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public WidgetConfig Widget { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public DebugConfig Debug { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 别把 "Ctrl+Alt+K" 转义成 "Ctrl\u002BAlt\u002BK"：配置文件要人能读能改
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>计算属性，不写进 config.json。</summary>
    [JsonIgnore]
    public LogLevel MinLogLevel => Enum.TryParse<LogLevel>(Debug.LogLevel, ignoreCase: true, out var l)
        ? l
        : Diagnostics.LogLevel.Info;

    /// <summary>读取配置；文件不存在 → 写一份默认配置；损坏 → 备份 + 默认值。</summary>
    public static AppConfig Load(string path, out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(path))
            {
                var fresh = new AppConfig();
                fresh.Save(path);
                return fresh;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) throw new JsonException("配置文件为空");

            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                      ?? throw new JsonException("反序列化结果为 null");
            cfg.Normalize();
            return cfg;
        }
        catch (Exception ex)
        {
            warning = $"config.json 损坏，已回落到默认值：{ex.Message}";
            try
            {
                if (File.Exists(path))
                {
                    var bad = AppPaths.ConfigBadPath;
                    File.Copy(path, bad, overwrite: true);
                }
            }
            catch { /* 备份失败也不能影响启动 */ }

            var fallback = new AppConfig();
            try { fallback.Save(path); } catch { }
            return fallback;
        }
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"配置保存失败: {ex.Message}");
        }
    }

    /// <summary>把越界值夹回合法范围，避免手改配置把程序弄崩。</summary>
    public void Normalize()
    {
        SchemaVersion = 2;
        Counting ??= new CountingConfig();
        Storage ??= new StorageConfig();
        Widget ??= new WidgetConfig();
        Ui ??= new UiConfig();
        Debug ??= new DebugConfig();

        Counting.IdleThresholdSec = Math.Clamp(Counting.IdleThresholdSec, 1, 3600);
        Storage.FlushIntervalSec = Math.Clamp(Storage.FlushIntervalSec, 1, 3600);
        Storage.DataRetentionDays = Math.Max(0, Storage.DataRetentionDays);

        Widget.Opacity = Math.Clamp(Widget.Opacity, 0.2, 1.0);
        Widget.FontSize = Math.Clamp(Widget.FontSize, 6, 32);
        if (string.IsNullOrWhiteSpace(Widget.AccentColor)) Widget.AccentColor = "#4F8DF7";
        if (string.IsNullOrWhiteSpace(Widget.Hotkey)) Widget.Hotkey = "Ctrl+Alt+K";

        Ui.DefaultRangeDays = Ui.DefaultRangeDays is 1 or 7 or 14 or 30 or 90 ? Ui.DefaultRangeDays : 7;
        if (Ui.LastMainWindowSize is not { Length: 2 }) Ui.LastMainWindowSize = new[] { 1000, 700 };
        Ui.LastMainWindowSize[0] = Math.Clamp(Ui.LastMainWindowSize[0], 600, 10000);
        Ui.LastMainWindowSize[1] = Math.Clamp(Ui.LastMainWindowSize[1], 400, 10000);
        if (Ui.DefaultTab is not ("TodayHour" or "TodayMinute" or "MultiDay")) Ui.DefaultTab = "TodayHour";
        if (Ui.DefaultChart is not ("Line" or "Bar" or "Heatmap" or "Table")) Ui.DefaultChart = "Line";
        if (Ui.Theme is not ("dark" or "light")) Ui.Theme = "dark";
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(
        JsonSerializer.Serialize(this, JsonOptions), JsonOptions) ?? new AppConfig();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// 配置热加载（§2.4）：每 60s 比较 LastWriteTimeUtc，变化则重载。
/// counting.* 与 widget.hotkey 由订阅者立即生效。
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
    private readonly string _path;
    private readonly AppConfig _working;      // 由 UI 改写的活配置
    private readonly Action<AppConfig> _onReloaded;
    private readonly System.Threading.Timer _timer;
    private DateTime _lastWriteUtc;
    private bool _saving;

    public ConfigWatcher(string path, AppConfig working, Action<AppConfig> onReloaded)
    {
        _path = path;
        _working = working;
        _onReloaded = onReloaded;
        try { _lastWriteUtc = File.GetLastWriteTimeUtc(path); } catch { _lastWriteUtc = DateTime.MinValue; }
        _timer = new System.Threading.Timer(_ => Poll(), null, 60_000, 60_000);
    }

    /// <summary>程序自己保存配置时调用，避免把自己的写入当成外部修改。</summary>
    public void NotifySelfSaved()
    {
        try { _lastWriteUtc = File.GetLastWriteTimeUtc(_path); } catch { }
    }

    /// <summary>UI 保存配置的入口。</summary>
    public void Save()
    {
        _saving = true;
        try
        {
            _working.Save(_path);
            NotifySelfSaved();
        }
        finally { _saving = false; }
    }

    private void Poll()
    {
        if (_saving) return;
        try
        {
            if (!File.Exists(_path)) return;
            var t = File.GetLastWriteTimeUtc(_path);
            if (t == _lastWriteUtc) return;
            _lastWriteUtc = t;

            var warning = (string?)null;
            var loaded = AppConfig.Load(_path, out warning);
            if (warning is not null) Log.Warn(warning);

            // 就地覆盖字段，保持对象标识不变（其他地方持有的是同一个实例）
            var target = _working;
            target.Counting = loaded.Counting;
            target.Storage = loaded.Storage;
            target.Ui = loaded.Ui;
            target.Debug = loaded.Debug;

            // 挂件位置由程序自己管理：外部文件里若为默认值就不要把用户的拖动结果抹掉
            var keepPos = loaded.Widget.X == 40 && loaded.Widget.Y == 40 &&
                          (target.Widget.X != 40 || target.Widget.Y != 40);
            var savedX = target.Widget.X;
            var savedY = target.Widget.Y;
            target.Widget = loaded.Widget;
            if (keepPos) { target.Widget.X = savedX; target.Widget.Y = savedY; }

            Log.Configure(target.MinLogLevel);
            _onReloaded(target);
            Log.Info("config reloaded");
        }
        catch (Exception ex)
        {
            Log.Warn($"配置热加载失败: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();
}

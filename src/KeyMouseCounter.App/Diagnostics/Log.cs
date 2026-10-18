using System.Text;

namespace KeyMouseCounter.Diagnostics;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3, Off = 4 }

/// <summary>
/// 自写的滚动文件日志（§9.3）。单文件 ≤ 2 MB，保留 3 个。
/// 硬性约束：禁止输出 vkCode 序列、窗口标题、输入文本（§10.1）。
/// 任何情况下都不允许抛异常——日志失败不能拖垮主流程。
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 3;

    private static readonly object Gate = new();
    private static readonly bool FullStack =
        string.Equals(Environment.GetEnvironmentVariable("KMC_LOG_STACK"), "1", StringComparison.Ordinal);
    private static string? _dir;
    private static string? _path;
    private static StreamWriter? _writer;
    private static LogLevel _min = LogLevel.Info;

    /// <summary>运行日志目录；默认 %LOCALAPPDATA%\KeyMouseCounter\logs。</summary>
    public static string Dir => _dir ?? AppPaths.LogDir;

    public static void Configure(LogLevel minLevel, string? dir = null)
    {
        lock (Gate)
        {
            _min = minLevel;
            if (dir is not null && !string.Equals(_dir, dir, StringComparison.OrdinalIgnoreCase))
            {
                CloseLocked();
                _dir = dir;
            }
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message) => Write(LogLevel.Warn, message, null);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < _min) return;
        try
        {
            var sb = new StringBuilder(160);
            sb.Append('[')
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append("][")
              .Append(level.ToString().ToUpperInvariant().PadRight(5))
              .Append("] ")
              .Append(message);
            if (ex is not null)
            {
                sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                if (ex.StackTrace is { Length: > 0 } st)
                {
                    // 默认只记栈首行（日志保持一行一条）；排查时用环境变量 KMC_LOG_STACK=1 放开前几帧
                    var first = st.Split('\n')[0].Trim();
                    sb.Append(" @ ").Append(first);
                    if (FullStack)
                    {
                        var frames = st.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 1; i < frames.Length && i < 6; i++)
                            sb.Append(" <- ").Append(frames[i].Trim());
                    }
                }
            }
            var line = sb.ToString();

            lock (Gate)
            {
                EnsureWriterLocked();
                _writer!.WriteLine(line);
                _writer.Flush();
                if (_writer.BaseStream.Length > MaxBytes) RollLocked();
            }
        }
        catch
        {
            // 磁盘满 / 目录只读：静默降级为不写日志（§9.2）
        }
    }

    private static void EnsureWriterLocked()
    {
        if (_writer is not null) return;
        Directory.CreateDirectory(Dir);
        _path = Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");
        var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
    }

    private static void RollLocked()
    {
        var baseName = Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}");
        CloseLocked();
        try
        {
            // app-YYYYMMDD.2.log → 丢弃；.1 → .2；当前 → .1
            var oldest = $"{baseName}.{MaxFiles - 1}.log";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = MaxFiles - 2; i >= 1; i--)
            {
                var src = $"{baseName}.{i}.log";
                if (File.Exists(src)) File.Move(src, $"{baseName}.{i + 1}.log", overwrite: true);
            }
            if (File.Exists(_path)) File.Move(_path, $"{baseName}.1.log", overwrite: true);
        }
        catch { /* 滚动失败不影响继续写 */ }
    }

    private static void CloseLocked()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _path = null;
    }

    public static void Shutdown()
    {
        lock (Gate) CloseLocked();
    }
}

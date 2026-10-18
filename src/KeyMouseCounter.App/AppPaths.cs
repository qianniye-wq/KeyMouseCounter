namespace KeyMouseCounter;

/// <summary>
/// 所有本地路径的唯一来源。数据一律放在 %LOCALAPPDATA%\KeyMouseCounter（§6.1、§15.5 坑 9）。
/// 测试可通过 <see cref="RootOverride"/> 重定向到临时目录。
/// </summary>
public static class AppPaths
{
    public const string AppName = "KeyMouseCounter";
    public const string Version = "2.3.0";

    private static string? _root;

    /// <summary>测试用：重定向数据根目录。null 表示使用 %LOCALAPPDATA%\KeyMouseCounter。</summary>
    public static string? RootOverride
    {
        get => _root;
        set { _root = value; }
    }

    public static string Root => _root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string DataDir => Path.Combine(Root, "data");
    public static string LogDir => Path.Combine(Root, "logs");
    public static string ExportDir => Path.Combine(Root, "exports");
    public static string DbPath => Path.Combine(Root, "data.db");
    public static string ConfigPath => Path.Combine(Root, "config.json");
    public static string ConfigBadPath => Path.Combine(Root, "config.bad.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(ExportDir);

        // §10.5：日志目录里放一句免责说明，避免被误认为记录了输入内容
        var readme = Path.Combine(DataDir, "README.txt");
        if (!File.Exists(readme))
        {
            try
            {
                File.WriteAllText(readme,
                    "本目录只包含按键/点击的次数统计，不含任何输入内容、窗口标题或按键序列。\r\n" +
                    "可以安全删除；删除后下次启动会从 data.db 重建。\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { /* 目录只读时忽略 */ }
        }
    }

    public static string DailyLogPath(DateOnly day) => Path.Combine(DataDir, $"{day:yyyy-MM-dd}.jsonl");
    public static string DailySummaryPath(DateOnly day) => Path.Combine(DataDir, $"{day:yyyy-MM-dd}.summary.json");
}

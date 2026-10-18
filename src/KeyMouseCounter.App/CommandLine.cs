namespace KeyMouseCounter;

/// <summary>命令行开关（§8.4：--register-autostart / --unregister-autostart / --purge）。</summary>
internal sealed class CommandLineOptions
{
    public bool RegisterAutostart { get; set; }
    public bool UnregisterAutostart { get; set; }
    public bool Purge { get; set; }
    public bool PurgeDataOnly { get; set; }
    public bool NoWidget { get; set; }
    public bool TestMode { get; set; }

    /// <summary>§14.4：自动化验证用，临时把 debug.countInjected 打开（默认过滤注入输入）。</summary>
    public bool CountInjected { get; set; }

    public bool IsCommand => RegisterAutostart || UnregisterAutostart || Purge;
}

internal static class CommandLine
{
    public const string Help = """
KeyMouseCounter —— 键鼠按键统计

用法: KeyMouseCounter.exe [命令]

命令:
  --register-autostart    注册开机自启（计划任务）
  --unregister-autostart  取消开机自启
  --purge                 删除全部用户数据（%LOCALAPPDATA%\KeyMouseCounter）后退出
  --reset-data            只删除统计数据（data 目录 + data.db），保留配置
  --no-widget             本次启动不显示挂件
  --count-injected        本次启动统计注入/合成输入（仅自动化验证用，不写配置）
  --help                  显示本帮助
""";

    /// <summary>解析命令行；返回 false 表示已处理完毕（帮助/自启/清空），调用方应直接退出。</summary>
    public static bool Parse(string[] args, out CommandLineOptions options)
    {
        options = new CommandLineOptions();
        
        if (args is null || args.Length == 0) return true;

        foreach (var raw in args)
        {
            string a = raw.Trim();
            if (a.Length == 0) continue;
            switch (a.ToLowerInvariant())
            {
                case "--register-autostart":
                case "/register-autostart":
                    options.RegisterAutostart = true;
                    break;
                case "--unregister-autostart":
                case "/unregister-autostart":
                    options.UnregisterAutostart = true;
                    break;
                case "--purge":
                case "/purge":
                    options.Purge = true;
                    break;
                case "--reset-data":
                    options.PurgeDataOnly = true;
                    break;
                case "--no-widget":
                    options.NoWidget = true;
                    break;
                case "--count-injected":
                case "/count-injected":
                    options.CountInjected = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    WriteOut(Help);
                    return false;
                default:
                    // 未知参数（比如开机自启时被资源管理器附加的路径）忽略即可
                    break;
            }
        }

        if (options.RegisterAutostart)
        {
            string? err = Services.AutostartService.Register();
            WriteOut(err is null
                ? $"已注册开机自启（计划任务 {Services.AutostartService.TaskName}）。"
                : "注册开机自启失败：" + err);
            return false;
        }

        if (options.UnregisterAutostart)
        {
            string? err = Services.AutostartService.Unregister();
            WriteOut(err is null
                ? $"已取消开机自启（计划任务 {Services.AutostartService.TaskName}）。"
                : "取消开机自启失败：" + err);
            return false;
        }

        if (options.Purge || options.PurgeDataOnly)
        {
            bool ok = options.PurgeDataOnly ? PurgeDataOnly() : PurgeEverything();
            WriteOut(ok ? "数据已删除。" : "删除数据时出错，详见日志。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 输出被重定向（管道/脚本捕获）时固定写 UTF-8 字节，避免中文提示在不同代码页下变乱码；
    /// 直接开在控制台里则交给控制台自己的代码页，cmd/PowerShell 显示正常。
    /// </summary>
    private static readonly bool OutRedirected = SafeRedirected(isError: false);
    private static readonly bool ErrRedirected = SafeRedirected(isError: true);

    private static bool SafeRedirected(bool isError)
    {
        try { return isError ? Console.IsErrorRedirected : Console.IsOutputRedirected; }
        catch { return true; }
    }

    private static void WriteOut(string text) => Write(Console.OpenStandardOutput, OutRedirected, isError: false, text);

    private static void WriteErr(string text) => Write(Console.OpenStandardError, ErrRedirected, isError: true, text);

    private static void Write(Func<Stream> open, bool redirected, bool isError, string text)
    {
        try
        {
            if (!redirected)
            {
                if (isError) Console.Error.WriteLine(text);
                else Console.WriteLine(text);
                return;
            }

            using var s = open();
            byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(text + Environment.NewLine);
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }
        catch
        {
            // 输出失败（无控制台/句柄失效）不影响命令本身
        }
    }

    private static bool PurgeEverything()
    {
        try
        {
            string root = AppPaths.Root;
            // 必须是本程序的固定目录，避免误删
            if (!string.Equals(Path.GetFileName(root), AppPaths.AppName, StringComparison.OrdinalIgnoreCase))
            {
                WriteErr("拒绝删除非常规路径：" + root);
                return false;
            }

            Services.AutostartService.Unregister();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            DropEmptyParent(root);
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("--purge 删除数据失败", ex);
            WriteErr(ex.Message);
            return false;
        }
    }

    private static bool PurgeDataOnly()
    {
        try
        {
            if (Directory.Exists(AppPaths.DataDir)) Directory.Delete(AppPaths.DataDir, recursive: true);
            foreach (var f in new[] { AppPaths.DbPath, AppPaths.DbPath + "-wal", AppPaths.DbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("--reset-data 失败", ex);
            WriteErr(ex.Message);
            return false;
        }
    }

    private static void DropEmptyParent(string root)
    {
        try
        {
            string? parent = Path.GetDirectoryName(root);
            if (parent is null) return;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch { }
    }
}


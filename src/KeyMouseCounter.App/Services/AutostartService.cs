using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Services;

/// <summary>
/// 开机自启（§8.2）：用计划任务而不是注册表 Run 键——能设"仅交互用户"、
/// 电池下也启动、失败自动重启。
/// <para>
/// 注意：<b>创建/删除计划任务本身需要管理员权限</b>（普通用户令牌下
/// <c>Register-ScheduledTask</c> 会返回 0x80070005 拒绝访问）。
/// 所以本类在未提权时直接返回可读的中文提示，不做无意义的重试。
/// </para>
/// </summary>
internal static class AutostartService
{
    internal const string TaskName = "KeyMouseCounter";

    public const string NeedElevation =
        "需要管理员权限：请右键「以管理员身份运行」KeyMouseCounter 后再操作，"
        + "或在管理员 PowerShell 中执行 scripts\\install-autostart.ps1。";

    /// <summary>exe 绝对路径（单文件发布时就是当前进程路径）。</summary>
    public static string ExecutablePath
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }
            return Path.Combine(AppContext.BaseDirectory, "KeyMouseCounter.exe");
        }
    }

    /// <summary>当前进程是否已提权。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsEnabled(out string? error)
    {
        error = null;
        var (code, _, stderr) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        if (code != 0 && !string.IsNullOrWhiteSpace(stderr))
            error = stderr.Trim();
        return code == 0;
    }

    /// <summary>注册计划任务；返回 null 表示成功。</summary>
    public static string? Register()
    {
        if (!IsElevated()) return NeedElevation;

        string exe = ExecutablePath;
        string script = $"""
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$exe = '{exe.Replace("'", "''")}'
$action = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
""";
        return RunPowerShell(script);
    }

    public static string? Unregister()
    {
        if (!IsElevated()) return NeedElevation;

        var (code, _, err) = Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        if (code == 0) return null;
        if (err.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("找不到", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("系统找不到指定的文件", StringComparison.OrdinalIgnoreCase))
            return null;
        return string.IsNullOrWhiteSpace(err) ? $"schtasks 退出码 {code}" : err.Trim();
    }

    private static string? RunPowerShell(string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        // -OutputFormat Text：否则错误走 CLIXML，用户会看到一坨 <Objs> XML
        var (code, outText, errText) = Run("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}");
        if (code == 0) return null;

        string msg = FirstMeaningfulLine(string.IsNullOrWhiteSpace(errText) ? outText : errText);
        if (IsAccessDenied(msg)) return NeedElevation;
        Log.Warn($"计划任务操作失败：{msg}");
        return msg;
    }

    private static bool IsAccessDenied(string text) =>
        text.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("PermissionDenied", StringComparison.OrdinalIgnoreCase);

    /// <summary>从多行输出里挑出真正的错误行，别把进度/调用栈全抛给用户。</summary>
    private static string FirstMeaningfulLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("<Objs", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("+", StringComparison.Ordinal)) continue;
            if (line.Contains("CategoryInfo", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("正在准备首次使用模块", StringComparison.OrdinalIgnoreCase)) continue;
            return line.Length > 300 ? line[..300] : line;
        }
        return text.Trim();
    }

    private static (int Code, string StdOut, string StdErr) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return (-1, "", "进程启动失败");
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.HasExited ? p.ExitCode : -1, o, e);
        }
        catch (Exception ex)
        {
            Log.Warn($"执行 {file} 失败：{ex.Message}");
            return (-1, "", ex.Message);
        }
    }
}

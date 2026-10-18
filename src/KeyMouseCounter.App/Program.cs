using KeyMouseCounter.UI;

namespace KeyMouseCounter;

/// <summary>入口（§4.2 / §14.6.6）。真正的装配全部在 AppHost 里。</summary>
internal static class Program
{
    /// <summary>
    /// UI 线程栈大小。默认主线程只有 1MB：SkiaSharp(Skia)+DirectWrite 在字体回退时
    /// 调用链很深，个别机器上会以 0xc00000fd（STACK_OVERFLOW，错误模块 TextShaping.dll）
    /// 直接杀掉进程，且托管层无法捕获。给 UI 线程 8MB 栈可显著提高余量（§9.2/§13）。
    /// </summary>
    private const int UiThreadStackBytes = 8 * 1024 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        // 命令行里显式要求时走无界面模式（自启脚本/计划任务/运维脚本用）
        if (!CommandLine.Parse(args, out var options)) return 0;

        var exitCode = 0;
        var ui = new Thread(() => exitCode = RunUi(options), UiThreadStackBytes)
        {
            Name = "KMC.UI",
            IsBackground = false,
        };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return exitCode;
    }

    private static int RunUi(CommandLineOptions options)
    {
        ApplicationConfiguration.Initialize();
        InstallExceptionHandlers();

        AppHost? host = null;
        try
        {
            host = AppHost.Create(options);
            host.Start();
            Application.Run(host);
            return 0;
        }
        catch (Services.AlreadyRunningException)
        {
            // 第二个实例：旧实例已被唤醒，直接安静退出（不要弹错误框）
            return 0;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("fatal startup error", ex);
            try
            {
                MessageBox.Show(
                    "KeyMouseCounter 启动失败：\r\n" + ex.Message + "\r\n\r\n日志位于：\r\n" + Diagnostics.Log.Dir,
                    "KeyMouseCounter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // 没有交互桌面（服务/计划任务场景）时弹不出框，忽略
            }
            return 1;
        }
        finally
        {
            try { host?.Shutdown(); } catch (Exception ex) { Diagnostics.Log.Error("shutdown error", ex); }
            Diagnostics.Log.Shutdown();
        }
    }

    /// <summary>
    /// 全局异常兜底（文档 §9.2）：UI 线程异常只记日志、不弹 .NET 的「JIT 调试」对话框
    /// （用户看到那种窗体只会以为程序坏了）；后台任务异常同理不留未观察状态。
    /// 必须在创建任何窗口之前装好。
    /// </summary>
    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            // 记日志即可：统计与刷盘都有各自的兜底，UI 单次绘制失败不应终止进程
            Diagnostics.Log.Error("UI 线程未处理异常", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Diagnostics.Log.Error($"未处理异常（IsTerminating={e.IsTerminating}）", ex);
            else
                Diagnostics.Log.Error($"未处理异常（IsTerminating={e.IsTerminating}）：{e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diagnostics.Log.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }
}

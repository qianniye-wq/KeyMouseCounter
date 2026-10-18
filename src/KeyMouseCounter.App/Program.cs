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
}

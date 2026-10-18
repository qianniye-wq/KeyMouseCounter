using KeyMouseCounter.UI;

namespace KeyMouseCounter;

/// <summary>入口（§4.2 / §14.6.6）。真正的装配全部在 AppHost 里。</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 命令行里显式要求时走无界面模式（自启脚本/计划任务/运维脚本用）
        if (!CommandLine.Parse(args, out var options)) return;

        // 没传 --no-redirect 时，退出自启脚本的"启动托盘程序"会被立刻杀掉：
        // 显式忽略 SIGTERM/CTRL_CLOSE。改成 Window 子系统后这里影响不大，留着无害。
        ApplicationConfiguration.Initialize();

        AppHost? host = null;
        try
        {
            host = AppHost.Create(options);
            host.Start();
            Application.Run(host);
        }
        catch (Services.AlreadyRunningException)
        {
            // 第二个实例：旧实例已被唤醒，直接安静退出（不要弹错误框）
            return;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("fatal startup error", ex);
            MessageBox.Show(
                "KeyMouseCounter 启动失败：\r\n" + ex.Message + "\r\n\r\n日志位于：\r\n" + Diagnostics.Log.Dir,
                "KeyMouseCounter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { host?.Shutdown(); } catch (Exception ex) { Diagnostics.Log.Error("shutdown error", ex); }
            Diagnostics.Log.Shutdown();
        }
    }
}

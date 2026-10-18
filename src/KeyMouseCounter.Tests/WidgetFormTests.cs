using System.Windows.Forms;
using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.UI;

namespace KeyMouseCounter.Tests;

/// <summary>
/// 挂件窗口句柄生命周期的回归测试（§5.9 / §9.2）。
///
/// 背景：Win10 兜底圆角曾经写成
/// <c>using var rgn = Region.FromHrgn(CreateRoundRectRgn(...)); Region = rgn;</c>，
/// 而 <see cref="Control.Region"/> 持有的就是同一个 Region 实例。using 离开时它被 Dispose，
/// 之后窗口句柄一旦重建，WinForms 会在 <c>OnHandleCreated</c> 里 <c>SetRegion</c> →
/// <c>Region.GetHrgn</c> 抛 <c>ArgumentException: Parameter is not valid</c>，
/// 真机表现为“程序一启动就弹 JIT 错误框、点继续后又能用”。
///
/// 修复方式是改用原生 SetWindowRgn（HRGN 交给系统托管），不再把托管的 Region 交给窗口。
/// 本测试通过真实创建 + 重建句柄，确保这条路上不再抛异常。
/// </summary>
public sealed class WidgetFormTests
{
    [Fact]
    public void WidgetForm_HandleRecreation_DoesNotThrow()
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            void OnThreadException(object? sender, ThreadExceptionEventArgs e) => captured ??= e.Exception;
            Application.ThreadException += OnThreadException;

            using var temp = new TempRoot();
            try
            {
                var cfg = new AppConfig();
                cfg.Widget.ClickThrough = false; // 非穿透 → 切换时真的要走 ExStyle 重建句柄
                using var watch = new ConfigWatcher(AppPaths.ConfigPath, cfg, _ => { });
                var engine = new CounterEngine(cfg, new FakeClock(new DateTimeOffset(2025, 1, 1, 8, 35, 0, TimeSpan.FromHours(8))), cacheClock: false);

                using var widget = new WidgetForm(
                    cfg, watch, engine,
                    () => new WidgetData(0, 0, 0, 0, false),
                    () => { }, () => { }, () => { }, () => { }, () => false);

                var handle = widget.Handle; // 第一次创建 → OnHandleCreated 走圆角落路径
                Assert.NotEqual(IntPtr.Zero, handle);

                // 改 ExStyle 后 WinForms 的真实路径：句柄销毁 → 重建 → OnHandleCreated 再走一次
                widget.ToggleClickThrough();
                Assert.NotEqual(IntPtr.Zero, widget.Handle);

                widget.ToggleClickThrough(); // 再来一次，确认幂等
                Assert.NotEqual(IntPtr.Zero, widget.Handle);
            }
            catch (Exception ex)
            {
                captured ??= ex;
            }
            finally
            {
                Application.ThreadException -= OnThreadException;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "挂件窗口测试超时（可能弹出了模态框）");
        Assert.Null(captured);
    }
}

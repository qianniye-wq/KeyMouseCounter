using System.Drawing;

namespace KeyMouseCounter.UI;

/// <summary>
/// 挂件位置校正（§5.9 / §15.5 坑 14）。抽成纯函数是为了 T23 能不开窗口直接测：
/// 显示器顺序、工作区、首次启动都从参数进来，不碰 Screen API。
/// </summary>
internal static class WidgetGeometry
{
    /// <summary>可见判定门槛：至少 40x30 的矩形落在某块屏的工作区里，才算"用户还看得见"。</summary>
    public const int MinVisibleWidth = 40;
    public const int MinVisibleHeight = 30;

    /// <summary>首次启动落点 / 越界归位落点（相对主屏工作区左上角）。</summary>
    public const int FirstRunOffset = 40;
    public const int OffscreenOffset = 100;

    /// <summary>
    /// 返回校正后的位置：可见且非首次启动时原样返回；
    /// 否则归位到主屏工作区左上角（首次启动 (40,40)，越界归位 +100px），并夹进工作区。
    /// </summary>
    public static Point EnsureVisible(
        Rectangle widget,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle primaryWorkingArea,
        bool firstRun)
    {
        if (!firstRun)
        {
            foreach (var wa in workingAreas)
            {
                var inter = Rectangle.Intersect(widget, wa);
                if (inter.Width >= MinVisibleWidth && inter.Height >= MinVisibleHeight)
                    return new Point(widget.X, widget.Y);
            }
        }

        int maxX = Math.Max(primaryWorkingArea.Left, primaryWorkingArea.Right - widget.Width);
        int maxY = Math.Max(primaryWorkingArea.Top, primaryWorkingArea.Bottom - widget.Height);
        int offsetX = primaryWorkingArea.Left + (firstRun ? FirstRunOffset : OffscreenOffset);
        int offsetY = primaryWorkingArea.Top + (firstRun ? FirstRunOffset : OffscreenOffset);

        return new Point(
            Math.Clamp(offsetX, primaryWorkingArea.Left, maxX),
            Math.Clamp(offsetY, primaryWorkingArea.Top, maxY));
    }
}

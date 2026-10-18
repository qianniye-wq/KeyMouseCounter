using System.Runtime.InteropServices;

namespace KeyMouseCounter.Native;

internal delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

/// <summary>§15.1 的 P/Invoke 声明清单，全部集中在这里。</summary>
internal static class NativeMethods
{
    // ── 钩子 ──
    internal const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;

    internal const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101,
                       WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105,
                       WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                       WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
                       WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
                       WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E,
                       WM_QUIT = 0x0012;

    internal const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetMessage(out MSG m, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG m);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG m);

    [DllImport("user32.dll")]
    internal static extern bool PostThreadMessage(uint id, uint msg, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? name);

    // ── 状态/窗口 ──
    [DllImport("user32.dll")]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    // ── 热键 ──
    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── Win11 圆角（Win10 调用返回失败，忽略即可） ──
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ── 空闲检测（钩子自愈） ──
    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();

    // ── 扩展样式（挂件穿透） ──    internal const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static int GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    internal static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        else SetWindowLong32(hWnd, nIndex, value);
    }

    /// <summary>释放 GetHicon() 得到的图标句柄（不释放会漏 GDI 对象）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}

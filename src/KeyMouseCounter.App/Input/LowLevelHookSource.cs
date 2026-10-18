using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Native;
using static KeyMouseCounter.Native.NativeMethods;

namespace KeyMouseCounter.Input;

/// <summary>
/// 主路线：WH_KEYBOARD_LL + WH_MOUSE_LL（§3.1 方案 A、§5.1）。
///
/// 三条硬规则（§4.1）：
/// 1. 回调里只有 读结构体 → 过滤 → 一次极短 lock 自增 → CallNextHookEx，零 I/O；
/// 2. 钩子线程与 UI 线程分离，UI 卡顿不会让回调超时被静默摘钩；
/// 3. 委托用字段持有，否则 GC 回收后崩进程（§15.5 坑 1）。
/// </summary>
public sealed class LowLevelHookSource : IInputSource
{
    private readonly CounterEngine _engine;
    private readonly AppConfig _cfg;

    // 必须用字段持有，否则回收后 CallbackOnCollectedDelegate 崩进程
    private readonly LowLevelProc _kbProc;
    private readonly LowLevelProc _msProc;

    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private IntPtr _kbHook = IntPtr.Zero;
    private IntPtr _msHook = IntPtr.Zero;
    private uint _threadId;
    private volatile bool _running;
    private volatile string? _lastError;
    private volatile bool _stopRequested;

    // §5.7 自身进程排除：每 500ms 缓存一次前台窗口所属 PID
    private readonly uint _selfPid = (uint)Environment.ProcessId;
    private IntPtr _cachedForeground = IntPtr.Zero;
    private uint _cachedForegroundPid;
    private long _fgCacheTick;

    public event Action<InputEvent>? EventReceived;

    public LowLevelHookSource(CounterEngine engine, AppConfig cfg)
    {
        _engine = engine;
        _cfg = cfg;
        _kbProc = KbCallback;
        _msProc = MsCallback;
    }

    public bool IsRunning => _running;
    public string? LastError => _lastError;

    public void Start()
    {
        if (_thread is not null) return;
        _stopRequested = false;
        _ready.Reset();
        _thread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "KMC.HookThread",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _lastError = "钩子线程启动超时";
            Log.Error(_lastError);
            return;
        }
        if (_running) Log.Info("hooks installed");
        else Log.Error($"hooks install failed: {_lastError}");
    }

    public void Stop()
    {
        var t = _thread;
        if (t is null) return;
        _stopRequested = true;
        try
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
        try { t.Join(TimeSpan.FromSeconds(3)); } catch { }
        _thread = null;
        _running = false;
    }

    private void HookThreadProc()
    {
        _threadId = GetCurrentThreadId();
        var hMod = GetModuleHandle(null);

        try
        {
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, hMod, 0);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }

        if (_kbHook == IntPtr.Zero || _msHook == IntPtr.Zero)
        {
            _lastError ??= new Win32Exception(Marshal.GetLastWin32Error()).Message;
            UnhookSafe();
            _running = false;
            _ready.Set();
            return;
        }

        _lastError = null;
        _running = true;
        _ready.Set();

        // 独立消息循环：低级钩子必须在安装它的线程上泵消息
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            if (_stopRequested) break;
        }

        // UnhookWindowsHookEx 必须在安装钩子的同一线程执行
        UnhookSafe();
        _running = false;
        Log.Info("hooks uninstalled");
    }

    private void UnhookSafe()
    {
        try { if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook); } catch { }
        try { if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook); } catch { }
        _kbHook = _msHook = IntPtr.Zero;
    }

    private IntPtr KbCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            if (down || up)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool injected = (info.flags & LLKHF_INJECTED) != 0;
                if (!SkipForSelf())
                {
                    _engine.OnKey((int)info.vkCode, down, injected);
                    var h = EventReceived;
                    if (h is not null)
                        h(new InputEvent(down ? InputKind.KeyDown : InputKind.KeyUp, (int)info.vkCode, injected));
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MsCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            // v2.1：最高频的消息在入口直接丢弃，不做任何计算（§5.1、§15.5 坑 19）
            if (msg != WM_MOUSEMOVE)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool injected = (info.flags & LLMHF_INJECTED) != 0;
                if (!SkipForSelf())
                {
                    switch (msg)
                    {
                        case WM_LBUTTONDOWN: Mouse(MouseButton.Left, true, injected); break;
                        case WM_LBUTTONUP: Mouse(MouseButton.Left, false, injected); break;
                        case WM_RBUTTONDOWN: Mouse(MouseButton.Right, true, injected); break;
                        case WM_RBUTTONUP: Mouse(MouseButton.Right, false, injected); break;
                        case WM_MBUTTONDOWN: Mouse(MouseButton.Middle, true, injected); break;
                        case WM_MBUTTONUP: Mouse(MouseButton.Middle, false, injected); break;
                        case WM_MOUSEWHEEL:
                        {
                            // mouseData 的高 16 位是有符号滚轮增量（§5.1 注意）
                            int delta = (short)(info.mouseData >> 16);
                            _engine.OnWheel(delta, horizontal: false);
                            EventReceived?.Invoke(new InputEvent(InputKind.Wheel, 0, false, delta, false));
                            break;
                        }
                        case WM_MOUSEHWHEEL:
                        {
                            int delta = (short)(info.mouseData >> 16);
                            _engine.OnWheel(delta, horizontal: true);
                            EventReceived?.Invoke(new InputEvent(InputKind.Wheel, 0, false, delta, true));
                            break;
                        }
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Mouse(MouseButton btn, bool down, bool injected)
    {
        _engine.OnMouseButton(btn, down, injected);
        EventReceived?.Invoke(new InputEvent(down ? InputKind.MouseDown : InputKind.MouseUp, (int)btn, injected));
    }

    /// <summary>
    /// §5.7：前台窗口属于本进程时跳过（挂件/主窗口上的操作不应污染统计）。
    /// 每 500ms 缓存一次，避免高频点击时反复调 API。
    /// </summary>
    private bool SkipForSelf()
    {
        if (!_cfg.Counting.ExcludeSelf) return false;

        long t = Stopwatch.GetTimestamp();
        if (t - _fgCacheTick >= Stopwatch.Frequency / 2)
        {
            _fgCacheTick = t;
            var hwnd = GetForegroundWindow();
            if (hwnd != _cachedForeground)
            {
                _cachedForeground = hwnd;
                _cachedForegroundPid = 0;
                try
                {
                    if (hwnd != IntPtr.Zero) GetWindowThreadProcessId(hwnd, out _cachedForegroundPid);
                }
                catch { _cachedForegroundPid = 0; }
            }
        }
        return _cachedForegroundPid != 0 && _cachedForegroundPid == _selfPid;
    }

    public void Dispose() => Stop();
}

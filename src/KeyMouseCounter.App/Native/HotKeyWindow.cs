using KeyMouseCounter.Diagnostics;
using static KeyMouseCounter.Native.NativeMethods;

namespace KeyMouseCounter.Native;

/// <summary>
/// 隐藏窗口，只用来收 WM_HOTKEY（§4.2、§5.8）。
/// 用 message-only 窗口，不进 Alt+Tab、不占任务栏。
/// </summary>
internal sealed class HotKeyWindow : NativeWindow, IDisposable
{
    private const int HotKeyId = 0x4B4D43;   // 'KMC'
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private bool _registered;
    private string? _current;

    public event Action? HotKeyPressed;

    public HotKeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "KeyMouseCounter.HotKeyWindow",
            Parent = HWND_MESSAGE,
            Style = 0,
            ExStyle = 0,
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0
        });
    }

    public string? Current => _current;

    /// <summary>注册热键；失败返回 false 并给出原因（被占用等）。</summary>
    public bool TryRegister(string spec, out string? error)
    {
        error = null;
        if (!HotkeyParser.TryParse(spec, out uint mods, out uint vk, out error)) return false;

        Unregister();
        if (!RegisterHotKey(Handle, HotKeyId, mods, vk))
        {
            error = "热键已被其他程序占用，请更换";
            Log.Warn($"RegisterHotKey failed for {spec}");
            return false;
        }

        _registered = true;
        _current = HotkeyParser.Normalize(spec);
        Log.Info($"hotkey registered: {_current}");
        return true;
    }

    /// <summary>冲突检测：尝试注册再立刻注销（设置页用）。</summary>
    public static bool IsAvailable(string spec, out string? error)
    {
        error = null;
        if (!HotkeyParser.TryParse(spec, out uint mods, out uint vk, out error)) return false;

        using var probe = new ProbeWindow();
        if (!RegisterHotKey(probe.Handle, 0x4B4D44, mods, vk))
        {
            error = "热键已被其他程序占用";
            return false;
        }
        UnregisterHotKey(probe.Handle, 0x4B4D44);
        return true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        try { UnregisterHotKey(Handle, HotKeyId); } catch { }
        _registered = false;
        _current = null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotKeyId)
        {
            try { HotKeyPressed?.Invoke(); } catch (Exception ex) { Log.Warn($"hotkey handler failed: {ex.Message}"); }
            return;   // 不交给 DefWndProc
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        if (Handle != IntPtr.Zero)
        {
            try { DestroyHandle(); } catch { }
        }
    }

    private sealed class ProbeWindow : NativeWindow, IDisposable
    {
        public ProbeWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "KeyMouseCounter.HotKeyProbe",
                Parent = HWND_MESSAGE,
                Style = 0,
                ExStyle = 0,
                Width = 0,
                Height = 0
            });
        }

        public void Dispose()
        {
            try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch { }
        }

        ~ProbeWindow()
        {
            try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch { }
        }
    }
}

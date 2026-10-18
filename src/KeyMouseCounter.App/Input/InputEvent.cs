namespace KeyMouseCounter.Input;

/// <summary>v2.1：没有 MouseMove（§4.3）。</summary>
public enum InputKind { KeyDown, KeyUp, MouseDown, MouseUp, Wheel }

public enum MouseButton { Left = 1, Right = 2, Middle = 3 }

/// <summary>钩子回调里产生的最小事件。cheng 上不携带任何可落盘的内容（§10）。</summary>
public readonly record struct InputEvent(InputKind Kind, int Code, bool Injected, int Delta = 0, bool Horizontal = false);

/// <summary>
/// 输入源抽象（§4.3）。实现可以是低级钩子（主路线）或 Raw Input（备选）。
/// 事件在钩子线程上同步触发。
/// </summary>
public interface IInputSource : IDisposable
{
    event Action<InputEvent>? EventReceived;

    void Start();
    void Stop();

    /// <summary>钩子是否处于已安装状态（自愈检测用，§9.2）。</summary>
    bool IsRunning { get; }

    /// <summary>最近一次安装失败的原因，成功则为 null。</summary>
    string? LastError { get; }
}

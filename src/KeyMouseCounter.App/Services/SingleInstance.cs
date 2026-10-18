using System.Threading;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Services;

/// <summary>已有实例在运行：第二个实例应**安静退出**，不要弹「启动失败」对话框。</summary>
internal sealed class AlreadyRunningException : Exception
{
    public AlreadyRunningException() : base("KeyMouseCounter 已经在运行（可在系统托盘找到它）。") { }
}

/// <summary>
/// 单实例（§15.5 坑 8：必须用 Local\ 前缀——Global\ 需要更高权限，多用户会话还会互相挡）。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Prefix = @"Local\";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activated;
    private readonly RegisteredWaitHandle _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activated)
    {
        _mutex = mutex;
        _activated = activated;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activated, (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>第二个实例请求激活主窗口时触发（在 ThreadPool 线程上）。</summary>
    public event Action? Activated;

    /// <summary>拿到锁返回实例；已有实例在运行返回 null。</summary>
    public static SingleInstance? TryAcquire(string appName)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, Prefix + appName + ".Mutex", out bool created);
            var evt = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + appName + ".Activate", out _);

            if (!created)
            {
                // 已有实例：敲一下事件让它把窗口弹出来，然后放弃
                try { evt.Set(); } catch { }
                mutex.Dispose();
                evt.Dispose();
                return null;
            }
            return new SingleInstance(mutex, evt);
        }
        catch (AbandonedMutexException)
        {
            // 上个进程崩了：这个异常表示我们仍然拿到了所有权，可以继续
            try
            {
                var mutex = new Mutex(initiallyOwned: true, Prefix + appName + ".Mutex", out _);
                var evt = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + appName + ".Activate", out _);
                return new SingleInstance(mutex, evt);
            }
            catch (Exception ex)
            {
                Log.Warn("单实例锁获取失败（将允许多开）：" + ex.Message);
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("单实例锁获取失败（将允许多开）：" + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _registration.Unregister(null); } catch { }
        try { _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
        try { _activated.Dispose(); } catch { }
    }
}

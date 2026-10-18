using System.Diagnostics;
using KeyMouseCounter.Config;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Input;

namespace KeyMouseCounter.Core;

/// <summary>
/// 计数引擎（§2.3 口径 + §5.3 分钟桶 + §5.4 跨天 + §5.5 自动重复）。
///
/// 线程模型：所有 On* 方法都在钩子线程调用；TakeSnapshot 在刷盘线程调用。
/// 锁 <see cref="_gate"/> 唯一持有者、永不跨 I/O 持有，只在自增与 1440 槽拷贝时短暂进入。
/// </summary>
public sealed class CounterEngine
{
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly bool _cacheClock;

    private AppConfig _cfg;
    private volatile bool _paused;
    private long _suppressUntilTick;

    private MinuteBuckets[] _cur = new MinuteBuckets[1440];
    private DateOnly _curDay;
    private readonly List<(DateOnly Day, MinuteBuckets[] Buf)> _pending = new();

    private DateTime _cachedNow;
    private long _cachedTick;
    private int _min;                 // minute_of_day，Add() 依赖此字段
    private long _lastInputTick;      // 单调时钟基准
    private readonly HashSet<int> _downKeys = new();
    private readonly HashSet<int> _downButtons = new();
    private int _vAcc, _hAcc;         // 滚轮不足 120 的余量，跨事件累积

    /// <summary>跨天事件（在钩子线程上触发）。</summary>
    public event Action<DateOnly>? DayRollover;

    public CounterEngine(AppConfig cfg, IClock? clock = null, bool cacheClock = true)
    {
        _cfg = cfg;
        _clock = clock ?? SystemClock.Instance;
        _cacheClock = cacheClock;
        _curDay = DateOnly.FromDateTime(_clock.Now.LocalDateTime);
    }

    /// <summary>热加载时替换配置（引用赋值，钩子线程读到的下一事件即生效）。</summary>
    public AppConfig Config
    {
        get => Volatile.Read(ref _cfg);
        set => Volatile.Write(ref _cfg, value);
    }

    public IClock Clock => _clock;

    /// <summary>最后一次被统计的输入对应的单调时钟（钩子自愈判定用，§9.2）。</summary>
    public long LastCountedTick
    {
        get { lock (_gate) return _lastInputTick; }
    }

    public bool IsPaused => _paused;

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (!paused)
        {
            // 恢复时不要把暂停期间的时间算进活跃时长
            _lastInputTick = 0;
        }
        Log.Info(paused ? "counting paused" : "counting resumed");
    }

    /// <summary>
    /// 在接下来的一段时间内丢弃事件（§5.7：挂件是非激活窗口，光靠前台进程判断排不掉自身点击）。
    /// </summary>
    public void SuppressFor(TimeSpan duration)
    {
        var until = _clock.Ticks + (long)(duration.TotalSeconds * _clock.Frequency);
        Interlocked.Exchange(ref _suppressUntilTick, until);
    }

    private bool IsSuppressed() => _clock.Ticks < Volatile.Read(ref _suppressUntilTick);

    public DateOnly CurrentDay => _curDay;

    // ────────────────────────── 事件入口（钩子线程） ──────────────────────────

    public void OnKey(int vk, bool down, bool injected)
    {
        if (_paused || IsSuppressed()) return;
        var cfg = _cfg;
        if (injected && cfg.Counting.IgnoreInjected && !cfg.Debug.CountInjected) return;

        RefreshClock();

        if (down)
        {
            // WH_KEYBOARD_LL 不提供 lParam bit30，必须自维护按下集合（§5.5）
            bool repeat = !_downKeys.Add(vk);
            if (!repeat || cfg.Counting.CountAutoRepeat)
            {
                if (cfg.Counting.CountModifierKeys || !IsModifier(vk))
                    Add(static (ref MinuteBuckets b) => b.K++);
            }
        }
        else
        {
            _downKeys.Remove(vk);
        }

        TouchActivity();   // 重复键也刷新活跃度
    }

    public void OnMouseButton(MouseButton btn, bool down, bool injected)
    {
        if (_paused || IsSuppressed()) return;
        var cfg = _cfg;
        if (injected && cfg.Counting.IgnoreInjected && !cfg.Debug.CountInjected) return;

        RefreshClock();

        int b = (int)btn;
        if (down)
        {
            // 双击 = 两次 down；拖拽只有一次 down（§2.3）
            if (_downButtons.Add(b))
            {
                switch (b)
                {
                    case 1: Add(static (ref MinuteBuckets x) => x.ML++); break;
                    case 2: Add(static (ref MinuteBuckets x) => x.MR++); break;
                    default: Add(static (ref MinuteBuckets x) => x.MM++); break;
                }
            }
        }
        else
        {
            _downButtons.Remove(b);
        }

        TouchActivity();
    }

    public void OnWheel(int delta, bool horizontal)
    {
        if (_paused || IsSuppressed()) return;
        var cfg = _cfg;
        if (!cfg.Counting.CountWheel) return;

        RefreshClock();

        Add((ref MinuteBuckets b) =>
        {
            // 每 120 delta 计 1 格；不足 120 的余量跨事件累积，不丢弃（§2.3）
            int acc = horizontal ? _hAcc : _vAcc;
            acc += delta;
            while (acc >= 120) { if (horizontal) b.WR++; else b.WU++; acc -= 120; }
            while (acc <= -120) { if (horizontal) b.WL++; else b.WD++; acc += 120; }
            if (horizontal) _hAcc = acc; else _vAcc = acc;
        });

        TouchActivity();
    }

    /// <summary>v2.1：没有 OnMouseMove。WM_MOUSEMOVE 在钩子回调入口直接返回。</summary>
    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;

    // ────────────────────────── 时间与分桶 ──────────────────────────

    /// <summary>200ms 缓存刷新分钟归属；测试里把 cacheClock 关掉即可逐事件取真实时间（§5.2）。</summary>
    private void RefreshClock()
    {
        long t = _clock.Ticks;
        if (_cacheClock && t - _cachedTick < _clock.Frequency / 5) return;
        _cachedTick = t;

        _cachedNow = _clock.Now.LocalDateTime;
        _min = _cachedNow.Hour * 60 + _cachedNow.Minute;

        var d = DateOnly.FromDateTime(_cachedNow);
        if (_curDay != default && d != _curDay) Rollover(d);
        _curDay = d;
    }

    private void Rollover(DateOnly newDay)
    {
        DateOnly oldDay;
        lock (_gate)
        {
            oldDay = _curDay;
            if (_curDay != default) _pending.Add((_curDay, _cur));   // 旧的一天整体入队
            _cur = new MinuteBuckets[1440];
            _curDay = newDay;
        }

        _downKeys.Clear();       // 清自动重复状态，避免脏状态
        _downButtons.Clear();
        _lastInputTick = 0;
        _vAcc = 0;
        _hAcc = 0;

        Log.Info($"day rollover -> {newDay:yyyy-MM-dd}");
        try { DayRollover?.Invoke(oldDay); } catch (Exception ex) { Log.Warn($"DayRollover handler failed: {ex.Message}"); }
    }

    /// <summary>锁内只有一次自增，微秒级。调用前必须已经 RefreshClock()。</summary>
    private void Add(Mutation mutate)
    {
        lock (_gate) { mutate(ref _cur[_min]); }
    }

    private delegate void Mutation(ref MinuteBuckets b);

    /// <summary>活跃时长：用单调时钟算间隔，归属到"间隔结束"所在的分钟（§2.3）。</summary>
    private void TouchActivity()
    {
        long now = _clock.Ticks;
        if (_lastInputTick != 0)
        {
            double gapSec = (now - _lastInputTick) / (double)_clock.Frequency;
            if (gapSec > 0 && gapSec <= _cfg.Counting.IdleThresholdSec)
                Add((ref MinuteBuckets b) => b.AS += (long)Math.Round(gapSec));
        }
        _lastInputTick = now;
    }

    // ────────────────────────── 快照 ──────────────────────────

    /// <summary>
    /// 取快照：所有滞留日 + 当天，已取走的槽清零（§5.3）。
    /// 典型一次只有 1–2 个非零槽位。
    /// </summary>
    public List<(DateOnly Day, int Minute, MinuteBuckets Data)> TakeSnapshot()
    {
        var list = new List<(DateOnly, int, MinuteBuckets)>(64);
        lock (_gate)
        {
            foreach (var (day, buf) in _pending) Drain(day, buf, list);
            _pending.Clear();
            if (_curDay != default) Drain(_curDay, _cur, list);
        }
        return list;
    }

    private static void Drain(DateOnly day, MinuteBuckets[] buf, List<(DateOnly, int, MinuteBuckets)> sink)
    {
        for (int i = 0; i < 1440; i++)
        {
            if (buf[i].IsZero) continue;
            sink.Add((day, i, buf[i]));
            buf[i] = default;          // 原地清零，数组复用，无 GC 压力
        }
    }

    /// <summary>未刷盘增量（含跨天滞留），用于"今日累计 = 库内 + 内存"的补差（§5.10）。</summary>
    public MinuteBuckets PendingFor(DateOnly day)
    {
        var total = default(MinuteBuckets);
        lock (_gate)
        {
            if (day == _curDay)
                for (int i = 0; i < 1440; i++) total.AddFrom(_cur[i]);
            foreach (var (d, buf) in _pending)
            {
                if (d != day) continue;
                for (int i = 0; i < 1440; i++) total.AddFrom(buf[i]);
            }
        }
        return total;
    }

    /// <summary>未刷盘的分钟明细（不取走），供今日视图"库内 + 内存"合并显示。</summary>
    public List<(int Minute, MinuteBuckets Data)> PendingMinutes(DateOnly day)
    {
        var list = new List<(int, MinuteBuckets)>(32);
        lock (_gate)
        {
            if (day == _curDay) Collect(_cur, list);
            foreach (var (d, buf) in _pending)
            {
                if (d == day) Collect(buf, list);
            }
        }
        return list;
    }

    private static void Collect(MinuteBuckets[] buf, List<(int, MinuteBuckets)> sink)
    {
        for (int i = 0; i < 1440; i++)
        {
            if (buf[i].IsZero) continue;
            sink.Add((i, buf[i]));
        }
    }

    /// <summary>清空某天的内存桶（§6.7 清空今日 / 重建前的准备）。</summary>
    public void ClearDay(DateOnly day)
    {
        lock (_gate)
        {
            if (day == _curDay) _cur = new MinuteBuckets[1440];
            _pending.RemoveAll(p => p.Day == day);
        }
        _lastInputTick = 0;
        _downKeys.Clear();
        _downButtons.Clear();
        _vAcc = 0;
        _hAcc = 0;
    }

    /// <summary>测试与自愈用：重置全部内存状态。</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            _pending.Clear();
            _cur = new MinuteBuckets[1440];
            _curDay = DateOnly.FromDateTime(_clock.Now.LocalDateTime);
        }
        _lastInputTick = 0;
        _cachedTick = 0;
        _downKeys.Clear();
        _downButtons.Clear();
        _vAcc = 0;
        _hAcc = 0;
    }

    /// <summary>诊断用：今日内存中已计入的按键/点击总数。</summary>
    public (long Key, long Click, long Wheel, long ActiveSeconds) TodayPendingTotals()
    {
        var b = PendingFor(_curDay);
        return (b.K, b.Clicks, b.Wheels, b.AS);
    }
}

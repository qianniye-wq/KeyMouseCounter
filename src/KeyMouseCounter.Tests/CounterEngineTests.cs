using System.Reflection;
using System.Runtime.InteropServices;
using KeyMouseCounter.Config;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Input;
using KeyMouseCounter.Native;
using KeyMouseCounter.Services;
using KeyMouseCounter.Storage;

namespace KeyMouseCounter.Tests;

// ─────────────────────────────── 测试夹具 ───────────────────────────────

/// <summary>可控时钟：1 tick = 1ms，测试不需要真的等（§5.2 要求活跃时长走单调时钟）。</summary>
internal sealed class FakeClock : IClock
{
    private DateTimeOffset _now;
    private long _ticks = 1000;   // 不能从 0 开始：引擎用 0 当"还没记过输入"的哨兵

    public FakeClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset Now => _now;
    public long Ticks => _ticks;
    public long Frequency => 1000;

    public void Advance(TimeSpan d)
    {
        _now = _now.Add(d);
        _ticks += (long)Math.Round(d.TotalMilliseconds);
    }

    public void AdvanceMs(double ms) => Advance(TimeSpan.FromMilliseconds(ms));
}

/// <summary>把 %LOCALAPPDATA% 换成临时目录，测试之间互不干扰。</summary>
internal sealed class TempRoot : IDisposable
{
    public string Path { get; }

    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kmc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        AppPaths.RootOverride = Path;
        AppPaths.EnsureCreated();
        Log.Configure(LogLevel.Off, System.IO.Path.Combine(Path, "logs"));
    }

    public string DataDir => AppPaths.DataDir;

    public void Dispose()
    {
        AppPaths.RootOverride = null;
        try { Directory.Delete(Path, recursive: true); } catch { /* 文件还被占用就算了 */ }
    }
}

/// <summary>引擎测试的公共底座：默认配置 + FakeClock + cacheClock=false（逐事件取时间）。</summary>
internal static class EngineTestKit
{
    public static AppConfig Config() => new();

    public static CounterEngine Engine(AppConfig cfg, FakeClock clock)
        => new(cfg, clock, cacheClock: false);

    /// <summary>当前日期（本地时区，与引擎的归属口径一致）。</summary>
    public static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.LocalDateTime);
}

/// <summary>§11.1 T01–T15：计数口径、分钟归属、跨天、活跃时长。</summary>
public sealed class CounterEngineTests
{
    private static readonly DateTimeOffset Morning = new(2025, 1, 1, 8, 35, 0, TimeSpan.FromHours(8));

    // T01 三个不同键各按一次 → key_press = 3
    [Fact]
    public void T01_ThreeDifferentKeys_CountsThree()
    {
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnKey(0x41, down: true, injected: false);
        engine.OnKey(0x42, down: true, injected: false);
        engine.OnKey(0x43, down: true, injected: false);

        var total = engine.PendingFor(EngineTestKit.Day(Morning));
        Assert.Equal(3, total.K);
    }

    // T02 A↓A↓A↓A↑（长按自动重复默认不计）→ key_press = 1
    [Fact]
    public void T02_AutoRepeat_NotCountedByDefault()
    {
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnKey(0x41, down: true, injected: false);
        engine.OnKey(0x41, down: true, injected: false);
        engine.OnKey(0x41, down: true, injected: false);
        engine.OnKey(0x41, down: false, injected: false);

        var total = engine.PendingFor(EngineTestKit.Day(Morning));
        Assert.Equal(1, total.K);
    }

    // T03 同上 + countAutoRepeat = true → key_press = 3
    [Fact]
    public void T03_AutoRepeat_CountedWhenEnabled()
    {
        var cfg = EngineTestKit.Config();
        cfg.Counting.CountAutoRepeat = true;
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnKey(0x41, down: true, injected: false);
        engine.OnKey(0x41, down: true, injected: false);
        engine.OnKey(0x41, down: true, injected: false);
        engine.OnKey(0x41, down: false, injected: false);

        Assert.Equal(3, engine.PendingFor(EngineTestKit.Day(Morning)).K);
    }

    // T04 注入键 ×5（默认 ignoreInjected=true）→ 0
    [Fact]
    public void T04_InjectedKeys_IgnoredByDefault()
    {
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        for (int vk = 0x41; vk < 0x46; vk++)
            engine.OnKey(vk, down: true, injected: true);

        Assert.Equal(0, engine.PendingFor(EngineTestKit.Day(Morning)).K);
    }

    // T05 注入键 ×5 + debug.countInjected = true → 5
    [Fact]
    public void T05_InjectedKeys_CountedInDebugMode()
    {
        var cfg = EngineTestKit.Config();
        cfg.Debug.CountInjected = true;
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        for (int vk = 0x41; vk < 0x46; vk++)
            engine.OnKey(vk, down: true, injected: true);

        Assert.Equal(5, engine.PendingFor(EngineTestKit.Day(Morning)).K);
    }

    // T06 Shift↓Ctrl↓A↓ + countModifierKeys = false → 只算 A = 1
    [Fact]
    public void T06_ModifierKeys_ExcludedWhenDisabled()
    {
        var cfg = EngineTestKit.Config();
        cfg.Counting.CountModifierKeys = false;
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnKey(0x10, down: true, injected: false);   // Shift
        engine.OnKey(0x11, down: true, injected: false);   // Ctrl
        engine.OnKey(0x41, down: true, injected: false);   // A

        Assert.Equal(1, engine.PendingFor(EngineTestKit.Day(Morning)).K);
    }

    // T07 鼠标左键 down ×2（中间各一次 up）→ mouse_left = 2（双击 = 2 次按下）
    [Fact]
    public void T07_DoubleClick_CountsTwoLeftDowns()
    {
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnMouseButton(MouseButton.Left, down: true, injected: false);
        engine.OnMouseButton(MouseButton.Left, down: false, injected: false);
        engine.OnMouseButton(MouseButton.Left, down: true, injected: false);
        engine.OnMouseButton(MouseButton.Left, down: false, injected: false);

        var total = engine.PendingFor(EngineTestKit.Day(Morning));
        Assert.Equal(2, total.ML);
        Assert.Equal(2, total.Clicks);
    }

    // T08 滚轮 +60 +60 +120 → 余量累积 → wheel_up = 2
    [Fact]
    public void T08_WheelRemainderAccumulates()
    {
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnWheel(60, horizontal: false);
        engine.OnWheel(60, horizontal: false);   // 60 + 60 = 120 → 1 格
        engine.OnWheel(120, horizontal: false);  // → 又 1 格

        Assert.Equal(2, engine.PendingFor(EngineTestKit.Day(Morning)).WU);
    }

    // T09 滚轮 −120 → wheel_down = 1
    [Fact]
    public void T09_WheelDown()
    {
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnWheel(-120, horizontal: false);

        var total = engine.PendingFor(EngineTestKit.Day(Morning));
        Assert.Equal(1, total.WD);
        Assert.Equal(0, total.WU);
    }

    // T10 WM_MOUSEMOVE ×1000 → 所有桶保持 0（v2.1 完全不统计光标移动）
    [Fact]
    public void T10_MouseMove_NeverCounted()
    {
        var cfg = EngineTestKit.Config();
        cfg.Counting.ExcludeSelf = false;   // 测试进程不是前台，关掉自身排除更稳定
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        using var src = new LowLevelHookSource(engine, cfg);
        var method = typeof(LowLevelHookSource)
            .GetMethod("MsCallback", BindingFlags.NonPublic | BindingFlags.Instance)!;

        int size = Marshal.SizeOf<MSLLHOOKSTRUCT>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new MSLLHOOKSTRUCT(), ptr, false);
            var args = new object[] { 0, (IntPtr)NativeMethods.WM_MOUSEMOVE, ptr };
            for (int i = 0; i < 1000; i++) method.Invoke(src, args);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        Assert.True(engine.PendingFor(EngineTestKit.Day(Morning)).IsZero);
    }

    // T11 08:35:00 与 08:35:59 各一次 → 同一个桶 min=515 值=2
    [Fact]
    public void T11_SameMinute_SameBucket()
    {
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnKey(0x41, down: true, injected: false);
        clock.Advance(TimeSpan.FromSeconds(59));
        engine.OnKey(0x42, down: true, injected: false);

        var minutes = engine.PendingMinutes(EngineTestKit.Day(Morning));
        Assert.Single(minutes);
        Assert.Equal(515, minutes[0].Minute);
        Assert.Equal(2, minutes[0].Data.K);
    }

    // T12 08:35:59.999 与 08:36:00.001 → 515 / 516 两个桶
    [Fact]
    public void T12_MinuteBoundary_SplitsBuckets()
    {
        var cfg = EngineTestKit.Config();
        var start = new DateTimeOffset(2025, 1, 1, 8, 35, 59, 999, TimeSpan.FromHours(8));
        var clock = new FakeClock(start);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnKey(0x41, down: true, injected: false);
        clock.AdvanceMs(2);
        engine.OnKey(0x42, down: true, injected: false);

        var minutes = engine.PendingMinutes(EngineTestKit.Day(start));
        Assert.Equal(2, minutes.Count);
        Assert.Equal(515, minutes[0].Minute);
        Assert.Equal(1, minutes[0].Data.K);
        Assert.Equal(516, minutes[1].Minute);
        Assert.Equal(1, minutes[1].Data.K);
    }

    // T13 刷盘横跨分钟边界 → 两桶各写各的（日志与库都各成一行）
    [Fact]
    public void T13_FlushAcrossMinutes_WritesBothBuckets()
    {
        using var root = new TempRoot();
        var cfg = EngineTestKit.Config();
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);
        var day = EngineTestKit.Day(Morning);

        using var store = new SqliteStore(AppPaths.DbPath);
        store.Open();
        using var writer = new DailyLogWriter(AppPaths.DataDir, store.UserSid);
        using var flush = new FlushService(engine, cfg, store, writer, clock) { DataDir = AppPaths.DataDir };

        engine.OnKey(0x41, down: true, injected: false);
        clock.Advance(TimeSpan.FromSeconds(90));   // 08:36:30
        engine.OnKey(0x42, down: true, injected: false);

        flush.FlushNow();

        var rows = store.GetMinutes(day);
        Assert.Equal(2, rows.Count);
        Assert.Equal(515, rows[0].Minute);
        Assert.Equal(1, rows[0].Key);
        Assert.Equal(516, rows[1].Minute);
        Assert.Equal(1, rows[1].Key);

        // 日志同样是两行增量，且与库一致（§6.4/§6.5）
        var parsed = DailyLogReader.ParseFile(AppPaths.DailyLogPath(day), day);
        Assert.Equal(2, parsed.LinesImported);
        Assert.Equal(1, parsed.Minutes[515].K);
        Assert.Equal(1, parsed.Minutes[516].K);
    }
    // T14 跨天：23:59:59 与 00:00:01 → 两天各入各的桶，快照能全部取走
    [Fact]
    public void T14_DayRollover_QueuesBothDays()
    {
        var cfg = EngineTestKit.Config();
        var start = new DateTimeOffset(2025, 1, 1, 23, 59, 59, TimeSpan.FromHours(8));
        var clock = new FakeClock(start);
        var engine = EngineTestKit.Engine(cfg, clock);

        DateOnly? rolled = null;
        engine.DayRollover += d => rolled = d;

        engine.OnKey(0x41, down: true, injected: false);
        clock.Advance(TimeSpan.FromSeconds(2));    // 2025-01-02 00:00:01
        engine.OnKey(0x42, down: true, injected: false);

        var day1 = new DateOnly(2025, 1, 1);
        var day2 = new DateOnly(2025, 1, 2);

        Assert.Equal(day1, rolled);
        Assert.Equal(1, engine.PendingFor(day1).K);
        Assert.Equal(1, engine.PendingFor(day2).K);

        var snapshot = engine.TakeSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, s => s.Day == day1 && s.Minute == 1439);
        Assert.Contains(snapshot, s => s.Day == day2 && s.Minute == 0);

        // 取走之后两天都清空
        Assert.True(engine.PendingFor(day1).IsZero);
        Assert.True(engine.PendingFor(day2).IsZero);
    }

    // T15 活跃时长：30s / 30s / 120s 三个间隔 → 只累加 ≤60 的两个 = 60
    [Fact]
    public void T15_ActiveSeconds_SkipsLongIdleGaps()
    {
        var cfg = EngineTestKit.Config();   // idleThresholdSec = 60
        var clock = new FakeClock(Morning);
        var engine = EngineTestKit.Engine(cfg, clock);

        engine.OnKey(0x41, down: true, injected: false);   // 起点，不算间隔
        clock.Advance(TimeSpan.FromSeconds(30));
        engine.OnKey(0x42, down: true, injected: false);   // +30
        clock.Advance(TimeSpan.FromSeconds(30));
        engine.OnKey(0x43, down: true, injected: false);   // +30
        clock.Advance(TimeSpan.FromSeconds(120));
        engine.OnKey(0x44, down: true, injected: false);   // 120 > 60，不计

        Assert.Equal(60, engine.PendingFor(EngineTestKit.Day(Morning)).AS);
    }
}

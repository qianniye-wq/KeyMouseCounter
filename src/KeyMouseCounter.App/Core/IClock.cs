using System.Diagnostics;

namespace KeyMouseCounter.Core;

/// <summary>
/// 时间抽象（§4.3）。<see cref="Ticks"/>/<see cref="Frequency"/> 是单调时钟，
/// 活跃时长必须用它计算——用 DateTime 相减会在系统改时间时炸掉（§15.5 坑 11）。
/// </summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    long Ticks { get; }
    long Frequency { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset Now => DateTimeOffset.Now;
    public long Ticks => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}

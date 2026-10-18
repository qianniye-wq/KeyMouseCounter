namespace KeyMouseCounter.Core;

/// <summary>
/// 一天 1440 个分钟槽位里的一个。字段名与 §6.2 的列一一对应。
/// 只用 long 累加，不做任何 I/O。
/// </summary>
public struct MinuteBuckets
{
    public long K;   // key_press
    public long ML;  // mouse_left
    public long MR;  // mouse_right
    public long MM;  // mouse_middle
    public long WU;  // wheel_up
    public long WD;  // wheel_down
    public long WL;  // wheel_left
    public long WR;  // wheel_right
    public long AS;  // active_seconds

    public readonly bool IsZero => (K | ML | MR | MM | WU | WD | WL | WR | AS) == 0;

    public readonly long Clicks => ML + MR + MM;
    public readonly long Wheels => WU + WD + WL + WR;

    public void AddFrom(in MinuteBuckets o)
    {
        K += o.K; ML += o.ML; MR += o.MR; MM += o.MM;
        WU += o.WU; WD += o.WD; WL += o.WL; WR += o.WR; AS += o.AS;
    }

    public static MinuteBuckets From(long k, long ml, long mr, long mm,
                                     long wu, long wd, long wl, long wr, long activeSeconds)
        => new() { K = k, ML = ml, MR = mr, MM = mm, WU = wu, WD = wd, WL = wl, WR = wr, AS = activeSeconds };
}

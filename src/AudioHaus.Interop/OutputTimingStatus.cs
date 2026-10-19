using System;

namespace AudioHaus.Interop;

/*
 * Name: OutputTimingStatus
 * Input: Native endpoint timing snapshot.
 * Process: Converts configured delay, device latency, and retry timing to managed values.
 * Output: Immutable renderer buffer/clock/recovery telemetry, not acoustic calibration.
 */
public sealed class OutputTimingStatus
{
    public uint HResult { get; }
    public uint DelayFrames { get; }
    public TimeSpan Delay { get; }
    public uint BufferCapacityFrames { get; }
    public uint PaddingFrames { get; }
    public bool DriftCorrectionEnabled { get; }
    public TimeSpan RetryAfter { get; }
    public uint LastErrorHResult { get; }
    public ulong UnderrunCount { get; }
    public ulong RecoveryAttempts { get; }
    public ulong RecoveryCount { get; }
    public ulong RebufferCount { get; }
    public double ClockRatePpm { get; }
    public TimeSpan MaxServiceGap { get; }
    public TimeSpan StreamLatency { get; }
    public ulong FirstSignalQpc { get; }
    /*
     * Name: OutputTimingStatus
     * Input: Native copied timing data.
     * Process: Converts normalized frame counts and Windows time units.
     * Output: Snapshot; invalid nonfinite native measurements throw.
     */
    internal OutputTimingStatus(OutputTimingInfo info)
    {
        if (!double.IsFinite(info.ClockRatePpm) || !double.IsFinite(info.MaxServiceGapMs))
        { throw new InvalidOperationException("Nonfinite output timing snapshot."); }
        HResult = info.HResult; DelayFrames = info.DelayFrames;
        Delay = TimeSpan.FromSeconds(info.DelayFrames / 48000.0);
        BufferCapacityFrames = info.CapacityFrames; PaddingFrames = info.PaddingFrames;
        DriftCorrectionEnabled = info.DriftEnabled != 0; RetryAfter = TimeSpan.FromMilliseconds(info.RetryInMs);
        LastErrorHResult = info.LastErrorHResult; UnderrunCount = info.UnderrunCount;
        RecoveryAttempts = info.RecoveryAttempts; RecoveryCount = info.RecoveryCount; RebufferCount = info.RebufferCount;
        ClockRatePpm = info.ClockRatePpm; MaxServiceGap = TimeSpan.FromMilliseconds(info.MaxServiceGapMs);
        StreamLatency = TimeSpan.FromTicks(checked((long)info.StreamLatency100Ns)); FirstSignalQpc = info.FirstSignalQpc;
    }
}

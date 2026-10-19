using System;

namespace AudioHaus.Interop;

/*
 * Name: RouteTimingStatus
 * Input: Native fractional route reader and buffer counters.
 * Process: Copies correction, phase, priming, and recovery information.
 * Output: Immutable timing snapshot independent of router lifetime.
 */
public sealed class RouteTimingStatus
{
    public uint HResult { get; }
    public bool Priming { get; }
    public uint BufferedFrames { get; }
    public uint TargetFrames { get; }
    public double CorrectionPpm { get; }
    public double PhaseErrorFrames { get; }
    public double ReadPositionFrame { get; }
    public ulong UnderrunCount { get; }
    public ulong UnderrunFrames { get; }
    public ulong OverrunCount { get; }
    public ulong RebufferCount { get; }
    /*
     * Name: RouteTimingStatus
     * Input: Native copied timing data.
     * Process: Validates finite controller output and copies lifetime counters.
     * Output: Snapshot; malformed native timing data throws.
     */
    internal RouteTimingStatus(RouteTimingInfo info)
    {
        if (!double.IsFinite(info.CorrectionPpm) || !double.IsFinite(info.PhaseErrorFrames) || !double.IsFinite(info.ReadPositionFrame))
        { throw new InvalidOperationException("Nonfinite route timing snapshot."); }
        HResult = info.HResult; Priming = info.Priming != 0; BufferedFrames = info.BufferedFrames; TargetFrames = info.TargetFrames;
        CorrectionPpm = info.CorrectionPpm; PhaseErrorFrames = info.PhaseErrorFrames; ReadPositionFrame = info.ReadPositionFrame;
        UnderrunCount = info.UnderrunCount; UnderrunFrames = info.UnderrunFrames; OverrunCount = info.OverrunCount; RebufferCount = info.RebufferCount;
    }
}

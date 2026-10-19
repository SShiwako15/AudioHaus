using System;

namespace AudioHaus.Interop;

/*
 * Name: ProcessCaptureStatus
 * Input: Native live process source snapshot.
 * Process: Copies packet, isolation-health, and bounded-buffer telemetry.
 * Output: Immutable values independent of router lifetime; timestamps are not acoustic latency.
 */
public sealed class ProcessCaptureStatus
{
    public uint ProcessId { get; }
    public FilePlaybackState State { get; }
    public uint HResult { get; }
    public ulong FramesCaptured { get; }
    public ulong NonSilentFrames { get; }
    public ulong Discontinuities { get; }
    public ulong FramesDropped { get; }
    public ulong BufferedFrames { get; }
    public ulong LastPacketQpc100Ns { get; }
    public double SumSquares { get; }
    public double LastPeak { get; }
    /*
     * Name: ProcessCaptureStatus
     * Input: Native copied capture information.
     * Process: Validates state and retains metrics without native references.
     * Output: Immutable status; unknown state throws InvalidOperationException.
     */
    internal ProcessCaptureStatus(ProcessCaptureInfo info)
    {
        ProcessId = info.ProcessId;
        State = (FilePlaybackState)info.State;
        if (!Enum.IsDefined(State)) { throw new InvalidOperationException("Unknown capture state."); }
        HResult = info.HResult;
        FramesCaptured = info.FramesCaptured;
        NonSilentFrames = info.NonSilentFrames;
        Discontinuities = info.Discontinuities;
        FramesDropped = info.FramesDropped;
        BufferedFrames = info.BufferedFrames;
        LastPacketQpc100Ns = info.LastPacketQpc100Ns;
        SumSquares = info.SumSquares;
        LastPeak = info.LastPeak;
    }
}

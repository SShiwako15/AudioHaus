using System;

namespace AudioHaus.Interop;

/*
 * Name: CaptureTimingStatus
 * Input: Process source retry and history snapshot.
 * Process: Copies background recovery counters and desired monitor state.
 * Output: Immutable values independent of router lifetime.
 */
public sealed class CaptureTimingStatus
{
    public uint HResult { get; }
    public TimeSpan RetryAfter { get; }
    public FilePlaybackState ResumeState { get; }
    public uint LastErrorHResult { get; }
    public ulong RecoveryAttempts { get; }
    public ulong RecoveryCount { get; }
    public ulong BufferCapacityFrames { get; }
    /*
     * Name: CaptureTimingStatus
     * Input: Native timing data.
     * Process: Converts retry milliseconds and checks the desired state value.
     * Output: Snapshot; unknown native state throws.
     */
    internal CaptureTimingStatus(CaptureTimingInfo info)
    {
        HResult = info.HResult; RetryAfter = TimeSpan.FromMilliseconds(info.RetryInMs);
        ResumeState = (FilePlaybackState)info.ResumeState;
        if (!Enum.IsDefined(ResumeState)) { throw new InvalidOperationException("Unknown capture resume state."); }
        LastErrorHResult = info.LastErrorHResult; RecoveryAttempts = info.RecoveryAttempts;
        RecoveryCount = info.RecoveryCount; BufferCapacityFrames = info.CapacityFrames;
    }
}

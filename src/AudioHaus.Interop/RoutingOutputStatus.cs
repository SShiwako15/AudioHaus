namespace AudioHaus.Interop;

/*
 * Name: RoutingOutputStatus
 * Input: One endpoint mixer's native snapshot.
 * Process: Copies stream counters and cumulative PCM energy before device effects.
 * Output: Immutable output evidence; state 1 is running and 4 is failed.
 */
public sealed class RoutingOutputStatus
{
    public uint State { get; }
    public uint HResult { get; }
    public uint SampleRate { get; }
    public uint ChannelCount { get; }
    public ulong FramesSubmitted { get; }
    public ulong NonSilentFrames { get; }
    public ulong ClippedSamples { get; }
    public double SumSquares { get; }
    public double LastPeak { get; }
    public ulong StartQpc { get; }
    public ulong StopQpc { get; }
    public uint RouteCount { get; }

    /*
     * Name: RoutingOutputStatus
     * Input: Native output information.
     * Process: Copies PCM and stream measurements without retaining native ownership.
     * Output: A status snapshot; counters do not measure acoustic latency or volume.
     */
    internal RoutingOutputStatus(RoutingOutputInfo info)
    {
        State = info.State;
        HResult = info.HResult;
        SampleRate = info.SampleRate;
        ChannelCount = info.ChannelCount;
        FramesSubmitted = info.FramesSubmitted;
        NonSilentFrames = info.NonSilentFrames;
        ClippedSamples = info.ClippedSamples;
        SumSquares = info.SumSquares;
        LastPeak = info.LastPeak;
        StartQpc = info.StartQpc;
        StopQpc = info.StopQpc;
        RouteCount = info.RouteCount;
    }
}

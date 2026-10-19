using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: RoutingSourceInfo
 * Input: Native shared-source snapshot.
 * Process: Matches the 56-byte C ABI layout.
 * Output: Media timing and bounded-cache counters.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct RoutingSourceInfo
{
    public uint State, HResult, SampleRate, ChannelCount;
    public ulong Position100Ns, Duration100Ns, FramesDecoded, CachedFrames;
    public uint RouteCount, Reserved;
}

/*
 * Name: RoutingOutputInfo
 * Input: Native endpoint-mixer snapshot.
 * Process: Matches the 80-byte C ABI layout.
 * Output: Render counters, PCM measurements, and stream boundaries.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct RoutingOutputInfo
{
    public uint State, HResult, SampleRate, ChannelCount;
    public ulong FramesSubmitted, NonSilentFrames, ClippedSamples;
    public double SumSquares, LastPeak;
    public ulong StartQpc, StopQpc;
    public uint RouteCount, Reserved;
}

/*
 * Name: RoutingRouteInfo
 * Input: Native source/output connection snapshot.
 * Process: Matches the 48-byte C ABI layout.
 * Output: Independent route settings and reader activity.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct RoutingRouteInfo
{
    public uint SourceId, OutputId, Muted, Reserved;
    public double Gain;
    public ulong FramesRead, CursorFrame, Rebases;
}

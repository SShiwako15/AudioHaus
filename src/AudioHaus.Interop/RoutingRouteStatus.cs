namespace AudioHaus.Interop;

/*
 * Name: RoutingRouteStatus
 * Input: One source-to-output connection snapshot.
 * Process: Copies its independent controls and shared-cache reader activity.
 * Output: Immutable route information.
 */
public sealed class RoutingRouteStatus
{
    public uint SourceId { get; }
    public uint OutputId { get; }
    public double Gain { get; }
    public bool Muted { get; }
    public ulong FramesRead { get; }
    public ulong CursorFrame { get; }
    public ulong Rebases { get; }

    /*
     * Name: RoutingRouteStatus
     * Input: Native route information.
     * Process: Converts the native mute flag and copies reader counters.
     * Output: A managed snapshot without native resource ownership.
     */
    internal RoutingRouteStatus(RoutingRouteInfo info)
    {
        SourceId = info.SourceId;
        OutputId = info.OutputId;
        Gain = info.Gain;
        Muted = info.Muted != 0;
        FramesRead = info.FramesRead;
        CursorFrame = info.CursorFrame;
        Rebases = info.Rebases;
    }
}

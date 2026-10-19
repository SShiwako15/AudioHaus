using System;

namespace AudioHaus.Interop;

/*
 * Name: RoutingSourceStatus
 * Input: A shared decoder/timeline snapshot.
 * Process: Converts media ticks and copies normalized-format and cache counters.
 * Output: Immutable source information independent of router lifetime.
 */
public sealed class RoutingSourceStatus
{
    public FilePlaybackState State { get; }
    public uint HResult { get; }
    public uint SampleRate { get; }
    public uint ChannelCount { get; }
    public TimeSpan Position { get; }
    public TimeSpan Duration { get; }
    public ulong FramesDecoded { get; }
    public ulong CachedFrames { get; }
    public uint RouteCount { get; }

    /*
     * Name: RoutingSourceStatus
     * Input: Native source information.
     * Process: Validates state and converts 100 ns ticks to managed time.
     * Output: A snapshot; invalid native state or overflowing time throws.
     */
    internal RoutingSourceStatus(RoutingSourceInfo info)
    {
        State = (FilePlaybackState)info.State;
        if (!Enum.IsDefined(State)) { throw new InvalidOperationException("Unknown routing source state."); }
        HResult = info.HResult;
        SampleRate = info.SampleRate;
        ChannelCount = info.ChannelCount;
        Position = TimeSpan.FromTicks(checked((long)info.Position100Ns));
        Duration = TimeSpan.FromTicks(checked((long)info.Duration100Ns));
        FramesDecoded = info.FramesDecoded;
        CachedFrames = info.CachedFrames;
        RouteCount = info.RouteCount;
    }
}

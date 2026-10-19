using System;

namespace AudioHaus.Interop;

/*
 * Name: FilePlaybackStatus
 * Input: A native file-player snapshot.
 * Process: Converts 100 ns media positions to TimeSpan and retains render evidence.
 * Output: Immutable managed status independent of the player's lifetime.
 */
public sealed class FilePlaybackStatus
{
    public FilePlaybackState State { get; }
    public uint HResult { get; }
    public uint SampleRate { get; }
    public uint ChannelCount { get; }
    public TimeSpan Position { get; }
    public TimeSpan Duration { get; }
    public ulong FramesSubmitted { get; }
    public ulong StartQpc { get; }
    public ulong StopQpc { get; }

    /*
     * Name: FilePlaybackStatus
     * Input: Native state and counters.
     * Process: Copies fields and converts bounded media time units.
     * Output: An immutable status; invalid native times or states throw.
     */
    internal FilePlaybackStatus(AudioHausFilePlayerInfo info)
    {
        State = (FilePlaybackState)info.State;
        if (!Enum.IsDefined(State))
        {
            throw new InvalidOperationException($"Unknown native file-player state: {info.State}.");
        }
        HResult = info.HResult;
        SampleRate = info.SampleRate;
        ChannelCount = info.ChannelCount;
        Position = TimeSpan.FromTicks(checked((long)info.Position100Ns));
        Duration = TimeSpan.FromTicks(checked((long)info.Duration100Ns));
        FramesSubmitted = info.FramesSubmitted;
        StartQpc = info.StartQpc;
        StopQpc = info.StopQpc;
    }
}

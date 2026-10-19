using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: AudioHausToneOutputInfo
 * Input: Per-output data from native tone playback.
 * Process: Matches the native report's unsigned fields and default Windows packing.
 * Output: A 40-byte interop value with frame counts, QPC bounds, and completion status.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct AudioHausToneOutputInfo
{
    public uint SampleRate;
    public uint ChannelCount;
    public ulong FramesSubmitted;
    public ulong StartQpc;
    public ulong StopQpc;
    public uint HResult;
    public uint Completed;
}

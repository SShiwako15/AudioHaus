using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: AudioHausFilePlayerInfo
 * Input: Native file-player status snapshot.
 * Process: Matches native field order and Windows default packing.
 * Output: A 56-byte value with transport state, timing, and render counters.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct AudioHausFilePlayerInfo
{
    public uint State;
    public uint HResult;
    public uint SampleRate;
    public uint ChannelCount;
    public ulong Position100Ns;
    public ulong Duration100Ns;
    public ulong FramesSubmitted;
    public ulong StartQpc;
    public ulong StopQpc;
}

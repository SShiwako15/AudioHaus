using System;
using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: AudioLevel
 * Input: Acknowledged normalized master gain and mute state.
 * Process: Separates source/output controls from route controls.
 * Output: Immutable level without changing existing telemetry layouts.
 */
[StructLayout(LayoutKind.Sequential)]
public readonly struct AudioLevel
{
    public readonly double Gain;
    private readonly uint muted;
    internal readonly uint HResult;
    public bool Muted => muted != 0;
}

/*
 * Name: SourceSignal
 * Input: Native source contributions after source gain/mute.
 * Process: Matches the additive 32-byte signal ABI without changing existing layouts.
 * Output: Cumulative stereo frames/energy across destinations and latest block peak.
 */
[StructLayout(LayoutKind.Sequential)]
public readonly struct SourceSignal
{
    public readonly ulong Frames;
    public readonly double SumSquares, LastPeak;
    internal readonly uint HResult;
    private readonly uint reserved;
}

/*
 * Name: NativeLevelMethods
 * Input: Router resource identity and master target.
 * Process: Binds additive level exports using the existing native ownership.
 * Output: Exact C status, levels, and HRESULT.
 */
internal static class NativeLevelMethods
{
    private const string LibraryName = "AudioHaus.Native.dll";
    /*
     * Name: GetSignal
     * Input: Native router, source ID, and signal destination.
     * Process: Reads post-source-master measurements through the additive C ABI.
     * Output: Native status and signal/HRESULT.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_source_signal", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult GetSignal(SafeRoutingHandle router, uint id, out SourceSignal signal);
    /*
     * Name: SetSource
     * Input: Router, source ID, normalized gain, mute, and error output.
     * Process: Sets a smoothed source master target.
     * Output: Native acknowledgment or validation failure.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_set_source_level", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult SetSource(SafeRoutingHandle router, uint id, double gain, uint muted, out uint error);
    /*
     * Name: SetOutput
     * Input: Router, output ID, normalized gain, mute, and error output.
     * Process: Sets a smoothed output master target.
     * Output: Native acknowledgment or validation failure.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_set_output_level", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult SetOutput(SafeRoutingHandle router, uint id, double gain, uint muted, out uint error);
    /*
     * Name: GetSource
     * Input: Router, source ID, and level destination.
     * Process: Reads the acknowledged source master target.
     * Output: Level and native status/HRESULT.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_source_level", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult GetSource(SafeRoutingHandle router, uint id, out AudioLevel level);
    /*
     * Name: GetOutput
     * Input: Router, output ID, and level destination.
     * Process: Reads the acknowledged output master target.
     * Output: Level and native status/HRESULT.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_output_level", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult GetOutput(SafeRoutingHandle router, uint id, out AudioLevel level);
}

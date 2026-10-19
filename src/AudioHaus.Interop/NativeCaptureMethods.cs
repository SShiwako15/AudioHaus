using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: ProcessCaptureInfo
 * Input: Native live-source telemetry.
 * Process: Matches the 80-byte capture ABI.
 * Output: Plain copied values without native ownership.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessCaptureInfo
{
    public uint ProcessId, State, HResult, Reserved;
    public ulong FramesCaptured, NonSilentFrames, Discontinuities, FramesDropped, BufferedFrames, LastPacketQpc100Ns;
    public double SumSquares, LastPeak;
}

/*
 * Name: NativeCaptureMethods
 * Input: Capture commands and active-session queries.
 * Process: Declares exact Cdecl bindings for the additive capture API.
 * Output: API results, HRESULTs, and copied snapshots.
 */
internal static class NativeCaptureMethods
{
    private const string LibraryName = "AudioHaus.Native.dll";
    /*
     * Name: AddProcess
     * Input: Router, application PID, and virtual render endpoint ID.
     * Process: Opens one stopped process-tree monitor.
     * Output: Status, new source ID, and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_add_process", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult AddProcess(SafeRoutingHandle router, uint processId,
        [MarshalAs(UnmanagedType.LPWStr)] string parkingId, out uint sourceId, out uint hresult);
    /*
     * Name: GetCapture
     * Input: Router and process source ID.
     * Process: Reads capture health and counters.
     * Output: Status and 80-byte snapshot.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_capture", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetCapture(SafeRoutingHandle router, uint sourceId, out ProcessCaptureInfo info);
    /*
     * Name: GetEndpointProcesses
     * Input: Endpoint ID and bounded PID array.
     * Process: Copies distinct active render-session PIDs.
     * Output: Status, required array size, and HRESULT; no partial writes.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_get_endpoint_processes", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetEndpointProcesses([MarshalAs(UnmanagedType.LPWStr)] string endpointId,
        [Out] uint[] processIds, uint capacity, out uint required, out uint hresult);
}

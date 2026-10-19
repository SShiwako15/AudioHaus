using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: OutputTimingInfo
 * Input: Native renderer timing values.
 * Process: Matches the additive 96-byte C ABI.
 * Output: Plain layout for immutable managed timing snapshots.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct OutputTimingInfo
{
    public uint HResult, DelayFrames, CapacityFrames, PaddingFrames;
    public uint DriftEnabled, RetryInMs, LastErrorHResult, Reserved;
    public ulong UnderrunCount, RecoveryAttempts, RecoveryCount, RebufferCount;
    public double ClockRatePpm, MaxServiceGapMs;
    public ulong StreamLatency100Ns, FirstSignalQpc;
}
/*
 * Name: RouteTimingInfo
 * Input: Native fractional-reader timing values.
 * Process: Matches the additive 80-byte C ABI.
 * Output: Plain layout for immutable managed route timing.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct RouteTimingInfo
{
    public uint HResult, Priming, BufferedFrames, TargetFrames;
    public double CorrectionPpm, PhaseErrorFrames, ReadPositionFrame;
    public ulong UnderrunCount, UnderrunFrames, OverrunCount, RebufferCount, Reserved;
}
/*
 * Name: CaptureTimingInfo
 * Input: Native process source recovery values.
 * Process: Matches the additive 48-byte C ABI.
 * Output: Plain copied capture timing values.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct CaptureTimingInfo
{
    public uint HResult, RetryInMs, ResumeState, LastErrorHResult;
    public ulong RecoveryAttempts, RecoveryCount, CapacityFrames, Reserved;
}
/*
 * Name: NativeTimingMethods
 * Input: Output timing commands and queries.
 * Process: Binds exact Cdecl exports with SafeHandle lifetime protection.
 * Output: Native results, HRESULTs, and copied snapshots.
 */
internal static class NativeTimingMethods
{
    private const string LibraryName = "AudioHaus.Native.dll";
    /*
     * Name: SetDelay
     * Input: Router, output ID, and additional normalized frames.
     * Process: Changes one output's delay.
     * Output: API result and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_set_output_delay", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult SetDelay(SafeRoutingHandle router, uint output, uint frames, out uint hresult);
    /*
     * Name: SetDrift
     * Input: Router, output ID, and enabled 0/1.
     * Process: Changes one output's drift correction policy.
     * Output: API result and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_set_output_drift", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult SetDrift(SafeRoutingHandle router, uint output, uint enabled, out uint hresult);
    /*
     * Name: GetOutput
     * Input: Router, output ID, and timing destination.
     * Process: Reads queue, clock, and recovery counters.
     * Output: API result and 96-byte timing snapshot.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_output_timing", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetOutput(SafeRoutingHandle router, uint output, out OutputTimingInfo info);
    /*
     * Name: GetRoute
     * Input: Router, route IDs, and timing destination.
     * Process: Reads fractional cursor and buffering/correction counters.
     * Output: API result and 80-byte timing snapshot.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_route_timing", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetRoute(SafeRoutingHandle router, uint source, uint output, out RouteTimingInfo info);
    /*
     * Name: GetCapture
     * Input: Router and process source ID.
     * Process: Reads recovery state and dynamic history capacity.
     * Output: API result and 48-byte snapshot.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_capture_timing", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetCapture(SafeRoutingHandle router, uint source, out CaptureTimingInfo info);
    /*
     * Name: RetryOutput
     * Input: Router and existing output ID.
     * Process: Requests an immediate asynchronous reopen.
     * Output: API result and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_retry_output", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult RetryOutput(SafeRoutingHandle router, uint output, out uint hresult);
    /*
     * Name: RetryCapture
     * Input: Router and existing process source ID.
     * Process: Requests an immediate same-root capture reopen.
     * Output: API result and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_retry_capture", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult RetryCapture(SafeRoutingHandle router, uint source, out uint hresult);
}

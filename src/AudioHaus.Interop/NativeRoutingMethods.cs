using System;
using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: NativeRoutingMethods
 * Input: Managed arguments matching RoutingApi.h.
 * Process: Calls exact native exports and retains SafeHandle ownership during calls.
 * Output: Native status codes, HRESULTs, IDs, and snapshots.
 */
internal static class NativeRoutingMethods
{
    private const string LibraryName = "AudioHaus.Native.dll";

    /*
     * Name: Create
     * Input: Handle and HRESULT output locations.
     * Process: Initializes an empty routing worker.
     * Output: Status and an owning raw pointer to wrap immediately.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_create_router", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult Create(out IntPtr router, out uint hresult);

    /*
     * Name: AddSource
     * Input: Live router and local UTF-16 file path.
     * Process: Opens a stopped shared decoder.
     * Output: Status, source ID, and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_add_source", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult AddSource(SafeRoutingHandle router, [MarshalAs(UnmanagedType.LPWStr)] string path, out uint id, out uint hresult);

    /*
     * Name: AddOutput
     * Input: Live router and UTF-16 render endpoint ID.
     * Process: Opens the endpoint's shared mixer stream.
     * Output: Status, output ID, and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_add_output", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult AddOutput(SafeRoutingHandle router, [MarshalAs(UnmanagedType.LPWStr)] string endpointId, out uint id, out uint hresult);

    /*
     * Name: RemoveSource
     * Input: Live router and source ID.
     * Process: Removes a decoder and its connections.
     * Output: Status and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_remove_source", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult RemoveSource(SafeRoutingHandle router, uint id, out uint hresult);

    /*
     * Name: RemoveOutput
     * Input: Live router and output ID.
     * Process: Stops one mixer and removes its connections.
     * Output: Status and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_remove_output", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult RemoveOutput(SafeRoutingHandle router, uint id, out uint hresult);

    /*
     * Name: Connect
     * Input: Source/output IDs, gain, and uint32 mute flag.
     * Process: Creates or updates the connection.
     * Output: Status and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_connect", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult Connect(SafeRoutingHandle router, uint source, uint output, double gain, uint muted, out uint hresult);

    /*
     * Name: Disconnect
     * Input: Existing source/output IDs.
     * Process: Removes one connection.
     * Output: Status and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_disconnect", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult Disconnect(SafeRoutingHandle router, uint source, uint output, out uint hresult);

    /*
     * Name: SetGain
     * Input: Existing route and gain.
     * Process: Changes only that route's gain.
     * Output: Status and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_set_gain", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult SetGain(SafeRoutingHandle router, uint source, uint output, double gain, out uint hresult);

    /*
     * Name: SetMute
     * Input: Existing route and uint32 mute flag.
     * Process: Changes only that route's mute state.
     * Output: Status and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_set_mute", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult SetMute(SafeRoutingHandle router, uint source, uint output, uint muted, out uint hresult);

    /*
     * Name: SourceCommand
     * Input: Source, transport command, and optional seek ticks.
     * Process: Controls the shared source timeline.
     * Output: Status and HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_source_command", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult SourceCommand(SafeRoutingHandle router, uint source, uint command, ulong ticks, out uint hresult);

    /*
     * Name: GetSource
     * Input: Source ID and snapshot output.
     * Process: Reads its shared timeline and cache counters.
     * Output: Status and snapshot.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_source", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetSource(SafeRoutingHandle router, uint source, out RoutingSourceInfo info);

    /*
     * Name: GetOutput
     * Input: Output ID and snapshot output.
     * Process: Reads endpoint and PCM counters.
     * Output: Status and snapshot.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_output", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetOutput(SafeRoutingHandle router, uint output, out RoutingOutputInfo info);

    /*
     * Name: GetRoute
     * Input: Existing route IDs and snapshot output.
     * Process: Reads connection controls and cursor activity.
     * Output: Status and snapshot.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_router_get_route", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetRoute(SafeRoutingHandle router, uint source, uint output, out RoutingRouteInfo info);

    /*
     * Name: Destroy
     * Input: Owned router pointer with no callers, or zero.
     * Process: Joins the routing worker and releases its graph.
     * Output: None; reserved for SafeHandle cleanup.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_destroy_router", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern void Destroy(IntPtr router);
}

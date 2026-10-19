using System;
using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: NativeMethods
 * Input: Managed arguments matching the native C API contract.
 * Process: Calls the Windows x64 DLL using exact export names and Cdecl.
 * Output: Native status codes and endpoint data; DLL loading failures throw.
 */
internal static class NativeMethods
{
    private const string LibraryName = "AudioHaus.Native.dll";

    /*
     * Name: GetApiVersion
     * Input: None.
     * Process: Queries the DLL's ABI version.
     * Output: Native API version; callers must verify compatibility before use.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_get_api_version",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern uint GetApiVersion();

    /*
     * Name: CreateEndpointSnapshot
     * Input: Output locations for the snapshot pointer and endpoint count.
     * Process: Captures endpoints; successful ownership must be wrapped or released.
     * Output: Status, native pointer, and count; failure returns zero outputs.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_create_endpoint_snapshot",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult CreateEndpointSnapshot(
        out IntPtr snapshot,
        out uint endpointCount);

    /*
     * Name: GetEndpoint
     * Input: Live snapshot, index, and separate writable UTF-16 buffers with
     *        capacities in code units; zero pointers require zero capacities.
     * Process: Keeps the SafeHandle alive during the native read; zero buffers
     *          query sizes. Callers must allocate enough memory for each capacity.
     * Output: Status and metadata; small buffers remain untouched. Successful
     *         buffers contain null-terminated text. Disposed handles throw.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_get_endpoint",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetEndpoint(
        SafeHandle snapshot,
        uint index,
        out AudioHausEndpointInfo info,
        IntPtr idBuffer,
        uint idCapacity,
        IntPtr displayNameBuffer,
        uint displayNameCapacity);

    /*
     * Name: DestroyEndpointSnapshot
     * Input: Live native snapshot pointer with no readers, or zero.
     * Process: Releases native ownership; reserved for SafeHandle cleanup.
     * Output: None; the pointer becomes invalid. Zero is a no-op.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_destroy_endpoint_snapshot",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern void DestroyEndpointSnapshot(IntPtr snapshot);

    /*
     * Name: PlayTestTone
     * Input: UTF-16 endpoint IDs, tone parameters, and per-output report storage.
     * Process: Blocks while native WASAPI streams render and drain the test tone.
     * Output: Status and reports; native failures retain per-output HRESULT values.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_play_test_tone",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult PlayTestTone(
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] endpointIds,
        uint endpointCount,
        double frequencyHz,
        double amplitude,
        uint durationMs,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 6)] AudioHausToneOutputInfo[] results,
        uint resultsCapacity);

    /*
     * Name: OpenFilePlayer
     * Input: Local path, endpoint ID, gain, and output locations.
     * Process: Creates a stopped native decoder and render worker.
     * Output: Status, owning pointer, and native HRESULT.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_open_file_player",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult OpenFilePlayer(
        [MarshalAs(UnmanagedType.LPWStr)] string filePath,
        [MarshalAs(UnmanagedType.LPWStr)] string endpointId,
        double gain,
        out IntPtr player,
        out uint errorHResult);

    /*
     * Name: FilePlayerCommand
     * Input: Live handle, command, and optional seek position in 100 ns units.
     * Process: Holds the handle during the worker's acknowledged command.
     * Output: Status and native HRESULT; closed handles throw.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_file_player_command",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult FilePlayerCommand(
        SafeFilePlayerHandle player, uint command, ulong position100Ns, out uint errorHResult);

    /*
     * Name: GetFilePlayerStatus
     * Input: Live handle and snapshot output.
     * Process: Obtains current native transport state without changing playback.
     * Output: Status and snapshot; closed handles throw.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_get_file_player_status",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern AudioHausResult GetFilePlayerStatus(
        SafeFilePlayerHandle player, out AudioHausFilePlayerInfo status);

    /*
     * Name: CloseFilePlayer
     * Input: Owned native pointer after its callers finish, or zero.
     * Process: Stops and joins the player before releasing it.
     * Output: None; reserved for SafeHandle cleanup.
     */
    [DllImport(LibraryName, EntryPoint = "audiohaus_close_file_player",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern void CloseFilePlayer(IntPtr player);
}

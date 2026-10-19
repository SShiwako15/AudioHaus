using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AudioHaus.Interop;

/*
 * Name: ProcessIdentity
 * Input: Windows PID and creation FILETIME.
 * Process: Identifies a single process lifetime despite PID reuse.
 * Output: A value suitable for discovery reconciliation and routing ownership.
 */
public readonly record struct ProcessIdentity(uint ProcessId, ulong CreationTime);

/*
 * Name: ApplicationRoutingPolicy
 * Input: Process identities and opaque Windows output preferences.
 * Process: Contains the Windows 11 internal policy dependency behind native calls.
 * Output: Explicit capability/failure results without changing the system default.
 */
public static class ApplicationRoutingPolicy
{
    /*
     * Name: Probe
     * Input: None.
     * Process: Activates the known policy interface without changing preferences.
     * Output: Zero on success, otherwise the exact failing Windows HRESULT.
     */
    public static uint Probe()
    {
        NativeApplicationMethods.Probe(out uint error);
        return error;
    }

    /*
     * Name: ReadPreference
     * Input: Live or retained PID and render role 0..2.
     * Process: Copies the opaque Windows preference through bounded buffers.
     * Output: Preference, or empty for follow-default; failures throw with HRESULT.
     */
    public static string ReadPreference(uint processId, uint role)
    {
        AudioHausResult result = NativeApplicationMethods.Get(processId, role, null, 0, out uint required, out uint error);
        if (result != AudioHausResult.BufferTooSmall) { AudioRouter.CheckResult(result, error, "Read application output"); }
        var buffer = new StringBuilder(checked((int)required));
        result = NativeApplicationMethods.Get(processId, role, buffer, required, out _, out error);
        AudioRouter.CheckResult(result, error, "Read application output");
        return buffer.ToString();
    }

    /*
     * Name: WritePreference
     * Input: Retained PID, render role 0..2, and previously read preference or empty.
     * Process: Writes only one application render preference.
     * Output: Completion; invalid/unsupported preferences throw with HRESULT.
     */
    public static void WritePreference(uint processId, uint role, string? preference)
    {
        AudioHausResult result = NativeApplicationMethods.Set(processId, role, preference, out uint error);
        AudioRouter.CheckResult(result, error, "Change application output");
    }

    /*
     * Name: EndpointPreference
     * Input: Opaque MMDevice render endpoint ID.
     * Process: Forms the Windows policy render-interface identifier.
     * Output: Opaque preference suitable for WritePreference.
     */
    public static string EndpointPreference(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        return @"\\?\SWD#MMDEVAPI#" + endpointId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    }

    /*
     * Name: CheckParking
     * Input: Root PID and exact cable endpoint ID.
     * Process: Checks all active original sessions in the process tree.
     * Output: Zero for parked/idle; exact discovery or endpoint-escape HRESULT otherwise.
     */
    public static uint CheckParking(uint processId, string parkingId)
    {
        NativeApplicationMethods.CheckParking(processId, parkingId, out uint error);
        return error;
    }

    /*
     * Name: ContainsProcess
     * Input: Root and candidate PIDs.
     * Process: Checks ancestry through the native process snapshot.
     * Output: Membership; discovery failures throw instead of permitting overlap.
     */
    public static bool ContainsProcess(uint root, uint candidate)
    {
        AudioHausResult result = NativeApplicationMethods.ContainsProcess(root, candidate, out uint included, out uint error);
        AudioRouter.CheckResult(result, error, "Check process ancestry"); return included != 0;
    }
}

/*
 * Name: NativeSessionInfo
 * Input: Native batched session metadata.
 * Process: Matches the 24-byte C layout.
 * Output: PID, creation time, and UTF-16 buffer capacities.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct NativeSessionInfo
{
    public uint ProcessId, EndpointCapacity;
    public ulong CreationTime;
    public uint IdentityCapacity, Reserved;
}

/*
 * Name: SafeSessionSnapshot
 * Input: Owned native session snapshot.
 * Process: Balances immutable discovery ownership, including exceptional paths.
 * Output: Snapshot released exactly once.
 */
internal sealed class SafeSessionSnapshot : SafeHandleZeroOrMinusOneIsInvalid
{
    /*
     * Name: SafeSessionSnapshot
     * Input: Owned native pointer.
     * Process: Transfers snapshot ownership to SafeHandle.
     * Output: Finalizer-safe immutable snapshot handle.
     */
    internal SafeSessionSnapshot(IntPtr value) : base(true) { SetHandle(value); }
    /*
     * Name: ReleaseHandle
     * Input: Final owning SafeHandle.
     * Process: Releases the native discovery snapshot.
     * Output: True after ownership is released.
     */
    protected override bool ReleaseHandle() { NativeApplicationMethods.DestroySnapshot(handle); return true; }
}

/*
 * Name: NativeApplicationMethods
 * Input: Managed policy operations and snapshot buffers.
 * Process: Binds the additive Cdecl policy/discovery ABI.
 * Output: Exact status codes and copied values.
 */
internal static class NativeApplicationMethods
{
    private const string LibraryName = "AudioHaus.Native.dll";
    /*
     * Name: ContainsProcess
     * Input: Root/candidate PIDs and result storage.
     * Process: Calls the native process-tree check.
     * Output: Membership and exact native failure.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_process_tree_contains", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult ContainsProcess(uint root, uint candidate, out uint included, out uint error);
    /*
     * Name: Probe
     * Input: HRESULT destination.
     * Process: Probes the isolated Windows policy factory.
     * Output: Capability status without changing preferences.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_policy_probe", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult Probe(out uint error);
    /*
     * Name: Get
     * Input: PID, render role, and optional UTF-16 buffer.
     * Process: Copies an opaque persisted output preference.
     * Output: Status, required capacity, and HRESULT.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_policy_get", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern AudioHausResult Get(uint pid, uint role, StringBuilder? buffer, uint capacity, out uint required, out uint error);
    /*
     * Name: Set
     * Input: PID, render role, and opaque preference or null.
     * Process: Changes one application's render-role preference.
     * Output: Status and exact HRESULT.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_policy_set", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern AudioHausResult Set(uint pid, uint role, string? preference, out uint error);
    /*
     * Name: CheckParking
     * Input: Root PID, cable ID, and HRESULT destination.
     * Process: Checks active original sessions throughout the process tree.
     * Output: Status indicating actual stream isolation.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_policy_check_parking", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern AudioHausResult CheckParking(uint pid, string parking, out uint error);
    /*
     * Name: CreateSnapshot
     * Input: Snapshot, count, and HRESULT destinations.
     * Process: Creates one immutable batch of active Windows audio sessions.
     * Output: Owned native snapshot or failure.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_create_session_snapshot", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioHausResult CreateSnapshot(out IntPtr snapshot, out uint count, out uint error);
    /*
     * Name: GetSession
     * Input: Owned snapshot, row index, and UTF-16 buffers.
     * Process: Copies one process lifetime and session endpoint.
     * Output: Status and metadata; short buffers receive required capacities.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_get_session", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern AudioHausResult GetSession(SafeSessionSnapshot snapshot, uint index, out NativeSessionInfo info,
        StringBuilder? endpoint, uint endpointCapacity, StringBuilder? identity, uint identityCapacity);
    /*
     * Name: DestroySnapshot
     * Input: Owned snapshot or null.
     * Process: Releases immutable discovery storage.
     * Output: No remaining native snapshot ownership.
     */
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(LibraryName, EntryPoint = "audiohaus_destroy_session_snapshot", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DestroySnapshot(IntPtr snapshot);
}

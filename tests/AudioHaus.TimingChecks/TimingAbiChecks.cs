using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AudioHaus.TimingChecks;

/*
 * Name: TimingAbiChecks
 * Input: Built native library beside the check executable.
 * Process: Calls the real C exports with guarded storage and invalid arguments.
 * Output: Evidence of exact snapshot writes and controlled native rejection.
 */
internal static class TimingAbiChecks
{
    /*
     * Name: Query
     * Input: Native router, output/source ID, and raw destination.
     * Process: Represents the common two-ID timing-query signature.
     * Output: Native API result.
     */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Query(IntPtr router, uint id, IntPtr destination);
    /*
     * Name: RouteQuery
     * Input: Native router, route IDs, and raw destination.
     * Process: Represents the route timing-query signature.
     * Output: Native API result.
     */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RouteQuery(IntPtr router, uint source, uint output, IntPtr destination);
    /*
     * Name: Create
     * Input: Router and HRESULT destinations.
     * Process: Represents native router creation.
     * Output: Native result and owned handle.
     */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Create(out IntPtr router, out uint hresult);
    /*
     * Name: Destroy
     * Input: Owned native router.
     * Process: Represents native router destruction.
     * Output: All router resources released.
     */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Destroy(IntPtr router);
    /*
     * Name: SetValue
     * Input: Native router, output, setting, and HRESULT destination.
     * Process: Represents delay/drift commands at the raw ABI boundary.
     * Output: API result and exact HRESULT.
     */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetValue(IntPtr router, uint output, uint value, out uint hresult);

    /*
     * Name: Run
     * Input: None.
     * Process: Loads exact exports, owns a native graph, and exercises guarded writes.
     * Output: PASS or a descriptive failure; all unmanaged ownership uses finally.
     */
    internal static void Run()
    {
        IntPtr library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "AudioHaus.Native.dll"));
        IntPtr router = IntPtr.Zero;
        try
        {
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            Program.Require(Export<Create>("audiohaus_create_router")(out router, out uint hr) == 0 && hr == 0, "Raw router creation failed.");
            Query output = Export<Query>("audiohaus_router_get_output_timing"), capture = Export<Query>("audiohaus_router_get_capture_timing");
            RouteQuery route = Export<RouteQuery>("audiohaus_router_get_route_timing");
            Guard(96, (owner, pointer) => output(owner, 999, pointer), router);
            Guard(48, (owner, pointer) => capture(owner, 999, pointer), router);
            Guard(80, (owner, pointer) => route(owner, 999, 999, pointer), router);
            Program.Require(Export<SetValue>("audiohaus_router_set_output_delay")(router, 999, 96001, out hr) == 1 && hr == 0x80070057, "Native delay bound was not rejected.");
            Program.Require(Export<SetValue>("audiohaus_router_set_output_drift")(router, 999, 2, out hr) == 1 && hr == 0x80070057, "Native drift flag was not rejected.");
            Console.WriteLine("PASS: native timing snapshot guard bytes, null handles/storage, missing IDs, delay bound, and drift flag.");
        }
        finally
        {
            if (router != IntPtr.Zero) { Marshal.GetDelegateForFunctionPointer<Destroy>(NativeLibrary.GetExport(library, "audiohaus_destroy_router"))(router); }
            NativeLibrary.Free(library);
        }
    }

    /*
     * Name: Guard
     * Input: Exact structure size, query delegate, and live empty router.
     * Process: Places sentinel bytes before/after storage and queries null/missing objects.
     * Output: Returns for bounded writes and cleared failure metadata; otherwise throws.
     */
    private static void Guard(int size, Func<IntPtr, IntPtr, int> query, IntPtr router)
    {
        IntPtr storage = Marshal.AllocHGlobal(size + 16);
        try
        {
            foreach (IntPtr owner in new[] { IntPtr.Zero, router })
            {
                for (int index = 0; index < size + 16; ++index) { Marshal.WriteByte(storage, index, 0xA5); }
                Program.Require(query(owner, storage + 8) == 1, "Invalid timing query unexpectedly succeeded.");
                for (int index = 0; index < size + 16; ++index)
                {
                    if (index < 8 || index >= size + 8) { Program.Require(Marshal.ReadByte(storage, index) == 0xA5, "Timing query overwrote a guard byte."); }
                    else if (owner == IntPtr.Zero || index >= 12) { Program.Require(Marshal.ReadByte(storage, index) == 0, "Timing query did not clear failure storage."); }
                }
                if (owner != IntPtr.Zero) { Program.Require(unchecked((uint)Marshal.ReadInt32(storage, 8)) == 0x80070057, "Timing query lost E_INVALIDARG."); }
            }
            Program.Require(query(router, IntPtr.Zero) == 1, "Null timing storage was not rejected.");
        }
        finally { Marshal.FreeHGlobal(storage); }
    }
}

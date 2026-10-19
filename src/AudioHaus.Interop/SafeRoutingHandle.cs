using System;
using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: SafeRoutingHandle
 * Input: A native router allocation.
 * Process: Owns the graph and defers destruction until its native callers finish.
 * Output: Disposable ownership with finalization as a fallback.
 */
internal sealed class SafeRoutingHandle : SafeHandle
{
    /*
     * Name: SafeRoutingHandle
     * Input: None.
     * Process: Creates empty ownership before native allocation.
     * Output: An invalid handle to be initialized by Create.
     */
    private SafeRoutingHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /*
     * Name: Create
     * Input: None.
     * Process: Initializes native ownership before checking the creation result.
     * Output: Owning handle, or an exception containing native failure details.
     */
    internal static SafeRoutingHandle Create()
    {
        var owned = new SafeRoutingHandle();
        try
        {
            AudioHausResult result = NativeRoutingMethods.Create(out IntPtr pointer, out uint hresult);
            owned.SetHandle(pointer);
            AudioRouter.CheckResult(result, hresult, "Create router");
            if (owned.IsInvalid) { throw new InvalidOperationException("Native router returned a null handle."); }
            return owned;
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    /*
     * Name: ReleaseHandle
     * Input: Owned pointer after outstanding calls finish.
     * Process: Releases sources, outputs, and the worker through the DLL.
     * Output: True after native destruction.
     */
    protected override bool ReleaseHandle()
    {
        NativeRoutingMethods.Destroy(handle);
        return true;
    }
}

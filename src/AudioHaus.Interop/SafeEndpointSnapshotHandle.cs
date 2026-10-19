using System;
using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: SafeEndpointSnapshotHandle
 * Input: A snapshot created through the native API.
 * Process: Owns the snapshot and defers release while native calls are using it.
 * Output: A disposable handle with garbage-collection cleanup as a fallback.
 */
internal sealed class SafeEndpointSnapshotHandle : SafeHandle
{
    /*
     * Name: SafeEndpointSnapshotHandle
     * Input: None; construction is restricted to Create.
     * Process: Initializes an empty owning handle before native allocation.
     * Output: An invalid handle ready to receive a native snapshot.
     */
    private SafeEndpointSnapshotHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /*
     * Name: Create
     * Input: An output location for the endpoint count.
     * Process: Creates a native snapshot and takes ownership before checking status.
     * Output: An owning handle and count. Throws InvalidOperationException for
     *         native failure or a null snapshot; DLL loading errors propagate.
     */
    internal static SafeEndpointSnapshotHandle Create(out uint endpointCount)
    {
        endpointCount = 0;
        var snapshot = new SafeEndpointSnapshotHandle();

        try
        {
            AudioHausResult result = NativeMethods.CreateEndpointSnapshot(
                out IntPtr pointer, out uint count);
            snapshot.SetHandle(pointer);

            if (result != AudioHausResult.Success)
            {
                throw new InvalidOperationException($"Endpoint discovery failed: {result}.");
            }

            if (snapshot.IsInvalid)
            {
                throw new InvalidOperationException("Native discovery returned a null snapshot.");
            }

            endpointCount = count;
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    /*
     * Name: ReleaseHandle
     * Input: The owned native pointer, after outstanding handle references finish.
     * Process: Releases the snapshot through the native destroy function.
     * Output: True after release; SafeHandle prevents repeated destruction.
     */
    protected override bool ReleaseHandle()
    {
        NativeMethods.DestroyEndpointSnapshot(handle);
        return true;
    }
}

using System;
using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: SafeFilePlayerHandle
 * Input: A native file-player allocation.
 * Process: Owns the native worker and defers cleanup until native callers finish.
 * Output: Deterministic disposal with garbage-collection cleanup as a fallback.
 */
internal sealed class SafeFilePlayerHandle : SafeHandle
{
    /*
     * Name: SafeFilePlayerHandle
     * Input: None.
     * Process: Initializes ownership before allocating the native player.
     * Output: An empty handle, constructed only by Open.
     */
    private SafeFilePlayerHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /*
     * Name: Open
     * Input: Validated local path, endpoint ID, and gain.
     * Process: Takes native ownership before checking the open result.
     * Output: A stopped player's owning handle, or an exception with native details.
     */
    internal static SafeFilePlayerHandle Open(string filePath, string endpointId, double gain)
    {
        var owned = new SafeFilePlayerHandle();
        try
        {
            AudioHausResult result = NativeMethods.OpenFilePlayer(
                filePath, endpointId, gain, out IntPtr pointer, out uint hresult);
            owned.SetHandle(pointer);
            if (result == AudioHausResult.OutOfMemory)
            {
                throw new OutOfMemoryException("Native file playback could not allocate resources.");
            }
            if (result != AudioHausResult.Success || owned.IsInvalid)
            {
                throw new InvalidOperationException($"Could not open audio file: {result}, HRESULT 0x{hresult:X8}.");
            }
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
     * Input: Owned pointer after all native callers finish.
     * Process: Stops the player and joins its independent worker through the DLL.
     * Output: True after release; repeated disposal does not release twice.
     */
    protected override bool ReleaseHandle()
    {
        NativeMethods.CloseFilePlayer(handle);
        return true;
    }
}

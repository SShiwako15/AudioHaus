using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: AudioEndpointEnumerator
 * Input: None.
 * Process: Reads native endpoint snapshots into managed models and releases resources.
 * Output: Read-only discovery results for display and output selection.
 */
public sealed class AudioEndpointEnumerator
{
    private const uint SupportedApiVersion = 1;

    /*
     * Name: Enumerate
     * Input: None.
     * Process: Checks ABI compatibility and copies all endpoint states from a snapshot.
     * Output: A read-only list, empty when no endpoints exist. Throws
     *         NotSupportedException for an incompatible ABI or InvalidOperationException
     *         for native failure. Loading, allocation, and overflow errors propagate.
     */
    public IReadOnlyList<AudioEndpoint> Enumerate()
    {
        uint version = NativeMethods.GetApiVersion();
        if (version != SupportedApiVersion)
        {
            throw new NotSupportedException($"Native API version {version} is unsupported; expected {SupportedApiVersion}.");
        }

        using var snapshot = SafeEndpointSnapshotHandle.Create(out uint count);
        var endpoints = new List<AudioEndpoint>(checked((int)count));

        for (uint index = 0; index < count; ++index)
        {
            endpoints.Add(ReadEndpoint(snapshot, index));
        }

        return endpoints.AsReadOnly();
    }

    /*
     * Name: ReadEndpoint
     * Input: Live snapshot and zero-based endpoint index.
     * Process: Queries UTF-16 buffer sizes, reads metadata and text, and frees buffers.
     * Output: An immutable endpoint; unknown state values map to Unknown. Throws
     *         InvalidOperationException for native failure or invalid sizes; allocation
     *         and overflow errors propagate. Buffers are released on every exit.
     */
    private static AudioEndpoint ReadEndpoint(SafeEndpointSnapshotHandle snapshot, uint index)
    {
        AudioHausResult result = NativeMethods.GetEndpoint(
            snapshot, index, out AudioHausEndpointInfo info, IntPtr.Zero, 0, IntPtr.Zero, 0);

        if (result != AudioHausResult.BufferTooSmall)
        {
            throw new InvalidOperationException($"Endpoint {index} size query failed: {result}.");
        }

        if (info.IdCapacityRequired < 2 || info.DisplayNameCapacityRequired < 2)
        {
            throw new InvalidOperationException($"Endpoint {index} returned invalid text buffer sizes.");
        }

        int idCapacity = checked((int)info.IdCapacityRequired);
        int nameCapacity = checked((int)info.DisplayNameCapacityRequired);
        IntPtr idBuffer = IntPtr.Zero;
        IntPtr nameBuffer = IntPtr.Zero;

        try
        {
            idBuffer = Marshal.AllocHGlobal(checked(idCapacity * sizeof(char)));
            nameBuffer = Marshal.AllocHGlobal(checked(nameCapacity * sizeof(char)));

            result = NativeMethods.GetEndpoint(
                snapshot, index, out info, idBuffer, (uint)idCapacity, nameBuffer, (uint)nameCapacity);

            if (result != AudioHausResult.Success)
            {
                throw new InvalidOperationException($"Endpoint {index} read failed: {result}.");
            }

            string id = Marshal.PtrToStringUni(idBuffer, idCapacity - 1)!;
            string displayName = Marshal.PtrToStringUni(nameBuffer, nameCapacity - 1)!;
            var state = (AudioHausEndpointState)info.State;
            if (!Enum.IsDefined(state))
            {
                state = AudioHausEndpointState.Unknown;
            }

            return new AudioEndpoint(id, displayName, state, info.IsDefaultMultimedia != 0);
        }
        finally
        {
            Marshal.FreeHGlobal(idBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }
}

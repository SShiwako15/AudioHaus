using System.Runtime.InteropServices;

namespace AudioHaus.Interop;

/*
 * Name: AudioHausEndpointInfo
 * Input: Metadata returned by the native endpoint API.
 * Process: Matches four native uint32_t fields in declaration order; capacities
 *          include UTF-16 null terminators, and default status is 0 or 1.
 * Output: A 16-byte interop value containing buffer sizes, state, and default status.
 */
[StructLayout(LayoutKind.Sequential)]
internal struct AudioHausEndpointInfo
{
    public uint IdCapacityRequired;
    public uint DisplayNameCapacityRequired;
    public uint State;
    public uint IsDefaultMultimedia;
}

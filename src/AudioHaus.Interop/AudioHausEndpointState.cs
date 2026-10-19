namespace AudioHaus.Interop;

/*
 * Name: AudioHausEndpointState
 * Input: Endpoint state returned by the native API.
 * Process: Matches the native endpoint state's unsigned 32-bit values.
 * Output: A named availability state; unknown means not yet known or recognized.
 */
public enum AudioHausEndpointState : uint
{
    Unknown = 0,
    Active = 1,
    Disabled = 2,
    NotPresent = 3,
    Unplugged = 4
}

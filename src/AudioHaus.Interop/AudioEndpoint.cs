using System;

namespace AudioHaus.Interop;

/*
 * Name: AudioEndpoint
 * Input: Opaque endpoint ID, non-unique display name, state, and multimedia default status.
 * Process: Stores an immutable discovery snapshot without retaining native resources.
 * Output: Managed endpoint information for display and output selection.
 */
public sealed class AudioEndpoint
{
    public string Id { get; }
    public string DisplayName { get; }
    public AudioHausEndpointState State { get; }
    public bool IsDefaultMultimedia { get; }

    /*
     * Name: AudioEndpoint
     * Input: Endpoint ID, display name, state, and multimedia default status.
     * Process: Validates the strings and stores the supplied discovery values.
     * Output: An immutable endpoint; throws ArgumentNullException for null strings
     *         or ArgumentException for empty strings. The ID is not parsed.
     */
    public AudioEndpoint(
        string id,
        string displayName,
        AudioHausEndpointState state,
        bool isDefaultMultimedia)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(displayName);

        Id = id;
        DisplayName = displayName;
        State = state;
        IsDefaultMultimedia = isDefaultMultimedia;
    }
}

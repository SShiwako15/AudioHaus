using AudioHaus.Interop;

namespace AudioHaus.App;

/*
 * Name: EndpointChoice
 * Input: An immutable Windows endpoint and an unambiguous display label.
 * Process: Retains the opaque ID without using the display name as identity.
 * Output: A selectable endpoint snapshot.
 */
public sealed class EndpointChoice
{
    public string Id { get; }
    public string DisplayName { get; }
    public string Label { get; }
    public bool IsActive { get; }

    /*
     * Name: EndpointChoice
     * Input: Endpoint snapshot and friendly selection label.
     * Process: Copies immutable discovery values.
     * Output: A choice independent of native lifetime.
     */
    internal EndpointChoice(AudioEndpoint endpoint, string label)
    {
        Id = endpoint.Id;
        DisplayName = endpoint.DisplayName;
        Label = label;
        IsActive = endpoint.State == AudioHausEndpointState.Active;
    }
}

/*
 * Name: ApplicationChoice
 * Input: Active application PID, friendly name, and observed playback endpoints.
 * Process: Presents best-effort discovery; native capture checks process identity again.
 * Output: A process selection that may become stale after discovery.
 */
public sealed record ApplicationChoice(uint ProcessId, string Label, string EndpointText);

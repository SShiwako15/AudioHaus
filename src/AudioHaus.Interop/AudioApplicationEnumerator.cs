using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioHaus.Interop;

/*
 * Name: AudioApplicationSession
 * Input: Active render-session PID and endpoint snapshot.
 * Process: Associates a best-effort process name with its actual playback destination.
 * Output: Discovery information; a PID may exit or render on several endpoints.
 */
public sealed record AudioApplicationSession(uint ProcessId, string ProcessName, AudioEndpoint Endpoint)
{
    public ulong CreationTime { get; init; }
    public string ApplicationIdentity { get; init; } = string.Empty;
    public ProcessIdentity Identity => new(ProcessId, CreationTime);
}

/*
 * Name: AudioApplicationEnumerator
 * Input: Current Windows audio sessions.
 * Process: Discovers active application PIDs on each active render endpoint.
 * Output: Read-only snapshots; inactive/paused apps may be absent.
 */
public sealed class AudioApplicationEnumerator
{
    /*
     * Name: Enumerate
     * Input: None.
     * Process: Retries bounded native PID buffers and resolves best-effort process names.
     * Output: Active sessions; native discovery errors throw with HRESULT details.
     */
    public IReadOnlyList<AudioApplicationSession> Enumerate()
        => Enumerate(new AudioEndpointEnumerator().Enumerate());

    /*
     * Name: Enumerate
     * Input: Endpoint snapshot already obtained by the caller.
     * Process: Reads one native session batch and joins by opaque endpoint identity.
     * Output: Active sessions with lifetime/app identities; discovery failures throw.
     */
    public IReadOnlyList<AudioApplicationSession> Enumerate(IReadOnlyList<AudioEndpoint> endpoints)
    {
        var sessions = new List<AudioApplicationSession>();
        AudioHausResult result = NativeApplicationMethods.CreateSnapshot(out IntPtr pointer, out uint count, out uint error);
        using var snapshot = new SafeSessionSnapshot(pointer);
        AudioRouter.CheckResult(result, error, "Discover application sessions");
        for (uint index = 0; index < count; ++index)
        {
            result = NativeApplicationMethods.GetSession(snapshot, index, out NativeSessionInfo info, null, 0, null, 0);
            if (result != AudioHausResult.BufferTooSmall) { AudioRouter.CheckResult(result, 0, "Read session metadata"); }
            var endpointText = new StringBuilder(checked((int)info.EndpointCapacity));
            var identityText = new StringBuilder(checked((int)info.IdentityCapacity));
            result = NativeApplicationMethods.GetSession(snapshot, index, out info, endpointText, info.EndpointCapacity, identityText, info.IdentityCapacity);
            AudioRouter.CheckResult(result, 0, "Read session identity");
            AudioEndpoint? endpoint = endpoints.FirstOrDefault(item => item.Id == endpointText.ToString());
            if (endpoint is null) { continue; }
            string identity = identityText.ToString();
            string name = Path.GetFileNameWithoutExtension(identity.Split('|')[0]);
            sessions.Add(new AudioApplicationSession(info.ProcessId, name, endpoint)
                { CreationTime = info.CreationTime, ApplicationIdentity = identity });
        }
        return sessions.AsReadOnly();
    }
}

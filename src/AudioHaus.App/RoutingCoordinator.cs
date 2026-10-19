using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioHaus.Interop;

namespace AudioHaus.App;

/*
 * Name: ConnectionRecord
 * Input: Process lifetime, endpoint identity, and friendly names.
 * Process: Identifies a confirmed routing connection independently of available cards.
 * Output: Stable connection data for inactive-source and disconnected-device views.
 */
public sealed record ConnectionRecord(ProcessIdentity Source, string EndpointId, string SourceName, string OutputName);

/*
 * Name: SourceTelemetry / OutputTelemetry / RoutingTelemetry
 * Input: Copied native snapshots and per-resource query errors.
 * Process: Carries telemetry across the MTA/UI boundary without ownership.
 * Output: Immutable observations, including resources temporarily recovering.
 */
public sealed record SourceTelemetry(ProcessIdentity Identity, uint Id, RoutingSourceStatus? Status, ProcessCaptureStatus? Capture, string? Error)
{ public SourceSignal? Signal { get; init; } }
public sealed record OutputTelemetry(string EndpointId, uint Id, RoutingOutputStatus? Status, OutputTimingStatus? Timing, string? Error);
public sealed record RoutingTelemetry(SourceTelemetry[] Sources, OutputTelemetry[] Outputs);

/*
 * Name: RoutingCoordinator
 * Input: Application lifetimes, active endpoints, and requested connections.
 * Process: Coordinates policy leases, recovery intent, and the existing native graph.
 * Output: Transactional automatic routing with deterministic restoration and cleanup.
 */
public sealed class RoutingCoordinator
{
    private readonly AudioService service = new();
    private readonly SemaphoreSlim structural = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string? settingsDirectory;
    private RoutingSettings? settings;
    private readonly Dictionary<ProcessIdentity, ManagedSource> sources = new();
    private readonly Dictionary<string, ManagedOutput> outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PolicyLease> leases = new(StringComparer.OrdinalIgnoreCase);
    private ManagedSource[] publishedSources = Array.Empty<ManagedSource>();
    private ManagedOutput[] publishedOutputs = Array.Empty<ManagedOutput>();
    private ConnectionRecord[] connections = Array.Empty<ConnectionRecord>();
    private AudioApplicationSession[] latestSessions = Array.Empty<AudioApplicationSession>();
    private bool closing;
    private Task? shutdown;
    public string? ParkingEndpointId => settings?.ParkingEndpointId;
    public uint PolicyHResult { get; private set; }
    public string RecoveryMessage { get; private set; } = string.Empty;
    public IReadOnlyList<ConnectionRecord> Connections => Volatile.Read(ref connections);

    /*
     * Name: RoutingCoordinator
     * Input: Optional isolated settings directory for validation.
     * Process: Creates asynchronous ownership without changing Windows preferences.
     * Output: Empty coordinator, initialized by InitializeAsync.
     */
    public RoutingCoordinator(string? settingsDirectory = null) { this.settingsDirectory = settingsDirectory; }

    /*
     * Name: InitializeAsync
     * Input: None.
     * Process: Starts native ownership, loads recovery data, and checks policy capability.
     * Output: Initialized coordinator; failures occur before application output changes.
     */
    public async Task InitializeAsync()
    {
        await service.InitializeAsync();
        settings = await service.ExecuteAsync(_ => new RoutingSettings(settingsDirectory));
        PolicyHResult = await service.ExecuteAsync(_ => ApplicationRoutingPolicy.Probe());
    }

    /*
     * Name: DiscoverAsync
     * Input: None.
     * Process: Reads a batched discovery result on the audio owner.
     * Output: Copied endpoints/sessions; no available-list mutation occurs here.
     */
    internal async Task<DiscoverySnapshot> DiscoverAsync()
    {
        DiscoverySnapshot snapshot = await service.DiscoverAsync();
        if (snapshot.ApplicationError is null) { Volatile.Write(ref latestSessions, snapshot.Applications.ToArray()); }
        return snapshot;
    }

    /*
     * Name: RefreshAsync
     * Input: None.
     * Process: Refreshes discovery and lifecycle recovery for a non-UI host.
     * Output: Updated ownership/restoration state; snapshot failures leave existing routes intact.
     */
    public async Task RefreshAsync()
    {
        DiscoverySnapshot snapshot = await DiscoverAsync();
        if (snapshot.ApplicationError is not null) { throw new InvalidOperationException(snapshot.ApplicationError); }
        await ReconcileAsync(snapshot);
    }

    /*
     * Name: ReconcileAsync
     * Input: Successful discovery snapshot.
     * Process: Selects a unique stereo cable, cleans exited sources, and retries saved restorations.
     * Output: Updated ownership/recovery state; busy structural operations defer this pass.
     */
    internal async Task ReconcileAsync(DiscoverySnapshot snapshot)
    {
        if (closing || settings is null || !await structural.WaitAsync(0)) { return; }
        try
        {
            if (settings.ParkingEndpointId is null)
            {
                AudioEndpoint[] candidates = snapshot.Endpoints.Where(item => item.State == AudioHausEndpointState.Active &&
                    item.DisplayName.StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase) &&
                    item.DisplayName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length == 1) { settings.ParkingEndpointId = candidates[0].Id; await SaveAsync(); }
            }
            foreach (ManagedSource source in sources.Values.Where(item => item.Process.HasExited).ToArray())
            { await RemoveSourceAsync(source); }
            await RemoveUnusedOutputsAsync(); Publish();
            RecoveryMessage = string.Empty;
            foreach (RestorationRecord record in settings.Restorations.ToArray())
            {
                if (leases.ContainsKey(record.ApplicationIdentity)) { continue; }
                await TryRestoreAsync(record, null);
            }
            if (RecoveryMessage.Length == 0 && settings.Restorations.Any(item => !leases.ContainsKey(item.ApplicationIdentity)))
            { RecoveryMessage = "A previous output restoration is waiting for that application to run again."; }
        }
        finally { structural.Release(); }
    }

    /*
     * Name: SetParkingEndpointAsync
     * Input: Exact active parking endpoint ID.
     * Process: Changes infrastructure only when no source is controlled.
     * Output: Saved cable identity; conflicting live routes fail without changes.
     */
    public async Task SetParkingEndpointAsync(string endpointId)
    {
        await structural.WaitAsync();
        try
        {
            CheckReady();
            if (sources.Count != 0) { throw new InvalidOperationException("Disconnect all sources before changing the parking cable."); }
            settings!.ParkingEndpointId = endpointId; await SaveAsync();
        }
        finally { structural.Release(); }
    }

    /*
     * Name: ConnectAsync
     * Input: Discovered process identity and exact active output ID.
     * Process: Validates, acquires durable policy leases, verifies parking, and starts one route.
     * Output: Confirmed connection; partial graph/policy changes are rolled back on failure.
     */
    public async Task ConnectAsync(ProcessIdentity identity, string endpointId, double sourceGain = 1, bool sourceMuted = false,
        double outputGain = 1, bool outputMuted = false, double outputDelayMilliseconds = 0, bool outputDrift = true)
    {
        if (!double.IsFinite(sourceGain) || sourceGain < 0 || sourceGain > 1) { throw new ArgumentOutOfRangeException(nameof(sourceGain)); }
        if (!double.IsFinite(outputGain) || outputGain < 0 || outputGain > 1) { throw new ArgumentOutOfRangeException(nameof(outputGain)); }
        if (!double.IsFinite(outputDelayMilliseconds) || outputDelayMilliseconds < 0 || outputDelayMilliseconds > 2000)
        { throw new ArgumentOutOfRangeException(nameof(outputDelayMilliseconds)); }
        await structural.WaitAsync(lifetime.Token);
        try
        {
            CheckReady();
            if (PolicyHResult != 0) { throw new NotSupportedException($"Automatic application switching is unavailable (HRESULT 0x{PolicyHResult:X8})."); }
            string parking = settings!.ParkingEndpointId ?? throw new InvalidOperationException("Choose an installed stereo virtual cable in Settings first.");
            DiscoverySnapshot fresh = await DiscoverAsync();
            if (fresh.ApplicationError is not null) { throw new InvalidOperationException(fresh.ApplicationError); }
            AudioEndpoint endpoint = fresh.Endpoints.FirstOrDefault(item => item.Id == endpointId && item.State == AudioHausEndpointState.Active)
                ?? throw new InvalidOperationException("That output is no longer connected.");
            if (endpointId == parking) { throw new InvalidOperationException("The parking cable cannot also be a playback destination."); }
            if (!fresh.Endpoints.Any(item => item.Id == parking && item.State == AudioHausEndpointState.Active))
            { throw new InvalidOperationException("The configured parking cable is unavailable. Enable it or choose a cable in Settings."); }
            AudioApplicationSession app = fresh.Applications.FirstOrDefault(item => item.Identity == identity)
                ?? throw new InvalidOperationException("That source is no longer active. Start its audio and select it again.");
            if (Connections.Any(item => item.Source == identity && item.EndpointId == endpointId)) { return; }
            if ((!sources.ContainsKey(identity) && sources.Count >= 8) || (!outputs.ContainsKey(endpointId) && outputs.Count >= 8))
            { throw new InvalidOperationException("AudioHaus currently supports eight managed sources and eight outputs at a time."); }
            foreach (ManagedSource existing in sources.Values.Where(item => item.Identity != identity))
            {
                bool overlap = await service.ExecuteAsync(_ => ApplicationRoutingPolicy.ContainsProcess(existing.Identity.ProcessId, identity.ProcessId) ||
                    ApplicationRoutingPolicy.ContainsProcess(identity.ProcessId, existing.Identity.ProcessId));
                if (overlap) { throw new InvalidOperationException("This process tree is already controlled through another source."); }
            }
            bool newOutput = false, newSource = false;
            ManagedSource? source = null;
            ManagedOutput? output = null;
            var acquired = new List<string>();
            Process? process = null;
            uint openedSource = 0;
            try
            {
                if (!outputs.TryGetValue(endpointId, out output))
                {
                    uint id = await service.ExecuteAsync(router => router.AddOutput(endpointId));
                    output = new ManagedOutput(endpointId, endpoint.DisplayName, id); outputs.Add(endpointId, output); newOutput = true;
                    await service.ExecuteAsync(router =>
                    {
                        router.SetOutputLevel(id, outputGain, outputMuted);
                        if (outputDelayMilliseconds != 0) { router.SetOutputDelay(id, TimeSpan.FromMilliseconds(outputDelayMilliseconds)); }
                        if (!outputDrift) { router.SetDriftCorrection(id, false); }
                    });
                }
                if (!sources.TryGetValue(identity, out source))
                {
                    process = OpenIdentity(identity);
                    var members = new List<AudioApplicationSession> { app };
                    foreach (AudioApplicationSession candidate in fresh.Applications.GroupBy(item => item.Identity).Select(item => item.First()))
                    {
                        if (candidate.Identity != identity && await service.ExecuteAsync(_ => ApplicationRoutingPolicy.ContainsProcess(identity.ProcessId, candidate.ProcessId)))
                        { members.Add(candidate); }
                    }
                    foreach (AudioApplicationSession member in members.GroupBy(item => item.ApplicationIdentity, StringComparer.OrdinalIgnoreCase).Select(item => item.First()))
                    {
                        await AcquireLeaseAsync(member, identity, parking); acquired.Add(member.ApplicationIdentity);
                    }
                    var wait = Stopwatch.StartNew();
                    while (true)
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        uint error = await service.ExecuteAsync(_ => ApplicationRoutingPolicy.CheckParking(identity.ProcessId, parking));
                        if (error == 0) { break; }
                        if (wait.Elapsed >= TimeSpan.FromSeconds(5))
                        { throw new InvalidOperationException($"The app did not move its original playback to the cable (HRESULT 0x{error:X8}). Restart its playback; apps pinned to an explicit device may not support automatic switching."); }
                        await Task.Delay(100, lifetime.Token);
                    }
                    openedSource = await service.ExecuteAsync(router => router.AddProcessSource(identity.ProcessId, parking));
                    await service.ExecuteAsync(router => router.SetSourceLevel(openedSource, sourceGain, sourceMuted));
                    source = new ManagedSource(identity, app.ProcessName, openedSource, process, acquired.ToArray());
                    sources.Add(identity, source); process = null; newSource = true;
                }
                await service.ExecuteAsync(router => { router.Connect(source.Id, output.Id, 1, false); router.Play(source.Id); });
                source.Targets.Add(endpointId);
                Publish();
            }
            catch
            {
                if (source is not null && output is not null)
                { try { await service.ExecuteAsync(router => router.Disconnect(source.Id, output.Id)); } catch { } }
                if (newSource && source is not null) { await RemoveSourceAsync(source); }
                else if (source is null)
                {
                    if (openedSource != 0) { await service.ExecuteAsync(router => router.RemoveSource(openedSource)); }
                    foreach (string key in acquired) { await ReleaseLeaseAsync(key, identity); }
                }
                if (newOutput && output is not null && outputs.ContainsKey(endpointId) && !sources.Values.Any(item => item.Targets.Contains(endpointId)))
                { await service.ExecuteAsync(router => router.RemoveOutput(output.Id)); outputs.Remove(endpointId); }
                process?.Dispose(); Publish(); throw;
            }
        }
        finally { structural.Release(); }
    }

    /*
     * Name: DisconnectAsync
     * Input: Confirmed source lifetime and output ID.
     * Process: Removes one route; final removal releases capture and app-policy ownership.
     * Output: Independent disconnection and previous-output restoration where Windows permits it.
     */
    public async Task DisconnectAsync(ProcessIdentity identity, string endpointId)
    {
        await structural.WaitAsync();
        try
        {
            if (!sources.TryGetValue(identity, out ManagedSource? source) || !source.Targets.Contains(endpointId)) { return; }
            await service.ExecuteAsync(router => router.Disconnect(source.Id, outputs[endpointId].Id));
            source.Targets.Remove(endpointId);
            if (source.Targets.Count == 0) { await RemoveSourceAsync(source); }
            await RemoveUnusedOutputsAsync(); Publish();
        }
        finally { structural.Release(); }
    }

    /*
     * Name: SetSourceLevelAsync / SetOutputLevelAsync
     * Input: Managed identity and desired normalized gain/mute.
     * Process: Dispatches only a level operation to the MTA/native owner.
     * Output: Acknowledgment or native failure; no structural lock delays telemetry.
     */
    public Task SetSourceLevelAsync(ProcessIdentity identity, double gain, bool muted)
    {
        ManagedSource source = Volatile.Read(ref publishedSources).FirstOrDefault(item => item.Identity == identity)
            ?? throw new InvalidOperationException("Connect this source before changing its volume.");
        return service.ExecuteAsync(router => router.SetSourceLevel(source.Id, gain, muted));
    }
    public Task SetOutputLevelAsync(string endpointId, double gain, bool muted)
    {
        ManagedOutput output = Volatile.Read(ref publishedOutputs).FirstOrDefault(item => item.EndpointId == endpointId)
            ?? throw new InvalidOperationException("Connect this output before changing its volume.");
        return service.ExecuteAsync(router => router.SetOutputLevel(output.Id, gain, muted));
    }

    /*
     * Name: ConfigureOutputAsync
     * Input: Output identity, master target, delay milliseconds, and drift policy.
     * Process: Applies controls on one owner; unchanged timing does not rebuffer audio.
     * Output: Acknowledged controls or validation/native error.
     */
    public Task ConfigureOutputAsync(string endpointId, double gain, bool muted, double delayMilliseconds, bool drift)
    {
        if (!double.IsFinite(gain) || gain < 0 || gain > 1 || !double.IsFinite(delayMilliseconds) || delayMilliseconds < 0 || delayMilliseconds > 2000)
        { throw new ArgumentOutOfRangeException(nameof(delayMilliseconds)); }
        ManagedOutput output = Volatile.Read(ref publishedOutputs).FirstOrDefault(item => item.EndpointId == endpointId)
            ?? throw new InvalidOperationException("Connect this output before changing its controls.");
        return service.ExecuteAsync(router =>
        {
            router.SetOutputLevel(output.Id, gain, muted);
            OutputTimingStatus timing = router.GetOutputTiming(output.Id);
            if (Math.Abs(timing.Delay.TotalMilliseconds - delayMilliseconds) > 0.02) { router.SetOutputDelay(output.Id, TimeSpan.FromMilliseconds(delayMilliseconds)); }
            if (timing.DriftCorrectionEnabled != drift) { router.SetDriftCorrection(output.Id, drift); }
        });
    }

    /*
     * Name: ReadTelemetryAsync
     * Input: Last published immutable resource identities.
     * Process: Queries each resource separately, tolerating removal races and outages.
     * Output: Immutable status/error rows; one failure does not suppress other meters.
     */
    public Task<RoutingTelemetry> ReadTelemetryAsync()
    {
        ManagedSource[] sourceRows = Volatile.Read(ref publishedSources);
        ManagedOutput[] outputRows = Volatile.Read(ref publishedOutputs);
        return service.ExecuteAsync(router => new RoutingTelemetry(sourceRows.Select(source =>
        {
            try { return new SourceTelemetry(source.Identity, source.Id, router.GetSourceStatus(source.Id), router.GetCaptureStatus(source.Id), null)
                { Signal = router.GetSourceSignal(source.Id) }; }
            catch (Exception error) { return new SourceTelemetry(source.Identity, source.Id, null, null, error.Message); }
        }).ToArray(), outputRows.Select(output =>
        {
            try { return new OutputTelemetry(output.EndpointId, output.Id, router.GetOutputStatus(output.Id), router.GetOutputTiming(output.Id), null); }
            catch (Exception error) { return new OutputTelemetry(output.EndpointId, output.Id, null, null, error.Message); }
        }).ToArray()));
    }

    /*
     * Name: ShutdownAsync
     * Input: Window closure or repeated shutdown request.
     * Process: Cancels pending Connect waits, drains structural work, restores leases, then stops native ownership.
     * Output: Shared completion; failed restoration records remain durable for recovery.
     */
    public Task ShutdownAsync()
    {
        if (shutdown is not null) { return shutdown; }
        closing = true; lifetime.Cancel(); return shutdown = ShutdownCoreAsync();
    }
    /*
     * Name: ShutdownCoreAsync
     * Input: Pending structural work and owned resources.
     * Process: Stops native playback before releasing all preference owners and process handles.
     * Output: Complete cleanup; independent cleanup failures are collected.
     */
    private async Task ShutdownCoreAsync()
    {
        await structural.WaitAsync();
        var failures = new List<Exception>();
        try
        {
            if (sources.Count != 0 || outputs.Count != 0)
            {
                try { await service.ExecuteAsync(router => router.Dispose()); }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (PolicyLease lease in leases.Values.ToArray())
            {
                foreach (ProcessIdentity owner in lease.Owners.ToArray())
                {
                    try { await ReleaseLeaseAsync(lease.Record.ApplicationIdentity, owner); }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            foreach (ManagedSource source in sources.Values) { source.Process.Dispose(); }
            sources.Clear(); outputs.Clear(); Publish();
        }
        finally { structural.Release(); await service.ShutdownAsync(); }
        if (failures.Count != 0) { throw new AggregateException("Audio cleanup could not complete every operation.", failures); }
    }

    /*
     * Name: CheckReady
     * Input: Coordinator lifecycle.
     * Process: Rejects operations after closure or before initialization.
     * Output: Returns when ready; otherwise throws without mutation.
     */
    private void CheckReady()
    {
        if (closing) { throw new ObjectDisposedException(nameof(RoutingCoordinator)); }
        if (settings is null) { throw new InvalidOperationException("AudioHaus is not initialized."); }
    }

    /*
     * Name: OpenIdentity
     * Input: PID and creation FILETIME.
     * Process: Opens and retains the process only if its lifetime still matches.
     * Output: Owned live process; missing, exited, or reused identities throw.
     */
    private static Process OpenIdentity(ProcessIdentity identity)
    {
        Process process = Process.GetProcessById(checked((int)identity.ProcessId));
        try
        {
            _ = process.SafeHandle;
            if (process.HasExited || (ulong)process.StartTime.ToFileTimeUtc() != identity.CreationTime)
            { throw new InvalidOperationException("The selected process has exited or its PID has been reused."); }
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    /*
     * Name: AcquireLeaseAsync
     * Input: Application identity, route owner, and cable ID.
     * Process: Shares existing ownership or journals all original roles before switching.
     * Output: Reference-counted policy lease; failed acquisition restores changes.
     */
    private async Task AcquireLeaseAsync(AudioApplicationSession app, ProcessIdentity owner, string parking)
    {
        if (leases.TryGetValue(app.ApplicationIdentity, out PolicyLease? existing)) { existing.Owners.Add(owner); return; }
        Process process = OpenIdentity(app.Identity);
        bool transferred = false;
        try
        {
            RestorationRecord? record = settings!.Restorations.FirstOrDefault(item => string.Equals(item.ApplicationIdentity, app.ApplicationIdentity, StringComparison.OrdinalIgnoreCase));
            if (record is not null)
            {
                await TryRestoreAsync(record, process);
                if (settings.Restorations.Contains(record)) { throw new InvalidOperationException("Previous output restoration must finish before this app can be routed again."); }
            }
            string[] preferences = await service.ExecuteAsync(_ => Enumerable.Range(0, 3).Select(role => ApplicationRoutingPolicy.ReadPreference(app.ProcessId, (uint)role)).ToArray());
            record = new RestorationRecord(app.ApplicationIdentity, preferences, ApplicationRoutingPolicy.EndpointPreference(parking));
            settings.Restorations.Add(record);
            try { await SaveAsync(); }
            catch { settings.Restorations.Remove(record); throw; }
            var lease = new PolicyLease(record, process); lease.Owners.Add(owner); leases.Add(app.ApplicationIdentity, lease);
            transferred = true;
            try
            {
                await service.ExecuteAsync(_ => { for (uint role = 0; role < 3; ++role) { ApplicationRoutingPolicy.WritePreference(app.ProcessId, role, record.ParkingPreference); } });
            }
            catch { await ReleaseLeaseAsync(app.ApplicationIdentity, owner); throw; }
        }
        finally { if (!transferred) { process.Dispose(); } }
    }

    /*
     * Name: ReleaseLeaseAsync
     * Input: Application key and route owner.
     * Process: Releases one owner and restores preferences after the final release.
     * Output: Restored output or durable pending recovery; closes the retained handle.
     */
    private async Task ReleaseLeaseAsync(string key, ProcessIdentity owner)
    {
        if (!leases.TryGetValue(key, out PolicyLease? lease)) { return; }
        lease.Owners.Remove(owner);
        if (lease.Owners.Count != 0) { return; }
        leases.Remove(key);
        try { await TryRestoreAsync(lease.Record, lease.Process); }
        finally { lease.Process.Dispose(); }
    }

    /*
     * Name: TryRestoreAsync
     * Input: Saved original roles and optional retained process.
     * Process: Restores only preferences still owned by AudioHaus through a live app identity.
     * Output: Completed journal entry removed; failures remain saved and visible.
     */
    private async Task TryRestoreAsync(RestorationRecord record, Process? retained)
    {
        Process? temporary = null;
        try
        {
            Process? target = retained is not null && !retained.HasExited ? retained : null;
            if (target is null)
            {
                AudioApplicationSession? candidate = Volatile.Read(ref latestSessions).FirstOrDefault(item =>
                    string.Equals(item.ApplicationIdentity, record.ApplicationIdentity, StringComparison.OrdinalIgnoreCase));
                if (candidate is null) { return; }
                target = temporary = OpenIdentity(candidate.Identity);
            }
            uint pid = (uint)target.Id;
            await service.ExecuteAsync(_ =>
            {
                for (uint role = 0; role < 3; ++role)
                {
                    string current = ApplicationRoutingPolicy.ReadPreference(pid, role);
                    if (string.Equals(current, record.ParkingPreference, StringComparison.OrdinalIgnoreCase))
                    { ApplicationRoutingPolicy.WritePreference(pid, role, record.Preferences[role]); }
                }
            });
            settings!.Restorations.Remove(record);
            try { await SaveAsync(); }
            catch { settings.Restorations.Add(record); throw; }
        }
        catch (Exception error) { RecoveryMessage = "Output restoration is pending: " + error.Message; }
        finally { temporary?.Dispose(); }
    }

    /*
     * Name: RemoveSourceAsync
     * Input: Managed source.
     * Process: Stops capture, removes its graph identity, and releases its policy owners.
     * Output: Other sources continue; native removal failures propagate.
     */
    private async Task RemoveSourceAsync(ManagedSource source)
    {
        await service.ExecuteAsync(router => router.RemoveSource(source.Id));
        sources.Remove(source.Identity);
        try { foreach (string key in source.LeaseKeys) { await ReleaseLeaseAsync(key, source.Identity); } }
        finally { source.Process.Dispose(); }
        Publish();
    }

    /*
     * Name: RemoveUnusedOutputsAsync
     * Input: Current source destinations.
     * Process: Closes outputs with no retained routes.
     * Output: Unused outputs removed; native failures propagate.
     */
    private async Task RemoveUnusedOutputsAsync()
    {
        foreach (ManagedOutput output in outputs.Values.Where(item => !sources.Values.Any(source => source.Targets.Contains(item.EndpointId))).ToArray())
        { await service.ExecuteAsync(router => router.RemoveOutput(output.Id)); outputs.Remove(output.EndpointId); }
    }

    /*
     * Name: SaveAsync
     * Input: Current in-memory settings.
     * Process: Persists restoration intent on the service worker.
     * Output: Completion or storage failure before further policy changes.
     */
    private Task SaveAsync() => service.ExecuteAsync(_ => settings!.Save());

    /*
     * Name: Publish
     * Input: Current managed resources and destinations.
     * Process: Copies graph identities into atomically published arrays.
     * Output: Stable snapshots for telemetry and UI reconciliation.
     */
    private void Publish()
    {
        Volatile.Write(ref publishedSources, sources.Values.ToArray());
        Volatile.Write(ref publishedOutputs, outputs.Values.ToArray());
        Volatile.Write(ref connections, sources.Values.SelectMany(source => source.Targets.Select(endpoint =>
            new ConnectionRecord(source.Identity, endpoint, source.Name, outputs[endpoint].Name))).ToArray());
    }

    /*
     * Name: ManagedOutput
     * Input: Endpoint ID, friendly name, and native ID.
     * Process: Keeps native resource ownership separate from available cards.
     * Output: Identity of one managed mixer.
     */
    private sealed record ManagedOutput(string EndpointId, string Name, uint Id);
    /*
     * Name: ManagedSource
     * Input: Process lifetime, native ID, retained handle, and lease keys.
     * Process: Keeps destinations and policy ownership for one capture.
     * Output: Managed source retained across temporary device/session loss.
     */
    private sealed record ManagedSource(ProcessIdentity Identity, string Name, uint Id, Process Process, string[] LeaseKeys)
    { public HashSet<string> Targets { get; } = new(StringComparer.OrdinalIgnoreCase); }
    /*
     * Name: PolicyLease
     * Input: Durable restoration record and retained live process.
     * Process: Tracks all captures sharing one Windows application preference.
     * Output: Ownership released only after the final instance disconnects.
     */
    private sealed record PolicyLease(RestorationRecord Record, Process Process)
    { public HashSet<ProcessIdentity> Owners { get; } = new(); }
}

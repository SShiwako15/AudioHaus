using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioHaus.Interop;

namespace AudioHaus.App;

/*
 * Name: ConnectionViewModel
 * Input: Confirmed connection and current availability.
 * Process: Keeps management accessible when an available card disappears.
 * Output: Labeled color, waiting status, and independent Disconnect command.
 */
public sealed class ConnectionViewModel : ObservableObject
{
    public ConnectionRecord Record { get; }
    public Brush Color { get; }
    public string Label => $"{Record.SourceName} (PID {Record.Source.ProcessId}) → {Record.OutputName}";
    private string state = "Connected";
    public string StateText { get => state; internal set => SetProperty(ref state, value); }
    public ICommand DisconnectCommand { get; }
    /*
     * Name: ConnectionViewModel
     * Input: Connection, color, command callbacks, and availability.
     * Process: Creates a retained connection row independently of available cards.
     * Output: Inspectable route with a guarded Disconnect command.
     */
    internal ConnectionViewModel(ConnectionRecord record, Brush color, Func<Task> disconnect, Func<bool> available, Action<Exception> error)
    { Record = record; Color = color; DisconnectCommand = new AsyncCommand(disconnect, available, error); }
}

/*
 * Name: MainViewModel
 * Input: Periodic discovery and user source/device selections.
 * Process: Reconciles active cards with the automatic routing coordinator.
 * Output: An app router with selection, master controls, and retained connections.
 */
public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly RoutingCoordinator coordinator;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<ProcessIdentity, SourceCardViewModel> sourceCache = new();
    private readonly Dictionary<string, OutputCardViewModel> outputCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> pending = new();
    private Task? polling, initialization, shutdown;
    private bool closing, ready, busy;
    private SourceCardViewModel? selectedSource;
    private OutputCardViewModel? selectedOutput;
    private EndpointChoice? selectedParking;
    private string status = "Starting AudioHaus…", error = string.Empty, discoveryMessage = string.Empty, parkingName = "No cable selected";
    public ObservableCollection<SourceCardViewModel> Sources { get; } = new();
    public ObservableCollection<OutputCardViewModel> Outputs { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    public ObservableCollection<EndpointChoice> ParkingEndpoints { get; } = new();
    public SourceCardViewModel? SelectedSource => selectedSource;
    public OutputCardViewModel? SelectedOutput => selectedOutput;
    public EndpointChoice? SelectedParkingEndpoint
    { get => selectedParking; set { if (SetProperty(ref selectedParking, value)) { RefreshCommands(); } } }
    public bool IsReady { get => ready; private set => SetProperty(ref ready, value); }
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) { RefreshCommands(); } } }
    public string StatusMessage { get => status; private set => SetProperty(ref status, value); }
    public string ErrorMessage { get => error; private set { if (SetProperty(ref error, value)) { OnPropertyChanged(nameof(HasError)); } } }
    public bool HasError => ErrorMessage.Length != 0;
    public string DiscoveryMessage { get => discoveryMessage; private set => SetProperty(ref discoveryMessage, value); }
    public string ParkingName { get => parkingName; private set => SetProperty(ref parkingName, value); }
    public string SelectionText => selectedSource is null ? "Select an application" : selectedOutput is null ? "Select an output" : $"{selectedSource.Name} → {selectedOutput.Name}";
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ApplyParkingCommand { get; }
    public ICommand DismissErrorCommand { get; }

    /*
     * Name: MainViewModel
     * Input: None.
     * Process: Creates the production routing coordinator and binds UI commands.
     * Output: Uninitialized desktop model; no Windows preferences changed.
     */
    public MainViewModel() : this(new RoutingCoordinator()) { }
    /*
     * Name: MainViewModel
     * Input: Coordinator owned by this desktop model.
     * Process: Binds commands, readiness guards, and visible error handling.
     * Output: Uninitialized model with serialized structural actions.
     */
    public MainViewModel(RoutingCoordinator coordinator)
    {
        this.coordinator = coordinator;
        ConnectCommand = new AsyncCommand(ConnectSelectedAsync, CanConnect, failure => ReportError("Connect", failure));
        DisconnectCommand = new AsyncCommand(DisconnectSelectedAsync, CanDisconnect, failure => ReportError("Disconnect", failure));
        ApplyParkingCommand = new AsyncCommand(ApplyParkingAsync, () => IsReady && !closing && !IsBusy && selectedParking?.IsActive == true && Connections.Count == 0,
            failure => ReportError("Configure cable", failure));
        DismissErrorCommand = new AsyncCommand(() => { ErrorMessage = string.Empty; return Task.CompletedTask; }, () => !closing, failure => ReportError("Dismiss error", failure));
    }

    /*
     * Name: InitializeAsync
     * Input: None.
     * Process: Initializes audio and one bounded discovery/telemetry loop.
     * Output: Ready active lists or a visible initialization error.
     */
    public Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    /*
     * Name: InitializeCoreAsync
     * Input: Window lifecycle.
     * Process: Initializes discovery and starts polling on the owning dispatcher.
     * Output: Active lists or visible startup failure.
     */
    private async Task InitializeCoreAsync()
    {
        dispatcher.VerifyAccess();
        try
        {
            await coordinator.InitializeAsync();
            if (closing) { return; }
            IsReady = true;
            await DiscoverAsync();
            StatusMessage = "Select an application and an output, then Connect.";
            if (coordinator.PolicyHResult != 0) { ErrorMessage = $"Automatic switching is unavailable (HRESULT 0x{coordinator.PolicyHResult:X8})."; }
            if (!closing) { polling = PollAsync(); RefreshCommands(); }
        }
        catch (Exception failure) { ReportError("Initialize AudioHaus", failure); }
    }

    /*
     * Name: SelectCard
     * Input: Available source/output selected by mouse, keyboard, or automation.
     * Process: Retains one selection on each side without changing audio.
     * Output: Action availability and selection styling independent of connection color.
     */
    public void SelectCard(AudioCardViewModel card)
    {
        if (closing) { return; }
        if (card is SourceCardViewModel source && Sources.Contains(source))
        {
            if (selectedSource is not null) { selectedSource.IsSelected = false; }
            selectedSource = source; source.IsSelected = true; OnPropertyChanged(nameof(SelectedSource));
        }
        if (card is OutputCardViewModel output && Outputs.Contains(output))
        {
            if (selectedOutput is not null) { selectedOutput.IsSelected = false; }
            selectedOutput = output; output.IsSelected = true; OnPropertyChanged(nameof(SelectedOutput));
        }
        OnPropertyChanged(nameof(SelectionText)); RefreshCommands();
    }

    /*
     * Name: CanConnect
     * Input: Readiness and selected cards.
     * Process: Checks capability, cable selection, and whether the pair exists.
     * Output: True only for an available unconnected pair.
     */
    private bool CanConnect() => IsReady && !closing && !IsBusy && selectedSource is not null && selectedOutput is not null &&
        coordinator.PolicyHResult == 0 && coordinator.ParkingEndpointId is not null && !ConnectedPair();
    /*
     * Name: CanDisconnect
     * Input: Readiness and selected cards.
     * Process: Checks whether the selected pair is an existing route.
     * Output: True when the connection can be removed.
     */
    private bool CanDisconnect() => IsReady && !closing && !IsBusy && ConnectedPair();
    /*
     * Name: ConnectedPair
     * Input: Selected process lifetime and endpoint.
     * Process: Matches the pair against retained connections.
     * Output: Whether that exact pair is connected.
     */
    private bool ConnectedPair() => selectedSource is not null && selectedOutput is not null &&
        Connections.Any(item => item.Record.Source == selectedSource.Identity && item.Record.EndpointId == selectedOutput.EndpointId);

    /*
     * Name: ConnectSelectedAsync
     * Input: Selected source/output and retained controls.
     * Process: Coordinates verified routing and acknowledges initial control targets.
     * Output: Connected cards or propagated failure; commands resume afterward.
     */
    private async Task ConnectSelectedAsync()
    {
        SourceCardViewModel source = selectedSource!; OutputCardViewModel output = selectedOutput!;
        IsBusy = true; StatusMessage = $"Connecting {source.Name} to {output.Name}…";
        try
        {
            await coordinator.ConnectAsync(source.Identity, output.EndpointId, source.VolumePercent / 100, source.Muted,
                output.VolumePercent / 100, output.Muted, output.DelayMilliseconds, output.DriftEnabled);
            if (!closing)
            {
                source.ConfirmedVolume = source.VolumePercent; source.ConfirmedMuted = source.Muted;
                output.ConfirmedVolume = output.VolumePercent; output.ConfirmedMuted = output.Muted;
                SyncConnections(); StatusMessage = "Connected. Original playback is isolated automatically.";
            }
        }
        finally { IsBusy = false; }
    }

    /*
     * Name: DisconnectSelectedAsync
     * Input: Selected connected pair.
     * Process: Delegates removal using exact process and endpoint identities.
     * Output: Asynchronous disconnection.
     */
    private Task DisconnectSelectedAsync() => DisconnectAsync(selectedSource!.Identity, selectedOutput!.EndpointId);
    /*
     * Name: DisconnectAsync
     * Input: Source lifetime and endpoint ID.
     * Process: Serializes route removal and updates retained cards.
     * Output: Disconnected pair; final source removal restores Windows preferences.
     */
    private async Task DisconnectAsync(ProcessIdentity source, string endpoint)
    {
        IsBusy = true;
        try
        {
            await coordinator.DisconnectAsync(source, endpoint);
            if (!closing) { SyncConnections(); StatusMessage = "Disconnected; final source routes restore the previous Windows output."; }
        }
        finally { IsBusy = false; }
    }
    /*
     * Name: ApplyParkingAsync
     * Input: Selected active parking endpoint.
     * Process: Saves cable identity and refreshes available output cards.
     * Output: Updated cable configuration or propagated failure.
     */
    private async Task ApplyParkingAsync()
    {
        await coordinator.SetParkingEndpointAsync(selectedParking!.Id);
        await DiscoverAsync(); StatusMessage = "Parking cable configured.";
    }

    /*
     * Name: PollAsync
     * Input: Window lifetime and independent discovery/telemetry cadence.
     * Process: Awaits each request so polling cannot overlap or accumulate.
     * Output: 250 ms meters and one-second discovery; transient failures retry.
     */
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var discoveryClock = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                if (closing) { break; }
                try
                {
                    if (discoveryClock.Elapsed >= TimeSpan.FromSeconds(1)) { await DiscoverAsync(); discoveryClock.Restart(); }
                    if (closing) { break; }
                    RoutingTelemetry telemetry = await coordinator.ReadTelemetryAsync();
                    if (closing) { break; }
                    foreach (SourceTelemetry row in telemetry.Sources)
                    { if (sourceCache.TryGetValue(row.Identity, out SourceCardViewModel? card) && card.IsManaged) { card.ApplyStatus(row); } }
                    foreach (OutputTelemetry row in telemetry.Outputs)
                    { if (outputCache.TryGetValue(row.EndpointId, out OutputCardViewModel? card) && card.IsManaged) { card.ApplyStatus(row); } }
                    UpdateConnectionStates();
                }
                catch (Exception failure) { if (!closing) { ReportError("Read routing status", failure); } }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    /*
     * Name: DiscoverAsync
     * Input: Last displayed snapshot.
     * Process: Reads and reconciles one discovery batch without clearing surviving cards.
     * Output: Fresh lists; failures preserve prior cards with a stale-state message.
     */
    private async Task DiscoverAsync()
    {
        try
        {
            DiscoverySnapshot snapshot = await coordinator.DiscoverAsync();
            if (closing) { return; }
            if (snapshot.ApplicationError is not null) { DiscoveryMessage = "Source discovery is stale: " + snapshot.ApplicationError; return; }
            await coordinator.ReconcileAsync(snapshot);
            if (closing) { return; }
            ReconcileOutputs(snapshot.Endpoints); ReconcileSources(snapshot.Applications); SyncConnections();
            string? configured = coordinator.ParkingEndpointId;
            ParkingName = snapshot.Endpoints.FirstOrDefault(item => item.Id == configured)?.DisplayName ?? "Choose a stereo virtual cable";
            DiscoveryMessage = snapshot.Endpoints.Any(item => item.Id == configured && item.State == AudioHausEndpointState.Active) ? coordinator.RecoveryMessage :
                "A connected parking cable is required. Open Settings to configure it.";
            var parking = snapshot.Endpoints.Where(item => item.State == AudioHausEndpointState.Active && !coordinator.Connections.Any(route => route.EndpointId == item.Id)).ToArray();
            string? selected = selectedParking?.Id ?? configured;
            Reconcile(ParkingEndpoints, parking.Select(endpoint => ParkingEndpoints.FirstOrDefault(item => item.Id == endpoint.Id) ?? new EndpointChoice(endpoint, endpoint.DisplayName)).ToArray());
            SelectedParkingEndpoint = ParkingEndpoints.FirstOrDefault(item => item.Id == selected);
        }
        catch (Exception failure) { if (!closing) { DiscoveryMessage = "Discovery is stale; retrying automatically. " + failure.Message; } }
    }

    /*
     * Name: ReconcileOutputs
     * Input: Discovered playback endpoints.
     * Process: Reuses active endpoint cards and session colors, excluding the parking cable.
     * Output: Current available output list with surviving selection preserved.
     */
    private void ReconcileOutputs(IReadOnlyList<AudioEndpoint> endpoints)
    {
        var desired = new List<OutputCardViewModel>();
        foreach (AudioEndpoint endpoint in endpoints.Where(item => item.State == AudioHausEndpointState.Active && item.Id != coordinator.ParkingEndpointId)
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!outputCache.TryGetValue(endpoint.Id, out OutputCardViewModel? card))
            {
                string[] colors = { "#6BD4BE", "#F3B36A", "#A99AF4", "#72B7F0", "#ED91AE", "#C8D674", "#EB9676", "#85C5DC" };
                var color = (SolidColorBrush)new BrushConverter().ConvertFromString(colors[outputCache.Count % colors.Length])!; color.Freeze();
                card = new OutputCardViewModel(endpoint, color, SelectCard, LevelChanged); outputCache.Add(endpoint.Id, card);
            }
            card.Name = endpoint.DisplayName; desired.Add(card);
        }
        Reconcile(Outputs, desired);
        if (selectedOutput is not null && !Outputs.Contains(selectedOutput)) { selectedOutput.IsSelected = false; selectedOutput = null; OnPropertyChanged(nameof(SelectedOutput)); }
    }

    /*
     * Name: ReconcileSources
     * Input: Active audio sessions.
     * Process: Groups by process lifetime and reuses cards while excluding AudioHaus.
     * Output: Current source list; inactive and replaced lifetimes disappear.
     */
    private void ReconcileSources(IReadOnlyList<AudioApplicationSession> sessions)
    {
        var desired = new List<SourceCardViewModel>();
        foreach (AudioApplicationSession app in sessions.Where(item => item.ProcessId != (uint)Environment.ProcessId)
            .GroupBy(item => item.Identity).Select(group => group.First()).OrderBy(item => item.ProcessName, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.ProcessId))
        {
            if (!sourceCache.TryGetValue(app.Identity, out SourceCardViewModel? card))
            { card = new SourceCardViewModel(app, SelectCard, LevelChanged); sourceCache.Add(app.Identity, card); }
            card.Name = app.ProcessName; desired.Add(card);
        }
        Reconcile(Sources, desired);
        if (selectedSource is not null && !Sources.Contains(selectedSource)) { selectedSource.IsSelected = false; selectedSource = null; OnPropertyChanged(nameof(SelectedSource)); }
    }

    /*
     * Name: SyncConnections
     * Input: Published routes and available cards.
     * Process: Updates resource availability, color badges, and retained connection rows.
     * Output: Consistent controls and waiting states without removing routes.
     */
    private void SyncConnections()
    {
        ConnectionRecord[] records = coordinator.Connections.ToArray();
        Reconcile(Connections, records.Select(record => Connections.FirstOrDefault(item => item.Record == record) ??
            new ConnectionViewModel(record, outputCache.TryGetValue(record.EndpointId, out OutputCardViewModel? output) ? output.Color : Brushes.Gray,
                () => DisconnectAsync(record.Source, record.EndpointId), () => !closing && !IsBusy, failure => ReportError("Disconnect", failure))).ToArray());
        foreach (SourceCardViewModel card in sourceCache.Values)
        {
            ConnectionRecord[] routes = records.Where(item => item.Source == card.Identity).ToArray(); SetManaged(card, routes.Length != 0);
            card.SetConnections(routes.Select(item => new ConnectionBadge(item.OutputName,
                outputCache.TryGetValue(item.EndpointId, out OutputCardViewModel? output) ? output.Color : Brushes.Gray)).ToArray());
        }
        foreach (OutputCardViewModel card in outputCache.Values)
        {
            ConnectionRecord[] routes = records.Where(item => item.EndpointId == card.EndpointId).ToArray(); SetManaged(card, routes.Length != 0);
            card.SetConnections(routes.Select(item => new ConnectionBadge($"{item.SourceName} · PID {item.Source.ProcessId}", card.Color)).ToArray(), card.Color);
        }
        UpdateConnectionStates();
        OnPropertyChanged(nameof(SelectionText)); RefreshCommands();
    }

    /*
     * Name: UpdateConnectionStates
     * Input: Available cards and their latest native health.
     * Process: Prioritizes waiting, recovery, and failure before reporting a connected route.
     * Output: Retained rows reflect availability and routing health together.
     */
    private void UpdateConnectionStates()
    {
        foreach (ConnectionViewModel connection in Connections)
        {
            SourceCardViewModel? source = Sources.FirstOrDefault(item => item.Identity == connection.Record.Source);
            OutputCardViewModel? output = Outputs.FirstOrDefault(item => item.EndpointId == connection.Record.EndpointId);
            connection.StateText = output is null ? "Waiting for device" : source is null ? "Waiting for source audio" :
                source.StateText == "Recovering" || output.StateText == "Recovering" ? "Recovering" :
                source.HasError || output.HasError ? "Needs attention" : "Connected";
        }
    }

    /*
     * Name: SetManaged
     * Input: Card and current resource availability.
     * Process: Enables master controls or clears stale telemetry for released resources.
     * Output: Card reflects its routing ownership.
     */
    private static void SetManaged(AudioCardViewModel card, bool value)
    {
        card.IsManaged = value;
        if (!value) { card.StateText = "Not connected"; card.Level = 0; card.LevelText = "No routed audio"; card.ErrorText = string.Empty; }
    }

    /*
     * Name: Reconcile
     * Input: Observable collection and ordered existing/new card references.
     * Process: Applies only insertions, removals, and moves.
     * Output: Current cards without clearing selection or replacing surviving objects.
     */
    private static void Reconcile<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired)
    {
        for (int index = collection.Count - 1; index >= 0; --index) { if (!desired.Contains(collection[index])) { collection.RemoveAt(index); } }
        for (int index = 0; index < desired.Count; ++index)
        {
            if (index < collection.Count && EqualityComparer<T>.Default.Equals(collection[index], desired[index])) { continue; }
            int current = collection.IndexOf(desired[index]);
            if (current >= 0) { collection.Move(current, index); } else { collection.Insert(index, desired[index]); }
        }
    }

    /*
     * Name: LevelChanged / ApplyLevelAsync
     * Input: Latest desired master revision.
     * Process: Coalesces rapid sliders and rolls back only the failed current revision.
     * Output: Native acknowledgment without detached work during shutdown.
     */
    private void LevelChanged(AudioCardViewModel card)
    {
        if (closing || !card.IsManaged || card.ApplyingLevel) { return; }
        Task task = ApplyLevelAsync(card); pending.Add(task); _ = ForgetWhenCompleteAsync(task);
    }
    /*
     * Name: ForgetWhenCompleteAsync
     * Input: Tracked control task.
     * Process: Waits for completion and removes the task from shutdown tracking.
     * Output: No completed tasks retained.
     */
    private async Task ForgetWhenCompleteAsync(Task task) { try { await task; } finally { pending.Remove(task); } }
    /*
     * Name: ApplyLevelAsync
     * Input: Latest source/output control targets.
     * Process: Applies revisions in order and rolls back only a failed current revision.
     * Output: Acknowledged master controls or visible native failure.
     */
    private async Task ApplyLevelAsync(AudioCardViewModel card)
    {
        card.ApplyingLevel = true;
        try
        {
            while (!closing && card.IsManaged)
            {
                long revision = card.Revision; double volume = card.VolumePercent; bool muted = card.Muted;
                try
                {
                    if (card is SourceCardViewModel source) { await coordinator.SetSourceLevelAsync(source.Identity, volume / 100, muted); }
                    else if (card is OutputCardViewModel output) { await coordinator.ConfigureOutputAsync(output.EndpointId, volume / 100, muted, output.DelayMilliseconds, output.DriftEnabled); }
                    card.ConfirmedVolume = volume; card.ConfirmedMuted = muted;
                }
                catch (Exception failure)
                { if (!closing && card.IsManaged && card.Revision == revision) { card.RollbackLevel(); ReportError("Set volume", failure); } }
                if (card.Revision == revision) { break; }
            }
        }
        finally { card.ApplyingLevel = false; }
    }

    /*
     * Name: RefreshCommands
     * Input: Current selection, busy state, and retained routes.
     * Process: Notifies structural and row commands that availability may have changed.
     * Output: Buttons reflect current valid actions.
     */
    private void RefreshCommands()
    {
        foreach (ICommand command in new[] { ConnectCommand, DisconnectCommand, ApplyParkingCommand }) { ((AsyncCommand)command).Refresh(); }
        foreach (ConnectionViewModel item in Connections) { ((AsyncCommand)item.DisconnectCommand).Refresh(); }
    }
    /*
     * Name: ReportError
     * Input: Operation name and exception.
     * Process: Formats a visible error and reconciles retained connection state.
     * Output: Actionable failure without changing Windows defaults.
     */
    private void ReportError(string operation, Exception failure)
    { if (!closing) { ErrorMessage = AudioPresentation.Error(operation, failure); StatusMessage = operation + " failed."; SyncConnections(); } }

    /*
     * Name: ShutdownAsync
     * Input: Window closure or repeated close.
     * Process: Cancels polling/connections and drains controls before releasing native ownership.
     * Output: Idempotent completion; outstanding restoration records remain durable.
     */
    public Task ShutdownAsync() => shutdown ??= ShutdownCoreAsync();
    /*
     * Name: ShutdownCoreAsync
     * Input: Window lifetime and pending requests.
     * Process: Cancels polling, drains controls, and awaits coordinator cleanup.
     * Output: Shared completion after playback stops and preferences restore.
     */
    private async Task ShutdownCoreAsync()
    {
        closing = true; cancellation.Cancel(); RefreshCommands();
        Task coordinatorShutdown = coordinator.ShutdownAsync();
        if (initialization is not null) { await initialization; }
        if (polling is not null) { await polling; }
        await Task.WhenAll(pending.ToArray()); await coordinatorShutdown;
    }
}

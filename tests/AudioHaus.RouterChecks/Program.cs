using AudioHaus.App;
using AudioHaus.Interop;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

/*
 * Name: Renderer
 * Input: Test renderer executable/frequency or fixed-device Diagnostics arguments.
 * Process: Owns one hidden test process with bounded readiness and cleanup.
 * Output: Actual PID and commands without touching user applications.
 */
internal sealed class Renderer : IDisposable
{
    public Process Process { get; }
    public uint Id => (uint)Process.Id;
    public Renderer(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        Process = Process.Start(start) ?? throw new InvalidOperationException("Renderer launch failed.");
        Process.StandardInput.AutoFlush = true; _ = Process.SafeHandle;
    }
    public void Send(string command) => Process.StandardInput.WriteLine(command);
    public void Stop()
    {
        if (!Process.HasExited) { try { Send("quit"); } catch (IOException) { } if (!Process.WaitForExit(5000)) { Process.Kill(); Process.WaitForExit(); } }
    }
    public void Dispose() { Stop(); Process.Dispose(); }
}

/*
 * Name: Program
 * Input: Automatic renderer, Diagnostics, cable, Realtek and headphone endpoint IDs.
 * Process: Exercises real automatic routing, master PCM, rollback, restoration, and recovery records.
 * Output: PASS evidence from owned processes and the production coordinator.
 */
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 5) { Console.WriteLine("RouterChecks <automatic-renderer> <diagnostics> <cable> <output-a> <output-b>"); return 1; }
        string directory = Path.Combine(Path.GetTempPath(), "AudioHaus.RouterChecks", Guid.NewGuid().ToString("N"));
        Console.WriteLine("Settings fixtures: " + directory);
        try
        {
            await CheckRoutingAsync(args, Path.Combine(directory, "routing"));
            await CheckInitiallyMutedAsync(args, Path.Combine(directory, "muted"));
            await CheckExitRecoveryAsync(args, Path.Combine(directory, "exit"));
            await CheckRollbackAsync(args, Path.Combine(directory, "rollback"));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    /*
     * Name: CheckRoutingAsync
     * Input: Renderer and endpoints with isolated settings.
     * Process: Tests fan-out, multiplicative masters, shared leases, mixing, and normal restoration.
     * Output: Measured PCM ratios and exact restored Windows preferences.
     */
    private static async Task CheckRoutingAsync(string[] args, string directory)
    {
        using var first = new Renderer(args[0], "330"); using var second = new Renderer(args[0], "550");
        ProcessIdentity a = await IdentityAsync(first.Id), b = await IdentityAsync(second.Id);
        string[] original = Preferences(first.Id);
        var coordinator = new RoutingCoordinator(directory);
        try
        {
            await coordinator.InitializeAsync(); await coordinator.SetParkingEndpointAsync(args[2]);
            await coordinator.ConnectAsync(a, args[3]); await coordinator.ConnectAsync(a, args[4]);
            Require(coordinator.Connections.Count == 2, "Fan-out did not produce two routes.");
            (double baselineA, double baselineB) = await RmsAsync(coordinator, args[3], args[4]);
            Require(baselineA > 0.001 && baselineB > 0.001, "Fan-out has no actual PCM.");
            await coordinator.SetSourceLevelAsync(a, 0.5, false);
            (double halfA, double halfB) = await RmsAsync(coordinator, args[3], args[4]);
            Ratio(halfA / baselineA, 0.5); Ratio(halfB / baselineB, 0.5);
            await coordinator.SetOutputLevelAsync(args[3], 0.5, false);
            (double quarterA, double unchangedB) = await RmsAsync(coordinator, args[3], args[4]);
            Ratio(quarterA / baselineA, 0.25); Ratio(unchangedB / baselineB, 0.5);
            await coordinator.SetSourceLevelAsync(a, 0.5, true);
            (double mutedA, double mutedB) = await RmsAsync(coordinator, args[3], args[4]);
            Require(mutedA < 1e-6 && mutedB < 1e-6, "Source mute did not mute every output.");
            await coordinator.SetSourceLevelAsync(a, 1, false); await coordinator.SetOutputLevelAsync(args[3], 1, true);
            (mutedA, unchangedB) = await RmsAsync(coordinator, args[3], args[4]);
            Require(mutedA < 1e-6 && unchangedB > 0.001, "Output mute affected the wrong destination.");
            await coordinator.SetOutputLevelAsync(args[3], 1, false);
            await coordinator.ConnectAsync(b, args[3]);
            Require(JournalCount(directory) == 1, "Same-application instances should share one original preference record.");
            (double mixedA, _) = await RmsAsync(coordinator, args[3], args[4]);
            Require(mixedA > baselineA * 1.2, "Two independent sources were not mixed on one output.");
            await coordinator.DisconnectAsync(a, args[3]); await coordinator.DisconnectAsync(a, args[4]);
            Require(coordinator.Connections.Count == 1 && JournalCount(directory) == 1, "Disconnect restored a preference still owned by another instance.");
            Require(ApplicationRoutingPolicy.CheckParking(second.Id, args[2]) == 0, "Remaining instance escaped the cable.");
            await coordinator.ShutdownAsync();
            Require(Preferences(first.Id).SequenceEqual(original) && Preferences(second.Id).SequenceEqual(original), "Normal shutdown did not restore all render roles.");
            Require(JournalCount(directory) == 0, "Normal restoration left stale journal records.");
            Console.WriteLine("PASS: fan-out, two-source mixing, 0.5/0.25 master PCM ratios, source/output mute isolation, shared ownership, and exact normal-close restoration.");
        }
        finally { await coordinator.ShutdownAsync(); Restore(first.Id, original); }
    }

    /*
     * Name: CheckInitiallyMutedAsync
     * Input: Policy-following renderer and isolated coordinator settings.
     * Process: Measures all submitted energy when connecting muted resources and checks source metering.
     * Output: Muted connections have no startup burst; source activity respects source mute and survives output mute.
     */
    private static async Task CheckInitiallyMutedAsync(string[] args, string directory)
    {
        using var renderer = new Renderer(args[0], "330");
        ProcessIdentity identity = await IdentityAsync(renderer.Id); string[] original = Preferences(renderer.Id);
        var coordinator = new RoutingCoordinator(directory);
        try
        {
            await coordinator.InitializeAsync(); await coordinator.SetParkingEndpointAsync(args[2]);
            await coordinator.ConnectAsync(identity, args[3], sourceMuted: true, outputDelayMilliseconds: 150, outputDrift: false);
            await Task.Delay(600);
            RoutingTelemetry muted = await coordinator.ReadTelemetryAsync();
            Require(muted.Outputs.Single().Status!.SumSquares == 0, "Initially muted source leaked a startup burst.");
            SourceSignal signal = muted.Sources.Single().Signal!.Value;
            Require(signal.Frames > 0 && signal.SumSquares == 0, "Muted source signal should advance without energy.");
            OutputTimingStatus timing = muted.Outputs.Single().Timing!;
            Require(Math.Abs(timing.Delay.TotalMilliseconds - 150) < 0.02 && !timing.DriftCorrectionEnabled, "New output lost retained timing controls.");
            await coordinator.DisconnectAsync(identity, args[3]);
            await coordinator.ConnectAsync(identity, args[3], sourceGain: 0.5, outputMuted: true);
            await Task.Delay(600);
            RoutingTelemetry outputMuted = await coordinator.ReadTelemetryAsync();
            Require(outputMuted.Outputs.Single().Status!.SumSquares == 0, "Initially muted output leaked a startup burst.");
            signal = outputMuted.Sources.Single().Signal!.Value;
            Require(signal.Frames > 0 && signal.SumSquares > 0, "Output mute must preserve measured source activity.");
            double rms = Math.Sqrt(signal.SumSquares / (signal.Frames * 2.0));
            Require(Math.Abs(rms - 0.007071) < 0.001, "Routed source meter did not reflect source gain.");
            await coordinator.ShutdownAsync();
            Require(Preferences(renderer.Id).SequenceEqual(original) && JournalCount(directory) == 0, "Muted routing shutdown did not restore preferences.");
            Console.WriteLine("PASS: initial source/output mute has zero startup energy, routed source meter follows gain/mute, retained timing, and restoration.");
        }
        finally { await coordinator.ShutdownAsync(); Restore(renderer.Id, original); }
    }

    /*
     * Name: CheckExitRecoveryAsync
     * Input: Automatic renderer and persistent test settings directory.
     * Process: Exits the only controlled PID and recovers its saved preference on a new app lifetime.
     * Output: No stale-PID routing; queued restoration survives coordinator restart.
     */
    private static async Task CheckExitRecoveryAsync(string[] args, string directory)
    {
        var old = new Renderer(args[0], "330");
        ProcessIdentity oldIdentity = await IdentityAsync(old.Id); string[] original = Preferences(old.Id);
        var coordinator = new RoutingCoordinator(directory);
        try
        {
            await coordinator.InitializeAsync(); await coordinator.SetParkingEndpointAsync(args[2]);
            await coordinator.ConnectAsync(oldIdentity, args[3]); old.Stop();
            await coordinator.RefreshAsync();
            Require(coordinator.Connections.Count == 0 && JournalCount(directory) == 1, "Exited source was not removed with durable recovery intent.");
        }
        finally { await coordinator.ShutdownAsync(); old.Dispose(); }
        using var next = new Renderer(args[0], "330");
        ProcessIdentity newIdentity = await IdentityAsync(next.Id);
        Require(oldIdentity != newIdentity, "Renderer lifetime did not change.");
        var recovery = new RoutingCoordinator(directory);
        try
        {
            await recovery.InitializeAsync(); await recovery.RefreshAsync();
            Require(JournalCount(directory) == 0 && Preferences(next.Id).SequenceEqual(original), "Queued restoration did not recover via the new app identity.");
            Require(recovery.Connections.Count == 0, "Recovery incorrectly created a route rule for the new process.");
            Console.WriteLine("PASS: exited PID removal, persistent recovery record, exact restoration on next app lifetime, and no automatic rerouting rule.");
        }
        finally { await recovery.ShutdownAsync(); Restore(next.Id, original); }
    }

    /*
     * Name: CheckRollbackAsync
     * Input: Fixed-output test process and production coordinator.
     * Process: Verifies explicit-device refusal and cancellation after a policy change.
     * Output: Five-second failure rolls back graph/preferences; shutdown cancels a pending Connect.
     */
    private static async Task CheckRollbackAsync(string[] args, string directory)
    {
        using var fixedOutput = new Renderer(args[1], "tone", "--output", args[3], "--seconds", "30", "--frequency", "440", "--level", "0.005");
        ProcessIdentity identity = await IdentityAsync(fixedOutput.Id); string[] original = Preferences(fixedOutput.Id);
        var coordinator = new RoutingCoordinator(directory);
        try
        {
            await coordinator.InitializeAsync(); await coordinator.SetParkingEndpointAsync(args[2]);
            bool failed = false;
            try { await coordinator.ConnectAsync(identity, args[4]); }
            catch (InvalidOperationException error) { failed = error.Message.Contains("did not move", StringComparison.Ordinal); }
            Require(failed && coordinator.Connections.Count == 0 && JournalCount(directory) == 0, "Pinned-device connection failed to roll back.");
            Require(Preferences(fixedOutput.Id).SequenceEqual(original), "Pinned-device failure changed original preferences.");
            Task pending = coordinator.ConnectAsync(identity, args[4]);
            await WaitAsync(() => ApplicationRoutingPolicy.ReadPreference(fixedOutput.Id, 1) == ApplicationRoutingPolicy.EndpointPreference(args[2]), "pending Connect changed policy");
            Task close = coordinator.ShutdownAsync();
            try { await pending; } catch (OperationCanceledException) { }
            await close;
            Require(Preferences(fixedOutput.Id).SequenceEqual(original) && JournalCount(directory) == 0, "Shutdown during Connect left app preferences parked.");
            Console.WriteLine("PASS: pinned-device five-second verification failure, transactional rollback, and restoration after shutdown during Connect.");
        }
        finally { await coordinator.ShutdownAsync(); Restore(fixedOutput.Id, original); }
    }

    private static string[] Preferences(uint pid) => Enumerable.Range(0, 3).Select(role => ApplicationRoutingPolicy.ReadPreference(pid, (uint)role)).ToArray();
    private static void Restore(uint pid, string[] preferences) { for (uint role = 0; role < 3; ++role) { ApplicationRoutingPolicy.WritePreference(pid, role, preferences[role]); } }
    private static int JournalCount(string directory)
    { using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "routing.json"))); return json.RootElement.GetProperty("Restorations").GetArrayLength(); }
    private static async Task<ProcessIdentity> IdentityAsync(uint pid)
    {
        ProcessIdentity identity = default;
        await WaitAsync(() => { var session = new AudioApplicationEnumerator().Enumerate().FirstOrDefault(item => item.ProcessId == pid); if (session is null) { return false; } identity = session.Identity; return true; }, "active renderer identity");
        return identity;
    }
    private static async Task<(double, double)> RmsAsync(RoutingCoordinator coordinator, string a, string b)
    {
        await Task.Delay(350);
        RoutingTelemetry before = await coordinator.ReadTelemetryAsync(); await Task.Delay(500); RoutingTelemetry after = await coordinator.ReadTelemetryAsync();
        double Read(string endpoint)
        {
            var x = before.Outputs.Single(item => item.EndpointId == endpoint).Status!;
            var y = after.Outputs.Single(item => item.EndpointId == endpoint).Status!;
            return Math.Sqrt(Math.Max(0, (y.SumSquares - x.SumSquares) / ((y.FramesSubmitted - x.FramesSubmitted) * 2.0)));
        }
        double left = Read(a), right = Read(b); Console.WriteLine($"  PCM RMS: {left:G6}, {right:G6}"); return (left, right);
    }
    private static void Ratio(double observed, double expected) => Require(Math.Abs(observed - expected) < 0.08, $"PCM ratio {observed:G5} differs from {expected}.");
    private static async Task WaitAsync(Func<bool> predicate, string description)
    { var timer = Stopwatch.StartNew(); while (timer.Elapsed < TimeSpan.FromSeconds(8)) { if (predicate()) { return; } await Task.Delay(100); } throw new TimeoutException(description); }
    private static void Require(bool condition, string description) { if (!condition) { throw new InvalidOperationException(description); } }
}

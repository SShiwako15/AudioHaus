using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AudioHaus.Interop;

namespace AudioHaus.Diagnostics;

/*
 * Name: RoutingConsole
 * Input: File/process sources, output selections, and interactive routing commands.
 * Process: Maintains a native routing graph with independent source controls.
 * Output: Graph status, command diagnostics, and a session exit code.
 */
internal sealed class RoutingConsole
{
    private readonly AudioRouter router;
    private readonly Dictionary<uint, string> sources = new();
    private readonly Dictionary<uint, AudioEndpoint> outputs = new();
    private readonly HashSet<(uint SourceId, uint OutputId)> routes = new();
    private readonly HashSet<uint> liveSources = new();
    private readonly Dictionary<uint, string> parkingEndpoints = new();
    private bool failed;

    /*
     * Name: RoutingConsole
     * Input: An open native router owned by Run.
     * Process: Retains the controller for an initially empty console session.
     * Output: A session with no sources, outputs, or routes.
     */
    private RoutingConsole(AudioRouter router)
    {
        this.router = router;
    }

    /*
     * Name: Run
     * Input: Arguments beginning with route, optionally followed by help.
     * Process: Handles help or owns an empty router until the session ends.
     * Output: Exit code 0, 1, or 2; creation/load errors propagate to Main.
     */
    internal static int Run(string[] args)
    {
        if (args.Length == 2 && (args[1] == "--help" || args[1] == "-h"))
        {
            PrintHelp(Console.Out);
            return 0;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("The route command accepts no options except --help or -h.");
            PrintHelp(Console.Error);
            return 2;
        }

        using AudioRouter router = AudioRouter.Create();
        return new RoutingConsole(router).RunSession();
    }

    /*
     * Name: RunSession
     * Input: Console command lines or end of input.
     * Process: Dispatches commands while preserving the graph after recoverable errors.
     * Output: Exit code 0 or 1; the router is disposed when Run returns or throws.
     */
    private int RunSession()
    {
        Console.WriteLine("Routing mixer ready. Graph is empty; type help for commands.");
        Console.WriteLine("Sources open stopped. Outputs run silence until a connected source plays.");
        Console.WriteLine("For applications: cable-setup, applications, then capture <pid> <cable-input-id>.");

        while (true)
        {
            if (!Console.IsInputRedirected)
            {
                Console.Write("route> ");
            }

            string? line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            try
            {
                string[] words = Tokenize(line);
                if (words.Length != 0 && !ExecuteCommand(words))
                {
                    break;
                }
            }
            catch (CommandException error)
            {
                Console.Error.WriteLine($"Command error: {error.Message}");
            }
            catch (Exception error) when (IsActionFailure(error))
            {
                ReportFailure(error);
            }
        }

        CheckFinalStatus();
        Console.WriteLine("Closing routing mixer.");
        return failed ? 1 : 0;
    }

    /*
     * Name: ExecuteCommand
     * Input: A nonempty tokenized command.
     * Process: Validates arguments and changes managed graph state after native success.
     * Output: False for quit, true otherwise; argument or native errors propagate.
     */
    private bool ExecuteCommand(string[] words)
    {
        string command = words[0].ToLowerInvariant();
        switch (command)
        {
            case "source":
                RequireCount(words, 2);
                AddSource(words[1]);
                break;
            case "capture":
                RequireCount(words, 3);
                AddCapture(words[1], words[2]);
                break;
            case "applications":
                RequireCount(words, 1);
                ApplicationCaptureConsole.PrintApplications();
                break;
            case "processes":
                if (words.Length > 2) { throw new CommandException("Use processes [name-without-.exe]."); }
                ApplicationCaptureConsole.PrintProcesses(words.Length == 2 ? words[1] : null);
                break;
            case "cable-setup":
                RequireCount(words, 1);
                ApplicationCaptureConsole.PrintCableSetup();
                break;
            case "delay": {
                RequireCount(words, 3);
                uint id = GetOutputId(words[1]);
                TimeSpan delay = ParseDelay(words[2]);
                router.SetOutputDelay(id, delay);
                Console.WriteLine(FormattableString.Invariant($"Output {id}: additional delay {delay.TotalMilliseconds:G} ms."));
                break;
            }
            case "drift": {
                RequireCount(words, 3);
                uint id = GetOutputId(words[1]);
                string value = words[2].ToLowerInvariant();
                if (value is not ("on" or "off")) { throw new CommandException("Drift correction accepts on or off."); }
                router.SetDriftCorrection(id, value == "on");
                Console.WriteLine($"Output {id}: drift correction {value}.");
                break;
            }
            case "retry-output":
                RequireCount(words, 2);
                router.RetryOutput(GetOutputId(words[1]));
                Console.WriteLine("Output reopen requested. Use timing/status to follow recovery.");
                break;
            case "retry-capture": {
                RequireCount(words, 2);
                uint id = GetSourceId(words[1]);
                if (!liveSources.Contains(id)) { throw new CommandException("Retry-capture requires a process source."); }
                router.RetryCapture(id);
                Console.WriteLine("Capture reopen requested. Use timing/status to follow recovery.");
                break;
            }
            case "timing":
                RequireCount(words, 1);
                PrintTiming();
                break;
            case "output":
                RequireCount(words, 2);
                AddOutput(words[1]);
                break;
            case "connect":
            {
                if (words.Length != 3 && words.Length != 4)
                {
                    throw new CommandException("Use connect <source-id> <output-id> [gain].");
                }

                uint sourceId = GetSourceId(words[1]);
                uint outputId = GetOutputId(words[2]);
                double gain = words.Length == 4 ? ParseGain(words[3]) : 0.15;
                router.Connect(sourceId, outputId, gain, muted: false);
                routes.Add((sourceId, outputId));
                Console.WriteLine(FormattableString.Invariant($"Connected source {sourceId} -> output {outputId}; gain {gain:G}, mute off."));
                break;
            }
            case "disconnect":
            {
                RequireCount(words, 3);
                (uint sourceId, uint outputId) = GetRoute(words[1], words[2]);
                router.Disconnect(sourceId, outputId);
                routes.Remove((sourceId, outputId));
                Console.WriteLine($"Disconnected source {sourceId} -> output {outputId}.");
                break;
            }
            case "gain":
            {
                RequireCount(words, 4);
                (uint sourceId, uint outputId) = GetRoute(words[1], words[2]);
                double gain = ParseGain(words[3]);
                router.SetRouteGain(sourceId, outputId, gain);
                Console.WriteLine(FormattableString.Invariant($"Route {sourceId} -> {outputId}: gain {gain:G}."));
                break;
            }
            case "mute":
            {
                RequireCount(words, 4);
                (uint sourceId, uint outputId) = GetRoute(words[1], words[2]);
                string value = words[3].ToLowerInvariant();
                if (value != "on" && value != "off")
                {
                    throw new CommandException("Mute accepts on or off.");
                }

                router.SetRouteMute(sourceId, outputId, value == "on");
                Console.WriteLine($"Route {sourceId} -> {outputId}: mute {value}.");
                break;
            }
            case "remove-source":
            {
                RequireCount(words, 2);
                uint sourceId = GetSourceId(words[1]);
                router.RemoveSource(sourceId);
                sources.Remove(sourceId);
                liveSources.Remove(sourceId);
                parkingEndpoints.Remove(sourceId);
                routes.RemoveWhere(route => route.SourceId == sourceId);
                Console.WriteLine($"Removed source {sourceId} and its routes.");
                break;
            }
            case "remove-output":
            {
                RequireCount(words, 2);
                uint outputId = GetOutputId(words[1]);
                router.RemoveOutput(outputId);
                outputs.Remove(outputId);
                routes.RemoveWhere(route => route.OutputId == outputId);
                Console.WriteLine($"Removed output {outputId} and its routes.");
                break;
            }
            case "play":
            case "pause":
            case "stop":
                RequireCount(words, 2);
                ControlSources(command, words[1]);
                break;
            case "seek":
            {
                RequireCount(words, 3);
                uint sourceId = GetSourceId(words[1]);
                if (liveSources.Contains(sourceId)) { throw new CommandException("Live capture cannot seek. Control media position in the source application."); }
                TimeSpan position = ParsePosition(words[2]);
                router.Seek(sourceId, position);
                Console.WriteLine(FormattableString.Invariant($"Source {sourceId}: seek {position.TotalSeconds:G} seconds."));
                break;
            }
            case "status":
                RequireCount(words, 1);
                PrintStatus();
                break;
            case "help":
                RequireCount(words, 1);
                PrintHelp(Console.Out);
                break;
            case "quit":
                RequireCount(words, 1);
                return false;
            default:
                throw new CommandException("Unknown command. Type help for available controls.");
        }

        return true;
    }

    /*
     * Name: AddSource
     * Input: Local file path from a possibly quoted console token.
     * Process: Validates existence and adds one stopped native decoder.
     * Output: Printed source ID; failures leave the managed source list unchanged.
     */
    private void AddSource(string filePath)
    {
        if (sources.Count == 8)
        {
            throw new CommandException("A router supports at most eight sources. Remove one first.");
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new CommandException("Source requires a nonempty local file path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CommandException($"Invalid file path: {error.Message}");
        }

        if (!File.Exists(fullPath))
        {
            throw new CommandException($"Audio file not found: {fullPath}");
        }

        uint sourceId = router.AddSource(fullPath);
        sources.Add(sourceId, fullPath);
        Console.WriteLine($"Source {sourceId}: {fullPath} (Stopped).");
    }

    /*
     * Name: AddCapture
     * Input: PID and exact VB-CABLE playback endpoint ID.
     * Process: Validates setup selection and creates a stopped process-tree monitor.
     * Output: Printed source ID; capture/parking failures preserve the existing graph.
     */
    private void AddCapture(string process, string parking)
    {
        if (sources.Count == 8) { throw new CommandException("A router supports at most eight sources. Remove one first."); }
        if (!uint.TryParse(process, NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) || pid == 0 || pid > int.MaxValue)
        { throw new CommandException("Capture requires a positive application PID. Use applications or processes."); }
        AudioEndpoint? endpoint = new AudioEndpointEnumerator().Enumerate().FirstOrDefault(item =>
            item.State == AudioHausEndpointState.Active && string.Equals(item.Id, parking, StringComparison.OrdinalIgnoreCase));
        if (endpoint is null || !ApplicationCaptureConsole.IsCableCandidate(endpoint.DisplayName))
        { throw new CommandException("Select an active CABLE Input playback endpoint. Run cable-setup; physical outputs do not silence original playback."); }
        if (outputs.Values.Any(item => string.Equals(item.Id, endpoint.Id, StringComparison.OrdinalIgnoreCase)))
        { throw new CommandException("A parking cable cannot also be an AudioHaus output."); }
        uint sourceId = router.AddProcessSource(pid, endpoint.Id);
        sources.Add(sourceId, $"Live process tree PID {pid}; parked on {endpoint.DisplayName}");
        liveSources.Add(sourceId);
        parkingEndpoints.Add(sourceId, endpoint.Id);
        Console.WriteLine($"Source {sourceId}: process tree PID {pid} (monitor stopped). Use connect and play to monitor.");
        Console.WriteLine("The application remains unmuted on the cable. Pause/Stop affect AudioHaus monitoring; seek in the application.");
    }

    /*
     * Name: AddOutput
     * Input: Exact endpoint ID or the default token.
     * Process: Resolves a fresh active endpoint and opens its silent native mixer.
     * Output: Printed output ID; selection errors leave the graph unchanged.
     */
    private void AddOutput(string selection)
    {
        if (outputs.Count == 8)
        {
            throw new CommandException("A router supports at most eight outputs. Remove one first.");
        }

        IReadOnlyList<AudioEndpoint> endpoints = new AudioEndpointEnumerator().Enumerate();
        AudioEndpoint? endpoint = selection == "default"
            ? endpoints.FirstOrDefault(item => item.IsDefaultMultimedia)
            : endpoints.FirstOrDefault(item => string.Equals(item.Id, selection, StringComparison.Ordinal));
        if (endpoint is null)
        {
            throw new CommandException("Output not found. Run diagnostics without arguments to list exact endpoint IDs.");
        }

        if (endpoint.State != AudioHausEndpointState.Active)
        {
            throw new CommandException($"Output is not active: {endpoint.DisplayName} ({endpoint.State}).");
        }

        if (parkingEndpoints.Values.Any(id => string.Equals(id, endpoint.Id, StringComparison.OrdinalIgnoreCase)))
        { throw new CommandException("That endpoint parks original playback; choose a physical destination."); }

        if (outputs.Values.Any(item => string.Equals(item.Id, endpoint.Id, StringComparison.Ordinal)))
        {
            throw new CommandException("That endpoint is already in the graph; connect more sources to its existing output ID.");
        }

        uint outputId = router.AddOutput(endpoint.Id);
        outputs.Add(outputId, endpoint);
        Console.WriteLine($"Output {outputId}: {endpoint.DisplayName}");
        Console.WriteLine($"  Endpoint ID: {endpoint.Id}");
    }

    /*
     * Name: ControlSources
     * Input: Transport command and one live source ID or all.
     * Process: Applies controls independently, continuing after a failed source.
     * Output: Per-source acknowledgments and retained native failure diagnostics.
     */
    private void ControlSources(string command, string target)
    {
        uint[] targets = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase)
            ? sources.Keys.OrderBy(id => id).ToArray()
            : new[] { GetSourceId(target) };
        if (targets.Length == 0)
        {
            throw new CommandException("There are no sources to control.");
        }

        foreach (uint sourceId in targets)
        {
            try
            {
                switch (command)
                {
                    case "play":
                        router.Play(sourceId);
                        break;
                    case "pause":
                        router.Pause(sourceId);
                        break;
                    case "stop":
                        router.Stop(sourceId);
                        break;
                }

                Console.WriteLine($"Source {sourceId}: {command}.");
            }
            catch (Exception error) when (IsActionFailure(error))
            {
                ReportFailure(error);
            }
        }
    }

    /*
     * Name: PrintStatus
     * Input: Current source, output, and route identifiers.
     * Process: Queries native snapshots without changing transport or routing.
     * Output: Graph membership, media progress, route controls, and mixer telemetry.
     */
    private void PrintStatus()
    {
        Console.WriteLine($"Graph: {sources.Count} sources, {outputs.Count} outputs, {routes.Count} routes.");
        foreach ((uint sourceId, string filePath) in sources.OrderBy(item => item.Key))
        {
            RoutingSourceStatus status = router.GetSourceStatus(sourceId);
            Console.WriteLine($"Source {sourceId}: {filePath}");
            if (liveSources.Contains(sourceId))
            {
                ProcessCaptureStatus capture = router.GetCaptureStatus(sourceId);
                Console.WriteLine($"  Live monitor: {capture.State}; PID: {capture.ProcessId}; HRESULT: 0x{capture.HResult:X8}");
                Console.WriteLine($"  Captured frames: {capture.FramesCaptured}; Non-silent: {capture.NonSilentFrames}; Buffered: {capture.BufferedFrames}; Dropped unread: {capture.FramesDropped}; Discontinuities: {capture.Discontinuities}");
                Console.WriteLine($"  Last packet QPC (100 ns): {capture.LastPacketQpc100Ns}");
                Console.WriteLine(FormattableString.Invariant($"  Captured last peak: {capture.LastPeak:G6}; Sum squares: {capture.SumSquares:G6}"));
                failed |= capture.State == FilePlaybackState.Failed || (capture.State != FilePlaybackState.Recovering && HasFailed(capture.HResult));
            }
            else { Console.WriteLine(FormattableString.Invariant($"  State: {status.State}; Position: {status.Position.TotalSeconds:F3} / {status.Duration.TotalSeconds:F3} seconds; HRESULT: 0x{status.HResult:X8}")); }
            Console.WriteLine($"  Format: {status.SampleRate} Hz, {status.ChannelCount} channels; Routes: {status.RouteCount}; Frames decoded: {status.FramesDecoded}; Cached frames: {status.CachedFrames}");
            failed |= status.State == FilePlaybackState.Failed || (status.State != FilePlaybackState.Recovering && HasFailed(status.HResult));
        }

        foreach ((uint outputId, AudioEndpoint endpoint) in outputs.OrderBy(item => item.Key))
        {
            RoutingOutputStatus status = router.GetOutputStatus(outputId);
            string state = status.State == 1 ? "Running" : status.State == 2 ? "Recovering" : status.State == 4 ? "Failed" : status.State.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine($"Output {outputId}: {endpoint.DisplayName}");
            Console.WriteLine($"  Endpoint ID: {endpoint.Id}");
            Console.WriteLine($"  State: {state}; HRESULT: 0x{status.HResult:X8}; Routes: {status.RouteCount}");
            Console.WriteLine($"  Format: {status.SampleRate} Hz, {status.ChannelCount} channels; Frames submitted: {status.FramesSubmitted}; Non-silent frames: {status.NonSilentFrames}");
            Console.WriteLine(FormattableString.Invariant($"  Last peak: {status.LastPeak:G6}; Sum squares: {status.SumSquares:G6}; Clipped samples: {status.ClippedSamples}"));
            Console.WriteLine($"  Start QPC: {status.StartQpc}; Stop QPC: {status.StopQpc}");
            failed |= status.State == 4 || (status.State != 2 && HasFailed(status.HResult));
        }

        foreach ((uint sourceId, uint outputId) in routes.OrderBy(route => route.SourceId).ThenBy(route => route.OutputId))
        {
            RoutingRouteStatus status = router.GetRouteStatus(sourceId, outputId);
            Console.WriteLine(FormattableString.Invariant($"Route {sourceId} -> {outputId}: gain {status.Gain:G}, mute {(status.Muted ? "on" : "off")}; Frames read: {status.FramesRead}; Cursor: {status.CursorFrame}; Rebases: {status.Rebases}"));
        }
        PrintTiming();
    }

    /*
     * Name: PrintTiming
     * Input: Current output, process source, and route IDs.
     * Process: Reads buffer depth, delay, drift, underrun, and reconnection telemetry.
     * Output: Human-readable timing snapshots; recoverable outages do not poison exit status.
     */
    private void PrintTiming()
    {
        foreach (uint id in outputs.Keys.OrderBy(value => value))
        {
            OutputTimingStatus status = router.GetOutputTiming(id);
            Console.WriteLine(FormattableString.Invariant($"Output {id} timing: delay {status.Delay.TotalMilliseconds:F3} ms; buffer {status.PaddingFrames}/{status.BufferCapacityFrames}; drift {(status.DriftCorrectionEnabled ? "on" : "off")}; clock {status.ClockRatePpm:F2} ppm."));
            Console.WriteLine(FormattableString.Invariant($"  Render underruns: {status.UnderrunCount}; recoveries: {status.RecoveryCount}/{status.RecoveryAttempts} attempts; retry in {status.RetryAfter.TotalMilliseconds:F0} ms; last error 0x{status.LastErrorHResult:X8}; max service gap {status.MaxServiceGap.TotalMilliseconds:F1} ms; reported device latency {status.StreamLatency.TotalMilliseconds:F1} ms."));
        }
        foreach (uint id in liveSources.OrderBy(value => value))
        {
            CaptureTimingStatus status = router.GetCaptureTiming(id);
            Console.WriteLine(FormattableString.Invariant($"Source {id} timing: history capacity {status.BufferCapacityFrames}; recoveries {status.RecoveryCount}/{status.RecoveryAttempts}; desired {status.ResumeState}; retry in {status.RetryAfter.TotalMilliseconds:F0} ms; last error 0x{status.LastErrorHResult:X8}."));
        }
        foreach ((uint source, uint output) in routes.OrderBy(item => item.SourceId).ThenBy(item => item.OutputId))
        {
            RouteTimingStatus status = router.GetRouteTiming(source, output);
            Console.WriteLine(FormattableString.Invariant($"Route {source} -> {output} timing: priming {(status.Priming ? "yes" : "no")}; buffered {status.BufferedFrames}, target {status.TargetFrames}; correction {status.CorrectionPpm:F2} ppm; phase {status.PhaseErrorFrames:F2} frames; underruns {status.UnderrunCount}/{status.UnderrunFrames} frames; overruns {status.OverrunCount}; rebuffers {status.RebufferCount}."));
        }
    }

    /*
     * Name: CheckFinalStatus
     * Input: Live graph members at quit or end of input.
     * Process: Records asynchronous failures before the native graph is disposed.
     * Output: Updated session failure flag and any status-query diagnostics.
     */
    private void CheckFinalStatus()
    {
        foreach (uint sourceId in sources.Keys)
        {
            try
            {
                RoutingSourceStatus status = router.GetSourceStatus(sourceId);
                if (status.State == FilePlaybackState.Failed || HasFailed(status.HResult))
                {
                    failed = true;
                    Console.Error.WriteLine($"Source {sourceId} failed: HRESULT 0x{status.HResult:X8}.");
                }
            }
            catch (Exception error) when (IsActionFailure(error))
            {
                ReportFailure(error);
            }
        }

        foreach (uint outputId in outputs.Keys)
        {
            try
            {
                RoutingOutputStatus status = router.GetOutputStatus(outputId);
                if (status.State == 4 || HasFailed(status.HResult))
                {
                    failed = true;
                    Console.Error.WriteLine($"Output {outputId} failed: HRESULT 0x{status.HResult:X8}.");
                }
            }
            catch (Exception error) when (IsActionFailure(error))
            {
                ReportFailure(error);
            }
        }
    }

    /*
     * Name: GetSourceId
     * Input: Console identifier token.
     * Process: Requires a positive identifier still present in the source list.
     * Output: Live source ID or a command error.
     */
    private uint GetSourceId(string value)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0 || !sources.ContainsKey(id))
        {
            throw new CommandException("Source ID is not in the graph. Use source to add one, or status to list IDs.");
        }

        return id;
    }

    /*
     * Name: GetOutputId
     * Input: Console identifier token.
     * Process: Requires a positive identifier still present in the output list.
     * Output: Live output ID or a command error.
     */
    private uint GetOutputId(string value)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0 || !outputs.ContainsKey(id))
        {
            throw new CommandException("Output ID is not in the graph. Use output to add one, or status to list IDs.");
        }

        return id;
    }

    /*
     * Name: GetRoute
     * Input: Source and output identifier tokens.
     * Process: Requires live graph members and an existing connection.
     * Output: Route identifiers or a command error.
     */
    private (uint SourceId, uint OutputId) GetRoute(string source, string output)
    {
        uint sourceId = GetSourceId(source);
        uint outputId = GetOutputId(output);
        if (!routes.Contains((sourceId, outputId)))
        {
            throw new CommandException("That route is not connected. Use connect first.");
        }

        return (sourceId, outputId);
    }

    /*
     * Name: ParseGain
     * Input: Gain token using an invariant decimal point.
     * Process: Requires a finite value from zero through one.
     * Output: Route gain or a command error.
     */
    private static double ParseGain(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gain) || !double.IsFinite(gain) || gain < 0.0 || gain > 1.0)
        {
            throw new CommandException("Gain requires a finite number from 0 through 1 using a decimal point.");
        }

        return gain;
    }

    /*
     * Name: ParsePosition
     * Input: Seek seconds using an invariant decimal point.
     * Process: Requires a finite nonnegative value representable as a TimeSpan.
     * Output: Seek position or a command error.
     */
    private static TimeSpan ParsePosition(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0.0)
        {
            throw new CommandException("Seek requires finite nonnegative seconds using a decimal point.");
        }

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            throw new CommandException("Seek position exceeds the supported time range.");
        }
    }

    /*
     * Name: ParseDelay
     * Input: Additional output delay in milliseconds.
     * Process: Requires a finite value from zero through 2000.
     * Output: Delay TimeSpan or a recoverable command error.
     */
    private static TimeSpan ParseDelay(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds) ||
            !double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 2000)
        { throw new CommandException("Delay requires finite milliseconds from 0 through 2000."); }
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /*
     * Name: Tokenize
     * Input: One console command line.
     * Process: Splits whitespace outside balanced double quotes, preserving backslashes.
     * Output: Tokens or a command error for an unclosed quote; no escape sequences apply.
     */
    private static string[] Tokenize(string line)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool started = false;
        foreach (char character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }

        if (quoted)
        {
            throw new CommandException("Unclosed double quote. Quote file paths containing spaces.");
        }

        if (started)
        {
            words.Add(current.ToString());
        }

        return words.ToArray();
    }

    /*
     * Name: RequireCount
     * Input: Command tokens and required total count.
     * Process: Rejects missing or surplus arguments before command execution.
     * Output: No value, or a command error with a help hint.
     */
    private static void RequireCount(string[] words, int count)
    {
        if (words.Length != count)
        {
            throw new CommandException($"Invalid arguments for {words[0]}. Type help for syntax.");
        }
    }

    /*
     * Name: HasFailed
     * Input: Unsigned HRESULT value.
     * Process: Tests the HRESULT failure bit.
     * Output: True for a native failure code.
     */
    private static bool HasFailed(uint hresult) => (hresult & 0x80000000u) != 0;

    /*
     * Name: IsActionFailure
     * Input: Exception raised by a native or managed audio operation.
     * Process: Identifies recoverable action errors while load failures reach Main.
     * Output: True when the session should report the failure and continue.
     */
    private static bool IsActionFailure(Exception error) => error is InvalidOperationException or IOException or ArgumentException or NotSupportedException;

    /*
     * Name: ReportFailure
     * Input: Failed action exception.
     * Process: Retains a failed exit status while keeping the session available.
     * Output: Error diagnostic on standard error.
     */
    private void ReportFailure(Exception error)
    {
        failed = true;
        Console.Error.WriteLine($"Routing action failed: {error.Message}");
    }

    /*
     * Name: PrintHelp
     * Input: Writer for command help or invocation errors.
     * Process: Describes graph editing, source transport, and route controls.
     * Output: Routing syntax and operating limits.
     */
    private static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: AudioHaus.Diagnostics route [--help | -h]");
        writer.WriteLine("source \"<file-path>\"                 Add a stopped WAV/MP3/M4A source; prints its ID.");
        writer.WriteLine("capture <pid> <cable-input-id>        Add a stopped live process-tree monitor; originals must play into the cable.");
        writer.WriteLine("applications; processes [name]       Discover active render sessions or distinguish process instances.");
        writer.WriteLine("cable-setup                         Show virtual-cable setup and possible installed playback endpoints.");
        writer.WriteLine("delay <output-id> <milliseconds>    Add 0..2000 ms to one output; readers re-prime.");
        writer.WriteLine("drift <output-id> on|off            Enable/disable gradual clock correction; default on.");
        writer.WriteLine("timing                             Show buffers, phase/rate, underruns, and retries.");
        writer.WriteLine("retry-output <output-id>            Reopen the same endpoint asynchronously.");
        writer.WriteLine("retry-capture <source-id>           Reopen the original process capture after fixing setup.");
        writer.WriteLine("output default|<exact-endpoint-id>    Add an active output mixer; prints its ID.");
        writer.WriteLine("connect <source-id> <output-id> [gain] Add/update a route, gain default 0.15, mute off.");
        writer.WriteLine("disconnect <source-id> <output-id>    Remove a route.");
        writer.WriteLine("gain <source-id> <output-id> <value>  Set one route's finite gain, 0..1.");
        writer.WriteLine("mute <source-id> <output-id> on|off   Mute only that route; source keeps advancing.");
        writer.WriteLine("remove-source <source-id>            Remove a source and its routes.");
        writer.WriteLine("remove-output <output-id>            Remove an output and its routes.");
        writer.WriteLine("play|pause|stop <source-id>|all        Control sources independently of their routes.");
        writer.WriteLine("seek <source-id> <seconds>            Seek one source; seconds finite/nonnegative.");
        writer.WriteLine("status; help; quit                    Inspect the graph, show help, or close it.");
        writer.WriteLine("Use the printed native IDs, which need not be consecutive. Maximum 8 sources, 8 outputs, 64 routes.");
        writer.WriteLine("Double quotes group spaces; Windows backslashes stay literal. No escape sequences apply.");
        writer.WriteLine("No file plays until play. Gain/mute changes affect future mixes; Windows/Bluetooth queues can delay audible changes.");
        writer.WriteLine("Quit or end input releases the graph. Bluetooth latency and clock drift remain best effort.");
        writer.WriteLine("Live Pause/Stop control monitoring only; live sources cannot seek. Source app controls media playback.");
    }

    /*
     * Name: CommandException
     * Input: A console validation error.
     * Process: Separates user command mistakes from failed audio operations.
     * Output: A recoverable error that does not mark the session failed.
     */
    private sealed class CommandException : Exception
    {
        /*
         * Name: CommandException
         * Input: Human-readable command validation diagnostic.
         * Process: Retains the diagnostic without changing native state.
         * Output: A console command exception.
         */
        internal CommandException(string message) : base(message)
        {
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AudioHaus.Interop;

namespace AudioHaus.Diagnostics;

/*
 * Name: Program
 * Input: Endpoint-listing, test-tone, file-player, or routing command-line arguments.
 * Process: Discovers playback endpoints and exercises selected native outputs.
 * Output: Discovery or playback reports, errors, and a process exit code.
 */
internal static class Program
{
    /*
     * Name: Main
     * Input: Command-line arguments.
     * Process: Dispatches commands and reports failures without stack traces.
     * Output: Exit code 0 for success, 1 for failure, or 2 for invalid arguments.
     */
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            {
                PrintUsage(Console.Out);
                return 0;
            }

            if (args.Length == 0 || (args.Length == 1 && args[0] == "list"))
            {
                return ListEndpoints();
            }

            if (args[0] == "files")
            {
                return FilePlaybackConsole.Run(args);
            }

            if (args[0] == "route")
            {
                return RoutingConsole.Run(args);
            }

            if (args.Length == 1 && args[0] == "applications")
            {
                ApplicationCaptureConsole.PrintApplications(); return 0;
            }
            if (args.Length <= 2 && args[0] == "processes")
            {
                ApplicationCaptureConsole.PrintProcesses(args.Length == 2 ? args[1] : null); return 0;
            }
            if (args.Length == 1 && args[0] == "cable-setup")
            {
                return ApplicationCaptureConsole.PrintCableSetup();
            }

            if (args[0] == "tone")
            {
                if (args.Length == 2 && (args[1] == "--help" || args[1] == "-h"))
                {
                    PrintUsage(Console.Out);
                    return 0;
                }

                if (!TryParseToneOptions(args, out ToneOptions options, out string error))
                {
                    Console.Error.WriteLine(error);
                    PrintUsage(Console.Error);
                    return 2;
                }

                return PlayTone(options);
            }

            Console.Error.WriteLine("Invalid arguments.");
            PrintUsage(Console.Error);
            return 2;
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("Could not load AudioHaus.Native.dll or one of its dependencies. Build AudioHaus.sln for x64 and keep the output files together.");
        }
        catch (EntryPointNotFoundException)
        {
            Console.Error.WriteLine("The native DLL is missing a required API function. Rebuild AudioHaus.sln for x64.");
        }
        catch (BadImageFormatException)
        {
            Console.Error.WriteLine("A library could not be loaded in this process. Build the native and managed projects for x64.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Diagnostics failed: {error.Message}");
        }

        return 1;
    }

    /*
     * Name: ListEndpoints
     * Input: None.
     * Process: Discovers and sorts endpoint snapshots for console display.
     * Output: Endpoint details and exit code 0; discovery errors propagate to Main.
     */
    private static int ListEndpoints()
    {
        IReadOnlyList<AudioEndpoint> endpoints = new AudioEndpointEnumerator().Enumerate();
        if (endpoints.Count == 0)
        {
            Console.WriteLine("No playback endpoints found.");
            return 0;
        }

        int activeCount = endpoints.Count(endpoint => endpoint.State == AudioHausEndpointState.Active);
        Console.WriteLine($"Playback endpoints: {endpoints.Count} total, {activeCount} active.");

        foreach (AudioEndpoint endpoint in endpoints
            .OrderByDescending(endpoint => endpoint.State == AudioHausEndpointState.Active)
            .ThenByDescending(endpoint => endpoint.IsDefaultMultimedia)
            .ThenBy(endpoint => endpoint.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine(endpoint.DisplayName);
            Console.WriteLine($"  ID: {endpoint.Id}");
            Console.WriteLine($"  State: {endpoint.State}");
            Console.WriteLine($"  Default multimedia: {(endpoint.IsDefaultMultimedia ? "Yes" : "No")}");
        }

        return 0;
    }

    /*
     * Name: TryParseToneOptions
     * Input: Tone command arguments, including the command name.
     * Process: Validates option names, counts, and invariant finite numeric ranges.
     * Output: True with parsed options, or false with a command-line error.
     */
    private static bool TryParseToneOptions(string[] args, out ToneOptions options, out string error)
    {
        options = new ToneOptions();
        error = string.Empty;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (option != "--output" && option != "--seconds" && option != "--frequency" && option != "--level")
            {
                error = $"Unsupported tone option: {option}";
                return false;
            }

            if (option != "--output" && !seenOptions.Add(option))
            {
                error = $"Option may appear only once: {option}";
                return false;
            }

            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
            {
                error = $"Missing value for {option}.";
                return false;
            }

            string value = args[index];
            if (option == "--output")
            {
                if (string.IsNullOrWhiteSpace(value) || options.OutputIds.Count == 2)
                {
                    error = "Specify one or two output endpoint IDs, or default.";
                    return false;
                }

                options.OutputIds.Add(value);
                continue;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            {
                error = $"{option} requires a finite number using a decimal point.";
                return false;
            }

            switch (option)
            {
                case "--seconds" when number >= 0.1 && number <= 60.0:
                    options.DurationSeconds = number;
                    break;
                case "--frequency" when number >= 20.0 && number <= 20000.0:
                    options.FrequencyHz = number;
                    break;
                case "--level" when number >= 0.0 && number <= 0.25:
                    options.Amplitude = number;
                    break;
                default:
                    error = $"Value out of range for {option}. Use --seconds 0.1..60, --frequency 20..20000, and --level 0..0.25.";
                    return false;
            }
        }

        if (options.OutputIds.Count == 0)
        {
            error = "The tone command requires --output default or an exact endpoint ID.";
            return false;
        }

        return true;
    }

    /*
     * Name: PlayTone
     * Input: Validated tone options and one or two endpoint selections.
     * Process: Resolves active outputs, plays a native tone, and checks stream overlap.
     * Output: Playback reports and exit code 0, 1, or 2; API errors propagate to Main.
     */
    private static int PlayTone(ToneOptions options)
    {
        IReadOnlyList<AudioEndpoint> endpoints = new AudioEndpointEnumerator().Enumerate();
        var selected = new List<AudioEndpoint>();
        foreach (string outputId in options.OutputIds)
        {
            AudioEndpoint? endpoint = outputId == "default"
                ? endpoints.FirstOrDefault(item => item.IsDefaultMultimedia)
                : endpoints.FirstOrDefault(item => string.Equals(item.Id, outputId, StringComparison.Ordinal));
            if (endpoint is null)
            {
                Console.Error.WriteLine($"Output not found: {outputId}. Run without arguments to list exact endpoint IDs.");
                return 2;
            }

            if (endpoint.State != AudioHausEndpointState.Active)
            {
                Console.Error.WriteLine($"Output is not active: {endpoint.DisplayName} ({endpoint.State}). Connect and enable it in Windows, then list endpoints again.");
                return 2;
            }

            if (selected.Any(item => string.Equals(item.Id, endpoint.Id, StringComparison.Ordinal)))
            {
                Console.Error.WriteLine("Select two different endpoint IDs for a simultaneous playback test.");
                return 2;
            }

            selected.Add(endpoint);
        }

        Console.WriteLine(FormattableString.Invariant($"Playing {options.FrequencyHz:G} Hz for {options.DurationSeconds:G} seconds at {options.Amplitude:G} peak amplitude."));
        foreach (AudioEndpoint endpoint in selected)
        {
            Console.WriteLine($"  {endpoint.DisplayName}");
            Console.WriteLine($"    ID: {endpoint.Id}");
        }

        IReadOnlyList<TonePlaybackResult> results = TestTonePlayer.Play(
            selected.Select(endpoint => endpoint.Id).ToArray(),
            TimeSpan.FromSeconds(options.DurationSeconds),
            options.FrequencyHz,
            options.Amplitude);

        bool success = results.Count == selected.Count;
        if (!success)
        {
            Console.Error.WriteLine("The native engine returned an unexpected number of output reports.");
        }

        foreach (TonePlaybackResult result in results)
        {
            Console.WriteLine();
            Console.WriteLine($"Output: {result.EndpointId}");
            Console.WriteLine($"  Format: {result.SampleRate} Hz, {result.ChannelCount} channels");
            Console.WriteLine($"  Frames submitted: {result.FramesSubmitted}");
            Console.WriteLine($"  HRESULT: 0x{result.WasapiResult:X8}");
            Console.WriteLine($"  Completed: {(result.Completed ? "Yes" : "No")}");

            if (!result.Completed || (result.WasapiResult & 0x80000000u) != 0 || result.FramesSubmitted == 0 || result.StartQpc == 0 || result.StopQpc <= result.StartQpc)
            {
                success = false;
                Console.Error.WriteLine($"Playback failed or did not complete for {result.EndpointId}. Check the HRESULT, Windows output state, and device connection.");
            }
        }

        if (selected.Count == 2)
        {
            double overlapSeconds = 0.0;
            if (results.Count == 2 && results.All(result => result.StartQpc != 0 && result.StopQpc > result.StartQpc))
            {
                ulong overlapStart = Math.Max(results[0].StartQpc, results[1].StartQpc);
                ulong overlapStop = Math.Min(results[0].StopQpc, results[1].StopQpc);
                if (overlapStop > overlapStart)
                {
                    overlapSeconds = (overlapStop - overlapStart) / (double)Stopwatch.Frequency;
                }
            }

            Console.WriteLine(FormattableString.Invariant($"\nNative stream overlap: {overlapSeconds:F3} seconds."));
            if (overlapSeconds <= 0.0)
            {
                Console.Error.WriteLine("The two outputs did not establish overlapping playback intervals.");
                success = false;
            }
        }

        Console.WriteLine("Listen to each selected device to confirm audible playback. Stream success does not prove Bluetooth compatibility or acoustic synchronization.");
        return success ? 0 : 1;
    }

    /*
     * Name: PrintUsage
     * Input: Output writer for help or argument errors.
     * Process: Describes the supported command-line options.
     * Output: Usage text; writer errors propagate to Main.
     */
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: AudioHaus.Diagnostics [list | --help | -h]");
        writer.WriteLine("       AudioHaus.Diagnostics tone --output default|<endpoint-id> [--output <endpoint-id>]");
        writer.WriteLine("           [--seconds 3] [--frequency 440] [--level 0.05]");
        writer.WriteLine("       AudioHaus.Diagnostics files --file <path> --output default|<endpoint-id>");
        writer.WriteLine("           [--file <path2> --output <endpoint-id2>] [--gain 0.15]");
        writer.WriteLine("       AudioHaus.Diagnostics route [--help | -h]");
        writer.WriteLine("       AudioHaus.Diagnostics applications | processes [name] | cable-setup");
        writer.WriteLine("With no arguments, lists all Windows playback endpoints.");
        writer.WriteLine("Tone requires one or two different active outputs. Quote endpoint IDs exactly as listed.");
        writer.WriteLine("Ranges: seconds 0.1..60; frequency 20..20000 Hz; level 0..0.25 peak amplitude.");
        writer.WriteLine("Numbers use a decimal point. For tone, only --output may be repeated. Press Ctrl+C to terminate.");
        writer.WriteLine("Files open stopped with independent console controls. Use files --help for commands.");
        writer.WriteLine("Route opens an empty routing mixer. Use route --help for graph and playback commands.");
    }

    /*
     * Name: ToneOptions
     * Input: Parsed output IDs and optional numeric tone settings.
     * Process: Stores command settings until output resolution and playback.
     * Output: A bounded tone request with explicit output selections.
     */
    private sealed class ToneOptions
    {
        public List<string> OutputIds { get; } = new();
        public double DurationSeconds { get; set; } = 3.0;
        public double FrequencyHz { get; set; } = 440.0;
        public double Amplitude { get; set; } = 0.05;
    }
}

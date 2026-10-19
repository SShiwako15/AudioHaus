using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AudioHaus.Interop;

namespace AudioHaus.Diagnostics;

/*
 * Name: FilePlaybackConsole
 * Input: One or two local files, their output selections, and console commands.
 * Process: Opens independent native players and dispatches playback controls.
 * Output: Playback status, diagnostics, and a session exit code.
 */
internal static class FilePlaybackConsole
{
    /*
     * Name: Run
     * Input: Command-line arguments beginning with files.
     * Process: Validates routes, opens stopped players, and owns their lifetimes.
     * Output: Exit code 0, 1, or 2; load/open errors propagate to Program.Main.
     */
    internal static int Run(string[] args)
    {
        if (args.Length == 2 && (args[1] == "--help" || args[1] == "-h"))
        {
            PrintUsage(Console.Out);
            return 0;
        }

        if (!TryParseOptions(args, out FileOptions options, out string error))
        {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error);
            return 2;
        }

        IReadOnlyList<AudioEndpoint> endpoints = new AudioEndpointEnumerator().Enumerate();
        var outputs = new List<AudioEndpoint>();
        foreach (FileSelection selection in options.Selections)
        {
            AudioEndpoint? endpoint = selection.OutputId == "default"
                ? endpoints.FirstOrDefault(item => item.IsDefaultMultimedia)
                : endpoints.FirstOrDefault(item => string.Equals(item.Id, selection.OutputId, StringComparison.Ordinal));
            if (endpoint is null)
            {
                Console.Error.WriteLine($"Output not found: {selection.OutputId}. Run without arguments to list exact endpoint IDs.");
                return 2;
            }

            if (endpoint.State != AudioHausEndpointState.Active)
            {
                Console.Error.WriteLine($"Output is not active: {endpoint.DisplayName} ({endpoint.State}). Connect and enable it in Windows, then list endpoints again.");
                return 2;
            }

            if (outputs.Any(item => string.Equals(item.Id, endpoint.Id, StringComparison.Ordinal)))
            {
                Console.Error.WriteLine("Select a different active output for each file.");
                return 2;
            }

            outputs.Add(endpoint);
        }

        var players = new List<AudioFilePlayer>();
        try
        {
            for (int index = 0; index < options.Selections.Count; index++)
            {
                FileSelection selection = options.Selections[index];
                players.Add(AudioFilePlayer.Open(selection.FilePath, outputs[index].Id, options.Gain));
            }

            Console.WriteLine("Files opened stopped. Use play 1, play 2, or play all to begin.");
            Console.WriteLine(FormattableString.Invariant($"File gain: {options.Gain:G}. Type help for controls; quit or end input to close all players."));
            return RunSession(players, outputs);
        }
        finally
        {
            foreach (AudioFilePlayer player in players)
            {
                player.Dispose();
            }
        }
    }

    /*
     * Name: TryParseOptions
     * Input: File command arguments, including the command name.
     * Process: Pairs files with outputs and validates paths and finite gain.
     * Output: True with options, or false with a command-line error.
     */
    private static bool TryParseOptions(string[] args, out FileOptions options, out string error)
    {
        options = new FileOptions();
        error = string.Empty;
        string? pendingFile = null;
        bool hasGain = false;

        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (option != "--file" && option != "--output" && option != "--gain")
            {
                error = $"Unsupported files option: {option}";
                return false;
            }

            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
            {
                error = $"Missing value for {option}.";
                return false;
            }

            string value = args[index];
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{option} requires a nonempty value.";
                return false;
            }

            switch (option)
            {
                case "--file":
                    if (pendingFile is not null || options.Selections.Count == 2)
                    {
                        error = "Specify one or two --file <path> --output <id> pairs in that order.";
                        return false;
                    }

                    try
                    {
                        pendingFile = Path.GetFullPath(value);
                    }
                    catch (Exception pathError) when (pathError is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        error = $"Invalid file path: {pathError.Message}";
                        return false;
                    }

                    if (!File.Exists(pendingFile))
                    {
                        error = $"Audio file not found: {pendingFile}";
                        return false;
                    }

                    break;
                case "--output":
                    if (pendingFile is null)
                    {
                        error = "Each --output must follow an unpaired --file.";
                        return false;
                    }

                    options.Selections.Add(new FileSelection(pendingFile, value));
                    pendingFile = null;
                    break;
                case "--gain":
                    if (hasGain)
                    {
                        error = "Option may appear only once: --gain";
                        return false;
                    }

                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gain) || !double.IsFinite(gain) || gain < 0.0 || gain > 1.0)
                    {
                        error = "--gain requires a finite number from 0 through 1 using a decimal point.";
                        return false;
                    }

                    options.Gain = gain;
                    hasGain = true;
                    break;
            }
        }

        if (pendingFile is not null || options.Selections.Count == 0)
        {
            error = "Specify one or two --file <path> --output <id> pairs.";
            return false;
        }

        return true;
    }

    /*
     * Name: RunSession
     * Input: Open players and their resolved output snapshots.
     * Process: Reads commands, controls targets independently, and retains errors.
     * Output: Exit code 0 or 1 when quit or end of input closes the session.
     */
    private static int RunSession(IReadOnlyList<AudioFilePlayer> players, IReadOnlyList<AudioEndpoint> outputs)
    {
        bool failed = PrintStatus(players, outputs);
        while (true)
        {
            if (!Console.IsInputRedirected)
            {
                Console.Write("files> ");
            }

            string? line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            string command = words[0].ToLowerInvariant();
            if (command == "quit" && words.Length == 1)
            {
                break;
            }

            if (command == "help" && words.Length == 1)
            {
                PrintCommands(Console.Out);
                continue;
            }

            if (command == "status" && words.Length == 1)
            {
                failed |= PrintStatus(players, outputs);
                continue;
            }

            bool isTransport = command == "play" || command == "pause" || command == "stop";
            bool isSeek = command == "seek";
            if ((!isTransport || words.Length != 2) && (!isSeek || words.Length != 3))
            {
                Console.Error.WriteLine("Invalid command. Type help for available controls.");
                continue;
            }

            if (!TryGetTargets(words[1], players.Count, isTransport, out int[] targets))
            {
                Console.Error.WriteLine(isSeek
                    ? "Seek requires an available player number (1 or 2)."
                    : "Select an available player number (1 or 2), or all.");
                continue;
            }

            TimeSpan position = TimeSpan.Zero;
            if (isSeek)
            {
                if (!double.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0.0)
                {
                    Console.Error.WriteLine("Seek requires finite, nonnegative seconds using a decimal point.");
                    continue;
                }

                try
                {
                    position = TimeSpan.FromSeconds(seconds);
                }
                catch (OverflowException)
                {
                    Console.Error.WriteLine("Seek position exceeds the supported time range.");
                    continue;
                }
            }

            foreach (int target in targets)
            {
                try
                {
                    AudioFilePlayer player = players[target];
                    switch (command)
                    {
                        case "play":
                            player.Play();
                            break;
                        case "pause":
                            player.Pause();
                            break;
                        case "stop":
                            player.Stop();
                            break;
                        case "seek":
                            player.Seek(position);
                            break;
                    }
                }
                catch (Exception controlError) when (controlError is InvalidOperationException or ArgumentException)
                {
                    failed = true;
                    Console.Error.WriteLine($"Player {target + 1} {command} failed: {controlError.Message}");
                }
            }

            failed |= PrintStatus(players, outputs);
        }

        failed |= players.Any(player => player.GetStatus().State == FilePlaybackState.Failed);
        Console.WriteLine("Closing file players.");
        return failed ? 1 : 0;
    }

    /*
     * Name: TryGetTargets
     * Input: Target token, player count, and whether all is permitted.
     * Process: Resolves a player number or all players without changing playback.
     * Output: True with zero-based player indices, or false for an invalid target.
     */
    private static bool TryGetTargets(string value, int playerCount, bool allowAll, out int[] targets)
    {
        targets = Array.Empty<int>();
        if (allowAll && string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            targets = Enumerable.Range(0, playerCount).ToArray();
            return true;
        }

        if ((value == "1" || value == "2") && int.Parse(value, CultureInfo.InvariantCulture) <= playerCount)
        {
            targets = new[] { int.Parse(value, CultureInfo.InvariantCulture) - 1 };
            return true;
        }

        return false;
    }

    /*
     * Name: PrintStatus
     * Input: Open players and matching output snapshots.
     * Process: Reads native state and prints route, position, format, and error data.
     * Output: True if a player has failed; status-read errors propagate to Main.
     */
    private static bool PrintStatus(IReadOnlyList<AudioFilePlayer> players, IReadOnlyList<AudioEndpoint> outputs)
    {
        bool failed = false;
        for (int index = 0; index < players.Count; index++)
        {
            AudioFilePlayer player = players[index];
            FilePlaybackStatus status = player.GetStatus();
            Console.WriteLine();
            Console.WriteLine($"[{index + 1}] {Path.GetFileName(player.FilePath)}");
            Console.WriteLine($"  File: {player.FilePath}");
            Console.WriteLine($"  Output: {outputs[index].DisplayName}");
            Console.WriteLine($"  ID: {player.EndpointId}");
            Console.WriteLine(FormattableString.Invariant($"  State: {status.State}; Position: {status.Position.TotalSeconds:F3} / {status.Duration.TotalSeconds:F3} seconds"));
            Console.WriteLine($"  Format: {status.SampleRate} Hz, {status.ChannelCount} channels; Frames submitted: {status.FramesSubmitted}");
            Console.WriteLine($"  HRESULT: 0x{status.HResult:X8}");
            failed |= status.State == FilePlaybackState.Failed || (status.HResult & 0x80000000u) != 0;
        }

        return failed;
    }

    /*
     * Name: PrintUsage
     * Input: Writer for help or argument errors.
     * Process: Describes file routes, gain, and independent playback controls.
     * Output: Usage text; writer errors propagate to Program.Main.
     */
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: AudioHaus.Diagnostics files --file <path> --output default|<endpoint-id>");
        writer.WriteLine("           [--file <path2> --output <endpoint-id2>] [--gain 0.15]");
        writer.WriteLine("Use one or two file/output pairs in that order, quoting paths and exact endpoint IDs.");
        writer.WriteLine("WAV, MP3, and M4A decoding uses Windows Media Foundation. Files must exist and outputs must be active.");
        writer.WriteLine("Two files require different outputs. Gain applies to both files, is finite 0..1, and may appear once.");
        writer.WriteLine("Files open stopped. No audio plays until a play command; quit or end input closes all players.");
        PrintCommands(writer);
    }

    /*
     * Name: PrintCommands
     * Input: Writer for console command help.
     * Process: Lists transport and session commands.
     * Output: Control syntax and seek behavior.
     */
    private static void PrintCommands(TextWriter writer)
    {
        writer.WriteLine("Commands: play 1|2|all; pause 1|2|all; stop 1|2|all; seek 1|2 <seconds>; status; help; quit");
        writer.WriteLine("Stop resets that file to zero. Seek uses nonnegative seconds and clamps to file duration.");
        writer.WriteLine("Controls affect only selected files. Status reads state without changing playback.");
    }

    /*
     * Name: FileOptions
     * Input: Parsed file/output pairs and global gain.
     * Process: Stores validated command settings until player creation.
     * Output: A file session request with explicit routes.
     */
    private sealed class FileOptions
    {
        public List<FileSelection> Selections { get; } = new();
        public double Gain { get; set; } = 0.15;
    }

    /*
     * Name: FileSelection
     * Input: Full file path and an output selection token.
     * Process: Associates a file with its requested playback endpoint.
     * Output: An immutable route request.
     */
    private sealed class FileSelection
    {
        public string FilePath { get; }
        public string OutputId { get; }

        /*
         * Name: FileSelection
         * Input: Full file path and output ID or default token.
         * Process: Stores values already validated by the option parser.
         * Output: A file/output selection.
         */
        public FileSelection(string filePath, string outputId)
        {
            FilePath = filePath;
            OutputId = outputId;
        }
    }
}

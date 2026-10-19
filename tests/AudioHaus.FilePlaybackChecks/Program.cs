using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AudioHaus.Interop;

namespace AudioHaus.FilePlaybackChecks;

/*
 * Name: Program
 * Input: Optional user media paths, selected outputs, and --hardware.
 * Process: Generates fixtures, checks silent file transports, and optionally
 *          verifies two independent players on distinct real WASAPI endpoints.
 * Output: PASS/FAIL evidence with separate acoustic and hardware limitations.
 */
internal static class Program
{
    /*
     * Name: Main
     * Input: Command-line arguments described by --help.
     * Process: Runs validation using temporary fixtures and selected real outputs.
     * Output: Exit code 0 for requested checks passed, 1 for failure, or 2 for
     *         unavailable required hardware. Temporary fixtures are retained.
     */
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            {
                PrintUsage(Console.Out);
                return 0;
            }

            Dictionary<string, string> options = ParseOptions(args, out bool hardware);
            if (hardware && (!options.ContainsKey("--file-a") || !options.ContainsKey("--file-b")))
            {
                throw new ArgumentException("--hardware requires --file-a and --file-b; choose tracks at least five seconds long.");
            }

            string fixtureDirectory = Path.Combine(Path.GetTempPath(), "AudioHaus.FilePlaybackChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureDirectory);
            string monoPath = Path.Combine(fixtureDirectory, "mono-22050.wav");
            string stereoPath = Path.Combine(fixtureDirectory, "stereo-44100.wav");
            string corruptPath = Path.Combine(fixtureDirectory, "corrupt.wav");
            WriteWave(monoPath, sampleRate: 22050, channels: 1, durationSeconds: 1.6, frequency: 330);
            WriteWave(stereoPath, sampleRate: 44100, channels: 2, durationSeconds: 2.0, frequency: 550);
            File.WriteAllText(corruptPath, "This file deliberately contains no audio container.");
            Console.WriteLine($"Generated fixture directory: {fixtureDirectory}");

            CheckArguments(monoPath, Path.Combine(fixtureDirectory, "missing.wav"));
            Console.WriteLine("PASS: managed file/endpoint/gain validation and missing-file failure.");
            CheckInteropLayout();

            AudioEndpoint[] active = new AudioEndpointEnumerator().Enumerate()
                .Where(item => item.State == AudioHausEndpointState.Active)
                .OrderByDescending(item => item.IsDefaultMultimedia)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AudioEndpoint? outputA = SelectOutput(active, options.GetValueOrDefault("--output-a"), excludedId: null);
            if (outputA is null)
            {
                Console.WriteLine("File playback: NOT VERIFIED; no selected active output is available.");
                return 2;
            }

            CheckNativeFailures(monoPath, corruptPath, outputA.Id);
            CheckFixtureLifecycle(monoPath, outputA.Id, expectedSeconds: 1.6);
            CheckFixtureLifecycle(stereoPath, outputA.Id, expectedSeconds: 2.0);

            foreach (string filePath in new[] { options.GetValueOrDefault("--file-a"), options.GetValueOrDefault("--file-b") }
                .Where(path => path is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CheckUserMedia(filePath, outputA.Id);
            }

            if (!hardware)
            {
                Console.WriteLine("PASS: requested file playback checks used silent PCM on one real WASAPI endpoint.");
                Console.WriteLine("Independent two-device playback: NOT VERIFIED. Use --hardware with two active outputs and two media files.");
                return 0;
            }

            AudioEndpoint? outputB = SelectOutput(active, options.GetValueOrDefault("--output-b"), outputA.Id);
            if (outputB is null || string.Equals(outputA.Id, outputB.Id, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Independent two-device playback: NOT VERIFIED; two distinct active outputs are required.");
                return 2;
            }

            Console.WriteLine($"Audible independent playback: {outputA.DisplayName} + {outputB.DisplayName}");
            CheckIndependentPlayers(options["--file-a"], options["--file-b"], outputA.Id, outputB.Id);
            Console.WriteLine("PASS: independent player state, position, and frame progression on two real WASAPI endpoints.");
            Console.WriteLine("Listening confirmation required: hear each track only on its selected device.");
            Console.WriteLine("These assertions do not prove acoustic isolation, Bluetooth transport, or synchronization.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL: {error.Message}");
            return 1;
        }
    }

    /*
     * Name: ParseOptions
     * Input: Command-line tokens.
     * Process: Reads optional media/output selections and one --hardware switch.
     * Output: Parsed values; throws ArgumentException for unknown or duplicate options.
     */
    private static Dictionary<string, string> ParseOptions(string[] args, out bool hardware)
    {
        hardware = false;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; ++index)
        {
            string name = args[index];
            if (name == "--hardware")
            {
                Require(!hardware, "--hardware was supplied twice.");
                hardware = true;
                continue;
            }

            if (name is not ("--file-a" or "--file-b" or "--output-a" or "--output-b") || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Unknown or incomplete option: {name}. Use --help.");
            }

            string value = args[++index];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(name, value))
            {
                throw new ArgumentException($"Invalid or duplicate option: {name}.");
            }
        }
        return options;
    }

    /*
     * Name: WriteWave
     * Input: Temporary path, PCM sample rate/channel count, duration, and frequency.
     * Process: Writes a RIFF/WAVE PCM16 sine fixture without external tools or packages.
     * Output: A valid mono or stereo WAV file; filesystem failures propagate.
     */
    private static void WriteWave(string filePath, int sampleRate, short channels, double durationSeconds, double frequency)
    {
        int frames = checked((int)Math.Round(sampleRate * durationSeconds));
        int dataBytes = checked(frames * channels * sizeof(short));
        using var writer = new BinaryWriter(File.Create(filePath), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(short));
        writer.Write((short)(channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int frame = 0; frame < frames; ++frame)
        {
            for (int channel = 0; channel < channels; ++channel)
            {
                double channelFrequency = frequency + channel * 80;
                writer.Write((short)(Math.Sin(2 * Math.PI * channelFrequency * frame / sampleRate) * short.MaxValue * 0.2));
            }
        }
    }

    /*
     * Name: CheckArguments
     * Input: Existing fixture path and a missing path in the same temporary directory.
     * Process: Rejects invalid paths, endpoint strings, gain values, and missing files.
     * Output: Throws when rejection differs from the managed contract; no playback.
     */
    private static void CheckArguments(string filePath, string missingPath)
    {
        const string endpoint = "AudioHaus.Validation.NotAnEndpoint";
        foreach (string? invalidPath in new string?[] { null, "", "invalid\0path.wav" })
        {
            ExpectException<ArgumentException>(() => { using var unused = AudioFilePlayer.Open(invalidPath!, endpoint); });
        }
        foreach (string? invalidEndpoint in new string?[] { null, "", "invalid\0endpoint" })
        {
            ExpectException<ArgumentException>(() => { using var unused = AudioFilePlayer.Open(filePath, invalidEndpoint!); });
        }
        foreach (double gain in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.001, 1.001 })
        {
            ExpectException<ArgumentException>(() => { using var unused = AudioFilePlayer.Open(filePath, endpoint, gain); });
        }
        ExpectException<FileNotFoundException>(() => { using var unused = AudioFilePlayer.Open(missingPath, endpoint); });
    }

    /*
     * Name: CheckInteropLayout
     * Input: Actual internal file-player status type in the production assembly.
     * Process: Checks native packing and all nine field offsets without exposing internals.
     * Output: PASS for the 56-byte ABI or an assertion failure.
     */
    private static void CheckInteropLayout()
    {
        Type statusType = typeof(AudioFilePlayer).Assembly.GetType("AudioHaus.Interop.AudioHausFilePlayerInfo", throwOnError: true)!;
        Require(Marshal.SizeOf(statusType) == 56, "Native file-player status must occupy 56 bytes.");
        string[] fields = { "State", "HResult", "SampleRate", "ChannelCount", "Position100Ns", "Duration100Ns", "FramesSubmitted", "StartQpc", "StopQpc" };
        int[] offsets = { 0, 4, 8, 12, 16, 24, 32, 40, 48 };
        for (int index = 0; index < fields.Length; ++index)
        {
            Require(Marshal.OffsetOf(statusType, fields[index]).ToInt32() == offsets[index], $"Native file-player field {fields[index]} has the wrong offset.");
        }
        Require((uint)FilePlaybackState.Stopped == 0 && (uint)FilePlaybackState.Playing == 1 &&
            (uint)FilePlaybackState.Paused == 2 && (uint)FilePlaybackState.Ended == 3 && (uint)FilePlaybackState.Failed == 4,
            "Managed transport states differ from native state values.");
        Console.WriteLine("PASS: 56-byte file-player status ABI, all nine field offsets, and transport-state values.");
    }

    /*
     * Name: SelectOutput
     * Input: Active endpoint snapshot, optional exact ID/default, and an excluded ID.
     * Process: Uses an explicit active endpoint or the preferred eligible endpoint.
     * Output: Selected endpoint or null; unavailable explicit selections remain unverified.
     */
    private static AudioEndpoint? SelectOutput(AudioEndpoint[] active, string? requestedId, string? excludedId)
    {
        if (requestedId is not null && !string.Equals(requestedId, "default", StringComparison.OrdinalIgnoreCase))
        {
            return active.FirstOrDefault(item => string.Equals(item.Id, requestedId, StringComparison.OrdinalIgnoreCase));
        }
        return active.FirstOrDefault(item => !string.Equals(item.Id, excludedId, StringComparison.OrdinalIgnoreCase));
    }

    /*
     * Name: CheckNativeFailures
     * Input: Valid fixture, corrupt fixture, and a real output endpoint.
     * Process: Verifies endpoint/decoder failures retain native HRESULT diagnostics.
     * Output: PASS for controlled failures; accepted bad input fails the check.
     */
    private static void CheckNativeFailures(string validPath, string corruptPath, string endpointId)
    {
        InvalidOperationException invalidOutput = ExpectException<InvalidOperationException>(() =>
        {
            using var unused = AudioFilePlayer.Open(validPath, $"AudioHaus.Validation.Missing.{Guid.NewGuid():N}", gain: 0);
        });
        Require(invalidOutput.Message.Contains("0x", StringComparison.OrdinalIgnoreCase), "Invalid endpoint failure did not include HRESULT.");

        InvalidOperationException corruptMedia = ExpectException<InvalidOperationException>(() =>
        {
            using var unused = AudioFilePlayer.Open(corruptPath, endpointId, gain: 0);
        });
        Require(corruptMedia.Message.Contains("0x", StringComparison.OrdinalIgnoreCase), "Corrupt media failure did not include HRESULT.");
        Console.WriteLine("PASS: invalid endpoint and corrupt media return HRESULT diagnostics without starting playback.");
    }

    /*
     * Name: CheckFixtureLifecycle
     * Input: Generated fixture, real output, and expected container duration.
     * Process: Checks conversion, frozen pause, seeks, stop/reset, EOF/restart,
     *          lifetime frame counts, and disposed-handle protection at zero gain.
     * Output: PASS for a complete transport lifecycle or a descriptive assertion failure.
     */
    private static void CheckFixtureLifecycle(string filePath, string endpointId, double expectedSeconds)
    {
        using var player = AudioFilePlayer.Open(filePath, endpointId, gain: 0);
        Require(Path.GetFullPath(player.FilePath) == Path.GetFullPath(filePath), "Player did not retain its source path.");
        Require(string.Equals(player.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase), "Player did not retain its endpoint ID.");
        FilePlaybackStatus initial = player.GetStatus();
        CheckFormat(initial);
        Require(initial.State == FilePlaybackState.Stopped && initial.Position == TimeSpan.Zero, "Open must initialize stopped at zero.");
        Require(initial.FramesSubmitted == 0 && initial.StartQpc == 0, "Opening a file must not start or submit playback.");
        Require(Math.Abs(initial.Duration.TotalSeconds - expectedSeconds) < 0.1, "Decoded fixture duration differs from WAV header duration.");
        ExpectException<ArgumentException>(() => player.Seek(TimeSpan.FromTicks(-1)));

        player.Seek(TimeSpan.MaxValue);
        CheckPosition(player.GetStatus(), initial.Duration, toleranceMs: 2);
        player.Seek(TimeSpan.Zero);
        player.Play();
        FilePlaybackStatus playing = WaitForStatus(player, status => status.State == FilePlaybackState.Playing && status.Position.TotalMilliseconds > 40, "initial playback");
        Require(playing.StartQpc > 0 && playing.FramesSubmitted > 0, "Playing stream has no start timestamp or submitted PCM.");

        player.Pause();
        FilePlaybackStatus paused = player.GetStatus();
        Require(paused.State == FilePlaybackState.Paused, "Pause did not enter Paused state.");
        Thread.Sleep(150);
        FilePlaybackStatus frozen = player.GetStatus();
        CheckPosition(frozen, paused.Position, toleranceMs: 2);
        Require(frozen.FramesSubmitted == paused.FramesSubmitted, "Paused renderer continued submitting PCM.");

        TimeSpan pausedSeek = TimeSpan.FromMilliseconds(700);
        player.Seek(pausedSeek);
        FilePlaybackStatus afterPausedSeek = player.GetStatus();
        Require(afterPausedSeek.State == FilePlaybackState.Paused, "Paused seek changed transport state.");
        CheckPosition(afterPausedSeek, pausedSeek, toleranceMs: 20);
        Require(afterPausedSeek.FramesSubmitted >= frozen.FramesSubmitted, "Seeking reset lifetime submitted-frame count.");
        Thread.Sleep(75);
        CheckPosition(player.GetStatus(), afterPausedSeek.Position, toleranceMs: 2);

        player.Play();
        FilePlaybackStatus resumed = WaitForStatus(player, status => status.State == FilePlaybackState.Playing && status.Position > pausedSeek + TimeSpan.FromMilliseconds(25), "resume after paused seek", afterPausedSeek.FramesSubmitted);
        player.Seek(TimeSpan.FromMilliseconds(200));
        FilePlaybackStatus afterPlayingSeek = player.GetStatus();
        Require(afterPlayingSeek.State == FilePlaybackState.Playing, "Playing seek changed transport state.");
        CheckPosition(afterPlayingSeek, TimeSpan.FromMilliseconds(200), toleranceMs: 80);
        Require(afterPlayingSeek.FramesSubmitted >= resumed.FramesSubmitted, "Playing seek reset lifetime submitted-frame count.");

        player.Stop();
        FilePlaybackStatus stopped = player.GetStatus();
        Require(stopped.State == FilePlaybackState.Stopped && stopped.Position == TimeSpan.Zero, "Stop must reset position and transport state.");
        Require(stopped.FramesSubmitted >= afterPlayingSeek.FramesSubmitted, "Stop reset lifetime submitted-frame count.");
        player.Seek(initial.Duration - TimeSpan.FromMilliseconds(120));
        player.Play();
        FilePlaybackStatus ended = WaitForStatus(player, status => status.State == FilePlaybackState.Ended, "end of file", stopped.FramesSubmitted);
        CheckPosition(ended, initial.Duration, toleranceMs: 30);
        Require(ended.StopQpc > 0, "Completed stream has no stop timestamp.");

        player.Seek(TimeSpan.FromMilliseconds(100));
        Require(player.GetStatus().State == FilePlaybackState.Stopped, "Seeking below EOF from Ended must enter Stopped.");
        player.Seek(initial.Duration - TimeSpan.FromMilliseconds(80));
        player.Play();
        FilePlaybackStatus endedAgain = WaitForStatus(player, status => status.State == FilePlaybackState.Ended, "second EOF", ended.FramesSubmitted);
        player.Play();
        FilePlaybackStatus restarted = WaitForStatus(player, status => status.State == FilePlaybackState.Playing && status.Position.TotalMilliseconds > 25, "restart from EOF", endedAgain.FramesSubmitted);
        Require(restarted.Position < TimeSpan.FromMilliseconds(500), "Play from Ended did not rewind to the beginning.");
        player.Stop();
        player.Dispose();
        player.Dispose();
        ExpectException<ObjectDisposedException>(() => player.Play());
        ExpectException<ObjectDisposedException>(() => player.Pause());
        ExpectException<ObjectDisposedException>(() => player.Stop());
        ExpectException<ObjectDisposedException>(() => player.Seek(TimeSpan.Zero));
        ExpectException<ObjectDisposedException>(() => player.GetStatus());
        Console.WriteLine($"PASS: {Path.GetFileName(filePath)} conversion, transport, seek, EOF/restart, frame lifetime, and disposal.");
    }

    /*
     * Name: CheckUserMedia
     * Input: User-owned media path and real output endpoint.
     * Process: Opens through Media Foundation, checks metadata/conversion, decodes
     *          and queues PCM, pauses, seeks, and resets using zero playback gain.
     * Output: PASS for the actual file; no user media is copied or modified.
     */
    private static void CheckUserMedia(string filePath, string endpointId)
    {
        using var player = AudioFilePlayer.Open(filePath, endpointId, gain: 0);
        FilePlaybackStatus initial = player.GetStatus();
        CheckFormat(initial);
        Require(initial.Duration > TimeSpan.Zero, "User media has no positive decoded duration.");
        Require(initial.State == FilePlaybackState.Stopped && initial.Position == TimeSpan.Zero, "User media did not open stopped.");
        player.Play();
        FilePlaybackStatus playing = WaitForStatus(player, status => status.State == FilePlaybackState.Playing && status.Position.TotalMilliseconds > 30 && status.FramesSubmitted > 0, "decode user media", timeoutMs: 5000);
        player.Pause();
        TimeSpan target = TimeSpan.FromTicks(initial.Duration.Ticks / 2);
        player.Seek(target);
        FilePlaybackStatus seeked = player.GetStatus();
        Require(seeked.State == FilePlaybackState.Paused, "User media seek did not preserve pause.");
        CheckPosition(seeked, target, toleranceMs: 100);
        player.Play();
        WaitForStatus(player, status => status.State == FilePlaybackState.Playing && status.Position > target + TimeSpan.FromMilliseconds(25), "decode user media after seek", playing.FramesSubmitted, timeoutMs: 5000);
        player.Stop();
        Require(player.GetStatus().Position == TimeSpan.Zero, "User media stop did not reset position.");
        Console.WriteLine($"PASS: actual {Path.GetExtension(filePath)} media, duration {initial.Duration.TotalSeconds:F2}s, 48 kHz stereo decode, pause, seek/resume, and stop: {filePath}");
    }

    /*
     * Name: CheckIndependentPlayers
     * Input: Two user media paths and distinct real active endpoint IDs.
     * Process: Plays at 0.15 gain, pauses/seeks/stops/restarts A while requiring
     *          B to remain Playing with advancing position and submitted frames.
     * Output: Independent WASAPI transport evidence; audible routing needs listening.
     */
    private static void CheckIndependentPlayers(string fileA, string fileB, string outputA, string outputB)
    {
        Require(!string.Equals(outputA, outputB, StringComparison.OrdinalIgnoreCase), "Hardware validation requires distinct output endpoints.");
        using var first = AudioFilePlayer.Open(fileA, outputA, gain: 0.15);
        using var second = AudioFilePlayer.Open(fileB, outputB, gain: 0.15);
        Require(first.GetStatus().Duration.TotalSeconds >= 5 && second.GetStatus().Duration.TotalSeconds >= 5, "Hardware validation requires both tracks to last at least five seconds.");
        first.Play();
        second.Play();
        FilePlaybackStatus firstPlaying = WaitForStatus(first, status => status.State == FilePlaybackState.Playing && status.Position.TotalMilliseconds > 30, "hardware player A");
        FilePlaybackStatus secondPlaying = WaitForStatus(second, status => status.State == FilePlaybackState.Playing && status.Position.TotalMilliseconds > 30, "hardware player B");
        Thread.Sleep(400);
        CheckAdvancing(firstPlaying, first.GetStatus(), "A while both play");
        CheckAdvancing(secondPlaying, second.GetStatus(), "B while both play");

        first.Pause();
        FilePlaybackStatus paused = first.GetStatus();
        FilePlaybackStatus before = second.GetStatus();
        Thread.Sleep(400);
        FilePlaybackStatus stillPaused = first.GetStatus();
        Require(stillPaused.State == FilePlaybackState.Paused && stillPaused.FramesSubmitted == paused.FramesSubmitted, "A did not remain paused.");
        CheckPosition(stillPaused, paused.Position, toleranceMs: 2);
        CheckAdvancing(before, second.GetStatus(), "B while A pauses");

        TimeSpan seekTarget = TimeSpan.FromTicks(first.GetStatus().Duration.Ticks / 2);
        before = second.GetStatus();
        first.Seek(seekTarget);
        CheckPosition(first.GetStatus(), seekTarget, toleranceMs: 100);
        Require(first.GetStatus().State == FilePlaybackState.Paused, "A seek did not preserve pause.");
        Thread.Sleep(300);
        CheckAdvancing(before, second.GetStatus(), "B while A seeks");
        first.Play();
        WaitForStatus(first, status => status.State == FilePlaybackState.Playing && status.Position > seekTarget + TimeSpan.FromMilliseconds(25), "hardware resume A");

        first.Stop();
        Require(first.GetStatus().State == FilePlaybackState.Stopped && first.GetStatus().Position == TimeSpan.Zero, "A stop did not reset only A.");
        before = second.GetStatus();
        Thread.Sleep(400);
        CheckAdvancing(before, second.GetStatus(), "B while A stops");
        first.Play();
        FilePlaybackStatus restarted = WaitForStatus(first, status => status.State == FilePlaybackState.Playing && status.Position.TotalMilliseconds > 25, "hardware restart A");
        before = second.GetStatus();
        Thread.Sleep(400);
        CheckAdvancing(restarted, first.GetStatus(), "A after restart");
        CheckAdvancing(before, second.GetStatus(), "B while A restarts");
        first.Stop();
        second.Stop();
        Require(first.GetStatus().State == FilePlaybackState.Stopped && second.GetStatus().State == FilePlaybackState.Stopped, "Final Stop did not stop both players.");
    }

    /*
     * Name: WaitForStatus
     * Input: Player, completion predicate, description, frame floor, and timeout.
     * Process: Polls bounded status while rejecting native failure or frame regression.
     * Output: Matching status; throws on failure, regressed counters, or timeout.
     */
    private static FilePlaybackStatus WaitForStatus(AudioFilePlayer player, Func<FilePlaybackStatus, bool> predicate, string description, ulong minimumFrames = 0, int timeoutMs = 3000)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            FilePlaybackStatus status = player.GetStatus();
            CheckFormat(status);
            Require(status.FramesSubmitted >= minimumFrames, $"Lifetime frame count regressed during {description}.");
            minimumFrames = status.FramesSubmitted;
            if (predicate(status))
            {
                return status;
            }
            Thread.Sleep(15);
        } while (timer.ElapsedMilliseconds < timeoutMs);
        throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    /*
     * Name: CheckFormat
     * Input: File player status snapshot.
     * Process: Requires normalized PCM and a healthy native playback state.
     * Output: Returns for 48 kHz stereo and HRESULT success; otherwise throws.
     */
    private static void CheckFormat(FilePlaybackStatus status)
    {
        Require(status.SampleRate == 48000 && status.ChannelCount == 2, "Decoded stream must be normalized to 48 kHz stereo.");
        Require(status.State != FilePlaybackState.Failed && status.HResult == 0, $"Native file player failed: 0x{status.HResult:X8}.");
    }

    /*
     * Name: CheckPosition
     * Input: Status snapshot, expected playback position, and tolerance.
     * Process: Compares logical media position against the transport expectation.
     * Output: Returns on a matching position or throws with observed values.
     */
    private static void CheckPosition(FilePlaybackStatus status, TimeSpan expected, double toleranceMs)
    {
        Require(Math.Abs((status.Position - expected).TotalMilliseconds) <= toleranceMs,
            $"Expected position {expected.TotalMilliseconds:F1} ms, received {status.Position.TotalMilliseconds:F1} ms.");
    }

    /*
     * Name: CheckAdvancing
     * Input: Before/after snapshots and context.
     * Process: Requires a healthy Playing state, forward media time, and new PCM.
     * Output: Returns for independent progression or throws a contextual failure.
     */
    private static void CheckAdvancing(FilePlaybackStatus before, FilePlaybackStatus after, string description)
    {
        CheckFormat(after);
        Require(after.State == FilePlaybackState.Playing && after.Position > before.Position && after.FramesSubmitted > before.FramesSubmitted,
            $"Playback did not advance for {description}.");
    }

    /*
     * Name: ExpectException
     * Input: Invalid operation and expected exception type.
     * Process: Requires the expected managed failure without leaking opened players.
     * Output: The expected exception or a failed-check exception.
     */
    private static TException ExpectException<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    /*
     * Name: Require
     * Input: Assertion condition and failure description.
     * Process: Reports failed checks consistently.
     * Output: Returns on success or throws InvalidOperationException.
     */
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /*
     * Name: PrintUsage
     * Input: Output writer.
     * Process: Describes silent checks and opt-in audible two-output validation.
     * Output: Usage and exit-code text.
     */
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: AudioHaus.FilePlaybackChecks [--hardware] [--file-a <path>] [--file-b <path>] [--output-a <id|default>] [--output-b <id>]");
        writer.WriteLine("Default: generated mono/stereo WAV transport checks at gain 0 on one active output.");
        writer.WriteLine("Optional --file-a/--file-b: silently validate actual WAV/MP3/M4A decoding and seek/resume.");
        writer.WriteLine("--hardware requires both media files (at least five seconds each) and two distinct active outputs; plays at gain 0.15.");
        writer.WriteLine("Unspecified outputs use the multimedia default first, then another active endpoint.");
        writer.WriteLine("Temporary generated fixtures are retained; user files are read only and never copied.");
        writer.WriteLine("Exit codes: 0 requested checks passed; 1 failure; 2 required output hardware unavailable.");
    }
}

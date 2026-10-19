using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AudioHaus.Interop;

namespace AudioHaus.RoutingChecks;

/*
 * Name: Program
 * Input: Optional --hardware, output IDs, and real media paths.
 * Process: Checks routing contracts, then optionally measures quiet generated
 *          audio through two real output mixers and exercises live graph changes.
 * Output: Reproducible software/PCM evidence, separate from listening confirmation.
 */
internal static class Program
{
    /*
     * Name: Main
     * Input: Command-line arguments; --help describes available checks.
     * Process: Creates fixtures, validates software behavior, and runs opt-in outputs.
     * Output: Exit code 0 for requested checks passed, 1 for failure, or 2 when
     *         required active hardware is unavailable. Fixtures remain in temp.
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
            string directory = Path.Combine(Path.GetTempPath(), "AudioHaus.RoutingChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string fileA = Path.Combine(directory, "mono-22050-330hz.wav");
            string fileB = Path.Combine(directory, "stereo-44100-550hz.wav");
            string corrupt = Path.Combine(directory, "corrupt.wav");
            WriteWave(fileA, sampleRate: 22050, channels: 1, frequency: 330);
            WriteWave(fileB, sampleRate: 44100, channels: 2, frequency: 550);
            File.WriteAllText(corrupt, "Deliberately invalid audio container.");
            Console.WriteLine($"Generated fixtures: {directory}");
            CheckLayouts();
            CheckSoftware(fileA, fileB, corrupt, Path.Combine(directory, "missing.wav"));

            AudioEndpoint[] active = new AudioEndpointEnumerator().Enumerate()
                .Where(item => item.State == AudioHausEndpointState.Active)
                .OrderByDescending(item => item.IsDefaultMultimedia)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!hardware)
            {
                Console.WriteLine("PASS: routing software checks; no output streams opened.");
                Console.WriteLine("Two-output routing: NOT VERIFIED. Use --hardware with two distinct active endpoints.");
                return active.Length == 0 ? 2 : 0;
            }

            AudioEndpoint? outputA = SelectOutput(active, options.GetValueOrDefault("--output-a"), null);
            AudioEndpoint? outputB = SelectOutput(active, options.GetValueOrDefault("--output-b"), outputA?.Id);
            if (outputA is null || outputB is null || string.Equals(outputA.Id, outputB.Id, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Two-output routing: NOT VERIFIED; two distinct selected active outputs are required.");
                return 2;
            }

            Console.WriteLine($"Quiet synthetic routing: {outputA.DisplayName} + {outputB.DisplayName}");
            CheckHardware(fileA, fileB, outputA.Id, outputB.Id);
            CheckTransport(fileA, outputA.Id);
            if (options.TryGetValue("--file-a", out string? realA) && options.TryGetValue("--file-b", out string? realB))
            {
                CheckRealFiles(realA, realB, outputA.Id, outputB.Id);
            }
            Console.WriteLine("PASS: two real WASAPI output mixers, fan-out, summing, route controls, and graph cleanup.");
            Console.WriteLine("Measurements describe PCM submitted before device processing; listening is required to confirm audible routing.");
            Console.WriteLine("No acoustic synchronization or Bluetooth transport claim is inferred from these counters.");
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
     * Process: Parses hardware selection and optional paired real-file paths.
     * Output: Option values; invalid combinations throw ArgumentException.
     */
    private static Dictionary<string, string> ParseOptions(string[] args, out bool hardware)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        hardware = false;
        for (int index = 0; index < args.Length; ++index)
        {
            string option = args[index];
            if (option == "--hardware")
            {
                Require(!hardware, "Duplicate --hardware option.");
                hardware = true;
                continue;
            }
            if (option is not ("--output-a" or "--output-b" or "--file-a" or "--file-b") || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Unknown or incomplete option: {option}. Use --help.");
            }
            string value = args[++index];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(option, value))
            {
                throw new ArgumentException($"Invalid or duplicate option: {option}.");
            }
        }
        if (options.ContainsKey("--file-a") != options.ContainsKey("--file-b") || (!hardware && options.Count != 0))
        {
            throw new ArgumentException("Media/output options require --hardware; provide both --file-a and --file-b together.");
        }
        return options;
    }

    /*
     * Name: WriteWave
     * Input: Temporary path, PCM16 source format, and sine frequency.
     * Process: Writes a quiet 24-second RIFF fixture using only the BCL.
     * Output: Mono 22.05 kHz or stereo 44.1 kHz audio at 0.2 peak amplitude.
     */
    private static void WriteWave(string path, int sampleRate, short channels, double frequency)
    {
        int frames = sampleRate * 24;
        int bytes = frames * channels * sizeof(short);
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write(channels); writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(short));
        writer.Write((short)(channels * sizeof(short))); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
        for (int frame = 0; frame < frames; ++frame)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * frame / sampleRate) * short.MaxValue * 0.2);
            for (int channel = 0; channel < channels; ++channel) { writer.Write(sample); }
        }
    }

    /*
     * Name: CheckLayouts
     * Input: Internal production interop types and the documented C ABI layouts.
     * Process: Reflects real marshaled sizes and every field offset.
     * Output: PASS for matching 56/80/48-byte snapshots or an assertion failure.
     */
    private static void CheckLayouts()
    {
        CheckLayout("AudioLevel", 16, new[] { "Gain", "muted", "HResult" }, new[] { 0, 8, 12 });
        CheckLayout("SourceSignal", 32, new[] { "Frames", "SumSquares", "LastPeak", "HResult", "reserved" }, new[] { 0, 8, 16, 24, 28 });
        CheckLayout("RoutingSourceInfo", 56,
            new[] { "State", "HResult", "SampleRate", "ChannelCount", "Position100Ns", "Duration100Ns", "FramesDecoded", "CachedFrames", "RouteCount", "Reserved" },
            new[] { 0, 4, 8, 12, 16, 24, 32, 40, 48, 52 });
        CheckLayout("RoutingOutputInfo", 80,
            new[] { "State", "HResult", "SampleRate", "ChannelCount", "FramesSubmitted", "NonSilentFrames", "ClippedSamples", "SumSquares", "LastPeak", "StartQpc", "StopQpc", "RouteCount", "Reserved" },
            new[] { 0, 4, 8, 12, 16, 24, 32, 40, 48, 56, 64, 72, 76 });
        CheckLayout("RoutingRouteInfo", 48,
            new[] { "SourceId", "OutputId", "Muted", "Reserved", "Gain", "FramesRead", "CursorFrame", "Rebases" },
            new[] { 0, 4, 8, 12, 16, 24, 32, 40 });
        Console.WriteLine("PASS: source/output/route ABI sizes 56/80/48 and all field offsets.");
    }

    /*
     * Name: CheckLayout
     * Input: Internal type name, expected size, field names, and byte offsets.
     * Process: Checks the actual managed ABI without exposing production internals.
     * Output: Returns for matching layout or throws with the mismatching field.
     */
    private static void CheckLayout(string name, int size, string[] fields, int[] offsets)
    {
        Type type = typeof(AudioRouter).Assembly.GetType($"AudioHaus.Interop.{name}", throwOnError: true)!;
        Require(Marshal.SizeOf(type) == size, $"{name} has the wrong native size.");
        for (int index = 0; index < fields.Length; ++index)
        {
            Require(Marshal.OffsetOf(type, fields[index]).ToInt32() == offsets[index], $"{name}.{fields[index]} has the wrong byte offset.");
        }
    }

    /*
     * Name: CheckSoftware
     * Input: Two valid fixtures, corrupt file, and missing path.
     * Process: Exercises validation, source metadata, graph failure isolation,
     *          non-reused IDs, and disposed ownership without opening output streams.
     * Output: PASS or a descriptive exception; no audio is played.
     */
    private static void CheckSoftware(string fileA, string fileB, string corrupt, string missing)
    {
        using var router = AudioRouter.Create();
        foreach (string? bad in new string?[] { null, "", "invalid\0value" })
        {
            Expect<ArgumentException>(() => router.AddSource(bad!));
            Expect<ArgumentException>(() => router.AddOutput(bad!));
        }
        Expect<FileNotFoundException>(() => router.AddSource(missing));
        uint sourceA = router.AddSource(fileA);
        uint sourceB = router.AddSource(fileB);
        Require(sourceA != 0 && sourceB != 0 && sourceA != sourceB, "Source IDs must be distinct and nonzero.");
        CheckSource(router.GetSourceStatus(sourceA));
        CheckSource(router.GetSourceStatus(sourceB));
        Require(router.GetSourceLevel(sourceA).Gain == 1 && !router.GetSourceLevel(sourceA).Muted, "Source master must start at unity and unmuted.");
        router.SetSourceLevel(sourceA, 0.25, true);
        Require(router.GetSourceLevel(sourceA).Gain == 0.25 && router.GetSourceLevel(sourceA).Muted, "Source level getter lost the acknowledged target.");
        Require(router.GetSourceSignal(sourceA).Frames == 0, "An unrouted source must have no routed-signal frames.");
        Require(router.GetSourceStatus(sourceA).State == FilePlaybackState.Stopped && router.GetSourceStatus(sourceA).Position == TimeSpan.Zero, "Sources must open stopped at zero.");
        Require(Math.Abs(router.GetSourceStatus(sourceA).Duration.TotalSeconds - 24) < 0.1, "Fixture metadata duration is incorrect.");
        Expect<ArgumentException>(() => router.Seek(sourceA, TimeSpan.FromTicks(-1)));
        foreach (double gain in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.001, 1.001 })
        {
            Expect<ArgumentException>(() => router.Connect(sourceA, 1, gain));
            Expect<ArgumentException>(() => router.SetRouteGain(sourceA, 1, gain));
            Expect<ArgumentException>(() => router.SetSourceLevel(sourceA, gain, false));
            Expect<ArgumentException>(() => router.SetOutputLevel(1, gain, false));
        }
        Require(router.GetSourceLevel(sourceA).Gain == 0.25 && router.GetSourceLevel(sourceA).Muted, "Rejected master targets changed the previous value.");
        ExpectFailure(() => router.GetSourceLevel(uint.MaxValue));
        ExpectFailure(() => router.GetSourceSignal(uint.MaxValue));
        ExpectFailure(() => router.GetOutputLevel(uint.MaxValue));
        ExpectFailure(() => router.SetSourceLevel(uint.MaxValue, 1, false));
        ExpectFailure(() => router.SetOutputLevel(uint.MaxValue, 1, false));
        InvalidOperationException badFile = Expect<InvalidOperationException>(() => router.AddSource(corrupt));
        Require(badFile.Message.Contains("0x", StringComparison.OrdinalIgnoreCase), "Decoder failure must include HRESULT.");
        ExpectFailure(() => router.GetSourceStatus(uint.MaxValue));
        ExpectFailure(() => router.GetOutputStatus(uint.MaxValue));
        ExpectFailure(() => router.Play(uint.MaxValue));
        ExpectFailure(() => router.RemoveOutput(uint.MaxValue));
        ExpectFailure(() => router.AddOutput($"AudioHaus.Validation.Missing.{Guid.NewGuid():N}"));
        router.Seek(sourceA, TimeSpan.MaxValue);
        Require(router.GetSourceStatus(sourceA).Position == router.GetSourceStatus(sourceA).Duration, "Maximum seek must clamp to duration.");
        router.Stop(sourceA);
        Require(router.GetSourceStatus(sourceA).Position == TimeSpan.Zero, "Stop must reset source position.");
        router.RemoveSource(sourceA);
        ExpectFailure(() => router.GetSourceStatus(sourceA));
        uint replacement = router.AddSource(fileA);
        Require(replacement != sourceA && replacement != sourceB, "A removed source ID was reused.");
        CheckSource(router.GetSourceStatus(sourceB));
        router.RemoveSource(replacement);
        router.RemoveSource(sourceB);
        router.Dispose();
        router.Dispose();
        Expect<ObjectDisposedException>(() => router.AddSource(fileA));
        Expect<ObjectDisposedException>(() => router.AddOutput("valid-shaped-id"));
        Expect<ObjectDisposedException>(() => router.GetSourceStatus(sourceB));
        Expect<ObjectDisposedException>(() => router.GetOutputStatus(1));
        Expect<ObjectDisposedException>(() => router.GetRouteStatus(sourceB, 1));
        Expect<ObjectDisposedException>(() => router.Play(sourceB));
        Expect<ObjectDisposedException>(() => router.Pause(sourceB));
        Expect<ObjectDisposedException>(() => router.Stop(sourceB));
        Expect<ObjectDisposedException>(() => router.Seek(sourceB, TimeSpan.Zero));
        Expect<ObjectDisposedException>(() => router.Connect(sourceB, 1));
        Expect<ObjectDisposedException>(() => router.Disconnect(sourceB, 1));
        Expect<ObjectDisposedException>(() => router.SetRouteGain(sourceB, 1, 0));
        Expect<ObjectDisposedException>(() => router.SetRouteMute(sourceB, 1, true));
        Expect<ObjectDisposedException>(() => router.RemoveSource(sourceB));
        Expect<ObjectDisposedException>(() => router.RemoveOutput(1));
        Console.WriteLine("PASS: managed validation, corrupt/missing source failures, graph isolation, source IDs, seek/reset, and disposal.");
    }

    /*
     * Name: SelectOutput
     * Input: Active endpoints, optional explicit ID/default, and an excluded ID.
     * Process: Chooses actual distinct endpoint IDs without inventing fallback hardware.
     * Output: Selected endpoint or null when unavailable.
     */
    private static AudioEndpoint? SelectOutput(AudioEndpoint[] active, string? selected, string? excluded)
    {
        if (selected is not null && !string.Equals(selected, "default", StringComparison.OrdinalIgnoreCase))
        {
            return active.FirstOrDefault(item => string.Equals(item.Id, selected, StringComparison.OrdinalIgnoreCase));
        }
        return active.FirstOrDefault(item => !string.Equals(item.Id, excluded, StringComparison.OrdinalIgnoreCase));
    }

    /*
     * Name: CheckHardware
     * Input: Generated source files and two distinct active endpoint IDs.
     * Process: Measures fan-out, single-decoder pace, mixing energy, independent
     *          gain/mute, current-position rejoin, and live source/output removal.
     * Output: Quiet real-stream validation and pre-device PCM measurements.
     */
    private static void CheckHardware(string fileA, string fileB, string endpointA, string endpointB)
    {
        using var router = AudioRouter.Create();
        uint sourceA = router.AddSource(fileA), sourceB = router.AddSource(fileB);
        uint outputA = router.AddOutput(endpointA), outputB = router.AddOutput(endpointB);
        router.Connect(sourceA, outputA, gain: 0.01);
        router.Connect(sourceA, outputB, gain: 0.01);
        router.Play(sourceA);
        Thread.Sleep(200);
        RoutingSourceStatus before = router.GetSourceStatus(sourceA);
        RoutingOutputStatus firstBefore = router.GetOutputStatus(outputA), secondBefore = router.GetOutputStatus(outputB);
        RoutingRouteStatus routeBefore = router.GetRouteStatus(sourceA, outputA);
        var clock = Stopwatch.StartNew();
        Thread.Sleep(800);
        RoutingSourceStatus after = router.GetSourceStatus(sourceA);
        double elapsed = clock.Elapsed.TotalSeconds;
        CheckAdvancing(firstBefore, router.GetOutputStatus(outputA), "fan-out output A", requireSound: true);
        CheckAdvancing(secondBefore, router.GetOutputStatus(outputB), "fan-out output B", requireSound: true);
        Require(after.FramesDecoded > before.FramesDecoded, "The shared source decoder did not advance.");
        double decodeRate = (after.FramesDecoded - before.FramesDecoded) / elapsed;
        Require(decodeRate > 48000 * 0.6 && decodeRate < 48000 * 1.5, $"Fan-out decoder rate {decodeRate:F0} frames/s is not approximately one 48 kHz stream.");
        CheckSource(after);
        Require(after.RouteCount == 2 && router.GetRouteStatus(sourceA, outputA).FramesRead > routeBefore.FramesRead, "Fan-out did not retain two active route readers.");
        Require(router.GetRouteStatus(sourceA, outputB).FramesRead > 0, "Second fan-out reader did not consume PCM.");
        Console.WriteLine($"PASS: one decoder feeds two outputs at {decodeRate:F0} decoded frames/s; cache {after.CachedFrames}/192000 frames.");

        double quiet = Measure(router, outputA, "one route gain 0.01").Rms;
        Require(quiet > 0.0005 && quiet < 0.003, $"Unexpected quiet-source RMS {quiet:F6}.");
        router.SetRouteGain(sourceA, outputA, 0.02);
        Require(Math.Abs(router.GetRouteStatus(sourceA, outputB).Gain - 0.01) < 1e-9, "Changing A gain changed B route.");
        double louder = Measure(router, outputA, "one route gain 0.02").Rms;
        RequireRatio(louder, quiet, 1.7, 2.3, "doubling route gain");

        router.SetRouteMute(sourceA, outputA, true);
        RoutingRouteStatus mutedBefore = router.GetRouteStatus(sourceA, outputA);
        Require(!router.GetRouteStatus(sourceA, outputB).Muted, "Muting A also muted B.");
        Require(Measure(router, outputA, "muted A route").Silent, "Muted route still contributed PCM.");
        Require(Measure(router, outputB, "unaffected B route").Rms > quiet * 0.7, "Mute leaked to the other output.");
        RoutingRouteStatus mutedAfter = router.GetRouteStatus(sourceA, outputA);
        Require(mutedAfter.CursorFrame > mutedBefore.CursorFrame, "Muted reader did not follow the source timeline.");
        router.SetRouteMute(sourceA, outputA, false);
        RoutingRouteStatus joined = router.GetRouteStatus(sourceA, outputA);
        Require(joined.CursorFrame >= mutedAfter.CursorFrame, "Unmute restarted the route at an old position.");
        RequireRatio(Measure(router, outputA, "unmuted route").Rms, louder, 0.8, 1.2, "restoring an unmuted route");
        router.SetRouteGain(sourceA, outputA, 0);
        Require(Measure(router, outputA, "gain-zero route").Silent, "Gain zero still contributed PCM.");
        router.SetRouteGain(sourceA, outputA, 0.02);

        router.Connect(sourceB, outputA, gain: 0.02);
        router.Play(sourceB);
        RoutingSourceStatus sourceBeforeMix = router.GetSourceStatus(sourceB);
        double mixed = Measure(router, outputA, "two distinct-frequency sources").Rms;
        RequireRatio(mixed, louder, 1.2, 1.7, "adding the second source");
        Require(router.GetOutputStatus(outputA).RouteCount == 2 && router.GetSourceStatus(sourceA).State == FilePlaybackState.Playing &&
            router.GetSourceStatus(sourceB).State == FilePlaybackState.Playing && router.GetSourceStatus(sourceB).FramesDecoded > sourceBeforeMix.FramesDecoded,
            "Both independent sources did not feed the two-route mixer.");
        router.SetRouteMute(sourceB, outputA, true);
        RequireRatio(Measure(router, outputA, "second source muted").Rms, louder, 0.8, 1.2, "muting only the second source");
        router.SetRouteMute(sourceA, outputA, true);
        Require(Measure(router, outputA, "both source routes muted").Silent, "Fully muted mixer submitted nonzero PCM.");
        router.SetRouteMute(sourceA, outputA, false);
        router.SetRouteMute(sourceB, outputA, false);
        router.Connect(sourceA, outputA, gain: 0.015);
        Require(router.GetOutputStatus(outputA).RouteCount == 2, "Connect upsert duplicated a route.");
        router.Disconnect(sourceB, outputA);
        Require(router.GetOutputStatus(outputA).RouteCount == 1 && router.GetSourceStatus(sourceB).RouteCount == 0, "Disconnect did not remove only its route.");
        ExpectFailure(() => router.GetRouteStatus(sourceB, outputA));
        router.RemoveSource(sourceB);
        RoutingOutputStatus remaining = router.GetOutputStatus(outputB);
        router.RemoveOutput(outputA);
        Require(router.GetSourceStatus(sourceA).RouteCount == 1, "Removing an output removed a surviving source route.");
        Thread.Sleep(250);
        CheckAdvancing(remaining, router.GetOutputStatus(outputB), "surviving output after removal", requireSound: true);
        uint replacementOutput = router.AddOutput(endpointA);
        Require(replacementOutput != outputA && replacementOutput != outputB, "Removed output ID was reused.");
        ExpectFailure(() => router.GetOutputStatus(outputA));
        router.Connect(sourceA, replacementOutput, gain: 0);
        Require(router.GetSourceStatus(sourceA).RouteCount == 2, "Adding an output after removal failed.");
        router.RemoveSource(sourceA);
        Require(router.GetOutputStatus(outputB).RouteCount == 0 && router.GetOutputStatus(replacementOutput).RouteCount == 0, "Removing a source left stale routes.");
        Require(Measure(router, outputB, "output after removing its final source").Silent, "Removed source left audible PCM contributions.");
        router.RemoveOutput(outputB);
        router.RemoveOutput(replacementOutput);
        Console.WriteLine("PASS: independent gain/mute, upsert/disconnect, current-position rejoin, and live graph cleanup.");
    }

    /*
     * Name: CheckTransport
     * Input: Generated WAV source and one active output ID.
     * Process: Silently checks unrouted clocks, late connections, pause, seek,
     *          stop, end-of-file, and restart through the routing decoder.
     * Output: PASS or a descriptive transport failure.
     */
    private static void CheckTransport(string file, string endpoint)
    {
        using var router = AudioRouter.Create();
        uint source = router.AddSource(file);
        router.Play(source);
        Thread.Sleep(200);
        RoutingSourceStatus unrouted = router.GetSourceStatus(source);
        Require(unrouted.Position > TimeSpan.FromMilliseconds(100) && unrouted.FramesDecoded == 0,
            "An unrouted source must advance without decoding unused PCM.");
        uint output = router.AddOutput(endpoint);
        router.Connect(source, output, gain: 0);
        Thread.Sleep(200);
        Require(router.GetRouteStatus(source, output).CursorFrame >= (ulong)(unrouted.Position.TotalSeconds * 48000),
            "A late connection did not join the source's current position.");
        Require(router.GetSourceStatus(source).FramesDecoded > 0, "Late connection did not start decoding.");

        router.Pause(source);
        RoutingSourceStatus paused = router.GetSourceStatus(source);
        RoutingRouteStatus pausedRoute = router.GetRouteStatus(source, output);
        Thread.Sleep(150);
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Paused &&
            router.GetSourceStatus(source).Position == paused.Position &&
            router.GetRouteStatus(source, output).CursorFrame == pausedRoute.CursorFrame,
            "Pause did not freeze source position and route cursor.");
        router.Seek(source, TimeSpan.FromSeconds(8));
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Paused &&
            router.GetSourceStatus(source).Position == TimeSpan.FromSeconds(8), "Paused seek changed state or missed its target.");
        router.Play(source);
        Thread.Sleep(150);
        Require(router.GetSourceStatus(source).Position > TimeSpan.FromSeconds(8), "Resume did not advance after seek.");
        router.Seek(source, TimeSpan.FromSeconds(3));
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Playing &&
            router.GetSourceStatus(source).Position >= TimeSpan.FromSeconds(3) &&
            router.GetSourceStatus(source).Position < TimeSpan.FromSeconds(4), "Playing seek lost playback state or target.");
        router.Stop(source);
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Stopped &&
            router.GetSourceStatus(source).Position == TimeSpan.Zero && router.GetRouteStatus(source, output).CursorFrame == 0,
            "Stop did not reset source and route positions.");

        TimeSpan duration = router.GetSourceStatus(source).Duration;
        router.Seek(source, duration - TimeSpan.FromMilliseconds(200));
        router.Play(source);
        var deadline = Stopwatch.StartNew();
        while (router.GetSourceStatus(source).State == FilePlaybackState.Playing && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(25);
        }
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Ended &&
            router.GetSourceStatus(source).Position == duration, "Routed source failed to finish at EOF.");
        router.Play(source);
        Thread.Sleep(150);
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Playing &&
            router.GetSourceStatus(source).Position > TimeSpan.Zero && router.GetSourceStatus(source).Position < TimeSpan.FromSeconds(1),
            "Play from Ended did not restart at the beginning.");
        CheckSource(router.GetSourceStatus(source));
        Console.WriteLine("PASS: unrouted clock, late connection, pause/frozen cursor, paused/playing seek, stop/reset, EOF, and restart.");
    }

    /*
     * Name: Measurement
     * Input: Stable-window RMS, submitted frames, and non-silent-frame delta.
     * Process: Stores measured PCM evidence without retaining the router.
     * Output: A window summary; silence requires zero energy and non-silent frames.
     */
    private readonly record struct Measurement(double Rms, ulong Frames, ulong NonSilentFrames)
    {
        public bool Silent => Rms < 1e-10 && NonSilentFrames == 0;
    }

    /*
     * Name: Measure
     * Input: Router, output ID, and a measurement label.
     * Process: Settles queued processing, then measures a 300 ms cumulative PCM window.
     * Output: RMS sqrt(deltaSumSquares/(2*deltaFrames)); unhealthy counters fail.
     */
    private static Measurement Measure(AudioRouter router, uint output, string description)
    {
        Thread.Sleep(100);
        RoutingOutputStatus before = router.GetOutputStatus(output);
        Thread.Sleep(300);
        RoutingOutputStatus after = router.GetOutputStatus(output);
        CheckAdvancing(before, after, description, requireSound: false);
        Require(after.SumSquares >= before.SumSquares && after.NonSilentFrames >= before.NonSilentFrames, "Cumulative mixer metrics regressed.");
        ulong frames = after.FramesSubmitted - before.FramesSubmitted;
        double rms = Math.Sqrt(Math.Max(0, after.SumSquares - before.SumSquares) / (2 * (double)frames));
        Require(double.IsFinite(rms), "Mixer RMS is nonfinite.");
        Require(after.ClippedSamples == before.ClippedSamples, "Quiet sine routes unexpectedly clipped.");
        Console.WriteLine($"  {description}: RMS {rms:F7}, {frames} frames, {after.NonSilentFrames - before.NonSilentFrames} non-silent.");
        return new Measurement(rms, frames, after.NonSilentFrames - before.NonSilentFrames);
    }

    /*
     * Name: CheckRealFiles
     * Input: User-owned media paths and two distinct real output IDs.
     * Process: Plays separate tracks at 0.15 route gain and verifies controlling A
     *          leaves B advancing. User files are read only and never copied.
     * Output: Real-codec stream evidence; audible source assignment requires listening.
     */
    private static void CheckRealFiles(string fileA, string fileB, string endpointA, string endpointB)
    {
        using var router = AudioRouter.Create();
        uint sourceA = router.AddSource(fileA), sourceB = router.AddSource(fileB);
        uint outputA = router.AddOutput(endpointA), outputB = router.AddOutput(endpointB);
        router.Connect(sourceA, outputA, gain: 0.15);
        router.Connect(sourceB, outputB, gain: 0.15);
        router.Play(sourceA); router.Play(sourceB);
        Thread.Sleep(500);
        RoutingOutputStatus a = router.GetOutputStatus(outputA), b = router.GetOutputStatus(outputB);
        Thread.Sleep(500);
        CheckAdvancing(a, router.GetOutputStatus(outputA), "actual file A", requireSound: true);
        CheckAdvancing(b, router.GetOutputStatus(outputB), "actual file B", requireSound: true);
        router.Pause(sourceA);
        RoutingSourceStatus paused = router.GetSourceStatus(sourceA);
        b = router.GetOutputStatus(outputB);
        Thread.Sleep(400);
        Require(router.GetSourceStatus(sourceA).State == FilePlaybackState.Paused && router.GetSourceStatus(sourceA).Position == paused.Position, "Pausing source A did not freeze its timeline.");
        CheckAdvancing(b, router.GetOutputStatus(outputB), "actual B while A pauses", requireSound: true);
        router.Seek(sourceA, TimeSpan.FromTicks(paused.Duration.Ticks / 2));
        router.Play(sourceA);
        b = router.GetOutputStatus(outputB);
        Thread.Sleep(400);
        CheckAdvancing(b, router.GetOutputStatus(outputB), "actual B while A seeks/resumes", requireSound: true);
        router.Stop(sourceA);
        b = router.GetOutputStatus(outputB);
        Thread.Sleep(400);
        CheckAdvancing(b, router.GetOutputStatus(outputB), "actual B while A stops", requireSound: true);
        router.Stop(sourceB);
        Console.WriteLine("PASS: real files decoded independently while source A paused, sought, resumed, and stopped.");
    }

    /*
     * Name: CheckSource
     * Input: Routing source snapshot.
     * Process: Checks source health, normalized format, and bounded four-second cache.
     * Output: Returns for valid source state or throws a descriptive failure.
     */
    private static void CheckSource(RoutingSourceStatus status)
    {
        Require(status.State != FilePlaybackState.Failed && status.HResult == 0, $"Source failed: 0x{status.HResult:X8}.");
        Require(status.SampleRate == 48000 && status.ChannelCount == 2 && status.Duration > TimeSpan.Zero, "Source format/duration is invalid.");
        Require(status.CachedFrames <= 192000, "Source cache exceeded four seconds of PCM.");
    }

    /*
     * Name: CheckAdvancing
     * Input: Before/after output snapshots, context, and whether sound is required.
     * Process: Requires a healthy running mixer and monotonic submitted PCM.
     * Output: Returns on observed progression or throws with the failed context.
     */
    private static void CheckAdvancing(RoutingOutputStatus before, RoutingOutputStatus after, string description, bool requireSound)
    {
        Require(after.State == 1 && after.HResult == 0 && after.SampleRate == 48000 && after.ChannelCount == 2,
            $"Output failed during {description}: 0x{after.HResult:X8}.");
        Require(after.FramesSubmitted > before.FramesSubmitted && after.StartQpc > 0, $"Output did not submit more frames during {description}.");
        if (requireSound)
        {
            Require(after.NonSilentFrames > before.NonSilentFrames && after.SumSquares > before.SumSquares, $"Output submitted no new signal during {description}.");
        }
    }

    /*
     * Name: RequireRatio
     * Input: Measured numerator/reference, acceptable ratio bounds, and context.
     * Process: Compares RMS energy across controlled sine/gain conditions.
     * Output: Returns inside bounds or throws with the measured ratio.
     */
    private static void RequireRatio(double measured, double reference, double minimum, double maximum, string description)
    {
        Require(reference > 0 && measured / reference >= minimum && measured / reference <= maximum,
            $"Unexpected RMS ratio {measured / reference:F3} for {description}.");
    }

    /*
     * Name: Expect
     * Input: Operation and expected managed exception type.
     * Process: Requires controlled rejection without accepting a successful operation.
     * Output: The expected exception or a failed-check exception.
     */
    private static TException Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    /*
     * Name: ExpectFailure
     * Input: Invalid graph operation.
     * Process: Accepts argument validation or a translated native graph failure.
     * Output: Returns on controlled rejection; unexpected success fails.
     */
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid graph operation unexpectedly succeeded.");
    }

    /*
     * Name: Require
     * Input: Assertion condition and failure description.
     * Process: Standardizes failed-check reporting.
     * Output: Returns for success or throws InvalidOperationException.
     */
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    /*
     * Name: PrintUsage
     * Input: Output writer.
     * Process: Describes software defaults and opt-in real-output validation.
     * Output: Usage, signal levels, and verification limits.
     */
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: AudioHaus.RoutingChecks [--hardware [--output-a <id|default>] [--output-b <id>] [--file-a <path> --file-b <path>]]");
        writer.WriteLine("Default: software/ABI/source checks without output streams.");
        writer.WriteLine("--hardware: two distinct active outputs; quiet synthetic fan-out/mix/gain/mute validation.");
        writer.WriteLine("Optional paired real media files play independently at route gain 0.15; no media is copied.");
        writer.WriteLine("PCM RMS and counters are measured before device processing; confirm acoustic routing by listening.");
        writer.WriteLine("Exit codes: 0 requested checks passed; 1 failure; 2 required hardware unavailable.");
    }
}

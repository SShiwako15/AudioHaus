using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AudioHaus.Interop;

namespace AudioHaus.CaptureChecks;

/*
 * Name: Program
 * Input: Optional --hardware <diagnostics-exe> <parking-id> <output-id>.
 * Process: Checks real process-loopback ownership, isolation, live monitoring, and exit.
 * Output: Repeatable evidence; physical parking never establishes silent originals.
 */
internal static class Program
{
    /*
     * Name: Main
     * Input: Capture-check arguments or internal --idle child mode.
     * Process: Runs no-sound capture contracts, then opt-in two-process audio checks.
     * Output: Exit 0 for requested checks, 1 failure, 2 missing required hardware.
     */
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--idle") { Console.ReadLine(); return 0; }
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine("CaptureChecks [--hardware <diagnostics-exe> <parking-id> <different-output-id>]");
            Console.WriteLine("CaptureChecks --listen <diagnostics-exe> <cable-id> <output-a> <output-b> <file-a> <file-b>");
            Console.WriteLine("CaptureChecks --listen-long <diagnostics-exe> <cable-id> <output-a> <output-b> <file-a> <file-b>");
            Console.WriteLine("CaptureChecks --stability <diagnostics-exe> <cable-id> <output-a> <output-b>");
            Console.WriteLine("Default opens an idle process capture; no output playback. Hardware mode plays quiet generated audio.");
            return 0;
        }
        try
        {
            if (args.Length == 7 && args[0] is "--listen" or "--listen-long") { CheckListening(args); return 0; }
            if (args.Length == 5 && args[0] == "--stability") { CheckStability(args); return 0; }
            Require(args.Length == 0 || (args.Length == 4 && args[0] == "--hardware"), "Invalid arguments; use --help.");
            AudioEndpoint? endpoint = new AudioEndpointEnumerator().Enumerate().FirstOrDefault(item => item.State == AudioHausEndpointState.Active);
            if (endpoint is null) { Console.WriteLine("NOT VERIFIED: no active endpoint for capture setup."); return 2; }
            CheckLayout();
            CheckIdle(endpoint.Id);
            if (args.Length != 0) { CheckAudio(Path.GetFullPath(args[1]), args[2], args[3]); }
            else { Console.WriteLine("Audible application routing and virtual-cable isolation: NOT VERIFIED. Use hardware mode after setup."); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"FAIL: {error}"); return 1; }
    }

    /*
     * Name: CheckStability
     * Input: Executable, virtual cable, and two distinct physical outputs.
     * Process: Keeps two independent captures and render clients active for 20 seconds
     *          at zero route gain while background isolation checks continue.
     * Output: PASS only with no unread loss, discontinuities, reader rebases, or material
     *         submission deficit after warmup; no sound is sent to physical outputs.
     */
    private static void CheckStability(string[] args)
    {
        string executable = Path.GetFullPath(args[1]), parking = args[2], outputA = args[3], outputB = args[4];
        Require(!string.Equals(outputA, outputB, StringComparison.OrdinalIgnoreCase), "Stability check requires two different physical outputs.");
        string directory = Path.Combine(Path.GetTempPath(), "AudioHaus.CaptureStability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string fileA = Path.Combine(directory, "330hz.wav"), fileB = Path.Combine(directory, "550hz.wav");
        WriteWave(fileA, 330); WriteWave(fileB, 550);
        using var first = new Child(executable, new[] { "files", "--file", fileA, "--output", parking, "--gain", "0.02" });
        using var second = new Child(executable, new[] { "files", "--file", fileB, "--output", parking, "--gain", "0.02" });
        using var router = AudioRouter.Create();
        uint a = router.AddProcessSource((uint)first.Id, parking), b = router.AddProcessSource((uint)second.Id, parking);
        uint speakers = router.AddOutput(outputA), headphones = router.AddOutput(outputB);
        router.Connect(a, speakers, 0); router.Connect(b, headphones, 0);
        router.Play(a); router.Play(b);
        first.Send("play all"); second.Send("play all");
        Thread.Sleep(2000);
        uint[] sources = { a, b }, outputs = { speakers, headphones };
        ProcessCaptureStatus[] captureBefore = sources.Select(router.GetCaptureStatus).ToArray();
        RoutingOutputStatus[] outputBefore = outputs.Select(router.GetOutputStatus).ToArray();
        RoutingRouteStatus[] routeBefore = { router.GetRouteStatus(a, speakers), router.GetRouteStatus(b, headphones) };
        Console.WriteLine("Silent stability check: two application captures and two physical render clients for 20 seconds.");
        var elapsed = Stopwatch.StartNew();
        Thread.Sleep(20000);
        double seconds = elapsed.Elapsed.TotalSeconds;
        for (int index = 0; index < sources.Length; ++index)
        {
            ProcessCaptureStatus capture = router.GetCaptureStatus(sources[index]);
            RoutingOutputStatus output = router.GetOutputStatus(outputs[index]);
            RoutingRouteStatus route = router.GetRouteStatus(sources[index], outputs[index]);
            Require(capture.State == FilePlaybackState.Playing && capture.HResult == 0 && output.State == 1 && output.HResult == 0,
                "Stream failed during stability check.");
            Require(capture.FramesDropped == captureBefore[index].FramesDropped && capture.Discontinuities == captureBefore[index].Discontinuities &&
                route.Rebases == routeBefore[index].Rebases, "Live audio lost unread data or rebased during steady playback.");
            Require(capture.NonSilentFrames > captureBefore[index].NonSilentFrames && capture.BufferedFrames <= 12000, "Source stalled or buffering is unbounded.");
            double rate = (output.FramesSubmitted - outputBefore[index].FramesSubmitted) / seconds;
            Require(rate > 48000 * 0.98 && rate < 48000 * 1.02 && output.SumSquares == 0,
                $"Output {index + 1} submission rate {rate:F0} frames/s missed real-time silence.");
            Console.WriteLine($"  Output {index + 1}: {rate:F1} frames/s; dropped unread +{capture.FramesDropped - captureBefore[index].FramesDropped}; discontinuities +{capture.Discontinuities - captureBefore[index].Discontinuities}; rebases +{route.Rebases - routeBefore[index].Rebases}.");
        }
        first.Quit(); second.Quit();
        Console.WriteLine("PASS: steady live streaming on both outputs with background parking checks and zero physical signal.");
    }

    /*
     * Name: CheckListening
     * Input: Listening mode, executable, cable, physical outputs, and two media files.
     * Process: Runs independent render processes into the cable, then captures each PID
     *          through AudioHaus with silence, routing, mute, gain, and live-resume phases.
     * Output: Successful stream checks; the listener must confirm physical silence/routing.
     */
    private static void CheckListening(string[] args)
    {
        string executable = Path.GetFullPath(args[1]), parking = args[2], outputA = args[3], outputB = args[4];
        Require(!string.Equals(outputA, outputB, StringComparison.OrdinalIgnoreCase), "Listening check needs distinct physical outputs.");
        Require(File.Exists(args[5]) && File.Exists(args[6]), "Both listening files must exist.");
        bool extended = args[0] == "--listen-long";
        int[] seconds = extended ? new[] { 8, 45, 15, 15, 10, 20, 12 } : new[] { 8, 15, 8, 8, 8, 10, 8 };
        if (extended)
        {
            Console.WriteLine("Listening lead-in / 15 seconds: get ready; no test audio is playing yet.");
            Thread.Sleep(15000);
        }
        using var first = new Child(executable, new[] { "files", "--file", Path.GetFullPath(args[5]), "--output", parking, "--gain", "1" });
        using var second = new Child(executable, new[] { "files", "--file", Path.GetFullPath(args[6]), "--output", parking, "--gain", "1" });
        first.Send("seek 1 15"); second.Send("seek 1 30");
        first.Send("play all"); second.Send("play all");
        Console.WriteLine($"Source applications: PID {first.Id} / {Path.GetFileName(args[5])}; PID {second.Id} / {Path.GetFileName(args[6])}.");
        Console.WriteLine($"Phase 0 / {seconds[0]} seconds: both applications play into the cable; physical outputs should be SILENT.");
        Thread.Sleep(seconds[0] * 1000);
        using (var router = AudioRouter.Create())
        {
            uint a = router.AddProcessSource((uint)first.Id, parking), b = router.AddProcessSource((uint)second.Id, parking);
            uint speakers = router.AddOutput(outputA), headphones = router.AddOutput(outputB);
            router.Connect(a, speakers, 0.15); router.Connect(b, headphones, 0.15);
            router.Play(a); router.Play(b);
            Console.WriteLine($"Phase 1 / {seconds[1]} seconds: file A on output A; file B on output B.");
            Thread.Sleep(seconds[1] * 1000);
            PrintListeningStatus(router, a, b, speakers, headphones);
            router.SetRouteMute(a, speakers, true);
            Console.WriteLine($"Phase 2 / {seconds[2]} seconds: output A SILENT; file B continues on output B.");
            Thread.Sleep(seconds[2] * 1000);
            Require(Measure(router, speakers, "A route muted") < 1e-9, "Muted capture route submitted signal.");
            router.SetRouteMute(a, speakers, false);
            router.SetRouteGain(b, headphones, 0.05);
            Console.WriteLine($"Phase 3 / {seconds[3]} seconds: file A restored; file B quieter on output B.");
            Thread.Sleep(seconds[3] * 1000);
            ProcessCaptureStatus beforeA = router.GetCaptureStatus(a), beforeB = router.GetCaptureStatus(b);
            router.Stop(a); router.Stop(b);
            Console.WriteLine($"Phase 4 / {seconds[4]} seconds: both monitors stopped; physical outputs should be SILENT while source applications keep playing.");
            Thread.Sleep(seconds[4] * 1000);
            Require(Measure(router, speakers, "A monitor stopped") < 1e-9 && Measure(router, headphones, "B monitor stopped") < 1e-9,
                "Stopped monitors submitted nonzero PCM.");
            Require(router.GetCaptureStatus(a).FramesCaptured > beforeA.FramesCaptured && router.GetCaptureStatus(b).FramesCaptured > beforeB.FramesCaptured,
                "Monitor Stop also stopped application capture.");
            router.SetRouteGain(b, headphones, 0.15);
            RoutingOutputStatus outputBeforeA = router.GetOutputStatus(speakers), outputBeforeB = router.GetOutputStatus(headphones);
            router.Play(a); router.Play(b);
            Console.WriteLine($"Phase 5 / {seconds[5]} seconds: both monitors rejoin live audio on their assigned outputs.");
            Thread.Sleep(seconds[5] * 1000);
            Require(router.GetOutputStatus(speakers).NonSilentFrames > outputBeforeA.NonSilentFrames &&
                router.GetOutputStatus(headphones).NonSilentFrames > outputBeforeB.NonSilentFrames,
                "Resumed monitors did not submit new signal on both outputs.");
            PrintListeningStatus(router, a, b, speakers, headphones);
        }
        Console.WriteLine($"Phase 6 / {seconds[6]} seconds: AudioHaus router closed; physical outputs should be SILENT while both source applications still play into the cable.");
        Thread.Sleep(seconds[6] * 1000);
        first.Send("status"); second.Send("status");
        first.Quit(); second.Quit();
        Console.WriteLine("PASS: two real application PIDs captured from the cable to separate physical outputs; live controls, silent submitted PCM, and cleanup.");
        Console.WriteLine("Listening confirmation required: initial/final physical silence, assigned tracks, independent mute/gain, and live resume.");
    }

    /*
     * Name: PrintListeningStatus
     * Input: Router, two process source IDs, and their output IDs.
     * Process: Requires healthy streams, bounded capture history, and actual signal.
     * Output: Compact telemetry for the listening record; failures throw.
     */
    private static void PrintListeningStatus(AudioRouter router, uint a, uint b, uint outputA, uint outputB)
    {
        foreach (uint source in new[] { a, b })
        {
            ProcessCaptureStatus status = router.GetCaptureStatus(source);
            Require(status.State == FilePlaybackState.Playing && status.HResult == 0 && status.NonSilentFrames > 0 && status.BufferedFrames <= 12000,
                $"Listening source {source} failed.");
            Console.WriteLine($"  Source {source}, PID {status.ProcessId}: captured {status.FramesCaptured}, non-silent {status.NonSilentFrames}, buffered {status.BufferedFrames}/12000, dropped {status.FramesDropped}, discontinuities {status.Discontinuities}; HRESULT 0x{status.HResult:X8}.");
        }
        foreach (uint output in new[] { outputA, outputB })
        {
            RoutingOutputStatus status = router.GetOutputStatus(output);
            Require(status.State == 1 && status.HResult == 0 && status.NonSilentFrames > 0 && status.ClippedSamples == 0,
                $"Listening output {output} failed or clipped.");
            Console.WriteLine($"  Output {output}: {status.FramesSubmitted} submitted, {status.NonSilentFrames} non-silent, {status.ClippedSamples} clipped; HRESULT 0x{status.HResult:X8}.");
        }
    }

    /*
     * Name: CheckLayout
     * Input: Production managed capture info.
     * Process: Reflects the actual native layout and every field offset.
     * Output: PASS or a descriptive assertion failure.
     */
    private static void CheckLayout()
    {
        Type type = typeof(AudioRouter).Assembly.GetType("AudioHaus.Interop.ProcessCaptureInfo", true)!;
        Require(Marshal.SizeOf(type) == 80, "Capture ABI size must be 80 bytes.");
        string[] names = { "ProcessId", "State", "HResult", "Reserved", "FramesCaptured", "NonSilentFrames", "Discontinuities", "FramesDropped", "BufferedFrames", "LastPacketQpc100Ns", "SumSquares", "LastPeak" };
        int[] offsets = { 0, 4, 8, 12, 16, 24, 32, 40, 48, 56, 64, 72 };
        for (int index = 0; index < names.Length; ++index)
        { Require(Marshal.OffsetOf(type, names[index]).ToInt32() == offsets[index], $"Wrong offset for {names[index]}."); }
        Console.WriteLine("PASS: 80-byte capture ABI and all field offsets.");
    }

    /*
     * Name: CheckIdle
     * Input: An active endpoint used only as a validation target; child produces no audio.
     * Process: Checks capture activation, validation, live transport, and process exit.
     * Output: PASS without rendering a sound; no virtual-cable claim is inferred.
     */
    private static void CheckIdle(string parking)
    {
        using var target = new Child(Environment.ProcessPath!, new[] { "--idle" });
        using var router = AudioRouter.Create();
        Expect<ArgumentException>(() => router.AddProcessSource(0, parking));
        Expect<ArgumentException>(() => router.AddProcessSource(uint.MaxValue, parking));
        Expect<ArgumentException>(() => router.AddProcessSource((uint)target.Id, "bad\0id"));
        Expect<InvalidOperationException>(() => router.AddProcessSource((uint)Environment.ProcessId, parking));
        Expect<InvalidOperationException>(() => router.AddProcessSource(int.MaxValue, parking));
        Expect<InvalidOperationException>(() => router.AddProcessSource((uint)target.Id, "AudioHaus.Missing.Endpoint"));
        uint source = router.AddProcessSource((uint)target.Id, parking);
        Expect<InvalidOperationException>(() => router.AddProcessSource((uint)target.Id, parking));
        Expect<InvalidOperationException>(() => router.AddOutput(parking));
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Stopped && router.GetSourceStatus(source).Duration == TimeSpan.Zero,
            "A live source must open as a stopped monitor with no finite duration.");
        router.Play(source); Thread.Sleep(150);
        ProcessCaptureStatus live = router.GetCaptureStatus(source);
        Require(live.ProcessId == (uint)target.Id && live.State == FilePlaybackState.Playing && live.HResult == 0 && live.NonSilentFrames == 0,
            "Idle process capture failed or captured another application's sound.");
        Require(live.BufferedFrames <= 12000, "Live cache exceeds 250 ms.");
        router.Pause(source);
        TimeSpan paused = router.GetSourceStatus(source).Position;
        Thread.Sleep(100);
        Require(router.GetSourceStatus(source).Position == paused, "Pause did not freeze monitor time.");
        Expect<InvalidOperationException>(() => router.Seek(source, TimeSpan.FromSeconds(1)));
        router.Stop(source);
        Require(router.GetSourceStatus(source).Position == TimeSpan.Zero, "Stop did not reset monitor time.");
        target.Quit();
        WaitUntil(() => router.GetCaptureStatus(source).State == FilePlaybackState.Ended, "Process exit did not end its source.");
        Expect<InvalidOperationException>(() => router.Play(source));
        router.RemoveSource(source);
        router.Dispose(); router.Dispose();
        Expect<ObjectDisposedException>(() => router.AddProcessSource(1, parking));
        Expect<ObjectDisposedException>(() => router.GetCaptureStatus(source));
        _ = new AudioApplicationEnumerator().Enumerate();
        Console.WriteLine("PASS: real idle process activation, invalid/duplicate/self PIDs, parking exclusion, live pause/stop/no-seek, root exit, discovery, and disposal.");
    }

    /*
     * Name: CheckAudio
     * Input: Diagnostics executable, shared source endpoint, and distinct destination.
     * Process: Runs two independent render processes and proves PID filtering with one
     *          silent while the other plays; tests mixing and monitoring independently.
     * Output: Real captured signal measurements; physical parking remains audible.
     */
    private static void CheckAudio(string executable, string parking, string output)
    {
        Require(!string.Equals(parking, output, StringComparison.OrdinalIgnoreCase), "Parking and output must be different.");
        string directory = Path.Combine(Path.GetTempPath(), "AudioHaus.CaptureChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string aPath = Path.Combine(directory, "330hz.wav"), bPath = Path.Combine(directory, "550hz.wav");
        WriteWave(aPath, 330); WriteWave(bPath, 550);
        using var first = new Child(executable, new[] { "files", "--file", aPath, "--output", parking, "--gain", "0.02" });
        using var second = new Child(executable, new[] { "files", "--file", bPath, "--output", parking, "--gain", "0.02" });
        Thread.Sleep(600);
        using var router = AudioRouter.Create();
        uint a = router.AddProcessSource((uint)first.Id, parking), b = router.AddProcessSource((uint)second.Id, parking);
        uint destination = router.AddOutput(output);
        router.Connect(a, destination, 0.1); router.Connect(b, destination, 0.1);
        router.Play(a); router.Play(b);
        first.Send("play all");
        Thread.Sleep(500);
        ProcessCaptureStatus beforeA = router.GetCaptureStatus(a), beforeB = router.GetCaptureStatus(b);
        Thread.Sleep(400);
        ProcessCaptureStatus afterA = router.GetCaptureStatus(a), afterB = router.GetCaptureStatus(b);
        Console.WriteLine($"  Capture A: {afterA.FramesCaptured} frames, {afterA.NonSilentFrames} non-silent, {afterA.Discontinuities} discontinuities, {afterA.FramesDropped} dropped; B: {afterB.FramesCaptured} frames, {afterB.NonSilentFrames} non-silent.");
        Require(afterA.NonSilentFrames > beforeA.NonSilentFrames && afterB.NonSilentFrames == beforeB.NonSilentFrames,
            "PID filtering leaked first-process audio into the silent second process.");
        var sessions = new AudioApplicationEnumerator().Enumerate();
        Require(sessions.Any(item => item.ProcessId == first.Id && string.Equals(item.Endpoint.Id, parking, StringComparison.OrdinalIgnoreCase)),
            "Active application discovery missed the source's true endpoint.");
        double one = Measure(router, destination, "one process", (a, 0.1));
        Require(one > 0, "First process contributed no captured audio.");
        second.Send("play all");
        double mixed = Measure(router, destination, "two processes", (a, 0.1), (b, 0.1));
        Require(mixed > 0, "Two processes contributed no captured audio.");
        CheckNativeBuffers(parking);
        router.SetRouteMute(b, destination, true);
        _ = Measure(router, destination, "second route muted", (a, 0.1));
        router.Pause(a);
        Require(Measure(router, destination, "A paused and B route muted") < 1e-9, "Paused monitoring was not silent.");
        Require(router.GetCaptureStatus(a).State == FilePlaybackState.Paused, "Monitor pause lost state.");
        router.Play(a);
        _ = Measure(router, destination, "A resumed at live position", (a, 0.1));
        router.SetRouteGain(a, destination, 0.05);
        _ = Measure(router, destination, "A gain halved", (a, 0.05));
        first.Send("pause all");
        Thread.Sleep(300);
        beforeA = router.GetCaptureStatus(a); beforeB = router.GetCaptureStatus(b);
        Thread.Sleep(350);
        afterA = router.GetCaptureStatus(a); afterB = router.GetCaptureStatus(b);
        Require(afterA.NonSilentFrames == beforeA.NonSilentFrames && afterB.NonSilentFrames > beforeB.NonSilentFrames,
            "PID filtering leaked B into paused application A.");
        Require(afterA.BufferedFrames <= 12000 && afterB.BufferedFrames <= 12000, "Live buffering is unbounded.");
        first.Quit();
        WaitUntil(() => router.GetCaptureStatus(a).State == FilePlaybackState.Ended, "Exited A did not become Ended.");
        Require(router.GetCaptureStatus(b).State == FilePlaybackState.Playing && router.GetCaptureStatus(b).HResult == 0, "A exit affected B.");
        router.RemoveSource(a); router.RemoveSource(b); router.RemoveOutput(destination);
        CheckParkingEscape(executable, aPath, bPath, parking, output);
        Console.WriteLine("PASS: two same-executable PIDs share one endpoint yet remain isolated; live mixing, gain/mute/pause/resume, root exit, and cleanup.");
        Console.WriteLine("Virtual-cable/no-original-playback acceptance still requires a real virtual sink with monitoring disabled.");
    }

    /*
     * Name: ReadEndpointProcesses
     * Input: Endpoint ID, PID storage/capacity, and required outputs.
     * Process: Calls the real discovery C ABI for buffer-boundary checks.
     * Output: Native status, required count, and HRESULT.
     */
    [DllImport("AudioHaus.Native.dll", EntryPoint = "audiohaus_get_endpoint_processes", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int ReadEndpointProcesses([MarshalAs(UnmanagedType.LPWStr)] string? endpoint,
        [Out] uint[] ids, uint capacity, out uint required, out uint hresult);

    /*
     * Name: CheckNativeBuffers
     * Input: Endpoint with the two active helper sessions.
     * Process: Exercises insufficient capacity and invalid input against guarded arrays.
     * Output: PASS for zeroed metadata and untouched caller storage on rejection.
     */
    private static void CheckNativeBuffers(string endpoint)
    {
        uint[] storage = { 0x12345678, 0xAABBCCDD };
        Require(ReadEndpointProcesses(endpoint, storage, 0, out uint required, out uint hr) == 2 && required >= 2 && hr == 0,
            "Active-session size query did not return required capacity.");
        Require(storage[0] == 0x12345678 && storage[1] == 0xAABBCCDD, "Size query wrote outside declared capacity.");
        Require(ReadEndpointProcesses(endpoint, storage, 1, out required, out hr) == 2 && required >= 2 &&
            storage[0] == 0x12345678 && storage[1] == 0xAABBCCDD, "Insufficient session buffer was partially overwritten.");
        Require(ReadEndpointProcesses(null, storage, 0, out required, out hr) == 1 && required == 0 && hr == 0x80070057,
            "Invalid native discovery input did not clear metadata/report E_INVALIDARG.");
        Console.WriteLine("PASS: native session discovery guarded buffers, required sizes, and invalid-input metadata.");
    }

    /*
     * Name: CheckParkingEscape
     * Input: Helper executable, generated files, parking endpoint, and another endpoint.
     * Process: Makes one helper add an original stream on an unapproved destination.
     * Output: Initial attachment rejects escape; runtime escape fails only its capture.
     */
    private static void CheckParkingEscape(string executable, string fileA, string fileB, string parking, string output)
    {
        using var target = new Child(executable, new[] { "files", "--file", fileA, "--output", parking,
            "--file", fileB, "--output", output, "--gain", "0.005" });
        using var survivor = new Child(Environment.ProcessPath!, new[] { "--idle" });
        Thread.Sleep(400);
        target.Send("play all");
        Thread.Sleep(400);
        using var router = AudioRouter.Create();
        Expect<InvalidOperationException>(() => router.AddProcessSource((uint)target.Id, parking));
        target.Send("stop 2");
        Thread.Sleep(400);
        uint guarded = router.AddProcessSource((uint)target.Id, parking);
        uint independent = router.AddProcessSource((uint)survivor.Id, parking);
        uint destination = router.AddOutput(output);
        router.Connect(guarded, destination, 0);
        router.Play(guarded);
        target.Send("play 2");
        WaitUntil(() => router.GetCaptureStatus(guarded).State == FilePlaybackState.Failed,
            "An original stream escaped parking without failing its capture.");
        Require(router.GetCaptureStatus(guarded).HResult == 0x8007139F, "Parking escape did not report ERROR_INVALID_STATE.");
        ulong submitted = router.GetOutputStatus(destination).FramesSubmitted;
        Thread.Sleep(150);
        Require(router.GetOutputStatus(destination).HResult == 0 && router.GetOutputStatus(destination).FramesSubmitted > submitted &&
            router.GetCaptureStatus(independent).HResult == 0, "Parking escape affected unrelated resources.");
        target.Quit(); survivor.Quit();
        Console.WriteLine("PASS: initial and runtime parking escape rejection; unrelated capture/output survive.");
    }

    /*
     * Name: Measure
     * Input: Router, output ID, and signal label.
     * Process: Settles live buffers, then measures 400 ms of submitted stereo PCM.
     * Output: RMS; failed streams, missing frames, or unexpected clipping fail.
     */
    private static double Measure(AudioRouter router, uint output, string label, params (uint Source, double Gain)[] sources)
    {
        Thread.Sleep(300);
        RoutingOutputStatus before = router.GetOutputStatus(output);
        ProcessCaptureStatus[] inputsBefore = sources.Select(item => router.GetCaptureStatus(item.Source)).ToArray();
        Thread.Sleep(400);
        RoutingOutputStatus after = router.GetOutputStatus(output);
        ProcessCaptureStatus[] inputsAfter = sources.Select(item => router.GetCaptureStatus(item.Source)).ToArray();
        Require(after.HResult == 0 && after.State == 1 && after.FramesSubmitted > before.FramesSubmitted && after.ClippedSamples == 0,
            $"Output failed during {label}.");
        double rms = Math.Sqrt(Math.Max(0, after.SumSquares - before.SumSquares) / (2 * (double)(after.FramesSubmitted - before.FramesSubmitted)));
        Console.WriteLine($"  {label}: RMS {rms:F8}, peak {after.LastPeak:F8}, {after.FramesSubmitted - before.FramesSubmitted} submitted frames");
        double expectedEnergy = 0;
        for (int index = 0; index < sources.Length; ++index)
        {
            Require(inputsAfter[index].HResult == 0 && inputsAfter[index].FramesCaptured > inputsBefore[index].FramesCaptured,
                "Capture stopped advancing during signal measurement.");
            double energy = (inputsAfter[index].SumSquares - inputsBefore[index].SumSquares) /
                (2 * (double)(inputsAfter[index].FramesCaptured - inputsBefore[index].FramesCaptured));
            expectedEnergy += energy * sources[index].Gain * sources[index].Gain;
            Console.WriteLine($"    Source {sources[index].Source} input RMS {Math.Sqrt(energy):F8}; gain {sources[index].Gain:F2}");
        }
        if (sources.Length > 0)
        {
            double expected = Math.Sqrt(expectedEnergy);
            Require(expected > 0 && rms / expected > 0.8 && rms / expected < 1.2,
                $"Output/input RMS ratio {rms / expected:F3} missed the expected gain/sum for {label}.");
        }
        return rms;
    }

    /*
     * Name: WriteWave
     * Input: Path and frequency.
     * Process: Writes a generated 30-second PCM16 mono sine at 48 kHz, peak 0.2.
     * Output: Temporary fixture; user media is never changed.
     */
    private static void WriteWave(string path, double frequency)
    {
        const int frames = 48000 * 30;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
        for (int frame = 0; frame < frames; ++frame) { writer.Write((short)(Math.Sin(2 * Math.PI * frequency * frame / 48000) * short.MaxValue * 0.2)); }
    }

    /*
     * Name: Child
     * Input: Test-owned executable and arguments.
     * Process: Redirects commands and drains output without blocking capture checks.
     * Output: Owned child process; disposal never targets user applications.
     */
    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly System.Threading.Tasks.Task<string> stdout, stderr;
        public int Id => process.Id;
        /*
         * Name: Child
         * Input: Executable and separate argument tokens.
         * Process: Starts a hidden redirected helper process.
         * Output: Ready command channel; launch errors propagate.
         */
        public Child(string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
            process = Process.Start(start)!;
            process.StandardInput.AutoFlush = true;
            stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
        }
        /*
         * Name: Send
         * Input: One helper console command.
         * Process: Writes and flushes stdin.
         * Output: No value; broken helpers fail the check.
         */
        public void Send(string command) { process.StandardInput.WriteLine(command); }
        /*
         * Name: Quit
         * Input: None.
         * Process: Closes the owned helper and requires a clean bounded exit.
         * Output: No value; native failures include captured child diagnostics.
         */
        public void Quit()
        {
            if (!process.HasExited) { Send("quit"); process.StandardInput.Close(); }
            Require(process.WaitForExit(5000), "Owned helper did not exit.");
            Require(process.ExitCode == 0, $"Helper failed: {stderr.GetAwaiter().GetResult()} {stdout.GetAwaiter().GetResult()}");
            Require(stderr.GetAwaiter().GetResult().Length == 0, $"Helper reported errors: {stderr.GetAwaiter().GetResult()}");
        }
        /*
         * Name: Dispose
         * Input: None.
         * Process: Releases the owned process, terminating it only after failed checks.
         * Output: No orphaned test helper; user processes are untouched.
         */
        public void Dispose() { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } process.Dispose(); }
    }

    /*
     * Name: WaitUntil
     * Input: Condition and failure description.
     * Process: Polls for at most three seconds.
     * Output: Returns on success or fails with the supplied context.
     */
    private static void WaitUntil(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(3)) { Thread.Sleep(25); }
        Require(condition(), message);
    }
    /*
     * Name: Expect
     * Input: Invalid operation and expected exception type.
     * Process: Requires controlled rejection.
     * Output: No value; unexpected success fails.
     */
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    /*
     * Name: Require
     * Input: Condition and failure message.
     * Process: Keeps assertions active in Release and Debug.
     * Output: Returns on success or throws.
     */
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}

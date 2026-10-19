using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AudioHaus.Interop;

namespace AudioHaus.TimingChecks;

/*
 * Name: Program
 * Input: Optional hardware or physical-disconnection check arguments.
 * Process: Exercises production timing APIs, independent routes, recovery, and cleanup.
 * Output: Exit zero for passed checks; one for failures, two for invalid arguments.
 */
internal static class Program
{
    /*
     * Name: Main
     * Input: --hardware <diagnostics-exe> <parking-id> <output-a> <output-b>, or
     *        --disconnect <output-a> <bluetooth-output>, or no arguments for ABI checks.
     * Process: Creates a generated fixture and runs requested checks with bounded ownership.
     * Output: PASS evidence or a diagnostic error; never copies user media.
     */
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { CheckLayout(); return 0; }
            string folder = Path.Combine(Path.GetTempPath(), "AudioHaus.TimingChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "TimingSine.wav");
            WriteWave(file, args[0] is "--disconnect" or "--cable-disconnect" ? 1800 : 300);
            if (args.Length == 5 && args[0] == "--hardware")
            {
                CheckLayout();
                CheckFile(file, args[3], args[4]);
                CheckCapture(file, args[1], args[2], args[3], args[4]);
                CheckUnderrun(file, args[1], args[3]);
                CheckUnderrun(file, args[1], args[3], args[2]);
                CheckPendingCleanup(file, args[1], args[2], args[3]);
                CheckConsole(file, args[1], args[3]);
                Console.WriteLine("PASS: all requested timing hardware checks.");
                return 0;
            }
            if (args.Length == 3 && args[0] == "--disconnect") { CheckDisconnect(file, args[1], args[2]); return 0; }
            if (args.Length == 5 && args[0] == "--cable-disconnect") { CheckCableDisconnect(file, args[1], args[2], args[3], args[4]); return 0; }
            if (args.Length == 4 && args[0] == "--starvation")
            {
                CheckUnderrun(file, args[1], args[3]); CheckUnderrun(file, args[1], args[3], args[2]); return 0;
            }
            Console.Error.WriteLine("Usage: AudioHaus.TimingChecks [--hardware <diagnostics-exe> <parking-id> <output-a> <output-b> | --disconnect <output-a> <bluetooth-output> | --cable-disconnect <diagnostics-exe> <parking-id> <output-a> <output-b> | --starvation <diagnostics-exe> <parking-id> <output>]");
            return 2;
        }
        catch (Exception error) { Console.Error.WriteLine($"FAIL: {error}"); return 1; }
    }

    /*
     * Name: CheckLayout
     * Input: Production managed interop assembly.
     * Process: Checks every new timing ABI field offset and managed rejection/disposal.
     * Output: PASS without opening any output stream; layout/validation failures throw.
     */
    private static void CheckLayout()
    {
        Layout("OutputTimingInfo", 96,
            "HResult DelayFrames CapacityFrames PaddingFrames DriftEnabled RetryInMs LastErrorHResult Reserved UnderrunCount RecoveryAttempts RecoveryCount RebufferCount ClockRatePpm MaxServiceGapMs StreamLatency100Ns FirstSignalQpc",
            new[] { 0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80, 88 });
        Layout("RouteTimingInfo", 80,
            "HResult Priming BufferedFrames TargetFrames CorrectionPpm PhaseErrorFrames ReadPositionFrame UnderrunCount UnderrunFrames OverrunCount RebufferCount Reserved",
            new[] { 0, 4, 8, 12, 16, 24, 32, 40, 48, 56, 64, 72 });
        Layout("CaptureTimingInfo", 48,
            "HResult RetryInMs ResumeState LastErrorHResult RecoveryAttempts RecoveryCount CapacityFrames Reserved",
            new[] { 0, 4, 8, 12, 16, 24, 32, 40 });
        using var router = AudioRouter.Create();
        Expect<ArgumentOutOfRangeException>(() => router.SetOutputDelay(1, TimeSpan.FromTicks(-1)));
        Expect<ArgumentOutOfRangeException>(() => router.SetOutputDelay(1, TimeSpan.FromSeconds(2.001)));
        Expect<ArgumentOutOfRangeException>(() => router.GetOutputTiming(0));
        Expect<InvalidOperationException>(() => router.SetOutputDelay(1, TimeSpan.Zero));
        Expect<InvalidOperationException>(() => router.RetryOutput(1));
        Expect<InvalidOperationException>(() => router.RetryCapture(1));
        Expect<InvalidOperationException>(() => router.GetRouteTiming(1, 1));
        Expect<InvalidOperationException>(() => router.GetCaptureTiming(1));
        router.Dispose();
        Expect<ObjectDisposedException>(() => router.GetOutputTiming(1));
        Expect<ObjectDisposedException>(() => router.SetDriftCorrection(1, true));
        TimingAbiChecks.Run();
        Console.WriteLine("PASS: timing ABI sizes 96/80/48, every field offset, argument errors, missing IDs, and disposal.");
    }

    /*
     * Name: Layout
     * Input: Production native-info type name, expected size, names, and offsets.
     * Process: Reflects actual marshalled storage rather than a duplicate test struct.
     * Output: Returns on exact layout; mismatch throws.
     */
    private static void Layout(string name, int size, string names, int[] offsets)
    {
        Type type = typeof(AudioRouter).Assembly.GetType($"AudioHaus.Interop.{name}", true)!;
        Require(Marshal.SizeOf(type) == size, $"Wrong {name} size.");
        string[] fields = names.Split(' ');
        for (int i = 0; i < fields.Length; ++i) { Require(Marshal.OffsetOf(type, fields[i]).ToInt32() == offsets[i], $"Wrong {name}.{fields[i]} offset."); }
    }

    /*
     * Name: CheckFile
     * Input: Generated PCM file and two distinct active outputs.
     * Process: Measures delayed first submission, runtime delay/policy, reconnect isolation,
     *          route settings, bounded queues, and a short-file tail with maximum delay.
     * Output: PASS with measured timing; hardware/native errors throw.
     */
    private static void CheckFile(string file, string endpointA, string endpointB)
    {
        Require(endpointA != endpointB, "Two distinct outputs are required.");
        using var router = AudioRouter.Create();
        uint source = router.AddSource(file), a = router.AddOutput(endpointA), b = router.AddOutput(endpointB);
        router.SetOutputDelay(b, TimeSpan.FromMilliseconds(500));
        router.Connect(source, a, 0.005); router.Connect(source, b, 0.005);
        router.Play(source);
        Thread.Sleep(250);
        Require(router.GetOutputTiming(a).FirstSignalQpc != 0 && router.GetOutputTiming(b).FirstSignalQpc == 0, "Delayed file output was not initially silent.");
        Wait(() => router.GetOutputTiming(b).FirstSignalQpc != 0, "Delayed file output never started.");
        double gap = (router.GetOutputTiming(b).FirstSignalQpc - router.GetOutputTiming(a).FirstSignalQpc) * 1000.0 / Stopwatch.Frequency;
        Require(gap >= 440 && gap <= 570, $"Requested 500 ms delay measured {gap:F2} ms at submission.");
        Thread.Sleep(2000);
        CheckOutput(router, a); CheckOutput(router, b);
        router.SetOutputDelay(b, TimeSpan.FromMilliseconds(125.5));
        Require(router.GetOutputTiming(b).DelayFrames == 6024 && router.GetOutputTiming(a).DelayFrames == 0, "Runtime delay changed the wrong destination.");
        router.SetDriftCorrection(b, false);
        Thread.Sleep(150);
        Require(!router.GetOutputTiming(b).DriftCorrectionEnabled && router.GetRouteTiming(source, b).CorrectionPpm == 0 &&
            router.GetOutputTiming(a).DriftCorrectionEnabled, "Disabling drift did not reset only its destination.");
        router.SetRouteGain(source, b, 0.003); router.SetRouteMute(source, b, true);
        for (int cycle = 0; cycle < 3; ++cycle)
        {
            ulong recoveries = router.GetOutputTiming(b).RecoveryCount;
            RoutingOutputStatus before = router.GetOutputStatus(a);
            router.RetryOutput(b);
            Wait(() => router.GetOutputTiming(b).RecoveryCount > recoveries, "Output failed to reopen.");
            Thread.Sleep(100);
            Require(router.GetOutputStatus(a).NonSilentFrames > before.NonSilentFrames, "Reopening B stalled A.");
            Require(router.GetRouteStatus(source, b).Muted && router.GetRouteStatus(source, b).Gain == 0.003 &&
                router.GetOutputTiming(b).DelayFrames == 6024 && !router.GetOutputTiming(b).DriftCorrectionEnabled, "Reopen lost route/output configuration.");
            CheckOutput(router, b);
        }
        router.SetRouteMute(source, b, false);
        router.SetOutputDelay(b, TimeSpan.FromSeconds(2));
        router.Stop(source);
        TimeSpan duration = router.GetSourceStatus(source).Duration;
        router.Seek(source, duration - TimeSpan.FromMilliseconds(150)); router.Play(source);
        Thread.Sleep(400);
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Playing, "Delayed tail ended before submission.");
        Wait(() => router.GetSourceStatus(source).State == FilePlaybackState.Ended, "Maximum-delay file tail never reached EOF.");
        Console.WriteLine($"PASS: 500 ms file delay measured {gap:F2} ms; runtime delay, drift toggle, three independent reopens, retained settings, and 2 s delayed EOF.");
    }

    /*
     * Name: CheckCapture
     * Input: Fixture, renderer helper, virtual cable, and two active physical outputs.
     * Process: Captures the helper PID, verifies delayed history and bounded readers,
     *          then reopens capture while preserving paused/stopped/playing monitor state.
     * Output: PASS with measured buffers/correction; no original sound escapes the cable.
     */
    private static void CheckCapture(string file, string executable, string parking, string endpointA, string endpointB)
    {
        using var child = new OwnedProcess(executable, "files", "--file", file, "--output", parking, "--gain", "0.02");
        Thread.Sleep(400); child.Send("play 1");
        using var router = AudioRouter.Create();
        uint source = router.AddProcessSource((uint)child.Id, parking), a = router.AddOutput(endpointA), b = router.AddOutput(endpointB);
        router.SetOutputDelay(b, TimeSpan.FromMilliseconds(500));
        router.Connect(source, a, 0); router.Connect(source, b, 0); router.Play(source);
        Wait(() => !router.GetRouteTiming(source, b).Priming, "Delayed process capture did not prime.");
        RouteTimingStatus initialA = router.GetRouteTiming(source, a), initialB = router.GetRouteTiming(source, b);
        Require(initialB.TargetFrames == 26400 && router.GetCaptureTiming(source).BufferCapacityFrames >= 36000, "Delay did not expand capture history/target.");
        ulong underruns = initialA.UnderrunCount + initialB.UnderrunCount;
        for (int sample = 0; sample < 60; ++sample)
        {
            Thread.Sleep(100);
            RouteTimingStatus x = router.GetRouteTiming(source, a), y = router.GetRouteTiming(source, b);
            Require(!x.Priming && !y.Priming && Math.Abs(x.CorrectionPpm) <= 2000 && Math.Abs(y.CorrectionPpm) <= 2000, "Live reader lost prime or escaped correction bounds.");
            Require(x.UnderrunCount + y.UnderrunCount == underruns, "Live reader underrun during the stable interval.");
            Require(x.BufferedFrames < 12000 && y.BufferedFrames >= 20000 && y.BufferedFrames <= 36000, "Live buffer depth is outside the expected delay window.");
        }
        foreach (FilePlaybackState state in new[] { FilePlaybackState.Paused, FilePlaybackState.Stopped, FilePlaybackState.Playing })
        {
            if (state == FilePlaybackState.Paused) { router.Pause(source); }
            else if (state == FilePlaybackState.Stopped) { router.Stop(source); }
            else { router.Play(source); }
            ulong count = router.GetCaptureTiming(source).RecoveryCount;
            router.RetryCapture(source);
            Wait(() => router.GetCaptureTiming(source).RecoveryCount > count, "Capture failed to reopen.");
            Require(router.GetSourceStatus(source).State == state && router.GetCaptureTiming(source).ResumeState == state, "Capture recovery changed the monitor state.");
        }
        Wait(() => !router.GetRouteTiming(source, b).Priming, "Recovered delayed capture did not re-prime.");
        ulong captured = router.GetCaptureStatus(source).NonSilentFrames;
        Thread.Sleep(300);
        Require(router.GetCaptureStatus(source).NonSilentFrames > captured, "Recovered source received no new signal.");
        Console.WriteLine($"PASS: 50/550 ms capture targets, expanded history, six-second bounded correction/queues, zero new underruns, and three capture reopens preserving transport.");
        router.Dispose(); child.Quit();
    }

    /*
     * Name: CheckUnderrun
     * Input: Fixture, diagnostics executable, and an active output.
     * Process: Stalls an owned router for 400 ms, then requires detected starvation and
     *          resumed nonzero PCM without restarting the router.
     * Output: PASS plus retained child log; shutdown must be clean.
     */
    private static void CheckUnderrun(string file, string executable, string endpoint, string? parking = null)
    {
        using OwnedProcess? renderer = parking is null ? null : new OwnedProcess(executable, "files", "--file", file, "--output", parking, "--gain", "0.02");
        if (renderer is not null) { Thread.Sleep(400); renderer.Send("play 1"); }
        using var child = new OwnedProcess(executable, "route");
        child.Send(renderer is null ? $"source \"{file}\"" : $"capture {renderer.Id} \"{parking}\""); child.Send($"output \"{endpoint}\"");
        child.Send("connect 1 1 0.001"); child.Send("play 1");
        Thread.Sleep(1500); child.Send("status"); Thread.Sleep(100);
        child.Stall(400);
        Thread.Sleep(1200); child.Send("status"); Thread.Sleep(400); child.Send("status");
        string log = child.Quit();
        string path = Path.Combine(Path.GetDirectoryName(file)!, renderer is null ? "Underrun.log" : "CaptureUnderrun.log"); File.WriteAllText(path, log);
        ulong[] counters = Regex.Matches(log, @"Render underruns: (\d+)").Select(match => ulong.Parse(match.Groups[1].Value)).ToArray();
        Require(counters.Length >= 3 && counters[^1] > counters[0], $"Forced starvation was not detected. See {path}.");
        Require(!log.Contains("Failed", StringComparison.Ordinal), $"Starved router failed. See {path}.");
        ulong[] signal = Regex.Matches(log, @"Non-silent frames: (\d+)").Select(match => ulong.Parse(match.Groups[1].Value)).ToArray();
        Require(signal.Length >= 3 && signal[^1] > signal[^2], $"Starved router did not resume signal. See {path}.");
        renderer?.Quit();
        Console.WriteLine($"PASS: {(renderer is null ? "file" : "process capture")} forced 400 ms scheduler outage, underrun detected, automatic fresh-reader recovery, advancing PCM, clean exit. Log: {path}");
    }

    /*
     * Name: CheckPendingCleanup
     * Input: Fixture, helper executable, parking endpoint, and physical output.
     * Process: Disposes graphs immediately after asynchronous output/capture retry requests.
     * Output: PASS if repeated pending activation cleanup remains bounded and helpers exit.
     */
    private static void CheckPendingCleanup(string file, string executable, string parking, string endpoint)
    {
        using var child = new OwnedProcess(executable, "files", "--file", file, "--output", parking, "--gain", "0.02");
        Thread.Sleep(300);
        for (int cycle = 0; cycle < 5; ++cycle)
        {
            var router = AudioRouter.Create();
            uint source = router.AddProcessSource((uint)child.Id, parking), output = router.AddOutput(endpoint);
            router.Connect(source, output, 0); router.Play(source);
            router.RetryOutput(output); router.RetryCapture(source);
            var elapsed = Stopwatch.StartNew(); router.Dispose();
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(7), "Pending activation disposal was not bounded.");
        }
        child.Quit();
        Console.WriteLine("PASS: five dispose-during-recovery cycles, all workers joined, helper closed cleanly.");
    }

    /*
     * Name: CheckConsole
     * Input: Test-owned fixture, diagnostics executable, and active output.
     * Process: Validates timing commands, finite delay parsing, IDs, and error containment.
     * Output: PASS for exact command rejection count and retained valid settings; silent.
     */
    private static void CheckConsole(string file, string executable, string endpoint)
    {
        using var child = new OwnedProcess(executable, "route");
        child.Send($"source \"{file}\""); child.Send($"output \"{endpoint}\"");
        child.Send("connect 1 1 0"); child.Send("delay 1 125.5");
        child.Send("drift 1 OFF"); child.Send("DRIFT 1 ON");
        string[] invalid = { "delay 1 -1", "delay 1 2001", "delay 1 NaN", "delay 1 Infinity", "delay 1 1e100",
            "delay 1", "delay 999 0", "drift 1 maybe", "drift 1", "retry-output 0", "retry-output 999", "retry-capture 1" };
        foreach (string line in invalid) { child.Send(line); }
        child.Send("timing");
        string output = child.Quit(invalid.Length);
        Require(output.Contains("delay 125.500 ms", StringComparison.Ordinal) && output.Contains("drift on", StringComparison.Ordinal),
            "Valid timing settings were lost after command errors.");
        Console.WriteLine($"PASS: timing console commands, case-insensitive drift, {invalid.Length} invalid delay/flag/ID cases, preserved settings, clean exit.");
    }

    /*
     * Name: CheckDisconnect
     * Input: Stable speaker and user-controlled Bluetooth endpoint.
     * Process: Waits for a physical disconnection and reconnection while preserving route
     *          identity/settings and requiring uninterrupted speaker submission.
     * Output: PASS only after an observed real outage and automatic audible-stream recovery.
     */
    private static void CheckDisconnect(string file, string endpointA, string endpointB)
    {
        using var router = AudioRouter.Create();
        uint source = router.AddSource(file), a = router.AddOutput(endpointA), b = router.AddOutput(endpointB);
        router.SetOutputDelay(b, TimeSpan.FromMilliseconds(125));
        router.Connect(source, a, 0.01); router.Connect(source, b, 0.01); router.Play(source);
        Console.WriteLine("READY: quiet tone on both outputs. Turn Bluetooth headphones OFF; leave Realtek enabled.");
        Wait(() => router.GetOutputStatus(b).State == 2, "No physical Bluetooth outage observed within ten minutes.", 600000);
        RoutingOutputStatus before = router.GetOutputStatus(a);
        ulong attempts = router.GetOutputTiming(b).RecoveryAttempts;
        Thread.Sleep(1500);
        Require(router.GetOutputStatus(a).NonSilentFrames > before.NonSilentFrames && router.GetOutputStatus(a).State == 1, "Bluetooth outage interrupted the speaker.");
        Require(router.GetOutputTiming(b).RecoveryAttempts > attempts, "Disconnected output did not retry.");
        Console.WriteLine("OUTAGE CONFIRMED: Realtek continues, Bluetooth is recovering. Turn headphones ON and reconnect them now.");
        Wait(() => router.GetOutputStatus(b).State == 1 && router.GetOutputTiming(b).RecoveryCount > 0, "Bluetooth did not automatically recover within ten minutes.", 600000);
        ulong signal = router.GetOutputStatus(b).NonSilentFrames;
        Thread.Sleep(1500);
        Require(router.GetOutputStatus(b).NonSilentFrames > signal && router.GetOutputTiming(b).DelayFrames == 6000 &&
            router.GetRouteStatus(source, b).Gain == 0.01 && router.GetSourceStatus(source).RouteCount == 2, "Reconnect lost signal, route identity, delay, or gain.");
        Console.WriteLine($"PASS: physical Bluetooth disconnect/reconnect, isolated speaker, automatic same-output recovery, fresh PCM, retained settings. Attempts {router.GetOutputTiming(b).RecoveryAttempts}.");
    }

    /*
     * Name: CheckOutput
     * Input: Router and output ID.
     * Process: Requires a healthy running stream and valid finite queue/clock telemetry.
     * Output: Returns on valid timing; corrupt/unhealthy state throws.
     */
    private static void CheckOutput(AudioRouter router, uint id)
    {
        OutputTimingStatus timing = router.GetOutputTiming(id);
        Require(router.GetOutputStatus(id).State == 1 && timing.HResult == 0 && timing.BufferCapacityFrames > 0 &&
            timing.PaddingFrames <= timing.BufferCapacityFrames && double.IsFinite(timing.ClockRatePpm) && timing.StreamLatency >= TimeSpan.Zero,
            "Invalid renderer timing snapshot.");
    }

    /*
     * Name: CheckCableDisconnect
     * Input: Fixture, diagnostics executable, parking endpoint, and two physical outputs.
     * Process: Keeps a test renderer pinned to the cable, observes a user-controlled
     *          cable disable/enable cycle, and checks automatic capture/render recovery.
     * Output: PASS only for a real observed outage, independent surviving stream, and
     *         new captured signal on the original PID; user output settings are untouched.
     */
    private static void CheckCableDisconnect(string file, string executable, string parking, string endpointA, string endpointB)
    {
        Console.WriteLine("SETUP: waiting for CABLE Input to be enabled before opening the capture session.");
        Wait(() => new AudioEndpointEnumerator().Enumerate().Any(endpoint => endpoint.Id == parking && endpoint.State == AudioHausEndpointState.Active),
            "CABLE Input was not enabled within ten minutes.", 600000);
        using var child = new OwnedProcess(executable, "route");
        child.Send($"source \"{file}\""); child.Send($"output \"{parking}\"");
        child.Send("connect 1 1 0.02"); child.Send("play 1"); Thread.Sleep(1000);
        using var router = AudioRouter.Create();
        uint source = router.AddProcessSource((uint)child.Id, parking), independent = router.AddSource(file);
        uint a = router.AddOutput(endpointA), b = router.AddOutput(endpointB);
        router.Connect(source, a, 0.1); router.Connect(independent, b, 0.005);
        router.SetOutputDelay(a, TimeSpan.FromMilliseconds(100));
        router.Play(source); router.Play(independent);
        Wait(() => router.GetOutputStatus(a).NonSilentFrames > 0, "Initial captured signal was absent.");
        Console.WriteLine("READY: disable only Playback > CABLE Input in mmsys.cpl. Leave CABLE In16ch and physical outputs enabled.");
        Wait(() => router.GetSourceStatus(source).State == FilePlaybackState.Recovering, "No cable outage observed within ten minutes.", 600000);
        RoutingOutputStatus before = router.GetOutputStatus(b);
        ulong attempts = router.GetCaptureTiming(source).RecoveryAttempts;
        Thread.Sleep(1500);
        Require(router.GetOutputStatus(b).NonSilentFrames > before.NonSilentFrames && router.GetOutputStatus(b).State == 1, "Cable outage interrupted the independent file output.");
        Require(router.GetSourceStatus(source).State == FilePlaybackState.Recovering && router.GetCaptureTiming(source).RecoveryAttempts > attempts,
            "Unavailable cable did not stay in transient recovery with retries.");
        Console.WriteLine("OUTAGE CONFIRMED: capture is recovering; independent headphones continue. Re-enable Playback > CABLE Input now.");
        Wait(() => router.GetSourceStatus(source).State == FilePlaybackState.Playing && router.GetCaptureTiming(source).RecoveryCount > 0,
            "Capture did not recover within ten minutes.", 600000);
        ulong captured = router.GetCaptureStatus(source).NonSilentFrames, signal = router.GetOutputStatus(a).NonSilentFrames;
        Wait(() => router.GetCaptureStatus(source).NonSilentFrames > captured && router.GetOutputStatus(a).NonSilentFrames > signal,
            "Recovered capture had no new application signal.");
        Require(router.GetCaptureStatus(source).ProcessId == child.Id && router.GetOutputTiming(a).DelayFrames == 4800 &&
            router.GetRouteStatus(source, a).Gain == 0.1 && router.GetCaptureTiming(source).ResumeState == FilePlaybackState.Playing,
            "Capture reconnect lost root identity, output delay, gain, or desired monitor state.");
        Console.WriteLine($"PASS: real cable disable/enable, transient capture retries, uninterrupted independent file output, automatic original-PID recovery and fresh routed signal. Attempts {router.GetCaptureTiming(source).RecoveryAttempts}.");
        router.Dispose(); child.Quit();
    }

    /*
     * Name: WriteWave
     * Input: Temporary fixture path and duration in seconds.
     * Process: Writes quiet, deterministic mono 48 kHz PCM for the bounded test window.
     * Output: A test-owned WAV file; filesystem errors propagate.
     */
    private static void WriteWave(string path, int seconds)
    {
        int frames = checked(48000 * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
        for (int frame = 0; frame < frames; ++frame) { writer.Write((short)(Math.Sin(2 * Math.PI * 330 * frame / 48000) * short.MaxValue * 0.2)); }
    }

    /*
     * Name: Wait
     * Input: Condition, failure context, and bounded timeout.
     * Process: Polls production status every 25 milliseconds.
     * Output: Returns on observed success or throws with context.
     */
    private static void Wait(Func<bool> condition, string message, int timeoutMs = 7000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(25); }
        Require(condition(), message);
    }

    /*
     * Name: Expect
     * Input: Action and expected exception type.
     * Process: Rejects unexpected successful validation.
     * Output: Expected exception is accepted; otherwise throws.
     */
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    /*
     * Name: Require
     * Input: Condition and failure message.
     * Process: Keeps assertions active in optimized builds.
     * Output: Returns on success or throws a descriptive failure.
     */
    internal static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}

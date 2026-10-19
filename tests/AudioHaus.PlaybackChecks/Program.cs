using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AudioHaus.Interop;

namespace AudioHaus.PlaybackChecks;

/*
 * Name: Program
 * Input: Optional single-output, dual-output, or automatic hardware-check arguments.
 * Process: Checks argument validation, then optionally verifies real WASAPI streams.
 * Output: Repeatable PASS/FAIL results and a separate hardware verification status.
 */
internal static class Program
{
    /*
     * Name: Main
     * Input: Command-line arguments.
     * Process: Runs nonplaying validation and any explicitly requested tone checks.
     * Output: Exit code 0 for passed requested checks, 1 for failure, or 2 when
     *         the requested hardware is unavailable. Errors include no stack trace.
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

            bool single = args.Length == 2 && args[0] == "--single";
            bool dual = args.Length == 3 && args[0] == "--dual";
            bool hardware = args.Length == 1 && args[0] == "--hardware";
            bool recovery = args.Length == 2 && args[0] == "--failure-recovery";
            if (args.Length != 0 && !single && !dual && !hardware && !recovery)
            {
                PrintUsage(Console.Error);
                return 1;
            }

            CheckArgumentValidation();
            Console.WriteLine("PASS: 23 managed argument checks; no playback requested by these checks.");
            CheckInteropLayout();
            CheckNativeBoundary();

            if (args.Length == 0)
            {
                Console.WriteLine("Hardware playback: NOT VERIFIED. Use --single, --dual, or --hardware.");
                return 0;
            }

            if (hardware)
            {
                return CheckAvailableHardware();
            }

            if (single || recovery)
            {
                string endpointId = args[1];
                if (string.Equals(endpointId, "default", StringComparison.OrdinalIgnoreCase))
                {
                    AudioEndpoint? endpoint = new AudioEndpointEnumerator().Enumerate()
                        .FirstOrDefault(item => item.State == AudioHausEndpointState.Active && item.IsDefaultMultimedia);
                    if (endpoint is null)
                    {
                        Console.WriteLine("Single-output playback: NOT VERIFIED; no active multimedia default.");
                        return 2;
                    }

                    endpointId = endpoint.Id;
                }

                if (recovery)
                {
                    CheckFailureRecovery(endpointId);
                    return 0;
                }

                CheckPlayback(new[] { endpointId }, TimeSpan.FromSeconds(1));
            }
            else
            {
                CheckPlayback(new[] { args[1], args[2] }, TimeSpan.FromSeconds(2));
            }

            PrintListeningRequirement();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL: {error.Message}");
            return 1;
        }
    }

    /*
     * Name: CheckArgumentValidation
     * Input: None; invalid IDs are deliberately unusable if validation regresses.
     * Process: Requires argument exceptions for invalid inputs before native playback.
     * Output: No audio; throws InvalidOperationException if a check fails.
     */
    private static void CheckArgumentValidation()
    {
        string[] ids = { "AudioHaus.Validation.NotAnEndpoint" };
        TimeSpan duration = TimeSpan.FromSeconds(1);

        ExpectArgumentError("null list", () => TestTonePlayer.Play(null!, duration));
        ExpectArgumentError("empty list", () => TestTonePlayer.Play(Array.Empty<string>(), duration));
        ExpectArgumentError("too many outputs", () => TestTonePlayer.Play(new[] { "a", "b", "c" }, duration));
        ExpectArgumentError("null ID", () => TestTonePlayer.Play(new[] { (string)null! }, duration));
        ExpectArgumentError("empty ID", () => TestTonePlayer.Play(new[] { string.Empty }, duration));
        ExpectArgumentError("embedded null ID", () => TestTonePlayer.Play(new[] { "invalid\0suffix" }, duration));
        ExpectArgumentError("duplicate IDs", () => TestTonePlayer.Play(new[] { "same", "SAME" }, duration));

        foreach (double frequency in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 19.99, 20000.01 })
        {
            ExpectArgumentError($"frequency {frequency}", () => TestTonePlayer.Play(ids, duration, frequencyHz: frequency));
        }

        foreach (double amplitude in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.01, 0.25001 })
        {
            ExpectArgumentError($"amplitude {amplitude}", () => TestTonePlayer.Play(ids, duration, amplitude: amplitude));
        }

        foreach (TimeSpan invalidDuration in new[]
        {
            TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromTicks(9999),
            TimeSpan.FromMilliseconds(60000.1), TimeSpan.MinValue, TimeSpan.MaxValue
        })
        {
            ExpectArgumentError($"duration {invalidDuration}", () => TestTonePlayer.Play(ids, invalidDuration));
        }
    }

    /*
     * Name: ExpectArgumentError
     * Input: Check name and an invocation with invalid arguments.
     * Process: Requires a managed argument exception rather than a native result.
     * Output: Returns on the expected exception; otherwise throws with check context.
     */
    private static void ExpectArgumentError(string name, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException($"Argument check did not reject {name}.");
    }

    /*
     * Name: CheckInteropLayout
     * Input: Internal report type from the production interop assembly.
     * Process: Verifies the actual marshaled size and field offsets against the C ABI.
     * Output: A PASS line or an assertion failure; no audio resources are opened.
     */
    private static void CheckInteropLayout()
    {
        Type reportType = typeof(TestTonePlayer).Assembly.GetType("AudioHaus.Interop.AudioHausToneOutputInfo", throwOnError: true)!;
        Require(Marshal.SizeOf(reportType) == 40, "Native playback report must occupy 40 bytes.");
        string[] names = { "SampleRate", "ChannelCount", "FramesSubmitted", "StartQpc", "StopQpc", "HResult", "Completed" };
        int[] offsets = { 0, 4, 8, 16, 24, 32, 36 };
        for (int index = 0; index < names.Length; ++index)
        {
            Require(Marshal.OffsetOf(reportType, names[index]).ToInt32() == offsets[index], $"Report field {names[index]} has the wrong native offset.");
        }
        Console.WriteLine("PASS: 40-byte playback report ABI and all seven field offsets.");
    }

    /*
     * Name: NativeToneCall
     * Input: Raw C ABI pointer, count, tone, and output-buffer arguments.
     * Process: Calls the real native export independently of production marshaling.
     * Output: Native status code for nonplaying boundary validation.
     */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeToneCall(IntPtr endpointIds, uint endpointCount,
        double frequencyHz, double amplitude, uint durationMs, IntPtr reports, uint capacity);

    /*
     * Name: CheckNativeBoundary
     * Input: Native DLL alongside the validation executable.
     * Process: Calls the C export with invalid requests and guarded report storage.
     * Output: A PASS line when errors, zeroing, and buffer limits match the ABI;
     *         no request can reach playback. All unmanaged resources are released.
     */
    private static void CheckNativeBoundary()
    {
        IntPtr library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "AudioHaus.Native.dll"));
        IntPtr ids = IntPtr.Zero;
        IntPtr firstId = IntPtr.Zero;
        IntPtr secondId = IntPtr.Zero;
        IntPtr reports = IntPtr.Zero;
        try
        {
            var invoke = Marshal.GetDelegateForFunctionPointer<NativeToneCall>(NativeLibrary.GetExport(library, "audiohaus_play_test_tone"));
            ids = Marshal.AllocHGlobal(IntPtr.Size * 2);
            firstId = Marshal.StringToHGlobalUni("AudioHaus.Validation.Missing.First");
            secondId = Marshal.StringToHGlobalUni("AudioHaus.Validation.Missing.Second");
            Marshal.WriteIntPtr(ids, 0, firstId);
            Marshal.WriteIntPtr(ids, IntPtr.Size, secondId);
            reports = Marshal.AllocHGlobal(96);
            byte[] guard = Enumerable.Repeat((byte)0xA5, 96).ToArray();

            Marshal.Copy(guard, 0, reports, guard.Length);
            Require(invoke(ids, 2, 440, 0, 10, reports, 1) == 2, "Native small buffer must return BufferTooSmall.");
            CheckReportBytes(reports, zeroBytes: 40, totalBytes: 96);

            Marshal.Copy(guard, 0, reports, guard.Length);
            Require(invoke(ids, 1, 440, 0, 10, reports, 0) == 2, "Native zero-capacity buffer must return BufferTooSmall.");
            CheckReportBytes(reports, zeroBytes: 0, totalBytes: 96);

            Marshal.Copy(guard, 0, reports, guard.Length);
            Require(invoke(ids, 2, double.NaN, 0, 10, reports, 2) == 1, "Native NaN frequency must return InvalidArgument.");
            CheckReportBytes(reports, zeroBytes: 80, totalBytes: 96);

            Marshal.Copy(guard, 0, reports, guard.Length);
            Require(invoke(IntPtr.Zero, 1, double.NaN, 0, 10, reports, 2) == 1, "Native null IDs must return InvalidArgument.");
            Require(invoke(ids, 0, double.NaN, 0, 10, reports, 2) == 1, "Native zero outputs must return InvalidArgument.");
            Require(invoke(ids, 3, double.NaN, 0, 10, reports, 2) == 1, "Native excess outputs must return InvalidArgument.");
            CheckReportBytes(reports, zeroBytes: 0, totalBytes: 96);
            Require(invoke(ids, 1, double.NaN, 0, 10, IntPtr.Zero, 1) == 1, "Native null reports must return InvalidArgument.");
            Console.WriteLine("PASS: native invalid requests, result zeroing, and guarded output-buffer boundaries.");
        }
        finally
        {
            Marshal.FreeHGlobal(reports);
            Marshal.FreeHGlobal(secondId);
            Marshal.FreeHGlobal(firstId);
            Marshal.FreeHGlobal(ids);
            NativeLibrary.Free(library);
        }
    }

    /*
     * Name: CheckReportBytes
     * Input: Report memory, expected cleared byte count, and allocated byte count.
     * Process: Checks cleared reports and unchanged guard bytes after native calls.
     * Output: Returns on matching storage or throws with the mismatching byte offset.
     */
    private static void CheckReportBytes(IntPtr reports, int zeroBytes, int totalBytes)
    {
        var bytes = new byte[totalBytes];
        Marshal.Copy(reports, bytes, 0, bytes.Length);
        for (int index = 0; index < bytes.Length; ++index)
        {
            byte expected = index < zeroBytes ? (byte)0 : (byte)0xA5;
            Require(bytes[index] == expected, $"Native report-buffer mismatch at byte {index}.");
        }
    }

    /*
     * Name: CheckFailureRecovery
     * Input: A real active playback endpoint ID.
     * Process: Forces second-output preparation failure, checks neither stream
     *          starts, then retries the first endpoint with silent PCM.
     * Output: Rollback and resource-reuse assertions; no audible tone is generated.
     */
    private static void CheckFailureRecovery(string endpointId)
    {
        string missingId = $"AudioHaus.Validation.Missing.{Guid.NewGuid():N}";
        IReadOnlyList<TonePlaybackResult> missingOnly = TestTonePlayer.Play(new[] { missingId }, TimeSpan.FromMilliseconds(100), amplitude: 0);
        Require(missingOnly.Count == 1 && !missingOnly[0].Completed && missingOnly[0].WasapiResult != 0, "The missing endpoint did not return a playback failure.");
        Require(missingOnly[0].StartQpc == 0 && missingOnly[0].StopQpc == 0 && missingOnly[0].FramesSubmitted == 0, "A missing endpoint unexpectedly started or accepted frames.");

        IReadOnlyList<TonePlaybackResult> failedGroup = TestTonePlayer.Play(new[] { endpointId, missingId }, TimeSpan.FromMilliseconds(100), amplitude: 0);
        Require(failedGroup.Count == 2, "Failed group returned the wrong report count.");
        Require(failedGroup[0].FramesSubmitted > 0, "The valid first output was not prepared and primed before the second failed.");
        Require(failedGroup[0].WasapiResult == 0x80004004, "The prepared first output did not retain E_ABORT.");
        Require(failedGroup[1].WasapiResult == missingOnly[0].WasapiResult, "The missing output HRESULT changed between single and grouped preparation.");
        foreach (TonePlaybackResult result in failedGroup)
        {
            Require(!result.Completed && result.StartQpc == 0 && result.StopQpc == 0, "An output started despite grouped preparation failure.");
        }
        Console.WriteLine($"PASS: second-output failure 0x{failedGroup[1].WasapiResult:X8} rolled back a primed first output; neither stream started.");

        CheckPlayback(new[] { endpointId }, TimeSpan.FromMilliseconds(100.5), amplitude: 0);
        Console.WriteLine("PASS: silent retry completed after rollback; 100.5 ms rounded to 101 ms (4,848 frames).");
        Console.WriteLine("Dual-device playback remains unverified by this failure-recovery check.");
    }

    /*
     * Name: CheckAvailableHardware
     * Input: Active endpoint discovery snapshot from Windows.
     * Process: Tests the preferred active endpoint, then two distinct active outputs.
     * Output: Exit code 0 for both stream checks, or 2 if fewer than two outputs
     *         are available. Playback and discovery failures propagate to Main.
     */
    private static int CheckAvailableHardware()
    {
        AudioEndpoint[] endpoints = new AudioEndpointEnumerator().Enumerate()
            .Where(item => item.State == AudioHausEndpointState.Active)
            .OrderByDescending(item => item.IsDefaultMultimedia)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (endpoints.Length == 0)
        {
            Console.WriteLine("Hardware playback: NOT VERIFIED; no active outputs.");
            return 2;
        }

        Console.WriteLine($"Single output: {endpoints[0].DisplayName}");
        CheckPlayback(new[] { endpoints[0].Id }, TimeSpan.FromSeconds(1));
        if (endpoints.Length < 2)
        {
            Console.WriteLine("Dual-output playback: NOT VERIFIED; two distinct active outputs are required.");
            PrintListeningRequirement();
            return 2;
        }

        Console.WriteLine($"Dual outputs: {endpoints[0].DisplayName} + {endpoints[1].DisplayName}");
        CheckPlayback(new[] { endpoints[0].Id, endpoints[1].Id }, TimeSpan.FromSeconds(2));
        PrintListeningRequirement();
        return 0;
    }

    /*
     * Name: CheckPlayback
     * Input: One or two distinct endpoint IDs, a duration, and peak amplitude.
     * Process: Plays a 440 Hz tone at the requested level and checks reports,
     *          submitted frame counts, and overlapping stream clock intervals.
     * Output: Stream evidence on standard output; throws for failed assertions.
     */
    private static void CheckPlayback(IReadOnlyList<string> ids, TimeSpan duration, double amplitude = 0.05)
    {
        IReadOnlyList<TonePlaybackResult> results = TestTonePlayer.Play(ids, duration, frequencyHz: 440, amplitude: amplitude);
        Require(results.Count == ids.Count, "Playback report count does not match output count.");

        ulong expectedFrames = checked((ulong)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero) * 48);
        ulong latestStart = 0;
        ulong earliestStop = ulong.MaxValue;
        for (int index = 0; index < results.Count; ++index)
        {
            TonePlaybackResult result = results[index];
            Require(string.Equals(result.EndpointId, ids[index], StringComparison.OrdinalIgnoreCase), "Playback report endpoint order mismatch.");
            Require(result.WasapiResult == 0, $"Output {index + 1} returned HRESULT 0x{result.WasapiResult:X8}.");
            Require(result.Completed, $"Output {index + 1} did not complete.");
            Require(result.SampleRate == 48000 && result.ChannelCount == 2, $"Output {index + 1} format is not 48 kHz stereo.");
            Require(result.FramesSubmitted == expectedFrames, $"Output {index + 1}: expected {expectedFrames} frames, received {result.FramesSubmitted}.");
            Require(result.StartQpc > 0 && result.StopQpc > result.StartQpc, $"Output {index + 1} has an invalid stream interval.");

            latestStart = Math.Max(latestStart, result.StartQpc);
            earliestStop = Math.Min(earliestStop, result.StopQpc);
            double elapsed = (result.StopQpc - result.StartQpc) / (double)Stopwatch.Frequency;
            Console.WriteLine($"PASS: output {index + 1}, {result.FramesSubmitted} frames, 48 kHz stereo, {elapsed:F3}s stream interval.");
        }

        if (results.Count == 2)
        {
            Require(earliestStop > latestStart, "The two output stream intervals do not overlap.");
            double overlap = (earliestStop - latestStart) / (double)Stopwatch.Frequency;
            Console.WriteLine($"PASS: simultaneous WASAPI stream overlap {overlap:F3}s.");
        }
    }

    /*
     * Name: Require
     * Input: Assertion condition and failure description.
     * Process: Converts failed assertions into a diagnostic exception.
     * Output: Returns for success or throws InvalidOperationException.
     */
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /*
     * Name: PrintListeningRequirement
     * Input: None.
     * Process: Separates WASAPI report verification from acoustic evidence.
     * Output: A listening confirmation reminder on standard output.
     */
    private static void PrintListeningRequirement()
    {
        Console.WriteLine("Listening confirmation still required: hear the tone on each intended device.");
        Console.WriteLine("Stream reports alone do not prove audible playback, Bluetooth transport, or acoustic synchronization.");
    }

    /*
     * Name: PrintUsage
     * Input: Output writer.
     * Process: Describes the nonplaying default and opt-in real-device checks.
     * Output: Usage text; writer failures propagate to Main.
     */
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: AudioHaus.PlaybackChecks [--single <endpoint-id|default> | --dual <id> <id> | --hardware | --failure-recovery <id|default> | --help]");
        writer.WriteLine("No arguments: managed validation, native ABI layout, and guarded invalid native calls; no playback.");
        writer.WriteLine("--single: one-second tone; --dual: simultaneous two-second tones.");
        writer.WriteLine("--hardware: single output, then two distinct active outputs when available.");
        writer.WriteLine("--failure-recovery: force a second-output failure and retry the real output silently.");
        writer.WriteLine("Exit codes: 0 requested checks passed; 1 failure; 2 insufficient active hardware.");
    }
}

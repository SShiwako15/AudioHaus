using System;
using System.Collections.Generic;

namespace AudioHaus.Interop;

/*
 * Name: TestTonePlayer
 * Input: One or two distinct endpoint IDs and bounded test-tone settings.
 * Process: Validates settings before invoking synchronous native WASAPI playback.
 * Output: Read-only per-output reports after every stream has stopped.
 */
public static class TestTonePlayer
{
    private const uint SupportedApiVersion = 1;

    /*
     * Name: Play
     * Input: Endpoint IDs, duration 1..60000 ms, frequency 20..20000 Hz, and peak
     *        amplitude 0..0.25. Fractional milliseconds round away from zero.
     * Process: Copies and validates IDs, checks the ABI, and plays all outputs as
     *          one group. Blocks until completion/failure; no device settings change.
     * Output: Per-output reports, including playback failures. Invalid arguments,
     *         incompatible ABI, loading failures, or internal errors throw.
     */
    public static IReadOnlyList<TonePlaybackResult> Play(
        IReadOnlyList<string> endpointIds,
        TimeSpan duration,
        double frequencyHz = 440.0,
        double amplitude = 0.05)
    {
        ArgumentNullException.ThrowIfNull(endpointIds);
        if (endpointIds.Count is < 1 or > 2)
        {
            throw new ArgumentException("Select one or two playback endpoints.", nameof(endpointIds));
        }

        var ids = new string[endpointIds.Count];
        for (int index = 0; index < ids.Length; ++index)
        {
            ArgumentException.ThrowIfNullOrEmpty(endpointIds[index], nameof(endpointIds));
            if (endpointIds[index].Contains('\0'))
            {
                throw new ArgumentException("Endpoint IDs cannot contain null characters.", nameof(endpointIds));
            }
            ids[index] = endpointIds[index];
        }

        if (ids.Length == 2 && StringComparer.OrdinalIgnoreCase.Equals(ids[0], ids[1]))
        {
            throw new ArgumentException("Playback endpoints must be distinct.", nameof(endpointIds));
        }

        if (!double.IsFinite(frequencyHz) || frequencyHz < 20.0 || frequencyHz > 20000.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be between 20 and 20000 Hz.");
        }
        if (!double.IsFinite(amplitude) || amplitude < 0.0 || amplitude > 0.25)
        {
            throw new ArgumentOutOfRangeException(nameof(amplitude), "Peak amplitude must be between 0 and 0.25.");
        }
        if (duration.TotalMilliseconds < 1.0 || duration.TotalMilliseconds > 60000.0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be between 1 and 60000 milliseconds.");
        }

        uint durationMs = checked((uint)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero));
        uint version = NativeMethods.GetApiVersion();
        if (version != SupportedApiVersion)
        {
            throw new NotSupportedException($"Native API version {version} is unsupported; expected {SupportedApiVersion}.");
        }

        var nativeResults = new AudioHausToneOutputInfo[ids.Length];
        AudioHausResult result = NativeMethods.PlayTestTone(
            ids, (uint)ids.Length, frequencyHz, amplitude, durationMs, nativeResults, (uint)nativeResults.Length);

        if (result == AudioHausResult.OutOfMemory)
        {
            throw new OutOfMemoryException("Native test-tone playback could not allocate its resources.");
        }
        if (result != AudioHausResult.Success && result != AudioHausResult.PlaybackFailed)
        {
            throw new InvalidOperationException($"Native test-tone request failed: {result}.");
        }

        var reports = new TonePlaybackResult[ids.Length];
        for (int index = 0; index < ids.Length; ++index)
        {
            reports[index] = new TonePlaybackResult(ids[index], nativeResults[index]);
        }
        return Array.AsReadOnly(reports);
    }
}

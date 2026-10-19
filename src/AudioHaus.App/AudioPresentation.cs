using System;
using System.Globalization;
using AudioHaus.Interop;

namespace AudioHaus.App;

/*
 * Name: AudioPresentation
 * Input: Copied native telemetry and failures.
 * Process: Formats actual PCM measurements and actionable audio errors.
 * Output: Display values without native resource access.
 */
internal static class AudioPresentation
{
    /*
     * Name: Error
     * Input: Operation context and exception.
     * Process: Preserves native HRESULT details and adds setup guidance where known.
     * Output: A concise actionable error.
     */
    public static string Error(string operation, Exception error) => $"{operation}: {Guidance(error.Message)}";

    /*
     * Name: HResult
     * Input: Native HRESULT and recovering flag.
     * Process: Converts failure identity into readable setup/recovery guidance.
     * Output: Empty text on success or an error with its original hexadecimal code.
     */
    public static string HResult(uint value, bool recovering = false) => value == 0 ? string.Empty :
        Guidance($"{(recovering ? "Waiting for the selected device" : "Audio operation failed")} (HRESULT 0x{value:X8}).");

    /*
     * Name: Guidance
     * Input: Native or managed failure text.
     * Process: Adds instructions for known parking and device failures.
     * Output: Text preserving the supplied diagnostic detail.
     */
    private static string Guidance(string message)
    {
        if (message.Contains("8007139F", StringComparison.OrdinalIgnoreCase))
        { return message + " Restart the app's playback, then disconnect and reconnect its route. Apps that pin an explicit output may not support automatic switching."; }
        if (message.Contains("88890004", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("80070490", StringComparison.OrdinalIgnoreCase))
        { return message + " Reconnect or enable that device; AudioHaus retains its routes while retrying."; }
        return message;
    }

    /*
     * Name: State
     * Input: Native source transport state.
     * Process: Maps a stable state value to readable text.
     * Output: The source state label.
     */
    public static string State(FilePlaybackState state) => state switch
    {
        FilePlaybackState.Stopped => "Stopped", FilePlaybackState.Playing => "Playing",
        FilePlaybackState.Paused => "Paused", FilePlaybackState.Ended => "Ended",
        FilePlaybackState.Failed => "Error", FilePlaybackState.Recovering => "Recovering", _ => "Unknown"
    };

    /*
     * Name: Time
     * Input: Media position.
     * Process: Formats durations without wrapping after an hour.
     * Output: Minutes/seconds or hours/minutes/seconds text.
     */
    public static string Time(TimeSpan value) => value.TotalHours >= 1 ?
        value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /*
     * Name: Meter
     * Input: Consecutive cumulative stereo PCM frames/energy and the latest real peak.
     * Process: Computes interval RMS only when fresh frames and valid energy exist.
     * Output: A -60..0 dBFS meter mapped to 0..100, with actual RMS/peak text.
     */
    public static (double Level, string Text) Meter(ulong frames, double squares, ulong previousFrames,
        double previousSquares, double peak, bool active)
    {
        if (!active || frames <= previousFrames || squares < previousSquares || !double.IsFinite(squares))
        { return (0, "Silent · −∞ dBFS"); }
        double rms = Math.Sqrt(Math.Max(0, (squares - previousSquares) / ((frames - previousFrames) * 2.0)));
        if (!double.IsFinite(rms) || rms < 0.000001) { return (0, "Silent · −∞ dBFS"); }
        double db = 20 * Math.Log10(rms);
        double peakDb = double.IsFinite(peak) && peak > 0 ? 20 * Math.Log10(peak) : -120;
        return (Math.Clamp((db + 60) / 60 * 100, 0, 100),
            $"{db.ToString("F1", CultureInfo.InvariantCulture)} dBFS RMS · peak {peakDb.ToString("F1", CultureInfo.InvariantCulture)} dBFS");
    }
}

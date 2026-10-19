using System;
using AudioHaus.Interop;

namespace AudioHaus.App;

/*
 * Name: SourceCardViewModel
 * Input: Active Windows audio-session lifetime and coordinator callbacks.
     * Process: Displays one application source without media-player transport.
 * Output: Identity, master controls, routed signal meter, and colored destinations.
 */
public sealed class SourceCardViewModel : AudioCardViewModel
{
    private ulong previousFrames;
    private double previousSquares;
    public ProcessIdentity Identity { get; }
    public string ApplicationIdentity { get; }
    public string ProcessLabel => $"PID {Identity.ProcessId}";
    public override string AutomationKey => $"{Identity.ProcessId}_{Identity.CreationTime}";

    /*
     * Name: SourceCardViewModel
     * Input: Active session and UI callbacks.
     * Process: Retains its process lifetime and application preference identity.
     * Output: Available source card with independent selection and master controls.
     */
    internal SourceCardViewModel(AudioApplicationSession app, Action<AudioCardViewModel> select, Action<AudioCardViewModel> changed)
        : base(app.ProcessName, select, changed) { Identity = app.Identity; ApplicationIdentity = app.ApplicationIdentity; }

    /*
     * Name: ApplyStatus
     * Input: Native source/capture status from the published routing graph.
     * Process: Computes RMS from measured source contributions after source gain/mute.
     * Output: Current connection health and routed activity; query failures stay visible.
     */
    internal void ApplyStatus(SourceTelemetry row)
    {
        ErrorText = row.Error ?? (row.Status is not null ? AudioPresentation.HResult(row.Status.HResult) : string.Empty);
        if (row.Status is not null) { StateText = row.Status.State == FilePlaybackState.Playing ? "Connected" : AudioPresentation.State(row.Status.State); }
        if (row.Signal is SourceSignal signal)
        {
            (double level, string text) = AudioPresentation.Meter(signal.Frames, signal.SumSquares,
                previousFrames, previousSquares, signal.LastPeak, !HasError);
            Level = level; LevelText = text;
            previousFrames = signal.Frames; previousSquares = signal.SumSquares;
        }
    }
}

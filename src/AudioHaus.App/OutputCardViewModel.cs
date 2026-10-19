using System;
using System.Windows.Media;
using AudioHaus.Interop;

namespace AudioHaus.App;

/*
 * Name: OutputCardViewModel
 * Input: Active endpoint identity and a stable session color.
 * Process: Displays routed-audio master controls without manual resource creation.
 * Output: Available device card with signal meter and expandable timing details.
 */
public sealed class OutputCardViewModel : AudioCardViewModel
{
    private ulong previousFrames;
    private double previousSquares;
    private string details = "Connect a source to start routing to this device.";
    private double delayMilliseconds;
    private bool driftEnabled = true;
    public string EndpointId { get; }
    public Brush Color { get; }
    public override string AutomationKey => EndpointId;
    public string Details { get => details; private set => SetProperty(ref details, value); }
    public double DelayMilliseconds
    {
        get => delayMilliseconds;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 2000) { OnPropertyChanged(nameof(DelayMilliseconds)); return; }
            if (SetProperty(ref delayMilliseconds, value)) { ControlChanged(); }
        }
    }
    public bool DriftEnabled { get => driftEnabled; set { if (SetProperty(ref driftEnabled, value)) { ControlChanged(); } } }

    /*
     * Name: OutputCardViewModel
     * Input: Active endpoint, session color, and UI callbacks.
     * Process: Retains its opaque endpoint ID and display name.
     * Output: Available output card with retained master and timing targets.
     */
    internal OutputCardViewModel(AudioEndpoint endpoint, Brush color, Action<AudioCardViewModel> select, Action<AudioCardViewModel> changed)
        : base(endpoint.DisplayName, select, changed) { EndpointId = endpoint.Id; Color = color; }

    /*
     * Name: ApplyStatus
     * Input: Output PCM/timing telemetry and isolated query error.
     * Process: Displays post-master RMS and expandable recovery details.
     * Output: Actual AudioHaus signal activity, rather than a system-wide device meter.
     */
    internal void ApplyStatus(OutputTelemetry row)
    {
        ErrorText = row.Error ?? (row.Status is not null ? AudioPresentation.HResult(row.Status.HResult, row.Status.State == 2) : string.Empty);
        if (row.Status is not null)
        {
            StateText = row.Status.State == 1 ? "Connected" : row.Status.State == 2 ? "Recovering" : "Error";
            (double level, string text) = AudioPresentation.Meter(row.Status.FramesSubmitted, row.Status.SumSquares,
                previousFrames, previousSquares, row.Status.LastPeak, row.Status.State == 1);
            Level = level; LevelText = text;
            previousFrames = row.Status.FramesSubmitted; previousSquares = row.Status.SumSquares;
        }
        if (row.Timing is not null) { Details = $"Device buffer {row.Timing.PaddingFrames}/{row.Timing.BufferCapacityFrames} frames · underruns {row.Timing.UnderrunCount} · recoveries {row.Timing.RecoveryCount}/{row.Timing.RecoveryAttempts}"; }
    }
}

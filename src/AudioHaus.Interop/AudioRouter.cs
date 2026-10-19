using System;
using System.IO;

namespace AudioHaus.Interop;

/*
 * Name: AudioRouter
 * Input: Local sources, active output endpoints, and source/output connections.
 * Process: Owns one file decoder or process capture per source, and one mixer
 *          per output. Route gain/mute do not change other connections.
 * Output: Disposable many-to-many routing and independent source transport.
 */
public sealed class AudioRouter : IDisposable
{
    private readonly SafeRoutingHandle handle;

    /*
     * Name: SetSourceLevel / SetOutputLevel
     * Input: Existing resource ID, finite normalized gain, and mute state.
     * Process: Sets a smoothed master target without changing other controls.
     * Output: Completion; argument/native failures throw with their diagnostic detail.
     */
    public void SetSourceLevel(uint id, double gain, bool muted)
    {
        CheckId(id); ValidateGain(gain);
        AudioHausResult result = NativeLevelMethods.SetSource(handle, id, gain, muted ? 1u : 0u, out uint error);
        CheckResult(result, error, "Set source volume");
    }
    public void SetOutputLevel(uint id, double gain, bool muted)
    {
        CheckId(id); ValidateGain(gain);
        AudioHausResult result = NativeLevelMethods.SetOutput(handle, id, gain, muted ? 1u : 0u, out uint error);
        CheckResult(result, error, "Set output volume");
    }
    /*
     * Name: GetSourceLevel / GetOutputLevel
     * Input: Existing source/output ID.
     * Process: Reads its acknowledged master target.
     * Output: Level; missing IDs and native failures throw.
     */
    public AudioLevel GetSourceLevel(uint id)
    {
        CheckId(id);
        AudioHausResult result = NativeLevelMethods.GetSource(handle, id, out AudioLevel level);
        CheckResult(result, level.HResult, "Read source volume"); return level;
    }
    public AudioLevel GetOutputLevel(uint id)
    {
        CheckId(id);
        AudioHausResult result = NativeLevelMethods.GetOutput(handle, id, out AudioLevel level);
        CheckResult(result, level.HResult, "Read output volume"); return level;
    }

    /*
     * Name: GetSourceSignal
     * Input: Existing source ID.
     * Process: Reads measured routed PCM after source gain/mute and before output gain.
     * Output: Signal snapshot; missing IDs and native failures throw.
     */
    public SourceSignal GetSourceSignal(uint id)
    {
        CheckId(id);
        AudioHausResult result = NativeLevelMethods.GetSignal(handle, id, out SourceSignal signal);
        CheckResult(result, signal.HResult, "Read source signal"); return signal;
    }

    /*
     * Name: AudioRouter
     * Input: An owning native router handle.
     * Process: Retains exclusive managed ownership.
     * Output: An empty managed routing controller.
     */
    private AudioRouter(SafeRoutingHandle handle) { this.handle = handle; }

    /*
     * Name: Create
     * Input: None.
     * Process: Checks ABI compatibility and initializes an empty graph.
     * Output: A router; incompatible API, loading, and native startup failures throw.
     */
    public static AudioRouter Create()
    {
        uint version = NativeMethods.GetApiVersion();
        if (version != 1) { throw new NotSupportedException($"Unsupported native routing ABI: {version}."); }
        SafeRoutingHandle owned = SafeRoutingHandle.Create();
        try { return new AudioRouter(owned); }
        catch { owned.Dispose(); throw; }
    }

    /*
     * Name: AddSource
     * Input: Existing local audio path.
     * Process: Opens one stopped decoder with a bounded PCM cache.
     * Output: Unique source ID; invalid paths and native errors throw.
     */
    public uint AddSource(string filePath)
    {
        CheckOpen();
        ValidateText(filePath, nameof(filePath));
        string path = Path.GetFullPath(filePath);
        if (!File.Exists(path)) { throw new FileNotFoundException("Audio file was not found.", path); }
        AudioHausResult result = NativeRoutingMethods.AddSource(handle, path, out uint id, out uint hresult);
        CheckResult(result, hresult, "Add source");
        return id;
    }

    /*
     * Name: AddProcessSource
     * Input: Live application PID and exact virtual playback endpoint ID.
     * Process: Captures the process tree and checks original streams remain parked.
     * Output: Stopped monitoring source ID; invalid PID, parking, or capture errors throw.
     */
    public uint AddProcessSource(uint processId, string parkingEndpointId)
    {
        CheckOpen();
        if (processId == 0 || processId > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(processId)); }
        ValidateText(parkingEndpointId, nameof(parkingEndpointId));
        AudioHausResult result = NativeCaptureMethods.AddProcess(handle, processId, parkingEndpointId, out uint id, out uint hresult);
        CheckResult(result, hresult, "Capture process (park original playback on the selected virtual endpoint first)");
        return id;
    }

    /*
     * Name: GetCaptureStatus
     * Input: A process source ID.
     * Process: Queries live packet counters and isolation health.
     * Output: Immutable capture status; file IDs and native failures throw.
     */
    public ProcessCaptureStatus GetCaptureStatus(uint sourceId)
    {
        CheckId(sourceId);
        AudioHausResult result = NativeCaptureMethods.GetCapture(handle, sourceId, out ProcessCaptureInfo info);
        CheckResult(result, info.HResult, "Query process capture");
        return new ProcessCaptureStatus(info);
    }

    /*
     * Name: AddOutput
     * Input: Active Windows render endpoint ID.
     * Process: Starts its single mixer stream, initially silent.
     * Output: Unique output ID; duplicate or unusable endpoints throw.
     */
    public uint AddOutput(string endpointId)
    {
        CheckOpen();
        ValidateText(endpointId, nameof(endpointId));
        AudioHausResult result = NativeRoutingMethods.AddOutput(handle, endpointId, out uint id, out uint hresult);
        CheckResult(result, hresult, "Add output");
        return id;
    }

    /*
     * Name: SetOutputDelay
     * Input: Live output ID and additional delay from zero through two seconds.
     * Process: Reprimes only the output's readers; delay is rounded to a 48 kHz frame.
     * Output: Updated delay; invalid ranges/IDs or native failures throw.
     */
    public void SetOutputDelay(uint outputId, TimeSpan delay)
    {
        CheckId(outputId);
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(2)) { throw new ArgumentOutOfRangeException(nameof(delay)); }
        uint frames = checked((uint)Math.Round(delay.TotalSeconds * 48000, MidpointRounding.AwayFromZero));
        AudioHausResult result = NativeTimingMethods.SetDelay(handle, outputId, frames, out uint hresult);
        CheckResult(result, hresult, "Set output delay");
    }
    /*
     * Name: SetDriftCorrection
     * Input: Live output ID and correction enable flag.
     * Process: Enables/disables smooth per-route correction for this destination.
     * Output: Updated policy; invalid IDs/native errors throw.
     */
    public void SetDriftCorrection(uint outputId, bool enabled)
    {
        CheckId(outputId);
        AudioHausResult result = NativeTimingMethods.SetDrift(handle, outputId, enabled ? 1u : 0u, out uint hresult);
        CheckResult(result, hresult, "Set drift correction");
    }
    /*
     * Name: GetOutputTiming
     * Input: Live output ID.
     * Process: Reads buffers, clock estimate, configured delay, and recovery history.
     * Output: Immutable timing; failed/recovering resources remain queryable.
     */
    public OutputTimingStatus GetOutputTiming(uint outputId)
    {
        CheckId(outputId);
        AudioHausResult result = NativeTimingMethods.GetOutput(handle, outputId, out OutputTimingInfo info);
        CheckResult(result, info.HResult, "Query output timing");
        return new OutputTimingStatus(info);
    }
    /*
     * Name: GetRouteTiming
     * Input: Connected source/output IDs.
     * Process: Reads fractional cursor, correction, buffer depth, and underrun counters.
     * Output: Immutable timing; missing routes/native failures throw.
     */
    public RouteTimingStatus GetRouteTiming(uint sourceId, uint outputId)
    {
        CheckRouteIds(sourceId, outputId);
        AudioHausResult result = NativeTimingMethods.GetRoute(handle, sourceId, outputId, out RouteTimingInfo info);
        CheckResult(result, info.HResult, "Query route timing");
        return new RouteTimingStatus(info);
    }
    /*
     * Name: GetCaptureTiming
     * Input: Process source ID.
     * Process: Reads retry history, desired monitor state, and history capacity.
     * Output: Immutable timing; file IDs and native query errors throw.
     */
    public CaptureTimingStatus GetCaptureTiming(uint sourceId)
    {
        CheckId(sourceId);
        AudioHausResult result = NativeTimingMethods.GetCapture(handle, sourceId, out CaptureTimingInfo info);
        CheckResult(result, info.HResult, "Query capture timing");
        return new CaptureTimingStatus(info);
    }
    /*
     * Name: RetryOutput
     * Input: Existing output ID.
     * Process: Requests background reopen of the same device while retaining routes/settings.
     * Output: Returns after acknowledgment; query status/timing for recovery completion.
     */
    public void RetryOutput(uint outputId)
    {
        CheckId(outputId);
        AudioHausResult result = NativeTimingMethods.RetryOutput(handle, outputId, out uint hresult);
        CheckResult(result, hresult, "Retry output");
    }
    /*
     * Name: RetryCapture
     * Input: Existing process source ID with its original root still alive.
     * Process: Requests background reopening after setup is restored.
     * Output: Returns after acknowledgment; query status/timing for completion.
     */
    public void RetryCapture(uint sourceId)
    {
        CheckId(sourceId);
        AudioHausResult result = NativeTimingMethods.RetryCapture(handle, sourceId, out uint hresult);
        CheckResult(result, hresult, "Retry capture");
    }

    /*
     * Name: RemoveSource
     * Input: Live source ID.
     * Process: Removes its decoder and all connected routes.
     * Output: Remaining graph continues; invalid IDs or native errors throw.
     */
    public void RemoveSource(uint sourceId)
    {
        CheckId(sourceId);
        AudioHausResult result = NativeRoutingMethods.RemoveSource(handle, sourceId, out uint hresult);
        CheckResult(result, hresult, "Remove source");
    }

    /*
     * Name: RemoveOutput
     * Input: Live output ID.
     * Process: Stops its mixer and removes its routes.
     * Output: Other outputs continue; invalid IDs or native errors throw.
     */
    public void RemoveOutput(uint outputId)
    {
        CheckId(outputId);
        AudioHausResult result = NativeRoutingMethods.RemoveOutput(handle, outputId, out uint hresult);
        CheckResult(result, hresult, "Remove output");
    }

    /*
     * Name: Connect
     * Input: Live IDs, finite gain 0..1, and initial route mute.
     * Process: Creates or updates one source/output connection.
     * Output: A live route; changes apply to future mix blocks. Errors throw.
     */
    public void Connect(uint sourceId, uint outputId, double gain = 0.15, bool muted = false)
    {
        CheckRouteIds(sourceId, outputId);
        ValidateGain(gain);
        AudioHausResult result = NativeRoutingMethods.Connect(handle, sourceId, outputId, gain, muted ? 1u : 0u, out uint hresult);
        CheckResult(result, hresult, "Connect route");
    }

    /*
     * Name: Disconnect
     * Input: Existing route's IDs.
     * Process: Removes only that connection without stopping the source.
     * Output: Other routes continue; missing routes throw.
     */
    public void Disconnect(uint sourceId, uint outputId)
    {
        CheckRouteIds(sourceId, outputId);
        AudioHausResult result = NativeRoutingMethods.Disconnect(handle, sourceId, outputId, out uint hresult);
        CheckResult(result, hresult, "Disconnect route");
    }

    /*
     * Name: SetRouteGain
     * Input: Existing route and finite gain 0..1.
     * Process: Adjusts only this connection's mixing scalar.
     * Output: Updated gain; mute and other routes remain unchanged. Errors throw.
     */
    public void SetRouteGain(uint sourceId, uint outputId, double gain)
    {
        CheckRouteIds(sourceId, outputId);
        ValidateGain(gain);
        AudioHausResult result = NativeRoutingMethods.SetGain(handle, sourceId, outputId, gain, out uint hresult);
        CheckResult(result, hresult, "Set route gain");
    }

    /*
     * Name: SetRouteMute
     * Input: Existing route and mute flag.
     * Process: Silences the connection while its source and reader keep progressing.
     * Output: Updated mute; gain and other routes remain unchanged. Errors throw.
     */
    public void SetRouteMute(uint sourceId, uint outputId, bool muted)
    {
        CheckRouteIds(sourceId, outputId);
        AudioHausResult result = NativeRoutingMethods.SetMute(handle, sourceId, outputId, muted ? 1u : 0u, out uint hresult);
        CheckResult(result, hresult, "Set route mute");
    }

    /*
     * Name: Play
     * Input: Live source ID.
     * Process: Starts/resumes its common timeline; naturally ended sources rewind.
     * Output: All its routes receive current audio. Errors throw.
     */
    public void Play(uint sourceId) => SourceCommand(sourceId, FilePlayerCommand.Play);

    /*
     * Name: Pause
     * Input: Live source ID.
     * Process: Freezes its timeline and omits it from future mixer blocks.
     * Output: Other sources remain independent. Errors throw.
     */
    public void Pause(uint sourceId) => SourceCommand(sourceId, FilePlayerCommand.Pause);

    /*
     * Name: Stop
     * Input: Live source ID.
     * Process: Rewinds it and clears its cache/readers without resetting output clients.
     * Output: A stopped source at zero; other sources continue. Errors throw.
     */
    public void Stop(uint sourceId) => SourceCommand(sourceId, FilePlayerCommand.Stop);

    /*
     * Name: Seek
     * Input: Live source ID and nonnegative media position.
     * Process: Flushes its cache and readers, clamping to file duration.
     * Output: Updated source position with playing/paused state retained. Errors throw.
     */
    public void Seek(uint sourceId, TimeSpan position)
    {
        CheckId(sourceId);
        if (position < TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(position)); }
        SourceCommand(sourceId, FilePlayerCommand.Seek, checked((ulong)position.Ticks));
    }

    /*
     * Name: GetSourceStatus
     * Input: Live source ID.
     * Process: Reads the shared media timeline and cache activity.
     * Output: Immutable status, including asynchronous failures. Query errors throw.
     */
    public RoutingSourceStatus GetSourceStatus(uint sourceId)
    {
        CheckId(sourceId);
        AudioHausResult result = NativeRoutingMethods.GetSource(handle, sourceId, out RoutingSourceInfo info);
        CheckResult(result, info.HResult, "Query source");
        return new RoutingSourceStatus(info);
    }

    /*
     * Name: GetOutputStatus
     * Input: Live output ID.
     * Process: Reads stream and pre-device PCM measurements.
     * Output: Immutable status, including asynchronous failures. Query errors throw.
     */
    public RoutingOutputStatus GetOutputStatus(uint outputId)
    {
        CheckId(outputId);
        AudioHausResult result = NativeRoutingMethods.GetOutput(handle, outputId, out RoutingOutputInfo info);
        CheckResult(result, info.HResult, "Query output");
        return new RoutingOutputStatus(info);
    }

    /*
     * Name: GetRouteStatus
     * Input: Existing route's IDs.
     * Process: Reads independent controls and reader counters.
     * Output: Immutable status; missing routes or query errors throw.
     */
    public RoutingRouteStatus GetRouteStatus(uint sourceId, uint outputId)
    {
        CheckRouteIds(sourceId, outputId);
        AudioHausResult result = NativeRoutingMethods.GetRoute(handle, sourceId, outputId, out RoutingRouteInfo info);
        CheckResult(result, null, "Query route");
        return new RoutingRouteStatus(info);
    }

    /*
     * Name: SourceCommand
     * Input: Source, named transport command, and optional seek ticks.
     * Process: Sends one acknowledged operation to the graph worker.
     * Output: Updated transport or an exception with native details.
     */
    private void SourceCommand(uint sourceId, FilePlayerCommand command, ulong ticks = 0)
    {
        CheckId(sourceId);
        AudioHausResult result = NativeRoutingMethods.SourceCommand(handle, sourceId, (uint)command, ticks, out uint hresult);
        CheckResult(result, hresult, "Control source");
    }

    /*
     * Name: CheckResult
     * Input: API status, optional HRESULT, and operation name.
     * Process: Converts native failures to managed exceptions.
     * Output: Returns on success; allocation or operation failures throw.
     */
    internal static void CheckResult(AudioHausResult result, uint? hresult, string operation)
    {
        if (result == AudioHausResult.Success) { return; }
        if (result == AudioHausResult.OutOfMemory) { throw new OutOfMemoryException($"{operation} ran out of native memory."); }
        string detail = hresult.HasValue ? $", HRESULT 0x{hresult.Value:X8}" : string.Empty;
        throw new InvalidOperationException($"{operation} failed: {result}{detail}.");
    }

    /*
     * Name: CheckOpen
     * Input: Current handle lifetime.
     * Process: Rejects disposed ownership before native dispatch.
     * Output: Returns for a live router or throws ObjectDisposedException.
     */
    private void CheckOpen() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);

    /*
     * Name: CheckId
     * Input: Resource ID.
     * Process: Validates ownership and rejects the reserved zero ID.
     * Output: Returns or throws a disposed/argument exception.
     */
    private void CheckId(uint id)
    {
        CheckOpen();
        if (id == 0) { throw new ArgumentOutOfRangeException(nameof(id), "Resource IDs must be nonzero."); }
    }

    /*
     * Name: CheckRouteIds
     * Input: Source and output IDs.
     * Process: Validates their nonzero representation before native lookup.
     * Output: Returns or throws a disposed/argument exception.
     */
    private void CheckRouteIds(uint sourceId, uint outputId) { CheckId(sourceId); CheckId(outputId); }

    /*
     * Name: ValidateGain
     * Input: Proposed route scalar.
     * Process: Rejects nonfinite values and values outside 0..1.
     * Output: Returns or throws ArgumentOutOfRangeException.
     */
    private static void ValidateGain(double gain)
    {
        if (!double.IsFinite(gain) || gain < 0 || gain > 1) { throw new ArgumentOutOfRangeException(nameof(gain)); }
    }

    /*
     * Name: ValidateText
     * Input: Path or endpoint text and parameter name.
     * Process: Rejects null, empty, and embedded-null strings.
     * Output: Returns or throws an argument exception.
     */
    private static void ValidateText(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameter);
        if (value.Contains('\0')) { throw new ArgumentException("Text cannot contain null characters.", parameter); }
    }

    /*
     * Name: Dispose
     * Input: None.
     * Process: Releases the graph after in-flight native calls finish.
     * Output: Stopped outputs and released ownership; repeated disposal is harmless.
     */
    public void Dispose() => handle.Dispose();
}

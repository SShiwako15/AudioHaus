namespace AudioHaus.Interop;

/*
 * Name: TonePlaybackResult
 * Input: Endpoint ID and its native playback report.
 * Process: Stores an immutable result after native playback has stopped.
 * Output: Frame counts, QPC stream bounds, HRESULT bits, and completion status;
 *         these values describe software streams, not acoustic timing.
 */
public sealed class TonePlaybackResult
{
    public string EndpointId { get; }
    public uint SampleRate { get; }
    public uint ChannelCount { get; }
    public ulong FramesSubmitted { get; }
    public ulong StartQpc { get; }
    public ulong StopQpc { get; }
    public uint WasapiResult { get; }
    public bool Completed { get; }

    /*
     * Name: TonePlaybackResult
     * Input: Endpoint ID and native report.
     * Process: Copies the native data without retaining unmanaged resources.
     * Output: A managed result for the requested endpoint.
     */
    internal TonePlaybackResult(string endpointId, AudioHausToneOutputInfo info)
    {
        EndpointId = endpointId;
        SampleRate = info.SampleRate;
        ChannelCount = info.ChannelCount;
        FramesSubmitted = info.FramesSubmitted;
        StartQpc = info.StartQpc;
        StopQpc = info.StopQpc;
        WasapiResult = info.HResult;
        Completed = info.Completed != 0;
    }
}

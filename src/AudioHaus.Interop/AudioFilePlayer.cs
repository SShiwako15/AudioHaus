using System;
using System.IO;

namespace AudioHaus.Interop;

/*
 * Name: AudioFilePlayer
 * Input: A local audio file and one Windows playback endpoint.
 * Process: Owns independent native decoding, transport controls, and output playback.
 * Output: A disposable player for WAV, MP3, and supported M4A audio.
 */
public sealed class AudioFilePlayer : IDisposable
{
    private const uint SupportedApiVersion = 1;
    private readonly SafeFilePlayerHandle handle;

    public string FilePath { get; }
    public string EndpointId { get; }

    /*
     * Name: AudioFilePlayer
     * Input: Validated path, endpoint ID, and an owning native handle.
     * Process: Retains the ownership created by Open.
     * Output: A managed controller for the stopped player.
     */
    private AudioFilePlayer(string filePath, string endpointId, SafeFilePlayerHandle handle)
    {
        FilePath = filePath;
        EndpointId = endpointId;
        this.handle = handle;
    }

    /*
     * Name: Open
     * Input: Local path, endpoint ID, and finite gain from 0 through 1.
     * Process: Validates arguments and ABI, then initializes the stopped native player.
     * Output: Independent player; invalid arguments, absent files, incompatible ABI,
     *         decoding errors, and device failures throw. No playback starts here.
     */
    public static AudioFilePlayer Open(string filePath, string endpointId, double gain = 0.15)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        if (filePath.Contains('\0') || endpointId.Contains('\0'))
        {
            throw new ArgumentException("Paths and endpoint IDs cannot contain null characters.");
        }
        if (!double.IsFinite(gain) || gain < 0.0 || gain > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(gain), "Gain must be between 0 and 1.");
        }

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }
        uint version = NativeMethods.GetApiVersion();
        if (version != SupportedApiVersion)
        {
            throw new NotSupportedException($"Native API version {version} is unsupported; expected {SupportedApiVersion}.");
        }

        SafeFilePlayerHandle native = SafeFilePlayerHandle.Open(fullPath, endpointId, gain);
        try
        {
            return new AudioFilePlayer(fullPath, endpointId, native);
        }
        catch
        {
            native.Dispose();
            throw;
        }
    }

    /*
     * Name: Play
     * Input: None.
     * Process: Starts or resumes this player; end-of-file restarts from the beginning.
     * Output: Playback progresses independently; native/device failures throw.
     */
    public void Play() => Execute(FilePlayerCommand.Play);

    /*
     * Name: Pause
     * Input: None.
     * Process: Stops rendering while retaining position and queued audio.
     * Output: A paused player without changing any other player's state.
     */
    public void Pause() => Execute(FilePlayerCommand.Pause);

    /*
     * Name: Stop
     * Input: None.
     * Process: Stops rendering, clears buffered audio, and rewinds the decoder.
     * Output: A stopped player at position zero; native failures throw.
     */
    public void Stop() => Execute(FilePlayerCommand.Stop);

    /*
     * Name: Seek
     * Input: Nonnegative media position.
     * Process: Flushes stale audio and seeks, clamping at the known duration.
     * Output: New position with playing/paused state preserved; negative input throws.
     */
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Seek position cannot be negative.");
        }
        Execute(FilePlayerCommand.Seek, checked((ulong)position.Ticks));
    }

    /*
     * Name: GetStatus
     * Input: None.
     * Process: Queries current transport state and media position from the worker.
     * Output: An immutable snapshot; closed handles or failed queries throw.
     */
    public FilePlaybackStatus GetStatus()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        AudioHausResult result = NativeMethods.GetFilePlayerStatus(handle, out AudioHausFilePlayerInfo info);
        if (result != AudioHausResult.Success)
        {
            throw new InvalidOperationException($"Could not query playback: {result}, HRESULT 0x{info.HResult:X8}.");
        }
        return new FilePlaybackStatus(info);
    }

    /*
     * Name: Execute
     * Input: Native command and optional seek ticks.
     * Process: Holds native ownership while waiting for command acknowledgment.
     * Output: Updated transport or an exception containing the failed HRESULT.
     */
    private void Execute(FilePlayerCommand command, ulong position100Ns = 0)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        AudioHausResult result = NativeMethods.FilePlayerCommand(handle, (uint)command, position100Ns, out uint hresult);
        if (result != AudioHausResult.Success)
        {
            throw new InvalidOperationException($"File playback command failed: {result}, HRESULT 0x{hresult:X8}.");
        }
    }

    /*
     * Name: Dispose
     * Input: None.
     * Process: Stops and releases this player's worker after outstanding calls finish.
     * Output: Released ownership; subsequent controls throw ObjectDisposedException.
     */
    public void Dispose() => handle.Dispose();
}

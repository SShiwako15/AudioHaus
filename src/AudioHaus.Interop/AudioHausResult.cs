namespace AudioHaus.Interop;

/*
 * Name: AudioHausResult
 * Input: Status returned by the native API.
 * Process: Matches the native AudioHausResult signed 32-bit values.
 * Output: A named success or failure status for managed interop code.
 */
internal enum AudioHausResult : int
{
    Success = 0,
    InvalidArgument = 1,
    BufferTooSmall = 2,
    DiscoveryFailed = 3,
    OutOfMemory = 4,
    InternalError = 5,
    PlaybackFailed = 6
}

namespace AudioHaus.Interop;

/*
 * Name: FilePlayerCommand
 * Input: A managed transport operation.
 * Process: Matches the native file-player command constants.
 * Output: An explicit command value for interop dispatch.
 */
internal enum FilePlayerCommand : uint
{
    Play = 0,
    Pause = 1,
    Stop = 2,
    Seek = 3
}

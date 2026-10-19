namespace AudioHaus.Interop;

/*
 * Name: FilePlaybackState
 * Input: Native transport state.
 * Process: Names the file player's independent lifecycle states.
 * Output: A state for controls and status displays.
 */
public enum FilePlaybackState : uint
{
    Stopped = 0,
    Playing = 1,
    Paused = 2,
    Ended = 3,
    Failed = 4,
    Recovering = 5
}

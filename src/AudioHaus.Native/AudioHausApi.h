#pragma once

#include <stdint.h>
#include <wchar.h>

#if defined(AUDIOHAUS_NATIVE_EXPORTS)
#define AUDIOHAUS_API __declspec(dllexport)
#else
#define AUDIOHAUS_API __declspec(dllimport)
#endif

#define AUDIOHAUS_CALL __cdecl

#ifdef __cplusplus
extern "C" {
#endif

typedef int32_t AudioHausResult;

enum {
    kAudioHausSuccess = 0,
    kAudioHausInvalidArgument = 1,
    kAudioHausBufferTooSmall = 2,
    kAudioHausDiscoveryFailed = 3,
    kAudioHausOutOfMemory = 4,
    kAudioHausInternalError = 5,
    kAudioHausPlaybackFailed = 6
};

enum {
    kAudioHausEndpointUnknown = 0,
    kAudioHausEndpointActive = 1,
    kAudioHausEndpointDisabled = 2,
    kAudioHausEndpointNotPresent = 3,
    kAudioHausEndpointUnplugged = 4
};

/*
 * Name: AudioHausEndpointSnapshot
 * Input: Created by audiohaus_create_endpoint_snapshot.
 * Process: Represents an immutable endpoint list owned by the native library.
 * Output: Opaque handle; release once, after all readers finish. Do not reuse it.
 */
typedef struct AudioHausEndpointSnapshotOpaque* AudioHausEndpointSnapshot;

/*
 * Name: AudioHausEndpointInfo
 * Input: Populated by audiohaus_get_endpoint.
 * Process: Describes an endpoint and required UTF-16 buffer sizes in wchar_t units.
 * Output: Sizes include null terminators; state uses kAudioHausEndpoint constants;
 *         is_default_multimedia is 0 or 1. Layout uses default Windows packing.
 */
typedef struct AudioHausEndpointInfo {
    uint32_t id_capacity_required;
    uint32_t display_name_capacity_required;
    uint32_t state;
    uint32_t is_default_multimedia;
} AudioHausEndpointInfo;

/*
 * Name: AudioHausToneOutputInfo
 * Input: Populated by audiohaus_play_test_tone for each requested output.
 * Process: Records submitted PCM frames and conservative running-stream QPC bounds.
 * Output: A 40-byte report; hresult holds Windows HRESULT bits. Completed is 0 or 1.
 *         Timestamps measure software stream lifetime, not acoustic arrival time.
 */
typedef struct AudioHausToneOutputInfo {
    uint32_t sample_rate;
    uint32_t channel_count;
    uint64_t frames_submitted;
    uint64_t start_qpc;
    uint64_t stop_qpc;
    uint32_t hresult;
    uint32_t completed;
} AudioHausToneOutputInfo;

/*
 * Name: AudioHausFilePlayer
 * Input: Created by audiohaus_open_file_player.
 * Process: Owns an independent decoder, output stream, and control worker.
 * Output: Opaque handle; close after its last caller finishes.
 */
typedef struct AudioHausFilePlayerOpaque* AudioHausFilePlayer;

enum {
    kAudioHausFileStopped = 0,
    kAudioHausFilePlaying = 1,
    kAudioHausFilePaused = 2,
    kAudioHausFileEnded = 3,
    kAudioHausFileFailed = 4
};

enum {
    kAudioHausFilePlay = 0,
    kAudioHausFilePause = 1,
    kAudioHausFileStop = 2,
    kAudioHausFileSeek = 3
};

/*
 * Name: AudioHausFilePlayerInfo
 * Input: Populated by audiohaus_get_file_player_status.
 * Process: Describes current transport state and independent playback progress.
 * Output: A 56-byte snapshot; position/duration use 100 ns units, frames_submitted
 *         is cumulative, and QPC values identify the most recent stream boundaries.
 */
typedef struct AudioHausFilePlayerInfo {
    uint32_t state;
    uint32_t hresult;
    uint32_t sample_rate;
    uint32_t channel_count;
    uint64_t position_100ns;
    uint64_t duration_100ns;
    uint64_t frames_submitted;
    uint64_t start_qpc;
    uint64_t stop_qpc;
} AudioHausFilePlayerInfo;

/*
 * Name: audiohaus_get_api_version
 * Input: None.
 * Process: Identifies the supported ABI contract.
 * Output: Version 1. No exceptions cross the API boundary.
 */
AUDIOHAUS_API uint32_t AUDIOHAUS_CALL audiohaus_get_api_version(void);

/*
 * Name: audiohaus_create_endpoint_snapshot
 * Input: Non-null snapshot and endpoint_count output pointers.
 * Process: Captures playback endpoints, their states, and the multimedia default.
 * Output: Status code; success transfers a handle to the caller, even for an empty
 *         list. On failure, valid output pointers receive null and zero. No throws.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_create_endpoint_snapshot(
    AudioHausEndpointSnapshot* snapshot,
    uint32_t* endpoint_count);

/*
 * Name: audiohaus_get_endpoint
 * Input: Live snapshot, zero-based index, non-null info, and separate writable
 *        UTF-16 buffers with capacities. A null buffer requires zero capacity.
 * Process: Fills info; copies both strings only when both buffers are sufficient.
 * Output: Status code. Null/zero buffers query sizes and return BufferTooSmall.
 *         Insufficient buffers remain untouched; invalid arguments zero info when
 *         provided. Successful strings are null-terminated. No throws.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_get_endpoint(
    AudioHausEndpointSnapshot snapshot,
    uint32_t index,
    AudioHausEndpointInfo* info,
    wchar_t* id_buffer,
    uint32_t id_capacity,
    wchar_t* display_name_buffer,
    uint32_t display_name_capacity);

/*
 * Name: audiohaus_destroy_endpoint_snapshot
 * Input: Live snapshot whose readers have finished, or null.
 * Process: Releases the native snapshot; null is a no-op.
 * Output: None. The handle becomes invalid. No exceptions cross the API boundary.
 */
AUDIOHAUS_API void AUDIOHAUS_CALL audiohaus_destroy_endpoint_snapshot(
    AudioHausEndpointSnapshot snapshot);

/*
 * Name: audiohaus_play_test_tone
 * Input: One or two distinct endpoint IDs; frequency 20..20000 Hz, peak amplitude
 *        0..0.25, duration 1..60000 ms, and space for endpoint_count output reports.
 * Process: Prepares all shared-mode streams before starting any; renders the tone
 *          concurrently and drains their buffers. Blocks until completion/failure.
 * Output: Status and per-output reports; PlaybackFailed means one or more streams
 *         failed. Success proves stream completion, not audible Bluetooth playback.
 *         BufferTooSmall means report capacity is insufficient. No throws.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_play_test_tone(
    const wchar_t* const* endpoint_ids,
    uint32_t endpoint_count,
    double frequency_hz,
    double amplitude,
    uint32_t duration_ms,
    AudioHausToneOutputInfo* results,
    uint32_t results_capacity);

/*
 * Name: audiohaus_open_file_player
 * Input: Local file path, active render endpoint ID, gain 0..1, and output pointers.
 * Process: Initializes Media Foundation decoding and a shared WASAPI stream stopped.
 * Output: Status, owning player handle, and HRESULT bits. Failure clears the handle.
 *         File data is streamed; no playback starts until the play command.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_open_file_player(
    const wchar_t* file_path,
    const wchar_t* endpoint_id,
    double gain,
    AudioHausFilePlayer* player,
    uint32_t* error_hresult);

/*
 * Name: audiohaus_file_player_command
 * Input: Live player, play/pause/stop/seek command, seek position in 100 ns units,
 *        and a non-null HRESULT output. Other commands ignore the seek position.
 * Process: Applies one command on this player's worker and waits for acknowledgment.
 * Output: Status and HRESULT bits. Stop resets position; seek clamps to duration
 *         and preserves playing/paused state. Play after end-of-file restarts.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_file_player_command(
    AudioHausFilePlayer player,
    uint32_t command,
    uint64_t position_100ns,
    uint32_t* error_hresult);

/*
 * Name: audiohaus_get_file_player_status
 * Input: Live player and a non-null output snapshot.
 * Process: Queries the worker for transport and render progress.
 * Output: Status and snapshot, including failed state and its underlying HRESULT.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_get_file_player_status(
    AudioHausFilePlayer player,
    AudioHausFilePlayerInfo* status);

/*
 * Name: audiohaus_close_file_player
 * Input: Live player with no remaining callers, or null.
 * Process: Stops the stream, joins the worker, and releases native resources.
 * Output: None; null is a no-op and the released handle must not be reused.
 */
AUDIOHAUS_API void AUDIOHAUS_CALL audiohaus_close_file_player(AudioHausFilePlayer player);

#ifdef __cplusplus
}
#endif

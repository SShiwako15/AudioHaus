#pragma once

#include "AudioHausApi.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Name: AudioHausRouter
 * Input: Created by audiohaus_create_router.
 * Process: Owns decoded source timelines, output mixers, and their routes.
 * Output: Opaque ownership; destroy after its callers finish.
 */
typedef struct AudioHausRouterOpaque* AudioHausRouter;

/*
 * Name: AudioHausLevelInfo
 * Input: Source or output master target.
 * Process: Adds level telemetry without modifying existing snapshot layouts.
 * Output: Normalized gain, mute 0/1, and HRESULT in a 16-byte structure.
 */
typedef struct AudioHausLevelInfo { double gain; uint32_t muted, hresult; } AudioHausLevelInfo;

/*
 * Name: audiohaus_router_set_source_level / audiohaus_router_set_output_level
 * Input: Router, resource ID, finite gain 0..1, mute 0/1, and HRESULT output.
 * Process: Acknowledges a smoothed master target on the native owner.
 * Output: API status and HRESULT; invalid arguments do not change the level.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_source_level(AudioHausRouter router, uint32_t id, double gain, uint32_t muted, uint32_t* error_hresult);
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_output_level(AudioHausRouter router, uint32_t id, double gain, uint32_t muted, uint32_t* error_hresult);
/*
 * Name: audiohaus_router_get_source_level / audiohaus_router_get_output_level
 * Input: Router, resource ID, and level destination.
 * Process: Copies the acknowledged target independently of route controls.
 * Output: API status and level/HRESULT; existing layouts remain unchanged.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_source_level(AudioHausRouter router, uint32_t id, AudioHausLevelInfo* info);
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_output_level(AudioHausRouter router, uint32_t id, AudioHausLevelInfo* info);

/*
 * Name: AudioHausSourceSignalInfo
 * Input: Source contributions across its route readers.
 * Process: Preserves existing telemetry layouts with an additive signal snapshot.
 * Output: A 32-byte value; frames/energy include every destination before output gain.
 */
typedef struct AudioHausSourceSignalInfo {
    uint64_t frames;
    double sum_squares, last_peak;
    uint32_t hresult, reserved;
} AudioHausSourceSignalInfo;

/*
 * Name: audiohaus_router_get_source_signal
 * Input: Router, source ID, and signal destination.
 * Process: Copies measured source PCM after source gain/mute and before output mixing.
 * Output: API status and signal/HRESULT; invalid IDs fail without changing playback.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_source_signal(AudioHausRouter router, uint32_t id, AudioHausSourceSignalInfo* info);

/*
 * Name: AudioHausRoutingSourceInfo
 * Input: A source snapshot from the routing worker.
 * Process: Describes normalized PCM, media time, and bounded decode-cache activity.
 * Output: A 56-byte value; transport states match the file-player states.
 */
typedef struct AudioHausRoutingSourceInfo {
    uint32_t state, hresult, sample_rate, channel_count;
    uint64_t position_100ns, duration_100ns, frames_decoded, cached_frames;
    uint32_t route_count, reserved;
} AudioHausRoutingSourceInfo;

/*
 * Name: AudioHausRoutingOutputInfo
 * Input: A mixer snapshot for one endpoint.
 * Process: Records submitted PCM and cumulative pre-device signal measurements.
 * Output: An 80-byte value; states are Running 1, Recovering 2, and Failed 4. Sum squares
 *         measures both channels after clipping; peak describes the latest block.
 */
typedef struct AudioHausRoutingOutputInfo {
    uint32_t state, hresult, sample_rate, channel_count;
    uint64_t frames_submitted, non_silent_frames, clipped_samples;
    double sum_squares, last_peak;
    uint64_t start_qpc, stop_qpc;
    uint32_t route_count, reserved;
} AudioHausRoutingOutputInfo;

/*
 * Name: AudioHausRoutingRouteInfo
 * Input: A connection between a source and output.
 * Process: Describes independent gain/mute and its source-cache reader.
 * Output: A 48-byte value; muted is 0 or 1; rebases counts reader realignments.
 */
typedef struct AudioHausRoutingRouteInfo {
    uint32_t source_id, output_id, muted, reserved;
    double gain;
    uint64_t frames_read, cursor_frame, rebases;
} AudioHausRoutingRouteInfo;

/*
 * Name: AudioHausProcessCaptureInfo
 * Input: A process-loopback source snapshot.
 * Process: Exposes live capture metrics without changing the file-source layout.
 * Output: 80 bytes; state uses file-player values; packet QPC uses 100 ns units.
 */
typedef struct AudioHausProcessCaptureInfo {
    uint32_t process_id, state, hresult, reserved;
    uint64_t frames_captured, non_silent_frames, discontinuities;
    uint64_t frames_dropped, buffered_frames, last_packet_qpc_100ns;
    double sum_squares, last_peak;
} AudioHausProcessCaptureInfo;

/*
 * Name: audiohaus_router_add_process
 * Input: Router, existing application PID, virtual render endpoint, and ID/HRESULT outputs.
 * Process: Opens process-tree loopback and checks that active original streams stay parked.
 * Output: Status and stopped monitor ID; does not change app/Windows output settings.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_add_process(AudioHausRouter router, uint32_t process_id, const wchar_t* parking_id, uint32_t* source_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_get_capture
 * Input: Process source ID and non-null status output.
 * Process: Queries capture health and bounded-buffer metrics.
 * Output: Status and 80-byte snapshot; file sources are rejected.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_capture(AudioHausRouter router, uint32_t source_id, AudioHausProcessCaptureInfo* info);

/*
 * Name: audiohaus_get_endpoint_processes
 * Input: Active render endpoint, PID buffer/capacity, required count, and HRESULT output.
 * Process: Enumerates distinct active session PIDs without changing volume or routing.
 * Output: Status; BufferTooSmall reports required capacity without partial writes.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_get_endpoint_processes(const wchar_t* endpoint_id, uint32_t* process_ids, uint32_t capacity, uint32_t* required, uint32_t* error_hresult);

/*
 * Name: audiohaus_create_router
 * Input: Non-null router and HRESULT output pointers.
 * Process: Initializes a routing worker without sources or outputs.
 * Output: Status and an owning handle, or null on failure. No exceptions escape.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_create_router(AudioHausRouter* router, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_add_source
 * Input: Live router, local path, and non-null ID/HRESULT outputs.
 * Process: Opens one stopped decoder; capacity is eight sources.
 * Output: Status and a unique nonzero ID. Failure leaves the existing graph intact.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_add_source(AudioHausRouter router, const wchar_t* path, uint32_t* source_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_add_output
 * Input: Live router, active endpoint ID, and non-null ID/HRESULT outputs.
 * Process: Starts one shared mixer stream; capacity is eight distinct endpoints.
 * Output: Status and unique nonzero ID. Unconnected output streams render silence.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_add_output(AudioHausRouter router, const wchar_t* endpoint_id, uint32_t* output_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_remove_source
 * Input: Live router, source ID, and HRESULT output.
 * Process: Removes the decoder and every route referencing it.
 * Output: Status; other sources and outputs remain live.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_remove_source(AudioHausRouter router, uint32_t source_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_remove_output
 * Input: Live router, output ID, and HRESULT output.
 * Process: Stops the endpoint mixer and removes every route referencing it.
 * Output: Status; other outputs and source transports remain live.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_remove_output(AudioHausRouter router, uint32_t output_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_connect
 * Input: Live source/output IDs, finite gain 0..1, mute 0/1, and HRESULT output.
 * Process: Creates or updates the route without duplicating its decoder.
 * Output: Status; updates affect future mixer blocks, including mute/gain zero.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_connect(AudioHausRouter router, uint32_t source_id, uint32_t output_id, double gain, uint32_t muted, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_disconnect
 * Input: Existing source/output connection and HRESULT output.
 * Process: Removes only that route; the source transport continues independently.
 * Output: Status; a missing connection is invalid.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_disconnect(AudioHausRouter router, uint32_t source_id, uint32_t output_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_set_gain
 * Input: Existing route, finite gain 0..1, and HRESULT output.
 * Process: Changes only that connection's gain.
 * Output: Status; mute and all other routes retain their values.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_gain(AudioHausRouter router, uint32_t source_id, uint32_t output_id, double gain, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_set_mute
 * Input: Existing route, mute 0/1, and HRESULT output.
 * Process: Silences or restores that connection while its timeline continues.
 * Output: Status; gain and other routes remain unchanged.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_mute(AudioHausRouter router, uint32_t source_id, uint32_t output_id, uint32_t muted, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_source_command
 * Input: Live source, file-player command 0..3, seek ticks, and HRESULT output.
 * Process: Applies source transport to all its routes without resetting other sources.
 * Output: Status; seek positions use 100 ns units and clamp to file duration.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_source_command(AudioHausRouter router, uint32_t source_id, uint32_t command, uint64_t position_100ns, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_get_source
 * Input: Live source ID and non-null snapshot output.
 * Process: Queries its shared timeline and decode-cache counters.
 * Output: Status and source snapshot, including a failed source's HRESULT.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_source(AudioHausRouter router, uint32_t source_id, AudioHausRoutingSourceInfo* info);

/*
 * Name: audiohaus_router_get_output
 * Input: Live output ID and non-null snapshot output.
 * Process: Queries the endpoint mixer and cumulative pre-device PCM counters.
 * Output: Status and output snapshot, including a failed output's HRESULT.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_output(AudioHausRouter router, uint32_t output_id, AudioHausRoutingOutputInfo* info);

/*
 * Name: audiohaus_router_get_route
 * Input: Existing source/output connection and non-null snapshot output.
 * Process: Queries gain, mute, and reader activity without changing playback.
 * Output: Status and route snapshot; missing connections are invalid.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_route(AudioHausRouter router, uint32_t source_id, uint32_t output_id, AudioHausRoutingRouteInfo* info);

/*
 * Name: audiohaus_destroy_router
 * Input: Live router without callers, or null.
 * Process: Stops outputs, joins the worker, and releases all sources and routes.
 * Output: None; the handle becomes invalid and null is a no-op.
 */
AUDIOHAUS_API void AUDIOHAUS_CALL audiohaus_destroy_router(AudioHausRouter router);

#ifdef __cplusplus
}
#endif

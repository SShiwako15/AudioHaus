#pragma once

#include "RoutingApi.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Name: AudioHausOutputTimingInfo
 * Input: Output timing snapshot.
 * Process: Describes configured delay, device queue, correction enable, and recovery.
 * Output: 96 bytes; delay is frames, latency is 100 ns, signal QPC is raw ticks.
 */
typedef struct AudioHausOutputTimingInfo {
    uint32_t hresult, delay_frames, capacity_frames, padding_frames;
    uint32_t drift_enabled, retry_in_ms, last_error_hresult, reserved;
    uint64_t underrun_count, recovery_attempts, recovery_count, rebuffer_count;
    double clock_rate_ppm, max_service_gap_ms;
    uint64_t stream_latency_100ns, first_signal_qpc;
} AudioHausOutputTimingInfo;

/*
 * Name: AudioHausRouteTimingInfo
 * Input: Per-route buffering and fractional reader snapshot.
 * Process: Describes smooth clock correction and buffer recovery.
 * Output: 80 bytes; positions/queue sizes use normalized source frames.
 */
typedef struct AudioHausRouteTimingInfo {
    uint32_t hresult, priming, buffered_frames, target_frames;
    double correction_ppm, phase_error_frames, read_position_frame;
    uint64_t underrun_count, underrun_frames, overrun_count, rebuffer_count, reserved;
} AudioHausRouteTimingInfo;

/*
 * Name: AudioHausCaptureTimingInfo
 * Input: Process source recovery snapshot.
 * Process: Describes retry policy, desired monitor state, and dynamic history capacity.
 * Output: 48 bytes; counters persist across automatic capture reconnection.
 */
typedef struct AudioHausCaptureTimingInfo {
    uint32_t hresult, retry_in_ms, resume_state, last_error_hresult;
    uint64_t recovery_attempts, recovery_count, capacity_frames, reserved;
} AudioHausCaptureTimingInfo;

/*
 * Name: audiohaus_router_get_capture_timing
 * Input: Router, process source ID, and destination.
 * Process: Reads capture reconnection and dynamic history telemetry.
 * Output: API result and 48-byte snapshot.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_capture_timing(AudioHausRouter router, uint32_t source_id, AudioHausCaptureTimingInfo* info);
/*
 * Name: audiohaus_router_retry_output
 * Input: Router, existing output ID, and HRESULT destination.
 * Process: Requests immediate background reopen of the same selected endpoint.
 * Output: API result/HRESULT; completion remains asynchronous and queryable.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_retry_output(AudioHausRouter router, uint32_t output_id, uint32_t* error_hresult);
/*
 * Name: audiohaus_router_retry_capture
 * Input: Router, existing process source ID, and HRESULT destination.
 * Process: Requests immediate capture reopening without replacing the root PID.
 * Output: API result/HRESULT; exited roots cannot be retried.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_retry_capture(AudioHausRouter router, uint32_t source_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_router_set_output_delay
 * Input: Router, output ID, additional delay 0..96000 frames, and HRESULT output.
 * Process: Reprimes only that output's route readers.
 * Output: API result/HRESULT; transports, gain, and mute remain independent.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_output_delay(AudioHausRouter router, uint32_t output_id, uint32_t frames, uint32_t* error_hresult);
/*
 * Name: audiohaus_router_set_output_drift
 * Input: Router, output ID, enabled 0/1, and HRESULT output.
 * Process: Enables or disables gradual software clock correction.
 * Output: API result/HRESULT; invalid flag values are rejected.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_output_drift(AudioHausRouter router, uint32_t output_id, uint32_t enabled, uint32_t* error_hresult);
/*
 * Name: audiohaus_router_get_output_timing
 * Input: Router, output ID, and timing destination.
 * Process: Queries queue, clock, and recovery telemetry without altering older ABI types.
 * Output: API result and 96-byte snapshot.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_output_timing(AudioHausRouter router, uint32_t output_id, AudioHausOutputTimingInfo* info);
/*
 * Name: audiohaus_router_get_route_timing
 * Input: Router, connected source/output IDs, and timing destination.
 * Process: Queries drift and buffer/underrun telemetry.
 * Output: API result and 80-byte snapshot.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_route_timing(AudioHausRouter router, uint32_t source_id, uint32_t output_id, AudioHausRouteTimingInfo* info);

#ifdef __cplusplus
}
#endif

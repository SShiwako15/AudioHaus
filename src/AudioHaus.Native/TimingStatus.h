#pragma once

#include <cstdint>

namespace audiohaus {
using namespace std;

/*
 * Name: OutputTimingStatus
 * Input: One output's renderer, clock, and recovery state.
 * Process: Describes configured delay, endpoint buffers, underruns, and retry history.
 * Output: A 96-byte snapshot; QPC is raw ticks, latency uses 100 ns units.
 */
struct OutputTimingStatus {
    uint32_t hresult = 0, delay_frames = 0, capacity_frames = 0, padding_frames = 0;
    uint32_t drift_enabled = 1, retry_in_ms = 0, last_error_hresult = 0, reserved = 0;
    uint64_t underrun_count = 0, recovery_attempts = 0, recovery_count = 0, rebuffer_count = 0;
    double clock_rate_ppm = 0, max_service_gap_ms = 0;
    uint64_t stream_latency_100ns = 0, first_signal_qpc = 0;
};

/*
 * Name: RouteTimingStatus
 * Input: One fractional PCM reader and its buffering controller.
 * Process: Reports phase/rate correction, priming, starvation, and resynchronization.
 * Output: An 80-byte snapshot; counts are lifetime values, positions are source frames.
 */
struct RouteTimingStatus {
    uint32_t hresult = 0, priming = 1, buffered_frames = 0, target_frames = 0;
    double correction_ppm = 0, phase_error_frames = 0, read_position_frame = 0;
    uint64_t underrun_count = 0, underrun_frames = 0, overrun_count = 0;
    uint64_t rebuffer_count = 0, reserved = 0;
};

/*
 * Name: CaptureTimingStatus
 * Input: Process source buffer/recovery state.
 * Process: Describes bounded history capacity and automatic same-root reconnection.
 * Output: A 48-byte snapshot; resume_state is the desired monitor state during recovery.
 */
struct CaptureTimingStatus {
    uint32_t hresult = 0, retry_in_ms = 0, resume_state = 0, last_error_hresult = 0;
    uint64_t recovery_attempts = 0, recovery_count = 0, capacity_frames = 0, reserved = 0;
};
}

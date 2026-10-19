#pragma once

#include <Windows.h>

#include <cstdint>
#include <memory>
#include <string>
#include "TimingStatus.h"

namespace audiohaus {

using namespace std;

/*
 * Name: RoutingSourceStatus
 * Input: One file/live source's playback state and cache activity.
 * Process: Describes its common source clock and bounded shared PCM cache.
 * Output: Stopped 0, Playing 1, Paused 2, Ended 3, Failed 4, or live Recovering 5.
 */
struct RoutingSourceStatus {
    uint32_t state = 0;
    uint32_t hresult = 0;
    uint32_t sample_rate = 48000;
    uint32_t channels = 2;
    uint64_t position_100ns = 0;
    uint64_t duration_100ns = 0;
    uint64_t frames_decoded = 0;
    uint64_t cached_frames = 0;
    uint32_t route_count = 0;
    uint32_t reserved = 0;
};

/*
 * Name: RoutingOutputStatus
 * Input: One physical output's submitted mixed PCM.
 * Process: Accumulates post-clamp statistics before device volume and conversion.
 * Output: Running 1, Recovering 2, or Failed 4; lifetime counters and last-block peak.
 */
struct RoutingOutputStatus {
    uint32_t state = 1;
    uint32_t hresult = 0;
    uint32_t sample_rate = 48000;
    uint32_t channels = 2;
    uint64_t frames_submitted = 0;
    uint64_t non_silent_frames = 0;
    uint64_t clipped_samples = 0;
    double sum_squares = 0.0;
    double last_peak = 0.0;
    uint64_t start_qpc = 0;
    uint64_t stop_qpc = 0;
    uint32_t route_count = 0;
    uint32_t reserved = 0;
};

/*
 * Name: RoutingRouteStatus
 * Input: A source-to-output connection with independent gain and mute.
 * Process: Records its PCM reader cursor independently of other connections.
 * Output: Route settings and lifetime read/rebase counters.
 */
struct RoutingRouteStatus {
    uint32_t source_id = 0;
    uint32_t output_id = 0;
    uint32_t muted = 0;
    uint32_t reserved = 0;
    double gain = 1.0;
    uint64_t frames_read = 0;
    uint64_t cursor_frame = 0;
    uint64_t rebases = 0;
};

/*
 * Name: AudioLevelStatus
 * Input: A source or output master level.
 * Process: Holds normalized gain/mute separately from the stable routing ABI.
 * Output: A 16-byte target-level snapshot; muted is 0 or 1.
 */
struct AudioLevelStatus { double gain = 1.0; uint32_t muted = 0, hresult = 0; };

/*
 * Name: SourceSignalStatus
 * Input: Stereo samples sent from a source to its route readers.
 * Process: Measures the source contribution after its gain/mute and before output mixing.
 * Output: Cumulative frames/energy across destinations and the latest block peak.
 */
struct SourceSignalStatus { uint64_t frames = 0; double sum_squares = 0, last_peak = 0; };

/*
 * Name: ProcessCaptureStatus
 * Input: One live process source snapshot.
 * Process: Separates capture telemetry from the unchanged file-source ABI.
 * Output: An 80-byte snapshot; QPC packet timestamps use 100 ns units.
 */
struct ProcessCaptureStatus {
    uint32_t process_id = 0, state = 0, hresult = 0, reserved = 0;
    uint64_t frames_captured = 0, non_silent_frames = 0, discontinuities = 0;
    uint64_t frames_dropped = 0, buffered_frames = 0, last_packet_qpc_100ns = 0;
    double sum_squares = 0.0, last_peak = 0.0;
};

/*
 * Name: RoutingEngine
 * Input: File/process sources, render endpoints, and source-to-output routes.
 * Process: Shares decoded/captured PCM and mixes outputs on one MTA coordinator.
 * Output: Independent transport and routing for up to eight sources and outputs.
 */
class RoutingEngine {
public:
    /*
     * Name: set_source_level / set_output_level
     * Input: Existing resource ID, finite gain 0..1, and mute flag.
     * Process: Sets a master target, smoothed over 240 output frames.
     * Output: Acknowledged HRESULT; unrelated source/output levels are unchanged.
     */
    HRESULT set_source_level(uint32_t id, double gain, bool muted);
    HRESULT set_output_level(uint32_t id, double gain, bool muted);
    /*
     * Name: get_source_level / get_output_level
     * Input: Existing resource ID and snapshot destination.
     * Process: Reads the acknowledged master target on the owning worker.
     * Output: HRESULT and level; missing IDs fail.
     */
    HRESULT get_source_level(uint32_t id, AudioLevelStatus& level);
    HRESULT get_output_level(uint32_t id, AudioLevelStatus& level);
    /*
     * Name: get_source_signal
     * Input: Existing source ID and signal destination.
     * Process: Copies post-source-master measurements on the owning worker.
     * Output: HRESULT and routed signal; missing IDs fail.
     */
    HRESULT get_source_signal(uint32_t id, SourceSignalStatus& signal);
    /*
     * Name: create
     * Input: An engine ownership destination.
     * Process: Initializes the worker's COM and Media Foundation environment.
     * Output: HRESULT and an empty routing engine, or no engine on failure.
     */
    static HRESULT create(unique_ptr<RoutingEngine>& engine);

    /*
     * Name: ~RoutingEngine
     * Input: None; other calls must finish before destruction.
     * Process: Wakes and joins the worker, releasing every decoder and output.
     * Output: Complete shutdown without an allocating command.
     */
    ~RoutingEngine();

    RoutingEngine(const RoutingEngine&) = delete;
    RoutingEngine& operator=(const RoutingEngine&) = delete;

    /*
     * Name: add_source
     * Input: Local audio file path and an ID destination.
     * Process: Opens one shared decoder and allocates its bounded PCM ring.
     * Output: HRESULT and a nonzero source ID; the new source is Stopped.
     */
    HRESULT add_source(const wstring& file_path, uint32_t& id);

    /*
     * Name: add_process_source
     * Input: Live application PID, virtual parking endpoint, and ID destination.
     * Process: Opens process-tree loopback with bounded live buffering and isolation checks.
     * Output: HRESULT and a stopped monitoring source; the application keeps playing.
     */
    HRESULT add_process_source(uint32_t process_id, const wstring& parking_id, uint32_t& id);

    /*
     * Name: get_capture_status
     * Input: A process source ID and destination.
     * Process: Copies capture health, packet, and overrun counters.
     * Output: HRESULT and live telemetry; file source IDs are rejected.
     */
    HRESULT get_capture_status(uint32_t id, ProcessCaptureStatus& status);

    /*
     * Name: add_output
     * Input: A distinct active render endpoint ID and an ID destination.
     * Process: Opens one mixer/client for that endpoint and starts streaming silence.
     * Output: HRESULT and a nonzero output ID; duplicate endpoints are rejected.
     */
    HRESULT add_output(const wstring& endpoint_id, uint32_t& id);

    /*
     * Name: remove_source
     * Input: Existing source ID.
     * Process: Removes its routes and releases its decoder without restarting outputs.
     * Output: HRESULT; unrelated playback continues.
     */
    HRESULT remove_source(uint32_t id);

    /*
     * Name: remove_output
     * Input: Existing output ID.
     * Process: Removes its routes and stops only the selected WASAPI client.
     * Output: HRESULT; other outputs continue.
     */
    HRESULT remove_output(uint32_t id);

    /*
     * Name: connect
     * Input: Existing source/output IDs, gain 0..1, and route mute.
     * Process: Creates or updates a connection without duplicating its decoder.
     * Output: HRESULT; a new reader starts at the source's current position.
     */
    HRESULT connect(uint32_t source_id, uint32_t output_id, double gain, bool muted);

    /*
     * Name: disconnect
     * Input: Existing route's source/output IDs.
     * Process: Removes that connection without stopping its source or output.
     * Output: HRESULT; other routes remain active.
     */
    HRESULT disconnect(uint32_t source_id, uint32_t output_id);

    /*
     * Name: set_route_gain
     * Input: Existing route IDs and gain 0..1.
     * Process: Changes only that connection's mixing scalar.
     * Output: HRESULT after worker acknowledgment.
     */
    HRESULT set_route_gain(uint32_t source_id, uint32_t output_id, double gain);

    /*
     * Name: set_route_mute
     * Input: Existing route IDs and mute state.
     * Process: Omits muted PCM while its reader continues advancing.
     * Output: HRESULT after worker acknowledgment.
     */
    HRESULT set_route_mute(uint32_t source_id, uint32_t output_id, bool muted);

    /*
     * Name: source_command
     * Input: Source ID, command Play 0/Pause 1/Stop 2/Seek 3, and seek time.
     * Process: Applies independent transport and resets affected reader cursors as needed.
     * Output: HRESULT; position uses 100-nanosecond units and seeks clamp to duration.
     */
    HRESULT source_command(uint32_t source_id, uint32_t command, uint64_t position_100ns);

    /*
     * Name: get_source_status
     * Input: Existing source ID and a status destination.
     * Process: Refreshes its common source clock and cache statistics.
     * Output: S_OK with a snapshot, including a queryable Failed state.
     */
    HRESULT get_source_status(uint32_t id, RoutingSourceStatus& status);

    /*
     * Name: get_output_status
     * Input: Existing output ID and a status destination.
     * Process: Copies accumulated PCM statistics and output state.
     * Output: S_OK with a snapshot, including a queryable Failed state.
     */
    HRESULT get_output_status(uint32_t id, RoutingOutputStatus& status);

    /*
     * Name: get_route_status
     * Input: Existing route IDs and a status destination.
     * Process: Copies that connection's settings and independent reader counters.
     * Output: S_OK and its snapshot, or a missing-route error.
     */
    HRESULT get_route_status(uint32_t source_id, uint32_t output_id, RoutingRouteStatus& status);

    /*
     * Name: set_output_delay
     * Input: Output ID and additional delay from zero through 96000 source frames.
     * Process: Reprimes only this output's routes while retaining transports and gain/mute.
     * Output: HRESULT; newly mixed audio adopts the delay without resetting other outputs.
     */
    HRESULT set_output_delay(uint32_t output_id, uint32_t frames);
    /*
     * Name: set_output_drift
     * Input: Output ID and correction enable flag.
     * Process: Resets rate controllers without changing the configured output delay.
     * Output: HRESULT; disabled correction uses unity resampling.
     */
    HRESULT set_output_drift(uint32_t output_id, bool enabled);
    /*
     * Name: get_output_timing
     * Input: Output ID and timing destination.
     * Process: Queries device buffers, clock estimates, and recovery counters.
     * Output: HRESULT and snapshot without changing the older output-status layout.
     */
    HRESULT get_output_timing(uint32_t output_id, OutputTimingStatus& status);
    /*
     * Name: get_route_timing
     * Input: Route IDs and timing destination.
     * Process: Queries fractional cursor, buffering, drift, and starvation counters.
     * Output: HRESULT and snapshot; missing routes are rejected.
     */
    HRESULT get_route_timing(uint32_t source_id, uint32_t output_id, RouteTimingStatus& status);
    /*
     * Name: get_capture_timing
     * Input: Process source ID and timing destination.
     * Process: Reads dynamic history capacity and capture reconnection counters.
     * Output: HRESULT and snapshot; file sources are rejected.
     */
    HRESULT get_capture_timing(uint32_t source_id, CaptureTimingStatus& status);
    /*
     * Name: retry_output
     * Input: Existing output ID.
     * Process: Requests an immediate background reopen of the same endpoint.
     * Output: HRESULT after queuing; completion is visible in output state/counters.
     */
    HRESULT retry_output(uint32_t output_id);
    /*
     * Name: retry_capture
     * Input: Existing process source ID with its original root still alive.
     * Process: Requests capture reopening after the user fixes its setup.
     * Output: HRESULT after queuing; no new PID is selected.
     */
    HRESULT retry_capture(uint32_t source_id);

private:
    class Impl;

    /*
     * Name: RoutingEngine
     * Input: An initialized implementation.
     * Process: Takes exclusive worker ownership.
     * Output: An engine created through create.
     */
    explicit RoutingEngine(unique_ptr<Impl> impl) noexcept;

    unique_ptr<Impl> impl_;
};

}

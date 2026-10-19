#ifndef AUDIOHAUS_NATIVE_EXPORTS
#define AUDIOHAUS_NATIVE_EXPORTS
#endif

#include "RoutingApi.h"
#include "RoutingEngine.h"
#include "TimingApi.h"
#include <cstring>
#include "AudioSessionDiscovery.h"
#include <objbase.h>

#include <cmath>
#include <limits>
#include <memory>
#include <new>
#include <algorithm>
#include <vector>

using namespace std;

static_assert(sizeof(AudioHausRoutingSourceInfo) == 56);
static_assert(sizeof(AudioHausLevelInfo) == 16);
static_assert(sizeof(AudioHausSourceSignalInfo) == 32);
static_assert(sizeof(AudioHausRoutingOutputInfo) == 80);
static_assert(sizeof(AudioHausRoutingRouteInfo) == 48);
static_assert(sizeof(AudioHausProcessCaptureInfo) == 80);
static_assert(sizeof(AudioHausOutputTimingInfo) == sizeof(audiohaus::OutputTimingStatus));
static_assert(sizeof(AudioHausOutputTimingInfo) == 96);
static_assert(sizeof(AudioHausRouteTimingInfo) == sizeof(audiohaus::RouteTimingStatus));
static_assert(sizeof(AudioHausRouteTimingInfo) == 80);
static_assert(sizeof(AudioHausCaptureTimingInfo) == sizeof(audiohaus::CaptureTimingStatus));
static_assert(sizeof(AudioHausCaptureTimingInfo) == 48);

/*
 * Name: AudioHausRouterOpaque
 * Input: A successfully created native routing engine.
 * Process: Keeps all engine ownership inside the DLL.
 * Output: Storage addressed by the C API handle.
 */
struct AudioHausRouterOpaque {
    unique_ptr<audiohaus::RoutingEngine> engine;
};

namespace {

/*
 * Name: result_code
 * Input: Windows operation result.
 * Process: Maps argument, allocation, and playback failures to C API status codes.
 * Output: An AudioHausResult without altering the underlying HRESULT.
 */
AudioHausResult result_code(HRESULT value) noexcept {
    if (value == E_INVALIDARG) { return kAudioHausInvalidArgument; }
    if (value == E_OUTOFMEMORY) { return kAudioHausOutOfMemory; }
    return FAILED(value) ? kAudioHausPlaybackFailed : kAudioHausSuccess;
}

/*
 * Name: invoke_router
 * Input: Live handle, HRESULT output, and a non-owning operation.
 * Process: Validates ownership and contains all exceptions from the worker call.
 * Output: API status and the exact operation HRESULT.
 */
template<typename Action>
AudioHausResult invoke_router(AudioHausRouter router, uint32_t* error, Action&& action) {
    if (error != nullptr) { *error = static_cast<uint32_t>(E_INVALIDARG); }
    if (router == nullptr || error == nullptr) { return kAudioHausInvalidArgument; }
    try {
        const HRESULT value = action(*router->engine);
        *error = static_cast<uint32_t>(value);
        return result_code(value);
    } catch (const bad_alloc&) {
        *error = static_cast<uint32_t>(E_OUTOFMEMORY);
        return kAudioHausOutOfMemory;
    } catch (...) {
        *error = static_cast<uint32_t>(E_FAIL);
        return kAudioHausInternalError;
    }
}

/*
 * Name: query_router
 * Input: Live handle, snapshot storage, and a query operation.
 * Process: Clears storage and contains exceptions while obtaining a snapshot.
 * Output: Status and snapshot; source/output errors retain their HRESULT bits.
 */
template<typename Info, typename Action>
AudioHausResult query_router(AudioHausRouter router, Info* info, Action&& action) {
    if (info != nullptr) { *info = {}; }
    if (router == nullptr || info == nullptr) { return kAudioHausInvalidArgument; }
    HRESULT value = E_FAIL;
    AudioHausResult result = kAudioHausInternalError;
    try {
        value = action(*router->engine, *info);
        result = result_code(value);
    } catch (const bad_alloc&) {
        value = E_OUTOFMEMORY;
        result = kAudioHausOutOfMemory;
    } catch (...) {
        value = E_FAIL;
    }
    if constexpr (requires { info->hresult; }) {
        if (FAILED(value)) { info->hresult = static_cast<uint32_t>(value); }
    }
    return result;
}

}

/*
 * Name: audiohaus_create_router
 * Input: Handle and HRESULT output pointers.
 * Process: Creates an empty worker and transfers ownership after successful startup.
 * Output: Status and handle; failures leave the handle null.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_create_router(AudioHausRouter* router, uint32_t* error_hresult) {
    if (router != nullptr) { *router = nullptr; }
    if (error_hresult != nullptr) { *error_hresult = static_cast<uint32_t>(E_INVALIDARG); }
    if (router == nullptr || error_hresult == nullptr) { return kAudioHausInvalidArgument; }
    try {
        auto owned = make_unique<AudioHausRouterOpaque>();
        const HRESULT value = audiohaus::RoutingEngine::create(owned->engine);
        *error_hresult = static_cast<uint32_t>(value);
        if (FAILED(value)) { return result_code(value); }
        if (!owned->engine) {
            *error_hresult = static_cast<uint32_t>(E_UNEXPECTED);
            return kAudioHausInternalError;
        }
        *router = owned.release();
        return kAudioHausSuccess;
    } catch (const bad_alloc&) {
        *error_hresult = static_cast<uint32_t>(E_OUTOFMEMORY);
        return kAudioHausOutOfMemory;
    } catch (...) {
        *error_hresult = static_cast<uint32_t>(E_FAIL);
        return kAudioHausInternalError;
    }
}

/*
 * Name: audiohaus_router_set_source_level
 * Input: Router, source ID, normalized gain, mute 0/1, and HRESULT output.
 * Process: Validates the ABI flag and acknowledges a smoothed source master.
 * Output: Status and exact HRESULT; invalid targets preserve the previous level.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_source_level(AudioHausRouter router, uint32_t id, double gain, uint32_t muted, uint32_t* error) {
    return invoke_router(router, error, [&](auto& engine) { return muted > 1 ? E_INVALIDARG : engine.set_source_level(id, gain, muted != 0); });
}
/*
 * Name: audiohaus_router_set_output_level
 * Input: Router, output ID, normalized gain, mute 0/1, and HRESULT output.
 * Process: Validates the ABI flag and acknowledges a smoothed output master.
 * Output: Status and exact HRESULT; invalid targets preserve the previous level.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_output_level(AudioHausRouter router, uint32_t id, double gain, uint32_t muted, uint32_t* error) {
    return invoke_router(router, error, [&](auto& engine) { return muted > 1 ? E_INVALIDARG : engine.set_output_level(id, gain, muted != 0); });
}
/*
 * Name: audiohaus_router_get_source_level
 * Input: Router, source ID, and level destination.
 * Process: Copies the acknowledged source master target.
 * Output: Status and level/HRESULT; invalid IDs fail.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_source_level(AudioHausRouter router, uint32_t id, AudioHausLevelInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& value) { audiohaus::AudioLevelStatus level;
        const HRESULT hr = engine.get_source_level(id, level); value = {level.gain, level.muted, static_cast<uint32_t>(hr)}; return hr; });
}
/*
 * Name: audiohaus_router_get_output_level
 * Input: Router, output ID, and level destination.
 * Process: Copies the acknowledged output master target.
 * Output: Status and level/HRESULT; invalid IDs fail.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_output_level(AudioHausRouter router, uint32_t id, AudioHausLevelInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& value) { audiohaus::AudioLevelStatus level;
        const HRESULT hr = engine.get_output_level(id, level); value = {level.gain, level.muted, static_cast<uint32_t>(hr)}; return hr; });
}

/*
 * Name: audiohaus_router_get_source_signal
 * Input: Router, source identity, and signal storage.
 * Process: Copies worker measurements with the standard exception/status boundary.
 * Output: Status and a zero-initialized snapshot on failure.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_source_signal(AudioHausRouter router, uint32_t id, AudioHausSourceSignalInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& value) { audiohaus::SourceSignalStatus signal;
        const HRESULT hr = engine.get_source_signal(id, signal);
        value = {signal.frames, signal.sum_squares, signal.last_peak, static_cast<uint32_t>(hr), 0}; return hr; });
}

/*
 * Name: audiohaus_router_add_source
 * Input: Router, local path, and ID/HRESULT outputs.
 * Process: Creates one stopped source without changing existing routes.
 * Output: Status and a new source ID, or zero on failure.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_add_source(AudioHausRouter router, const wchar_t* path, uint32_t* source_id, uint32_t* error_hresult) {
    if (source_id != nullptr) { *source_id = 0; }
    return invoke_router(router, error_hresult, [&](auto& engine) {
        if (path == nullptr || path[0] == L'\0' || source_id == nullptr) { return E_INVALIDARG; }
        return engine.add_source(path, *source_id);
    });
}

/*
 * Name: audiohaus_router_add_output
 * Input: Router, endpoint ID, and ID/HRESULT outputs.
 * Process: Creates the endpoint's single live mixer stream.
 * Output: Status and a new output ID, or zero on failure.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_add_output(AudioHausRouter router, const wchar_t* endpoint_id, uint32_t* output_id, uint32_t* error_hresult) {
    if (output_id != nullptr) { *output_id = 0; }
    return invoke_router(router, error_hresult, [&](auto& engine) {
        if (endpoint_id == nullptr || endpoint_id[0] == L'\0' || output_id == nullptr) { return E_INVALIDARG; }
        return engine.add_output(endpoint_id, *output_id);
    });
}

/*
 * Name: audiohaus_router_remove_source
 * Input: Router, source ID, and HRESULT output.
 * Process: Removes the decoder and its routes.
 * Output: Operation status.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_remove_source(AudioHausRouter router, uint32_t source_id, uint32_t* error_hresult) {
    return invoke_router(router, error_hresult, [&](auto& engine) { return engine.remove_source(source_id); });
}

/*
 * Name: audiohaus_router_remove_output
 * Input: Router, output ID, and HRESULT output.
 * Process: Removes the mixer stream and its routes.
 * Output: Operation status.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_remove_output(AudioHausRouter router, uint32_t output_id, uint32_t* error_hresult) {
    return invoke_router(router, error_hresult, [&](auto& engine) { return engine.remove_output(output_id); });
}

/*
 * Name: audiohaus_router_connect
 * Input: Router, source/output IDs, gain, mute, and HRESULT output.
 * Process: Validates values and inserts or updates a route.
 * Output: Operation status.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_connect(AudioHausRouter router, uint32_t source_id, uint32_t output_id, double gain, uint32_t muted, uint32_t* error_hresult) {
    return invoke_router(router, error_hresult, [&](auto& engine) {
        if (!isfinite(gain) || gain < 0.0 || gain > 1.0 || muted > 1) { return E_INVALIDARG; }
        return engine.connect(source_id, output_id, gain, muted != 0);
    });
}

/*
 * Name: audiohaus_router_disconnect
 * Input: Router, route IDs, and HRESULT output.
 * Process: Removes one existing connection.
 * Output: Operation status.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_disconnect(AudioHausRouter router, uint32_t source_id, uint32_t output_id, uint32_t* error_hresult) {
    return invoke_router(router, error_hresult, [&](auto& engine) { return engine.disconnect(source_id, output_id); });
}

/*
 * Name: audiohaus_router_set_gain
 * Input: Router, route IDs, finite gain, and HRESULT output.
 * Process: Changes only one route's gain.
 * Output: Operation status.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_gain(AudioHausRouter router, uint32_t source_id, uint32_t output_id, double gain, uint32_t* error_hresult) {
    return invoke_router(router, error_hresult, [&](auto& engine) {
        if (!isfinite(gain) || gain < 0.0 || gain > 1.0) { return E_INVALIDARG; }
        return engine.set_route_gain(source_id, output_id, gain);
    });
}

/*
 * Name: audiohaus_router_set_mute
 * Input: Router, route IDs, mute 0/1, and HRESULT output.
 * Process: Changes only one route's mute state.
 * Output: Operation status.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_mute(AudioHausRouter router, uint32_t source_id, uint32_t output_id, uint32_t muted, uint32_t* error_hresult) {
    return invoke_router(router, error_hresult, [&](auto& engine) {
        if (muted > 1) { return E_INVALIDARG; }
        return engine.set_route_mute(source_id, output_id, muted != 0);
    });
}

/*
 * Name: audiohaus_router_source_command
 * Input: Router, source, transport command, seek ticks, and HRESULT output.
 * Process: Dispatches independent source transport on the routing worker.
 * Output: Operation status.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_source_command(AudioHausRouter router, uint32_t source_id, uint32_t command, uint64_t position_100ns, uint32_t* error_hresult) {
    return invoke_router(router, error_hresult, [&](auto& engine) {
        if (command > kAudioHausFileSeek || position_100ns > static_cast<uint64_t>((numeric_limits<int64_t>::max)())) { return E_INVALIDARG; }
        return engine.source_command(source_id, command, position_100ns);
    });
}

/*
 * Name: audiohaus_router_get_source
 * Input: Router, source ID, and snapshot output.
 * Process: Copies source transport and cache counters.
 * Output: Status and snapshot.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_source(AudioHausRouter router, uint32_t source_id, AudioHausRoutingSourceInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& destination) {
        audiohaus::RoutingSourceStatus value;
        const HRESULT result = engine.get_source_status(source_id, value);
        if (SUCCEEDED(result)) {
            destination = { value.state, value.hresult, value.sample_rate, value.channels,
                value.position_100ns, value.duration_100ns, value.frames_decoded,
                value.cached_frames, value.route_count, 0 };
        }
        return result;
    });
}

/*
 * Name: audiohaus_router_get_output
 * Input: Router, output ID, and snapshot output.
 * Process: Copies endpoint mixer activity and cumulative PCM measurements.
 * Output: Status and snapshot.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_output(AudioHausRouter router, uint32_t output_id, AudioHausRoutingOutputInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& destination) {
        audiohaus::RoutingOutputStatus value;
        const HRESULT result = engine.get_output_status(output_id, value);
        if (SUCCEEDED(result)) {
            destination = { value.state, value.hresult, value.sample_rate, value.channels,
                value.frames_submitted, value.non_silent_frames, value.clipped_samples,
                value.sum_squares, value.last_peak, value.start_qpc, value.stop_qpc,
                value.route_count, 0 };
        }
        return result;
    });
}

/*
 * Name: audiohaus_router_get_route
 * Input: Router, route IDs, and snapshot output.
 * Process: Copies route controls and reader activity.
 * Output: Status and snapshot.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_route(AudioHausRouter router, uint32_t source_id, uint32_t output_id, AudioHausRoutingRouteInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& destination) {
        audiohaus::RoutingRouteStatus value;
        const HRESULT result = engine.get_route_status(source_id, output_id, value);
        if (SUCCEEDED(result)) {
            destination = { value.source_id, value.output_id, value.muted, 0,
                value.gain, value.frames_read, value.cursor_frame, value.rebases };
        }
        return result;
    });
}

/*
 * Name: audiohaus_destroy_router
 * Input: Owned router without callers, or null.
 * Process: Stops its outputs and releases all graph resources.
 * Output: None; null is harmless and the handle becomes invalid.
 */
void AUDIOHAUS_CALL audiohaus_destroy_router(AudioHausRouter router) { delete router; }

/*
 * Name: audiohaus_router_set_output_delay
 * Input: Router, output ID, delay frames, and HRESULT output.
 * Process: Contains exceptions around a validated delay update.
 * Output: API result and exact native HRESULT.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_output_delay(AudioHausRouter router, uint32_t id, uint32_t frames, uint32_t* error) {
    return invoke_router(router, error, [&](auto& engine) { return engine.set_output_delay(id, frames); });
}
/*
 * Name: audiohaus_router_set_output_drift
 * Input: Router, output ID, enabled 0/1, and HRESULT output.
 * Process: Validates the flag and changes one output's correction policy.
 * Output: API result and exact native HRESULT.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_set_output_drift(AudioHausRouter router, uint32_t id, uint32_t enabled, uint32_t* error) {
    return invoke_router(router, error, [&](auto& engine) { return enabled > 1 ? E_INVALIDARG : engine.set_output_drift(id, enabled != 0); });
}
/*
 * Name: audiohaus_router_get_output_timing
 * Input: Router, output ID, and snapshot destination.
 * Process: Copies the layout-checked plain timing snapshot.
 * Output: API result and telemetry; query errors clear storage and set HRESULT.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_output_timing(AudioHausRouter router, uint32_t id, AudioHausOutputTimingInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& destination) {
        audiohaus::OutputTimingStatus value;
        const HRESULT hr = engine.get_output_timing(id, value);
        if (SUCCEEDED(hr)) { memcpy(&destination, &value, sizeof(value)); }
        return hr;
    });
}
/*
 * Name: audiohaus_router_get_route_timing
 * Input: Router, route IDs, and snapshot destination.
 * Process: Copies the layout-checked fractional-reader snapshot.
 * Output: API result and telemetry; missing connections are rejected.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_route_timing(AudioHausRouter router, uint32_t source, uint32_t output, AudioHausRouteTimingInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& destination) {
        audiohaus::RouteTimingStatus value;
        const HRESULT hr = engine.get_route_timing(source, output, value);
        if (SUCCEEDED(hr)) { memcpy(&destination, &value, sizeof(value)); }
        return hr;
    });
}

/*
 * Name: audiohaus_router_get_capture_timing
 * Input: Router, process source ID, and timing destination.
 * Process: Copies the layout-checked capture recovery snapshot.
 * Output: API result and metadata; file sources are rejected.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_capture_timing(AudioHausRouter router, uint32_t id, AudioHausCaptureTimingInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& destination) {
        audiohaus::CaptureTimingStatus value;
        const HRESULT hr = engine.get_capture_timing(id, value);
        if (SUCCEEDED(hr)) { memcpy(&destination, &value, sizeof(value)); }
        return hr;
    });
}
/*
 * Name: audiohaus_router_retry_output
 * Input: Router, output ID, and HRESULT destination.
 * Process: Contains exceptions around a user-requested reopen.
 * Output: API result and exact HRESULT.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_retry_output(AudioHausRouter router, uint32_t id, uint32_t* error) {
    return invoke_router(router, error, [&](auto& engine) { return engine.retry_output(id); });
}
/*
 * Name: audiohaus_router_retry_capture
 * Input: Router, process source ID, and HRESULT destination.
 * Process: Contains exceptions around same-root capture reopening.
 * Output: API result and exact HRESULT.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_retry_capture(AudioHausRouter router, uint32_t id, uint32_t* error) {
    return invoke_router(router, error, [&](auto& engine) { return engine.retry_capture(id); });
}

/*
 * Name: audiohaus_router_add_process
 * Input: Router, PID, parking endpoint, and required outputs.
 * Process: Contains exceptions while creating a process capture source.
 * Output: API status, zero ID on failure, and HRESULT details.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_add_process(AudioHausRouter router, uint32_t process_id,
    const wchar_t* parking_id, uint32_t* source_id, uint32_t* error_hresult) {
    if (source_id != nullptr) { *source_id = 0; }
    return invoke_router(router, error_hresult, [&](auto& engine) {
        if (source_id == nullptr || parking_id == nullptr || process_id == 0) { return E_INVALIDARG; }
        return engine.add_process_source(process_id, parking_id, *source_id);
    });
}

/*
 * Name: audiohaus_router_get_capture
 * Input: Router, process source ID, and status destination.
 * Process: Queries live capture telemetry without exposing COM ownership.
 * Output: API status and a snapshot including asynchronous source failures.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_router_get_capture(AudioHausRouter router, uint32_t source_id, AudioHausProcessCaptureInfo* info) {
    return query_router(router, info, [&](auto& engine, auto& destination) {
        audiohaus::ProcessCaptureStatus value;
        const HRESULT hr = engine.get_capture_status(source_id, value);
        if (SUCCEEDED(hr)) {
            destination = {value.process_id, value.state, value.hresult, 0,
                value.frames_captured, value.non_silent_frames, value.discontinuities,
                value.frames_dropped, value.buffered_frames, value.last_packet_qpc_100ns, value.sum_squares, value.last_peak};
        }
        return hr;
    });
}

/*
 * Name: audiohaus_get_endpoint_processes
 * Input: Endpoint ID, optional PID storage, capacity, and required outputs.
 * Process: Initializes COM when needed and copies a bounded active-session snapshot.
 * Output: Status/HRESULT; caller buffers remain untouched when insufficient.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_get_endpoint_processes(const wchar_t* endpoint_id,
    uint32_t* process_ids, uint32_t capacity, uint32_t* required, uint32_t* error_hresult) {
    if (required != nullptr) { *required = 0; }
    if (error_hresult != nullptr) { *error_hresult = static_cast<uint32_t>(E_INVALIDARG); }
    if (endpoint_id == nullptr || *endpoint_id == L'\0' || required == nullptr || error_hresult == nullptr ||
        (capacity != 0 && process_ids == nullptr)) { return kAudioHausInvalidArgument; }
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) {
        *error_hresult = static_cast<uint32_t>(initialized); return result_code(initialized);
    }
    AudioHausResult result = kAudioHausSuccess;
    try {
        vector<audiohaus::AudioSessionProcess> sessions;
        const HRESULT hr = audiohaus::enumerate_audio_sessions(sessions);
        *error_hresult = static_cast<uint32_t>(hr);
        result = result_code(hr);
        if (SUCCEEDED(hr)) {
            vector<uint32_t> ids;
            for (const auto& session : sessions) {
                if (CompareStringOrdinal(endpoint_id, -1, session.endpoint_id.c_str(), -1, TRUE) == CSTR_EQUAL) {
                    ids.push_back(session.process_id);
                }
            }
            sort(ids.begin(), ids.end());
            ids.erase(unique(ids.begin(), ids.end()), ids.end());
            *required = static_cast<uint32_t>(ids.size());
            if (capacity < ids.size()) { result = kAudioHausBufferTooSmall; }
            else if (!ids.empty()) { copy(ids.begin(), ids.end(), process_ids); }
        }
    } catch (const bad_alloc&) { *error_hresult = static_cast<uint32_t>(E_OUTOFMEMORY); result = kAudioHausOutOfMemory; }
    catch (...) { *error_hresult = static_cast<uint32_t>(E_FAIL); result = kAudioHausInternalError; }
    if (SUCCEEDED(initialized)) { CoUninitialize(); }
    return result;
}

#ifndef AUDIOHAUS_NATIVE_EXPORTS
#define AUDIOHAUS_NATIVE_EXPORTS
#endif

#include "AudioHausApi.h"
#include "AudioEndpointEnumerator.h"
#include "TonePlayback.h"
#include "FilePlayer.h"

#include <Windows.h>

#include <algorithm>
#include <cmath>
#include <limits>
#include <memory>
#include <new>
#include <stdexcept>
#include <utility>
#include <vector>

using namespace std;

static_assert(sizeof(AudioHausFilePlayerInfo) == 56);

/*
 * Name: AudioHausEndpointSnapshotOpaque
 * Input: Endpoint snapshots produced by native discovery.
 * Process: Owns endpoint data independently of Windows device handles.
 * Output: Storage accessed through the opaque C API handle.
 */
struct AudioHausEndpointSnapshotOpaque {
    vector<audiohaus::AudioEndpoint> endpoints;
};

/*
 * Name: AudioHausFilePlayerOpaque
 * Input: A successfully opened native file player.
 * Process: Keeps ownership private to the native DLL.
 * Output: Storage addressed by the C API's opaque player handle.
 */
struct AudioHausFilePlayerOpaque {
    unique_ptr<audiohaus::FilePlayer> player;
};

namespace {

/*
 * Name: to_api_state
 * Input: Native endpoint state.
 * Process: Maps the internal enum to explicit C API constants.
 * Output: ABI-stable state value; unrecognized states become unknown.
 */
uint32_t to_api_state(audiohaus::AudioEndpointState state) {
    switch (state) {
    case audiohaus::AudioEndpointState::kActive: return kAudioHausEndpointActive;
    case audiohaus::AudioEndpointState::kDisabled: return kAudioHausEndpointDisabled;
    case audiohaus::AudioEndpointState::kNotPresent: return kAudioHausEndpointNotPresent;
    case audiohaus::AudioEndpointState::kUnplugged: return kAudioHausEndpointUnplugged;
    default: return kAudioHausEndpointUnknown;
    }
}

}

/*
 * Name: audiohaus_get_api_version
 * Input: None.
 * Process: Identifies this implementation's ABI contract.
 * Output: Version 1; does not throw.
 */
uint32_t AUDIOHAUS_CALL audiohaus_get_api_version(void) {
    return 1;
}

/*
 * Name: audiohaus_create_endpoint_snapshot
 * Input: Non-null snapshot and endpoint_count output pointers.
 * Process: Discovers endpoints and transfers ownership only after success.
 * Output: Status code and handle/count; failure clears valid outputs. No throws.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_create_endpoint_snapshot(
    AudioHausEndpointSnapshot* snapshot,
    uint32_t* endpoint_count) {
    if (snapshot != nullptr) {
        *snapshot = nullptr;
    }
    if (endpoint_count != nullptr) {
        *endpoint_count = 0;
    }
    if (snapshot == nullptr || endpoint_count == nullptr) {
        return kAudioHausInvalidArgument;
    }

    try {
        auto owned_snapshot = make_unique<AudioHausEndpointSnapshotOpaque>();
        owned_snapshot->endpoints = audiohaus::AudioEndpointEnumerator{}.enumerate();
        if (owned_snapshot->endpoints.size() > (numeric_limits<uint32_t>::max)()) {
            return kAudioHausInternalError;
        }
        *endpoint_count = static_cast<uint32_t>(owned_snapshot->endpoints.size());
        *snapshot = owned_snapshot.release();
        return kAudioHausSuccess;
    } catch (const bad_alloc&) {
        return kAudioHausOutOfMemory;
    } catch (const runtime_error&) {
        return kAudioHausDiscoveryFailed;
    } catch (...) {
        return kAudioHausInternalError;
    }
}

/*
 * Name: audiohaus_get_endpoint
 * Input: Live snapshot, index, info pointer, and separate UTF-16 output buffers.
 * Process: Validates arguments and sizes before copying either string.
 * Output: Status and metadata; small buffers stay untouched, invalid arguments
 *         clear info when provided. Success writes null-terminated strings.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_get_endpoint(
    AudioHausEndpointSnapshot snapshot,
    uint32_t index,
    AudioHausEndpointInfo* info,
    wchar_t* id_buffer,
    uint32_t id_capacity,
    wchar_t* display_name_buffer,
    uint32_t display_name_capacity) {
    if (info != nullptr) {
        *info = {};
    }
    if (snapshot == nullptr || info == nullptr
        || (id_buffer == nullptr && id_capacity != 0)
        || (display_name_buffer == nullptr && display_name_capacity != 0)) {
        return kAudioHausInvalidArgument;
    }

    try {
        if (index >= snapshot->endpoints.size()) {
            return kAudioHausInvalidArgument;
        }
        const auto& endpoint = snapshot->endpoints[index];
        const auto max_capacity = (numeric_limits<uint32_t>::max)();
        if (endpoint.id.size() >= max_capacity || endpoint.display_name.size() >= max_capacity) {
            return kAudioHausInternalError;
        }

        info->id_capacity_required = static_cast<uint32_t>(endpoint.id.size() + 1);
        info->display_name_capacity_required = static_cast<uint32_t>(endpoint.display_name.size() + 1);
        info->state = to_api_state(endpoint.state);
        info->is_default_multimedia = endpoint.is_default_multimedia ? 1u : 0u;

        if (id_capacity < info->id_capacity_required
            || display_name_capacity < info->display_name_capacity_required) {
            return kAudioHausBufferTooSmall;
        }

        copy_n(endpoint.id.c_str(), info->id_capacity_required, id_buffer);
        copy_n(endpoint.display_name.c_str(), info->display_name_capacity_required, display_name_buffer);
        return kAudioHausSuccess;
    } catch (const bad_alloc&) {
        return kAudioHausOutOfMemory;
    } catch (...) {
        return kAudioHausInternalError;
    }
}

/*
 * Name: audiohaus_destroy_endpoint_snapshot
 * Input: Live snapshot with no remaining readers, or null.
 * Process: Deletes the snapshot and its owned data; null is a no-op.
 * Output: None; the released handle must not be reused. Does not throw.
 */
void AUDIOHAUS_CALL audiohaus_destroy_endpoint_snapshot(AudioHausEndpointSnapshot snapshot) {
    delete snapshot;
}

/*
 * Name: audiohaus_play_test_tone
 * Input: Endpoint IDs, tone parameters, and caller-owned output report storage.
 * Process: Validates the request, invokes native playback, and translates results.
 * Output: Status and per-output reports; exceptions become status codes.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_play_test_tone(
    const wchar_t* const* endpoint_ids,
    uint32_t endpoint_count,
    double frequency_hz,
    double amplitude,
    uint32_t duration_ms,
    AudioHausToneOutputInfo* results,
    uint32_t results_capacity) {
    if (endpoint_count < 1 || endpoint_count > 2 || endpoint_ids == nullptr || results == nullptr) {
        return kAudioHausInvalidArgument;
    }
    fill_n(results, (min)(results_capacity, endpoint_count), AudioHausToneOutputInfo{});
    if (results_capacity < endpoint_count) {
        return kAudioHausBufferTooSmall;
    }
    if (!isfinite(frequency_hz) || frequency_hz < 20.0 || frequency_hz > 20000.0
        || !isfinite(amplitude) || amplitude < 0.0 || amplitude > 0.25
        || duration_ms == 0 || duration_ms > 60000) {
        return kAudioHausInvalidArgument;
    }
    for (uint32_t index = 0; index < endpoint_count; ++index) {
        if (endpoint_ids[index] == nullptr || endpoint_ids[index][0] == L'\0') {
            return kAudioHausInvalidArgument;
        }
    }
    if (endpoint_count == 2 && CompareStringOrdinal(endpoint_ids[0], -1, endpoint_ids[1], -1, TRUE) == CSTR_EQUAL) {
        return kAudioHausInvalidArgument;
    }

    try {
        audiohaus::TonePlaybackRequest request;
        request.endpoint_ids.assign(endpoint_ids, endpoint_ids + endpoint_count);
        request.frequency_hz = frequency_hz;
        request.amplitude = amplitude;
        request.duration_ms = duration_ms;
        vector<audiohaus::ToneOutputResult> native_results;
        const bool success = audiohaus::play_test_tone(request, native_results);
        if (native_results.size() != endpoint_count) {
            return kAudioHausInternalError;
        }
        for (uint32_t index = 0; index < endpoint_count; ++index) {
            const auto& result = native_results[index];
            results[index] = {
                result.sample_rate, result.channels, result.frames_submitted,
                result.start_qpc, result.stop_qpc, result.hresult, result.completed
            };
        }
        return success ? kAudioHausSuccess : kAudioHausPlaybackFailed;
    } catch (const bad_alloc&) {
        return kAudioHausOutOfMemory;
    } catch (...) {
        return kAudioHausInternalError;
    }
}

/*
 * Name: audiohaus_open_file_player
 * Input: File path, output ID, gain, and output locations.
 * Process: Opens a stopped native player and transfers ownership on success.
 * Output: API status and Windows HRESULT; exceptions remain inside the DLL.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_open_file_player(
    const wchar_t* file_path, const wchar_t* endpoint_id, double gain,
    AudioHausFilePlayer* player, uint32_t* error_hresult) {
    if (player != nullptr) {
        *player = nullptr;
    }
    if (error_hresult != nullptr) {
        *error_hresult = static_cast<uint32_t>(E_INVALIDARG);
    }
    if (file_path == nullptr || file_path[0] == L'\0' || endpoint_id == nullptr
        || endpoint_id[0] == L'\0' || player == nullptr || error_hresult == nullptr
        || !isfinite(gain) || gain < 0.0 || gain > 1.0) {
        return kAudioHausInvalidArgument;
    }

    try {
        auto owned = make_unique<AudioHausFilePlayerOpaque>();
        const HRESULT result = audiohaus::FilePlayer::open(file_path, endpoint_id, gain, owned->player);
        *error_hresult = static_cast<uint32_t>(result);
        if (FAILED(result)) {
            return result == E_OUTOFMEMORY ? kAudioHausOutOfMemory : kAudioHausPlaybackFailed;
        }
        if (!owned->player) {
            *error_hresult = static_cast<uint32_t>(E_UNEXPECTED);
            return kAudioHausInternalError;
        }
        *player = owned.release();
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
 * Name: audiohaus_file_player_command
 * Input: Player handle, transport command, optional seek position, and HRESULT output.
 * Process: Dispatches to the player's independent control worker.
 * Output: Status and HRESULT, including failures without releasing the handle.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_file_player_command(
    AudioHausFilePlayer player, uint32_t command, uint64_t position_100ns, uint32_t* error_hresult) {
    if (error_hresult != nullptr) {
        *error_hresult = static_cast<uint32_t>(E_INVALIDARG);
    }
    if (player == nullptr || error_hresult == nullptr || command > kAudioHausFileSeek
        || (command == kAudioHausFileSeek && position_100ns > static_cast<uint64_t>((numeric_limits<int64_t>::max)()))) {
        return kAudioHausInvalidArgument;
    }
    try {
        HRESULT result = E_INVALIDARG;
        switch (command) {
        case kAudioHausFilePlay: result = player->player->play(); break;
        case kAudioHausFilePause: result = player->player->pause(); break;
        case kAudioHausFileStop: result = player->player->stop(); break;
        case kAudioHausFileSeek: result = player->player->seek(position_100ns); break;
        }
        *error_hresult = static_cast<uint32_t>(result);
        return FAILED(result)
            ? (result == E_OUTOFMEMORY ? kAudioHausOutOfMemory : kAudioHausPlaybackFailed)
            : kAudioHausSuccess;
    } catch (const bad_alloc&) {
        *error_hresult = static_cast<uint32_t>(E_OUTOFMEMORY);
        return kAudioHausOutOfMemory;
    } catch (...) {
        *error_hresult = static_cast<uint32_t>(E_FAIL);
        return kAudioHausInternalError;
    }
}

/*
 * Name: audiohaus_get_file_player_status
 * Input: Player handle and snapshot output.
 * Process: Obtains the latest transport snapshot from the player's worker.
 * Output: API status and snapshot; invalid arguments clear valid snapshot storage.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_get_file_player_status(
    AudioHausFilePlayer player, AudioHausFilePlayerInfo* status) {
    if (status != nullptr) {
        *status = {};
    }
    if (player == nullptr || status == nullptr) {
        return kAudioHausInvalidArgument;
    }
    try {
        audiohaus::FilePlayerStatus native;
        const HRESULT result = player->player->get_status(native);
        if (FAILED(result)) {
            status->hresult = static_cast<uint32_t>(result);
            return result == E_OUTOFMEMORY ? kAudioHausOutOfMemory : kAudioHausPlaybackFailed;
        }
        *status = { native.state, native.hresult, native.sample_rate, native.channels,
                    native.position_100ns, native.duration_100ns, native.frames_submitted,
                    native.start_qpc, native.stop_qpc };
        return kAudioHausSuccess;
    } catch (const bad_alloc&) {
        status->hresult = static_cast<uint32_t>(E_OUTOFMEMORY);
        return kAudioHausOutOfMemory;
    } catch (...) {
        status->hresult = static_cast<uint32_t>(E_FAIL);
        return kAudioHausInternalError;
    }
}

/*
 * Name: audiohaus_close_file_player
 * Input: Live player without callers, or null.
 * Process: Destroys its worker and all owned native audio resources.
 * Output: None; the handle becomes invalid.
 */
void AUDIOHAUS_CALL audiohaus_close_file_player(AudioHausFilePlayer player) {
    delete player;
}

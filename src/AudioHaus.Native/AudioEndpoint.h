#pragma once

#include <string>

namespace audiohaus {

using namespace std;

/*
 * Name: AudioEndpointState
 * Input: Endpoint state reported by Windows during discovery.
 * Process: Classifies endpoint availability; unknown means not yet queried.
 * Output: A named endpoint state.
 */
enum class AudioEndpointState {
    kUnknown,
    kActive,
    kDisabled,
    kNotPresent,
    kUnplugged
};

/*
 * Name: AudioEndpoint
 * Input: Opaque Windows ID, non-unique display name, state, and default status.
 * Process: Stores a discovery snapshot; default status refers to multimedia audio.
 * Output: Endpoint information for output selection, refreshed during discovery.
 */
struct AudioEndpoint {
    wstring id;
    wstring display_name;
    AudioEndpointState state = AudioEndpointState::kUnknown;
    bool is_default_multimedia = false;
};

}

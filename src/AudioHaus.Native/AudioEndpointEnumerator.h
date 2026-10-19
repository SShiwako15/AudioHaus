#pragma once

#include "AudioEndpoint.h"

#include <vector>

namespace audiohaus {

using namespace std;

/*
 * Name: AudioEndpointEnumerator
 * Input: None.
 * Process: Discovers Windows playback endpoints without retaining device handles.
 * Output: Endpoint snapshots for output selection.
 */
class AudioEndpointEnumerator {
public:
    /*
     * Name: enumerate
     * Input: None; COM initialization is handled internally for the calling thread.
     * Process: Queries all playback endpoint states and the multimedia default.
     * Output: Endpoint snapshots, or an empty vector when none exist. Throws
     *         std::runtime_error on discovery failure; no default is not an error.
     */
    vector<AudioEndpoint> enumerate() const;
};

}

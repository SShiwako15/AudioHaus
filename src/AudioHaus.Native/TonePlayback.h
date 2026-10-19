#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace audiohaus {

using namespace std;

/*
 * Name: TonePlaybackRequest
 * Input: One or two distinct playback endpoint IDs and tone settings.
 * Process: Describes a bounded shared-mode playback probe.
 * Output: Settings consumed by play_test_tone.
 */
struct TonePlaybackRequest {
    vector<wstring> endpoint_ids;
    double frequency_hz = 440.0;
    double amplitude = 0.05;
    uint32_t duration_ms = 3000;
};

/*
 * Name: ToneOutputResult
 * Input: Playback operations for one requested endpoint.
 * Process: Records submitted frames, HRESULT, and successful stream boundaries.
 * Output: Per-endpoint evidence; QPC times describe WASAPI calls, not audibility.
 */
struct ToneOutputResult {
    uint32_t sample_rate = 48000;
    uint32_t channels = 2;
    uint64_t frames_submitted = 0;
    uint64_t start_qpc = 0;
    uint64_t stop_qpc = 0;
    uint32_t hresult = 0;
    uint32_t completed = 0;
};

/*
 * Name: play_test_tone
 * Input: Endpoint IDs, 20-20000 Hz, amplitude 0-0.25, and duration 1-60000 ms.
 * Process: Prepares all outputs, plays on one MTA worker, and drains each stream.
 * Output: True only when all outputs finish; results retain input order. Runtime
 *         failures return HRESULTs; allocation and thread failures may throw.
 */
bool play_test_tone(const TonePlaybackRequest& request, vector<ToneOutputResult>& results);

}

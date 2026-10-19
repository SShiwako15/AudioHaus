#pragma once

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace audiohaus {

using namespace std;

/*
 * Name: MixingBlockMetrics
 * Input: One completed stereo mix block.
 * Process: Describes clipping and energy after final sample clamping.
 * Output: Additive counters and the block's absolute peak.
 */
struct MixingBlockMetrics {
    uint64_t non_silent_frames = 0;
    uint64_t clipped_samples = 0;
    double sum_squares = 0.0;
    double peak = 0.0;
};

/*
 * Name: accumulate_pcm
 * Input: Writable mix, normalized PCM, sample count, and route gain.
 * Process: Adds the source samples without intermediate clipping.
 * Output: Updated mix suitable for further source accumulation.
 */
inline void accumulate_pcm(float* mix, const float* source, size_t sample_count,
                           double gain) noexcept {
    const float scalar = static_cast<float>(gain);
    for (size_t index = 0; index < sample_count; ++index) {
        mix[index] += source[index] * scalar;
    }
}

/*
 * Name: finalize_pcm
 * Input: An interleaved stereo mix and frame count.
 * Process: Sanitizes nonfinite samples, clamps final peaks, and measures submitted PCM.
 * Output: In-place bounded PCM plus energy, clipping, non-silent-frame, and peak metrics.
 */
inline MixingBlockMetrics finalize_pcm(float* mix, uint32_t frame_count) noexcept {
    MixingBlockMetrics metrics;
    for (uint32_t frame = 0; frame < frame_count; ++frame) {
        bool non_silent = false;
        for (uint32_t channel = 0; channel < 2; ++channel) {
            float& sample = mix[static_cast<size_t>(frame) * 2 + channel];
            if (!isfinite(sample)) { sample = 0.0f; }
            if (sample < -1.0f || sample > 1.0f) { ++metrics.clipped_samples; }
            sample = clamp(sample, -1.0f, 1.0f);
            const double value = sample;
            const double magnitude = abs(value);
            metrics.peak = max(metrics.peak, magnitude);
            metrics.sum_squares += value * value;
            non_silent = non_silent || magnitude > 1.0e-9;
        }
        if (non_silent) { ++metrics.non_silent_frames; }
    }
    return metrics;
}

}

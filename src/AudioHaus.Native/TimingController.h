#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace audiohaus {
using namespace std;

constexpr double kMaximumDriftPpm = 2000.0;
constexpr uint32_t kMaximumDelayFrames = 96000;
constexpr uint32_t kTimingSampleRate = 48000;

/*
 * Name: live_target_frames
 * Input: Initialized output capacity and validated additional delay frames.
 * Process: Reserves a complete output block plus interpolation lookahead, or 50 ms.
 * Output: Required source history behind the live capture head.
 */
constexpr uint32_t live_target_frames(uint32_t capacity, uint32_t delay) noexcept {
    return max(2400u, capacity + 2) + delay;
}

/*
 * Name: live_history_capacity
 * Input: Largest live reader target among a source's destinations.
 * Process: Adds 200 ms safety history within the allocated four-second ring.
 * Output: Retained history; ordinary zero-delay endpoints retain 250 ms.
 */
constexpr uint32_t live_history_capacity(uint32_t target) noexcept {
    return min(192000u, max(12000u, target + 9600));
}

/*
 * Name: DriftController
 * Input: Source/read-position phase error and elapsed output frames.
 * Process: Filters packet jitter and applies a bounded, slew-limited PI correction.
 * Output: Fractional input frames per output frame; no allocation or frame dropping.
 */
class DriftController {
public:
    /*
     * Name: reset
     * Input: None.
     * Process: Clears rate estimation after transport, configuration, or recovery.
     * Output: Unity rate and zero retained controller error.
     */
    void reset() noexcept { filtered_error_ = 0; integral_ = 0; correction_ppm_ = 0; }

    /*
     * Name: update
     * Input: Desired minus actual read position, output-frame count, and enable flag.
     * Process: Rejects nonfinite input, filters jitter, and limits correction to 2000 ppm.
     * Output: Positive resampling ratio; disabled correction resets to exactly one.
     */
    double update(double error_frames, uint32_t output_frames, bool enabled = true) noexcept {
        if (!enabled || !isfinite(error_frames)) { reset(); return 1.0; }
        const double elapsed = min(0.25, static_cast<double>(output_frames) / kTimingSampleRate);
        if (elapsed <= 0) { return 1.0 + correction_ppm_ * 1.0e-6; }
        filtered_error_ += (clamp(error_frames, -4800.0, 4800.0) - filtered_error_) * elapsed / (0.5 + elapsed);
        integral_ = clamp(integral_ + filtered_error_ * elapsed * 2.0, -kMaximumDriftPpm, kMaximumDriftPpm);
        const double target = clamp(filtered_error_ * 10.0 + integral_, -kMaximumDriftPpm, kMaximumDriftPpm);
        correction_ppm_ += clamp(target - correction_ppm_, -1000.0 * elapsed, 1000.0 * elapsed);
        return 1.0 + correction_ppm_ * 1.0e-6;
    }

    /*
     * Name: correction_ppm
     * Input: None.
     * Process: Reads the current rate adjustment.
     * Output: Signed parts per million; positive consumes source PCM faster.
     */
    double correction_ppm() const noexcept { return correction_ppm_; }
private:
    double filtered_error_ = 0, integral_ = 0, correction_ppm_ = 0;
};

/*
 * Name: interpolate_sample
 * Input: Adjacent finite PCM samples and a fractional position from zero through one.
 * Process: Performs linear interpolation for smooth fractional-rate consumption.
 * Output: Interpolated sample; summing and final clipping happen in the mixer.
 */
inline float interpolate_sample(float first, float second, double fraction) noexcept {
    return static_cast<float>(first + (second - first) * fraction);
}

/*
 * Name: recovery_backoff_ms
 * Input: Number of consecutive unsuccessful recovery attempts.
 * Process: Bounds exponential retry delay while retaining indefinite automatic recovery.
 * Output: 250, 500, 1000, 2000, or 4000 milliseconds.
 */
inline uint32_t recovery_backoff_ms(uint32_t failures) noexcept {
    return 250u << min(failures, 4u);
}
}

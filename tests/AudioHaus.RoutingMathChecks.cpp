#include "../src/AudioHaus.Native/MixingMath.h"

#include <array>
#include <cmath>
#include <iostream>
#include <limits>
#include <stdexcept>

namespace {

/*
 * Name: require
 * Input: Assertion condition and failure description.
 * Process: Keeps assertions active in both Debug and Release builds.
 * Output: Returns on success or throws a diagnostic exception.
 */
void require(bool condition, const char* description) {
    if (!condition) { throw std::runtime_error(description); }
}

/*
 * Name: near
 * Input: Actual and expected floating-point values.
 * Process: Compares deterministic mixing results with a small rounding tolerance.
 * Output: True within tolerance.
 */
bool near(double actual, double expected) noexcept {
    return std::abs(actual - expected) < 1.0e-6;
}

/*
 * Name: check_weighted_sum
 * Input: Two known stereo PCM blocks.
 * Process: Uses production accumulation/finalization and checks exact expected energy.
 * Output: Returns on correct gain/summing/metrics or throws; no output device is used.
 */
void check_weighted_sum() {
    std::array<float, 4> mix{};
    const std::array<float, 4> first = {0.25f, -0.5f, 0.75f, -1.0f};
    const std::array<float, 4> second = {0.5f, 0.25f, -0.25f, 0.5f};
    audiohaus::accumulate_pcm(mix.data(), first.data(), mix.size(), 0.5);
    audiohaus::accumulate_pcm(mix.data(), second.data(), mix.size(), 0.25);
    const std::array<double, 4> expected = {0.25, -0.1875, 0.3125, -0.375};
    for (size_t index = 0; index < mix.size(); ++index) {
        require(near(mix[index], expected[index]), "Weighted source sum is incorrect.");
    }
    const auto metrics = audiohaus::finalize_pcm(mix.data(), 2);
    require(metrics.non_silent_frames == 2 && metrics.clipped_samples == 0, "Weighted metrics have wrong frame/clip counts.");
    require(near(metrics.sum_squares, 0.3359375) && near(metrics.peak, 0.375), "Weighted energy or peak is incorrect.");
    const auto before = mix;
    audiohaus::accumulate_pcm(mix.data(), first.data(), mix.size(), 0);
    require(mix == before, "Zero gain changed existing mixed PCM.");
}

/*
 * Name: check_final_clipping
 * Input: Over-range, cancelling, and nonfinite samples.
 * Process: Verifies clipping only after summing and sanitization before measurement.
 * Output: Returns for bounded PCM and exact metrics or throws.
 */
void check_final_clipping() {
    std::array<float, 2> mix{};
    const std::array<float, 2> source = {0.8f, -0.8f};
    const std::array<float, 2> cancellation = {-0.8f, 0.8f};
    audiohaus::accumulate_pcm(mix.data(), source.data(), mix.size(), 1);
    audiohaus::accumulate_pcm(mix.data(), source.data(), mix.size(), 1);
    require(near(mix[0], 1.6) && near(mix[1], -1.6), "Sources clipped before all routes were summed.");
    audiohaus::accumulate_pcm(mix.data(), cancellation.data(), mix.size(), 1);
    const auto cancelled = audiohaus::finalize_pcm(mix.data(), 1);
    require(near(mix[0], 0.8) && near(mix[1], -0.8) && cancelled.clipped_samples == 0, "Cancellation was distorted by intermediate clipping.");

    std::array<float, 6> extremes = {1.5f, -2.0f, std::numeric_limits<float>::quiet_NaN(),
        std::numeric_limits<float>::infinity(), -std::numeric_limits<float>::infinity(), 0.5f};
    const auto clipped = audiohaus::finalize_pcm(extremes.data(), 3);
    require(extremes == std::array<float, 6>{1, -1, 0, 0, 0, 0.5f}, "Final PCM was not clamped/sanitized correctly.");
    require(clipped.non_silent_frames == 2 && clipped.clipped_samples == 2, "Clipped/nonfinite metrics are incorrect.");
    require(near(clipped.sum_squares, 2.25) && near(clipped.peak, 1), "Clipped PCM energy or peak is incorrect.");
    std::array<float, 4> silence{};
    const auto silent = audiohaus::finalize_pcm(silence.data(), 2);
    require(silent.non_silent_frames == 0 && silent.clipped_samples == 0 && silent.sum_squares == 0 && silent.peak == 0,
        "Silent mix has nonzero metrics.");
    const auto empty = audiohaus::finalize_pcm(nullptr, 0);
    require(empty.non_silent_frames == 0 && empty.sum_squares == 0, "Empty block produced PCM metrics.");
}

}

/*
 * Name: main
 * Input: None.
 * Process: Runs device-independent checks against production mixer math.
 * Output: Exit code 0 for success or 1 with a failure description.
 */
int main() {
    try {
        check_weighted_sum();
        check_final_clipping();
        std::cout << "PASS: production mixing math, gains, zero gain, summing, final clipping, nonfinite sanitization, and metrics.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}

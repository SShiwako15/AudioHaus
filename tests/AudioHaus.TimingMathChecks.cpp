#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "../src/AudioHaus.Native/TimingController.h"
#include "../src/AudioHaus.Native/AudioRecovery.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
#include <stdexcept>

using namespace std;

/*
 * Name: require
 * Input: Assertion and failure description.
 * Process: Keeps checks active in both Debug and optimized builds.
 * Output: Returns on success or throws a diagnostic exception.
 */
void require(bool condition, const char* message) { if (!condition) { throw runtime_error(message); } }

/*
 * Name: check_clock_skew
 * Input: Physical consumer clock skew in parts per million.
 * Process: Simulates five minutes of independent source/output clocks in 10 ms blocks.
 * Output: Requires bounded phase/rate and convergence within two frames without resets.
 */
void check_clock_skew(double skew_ppm) {
    audiohaus::DriftController controller;
    double error = 0;
    const double consumer_rate = 1.0 + skew_ppm * 1.0e-6;
    for (uint32_t block = 0; block < 30000; ++block) {
        const double ratio = controller.update(error, 480);
        require(isfinite(ratio) && ratio >= 0.998 && ratio <= 1.002, "Resampling ratio escaped its correction bound.");
        error += 480.0 / consumer_rate - 480.0 * ratio;
        require(abs(error) < 200, "Clock phase became unbounded.");
        if (block > 10000) { require(abs(error) < 2, "Drift correction did not converge to source time."); }
    }
    const double expected = (1.0 / consumer_rate - 1.0) * 1.0e6;
    require(abs(controller.correction_ppm() - expected) < 2, "Controller rate does not cancel clock skew.");
    cout << "PASS: " << skew_ppm << " ppm consumer clock; correction " << controller.correction_ppm()
        << " ppm, final phase " << error << " frames.\n";
}

/*
 * Name: check_jitter_and_change
 * Input: None.
 * Process: Simulates packet-timing noise, variable output blocks, and a clock change.
 * Output: Bounded phase and slew across ten simulated minutes without reader resets.
 */
void check_jitter_and_change() {
    audiohaus::DriftController controller;
    double error = 0, elapsed = 0, previous = 0;
    uint32_t block = 0;
    while (elapsed < 600) {
        const uint32_t frames = block % 2 == 0 ? 240u : 720u;
        const double seconds = static_cast<double>(frames) / 48000;
        const double skew = elapsed < 300 ? 700.0 : -900.0;
        const double jitter = 120.0 * sin(elapsed * 2 * 3.141592653589793 * 17);
        const double ratio = controller.update(error + jitter, frames);
        require(abs(controller.correction_ppm() - previous) <= seconds * 1000 + 1.0e-6, "Correction was not slew limited.");
        previous = controller.correction_ppm();
        error += static_cast<double>(frames) / (1 + skew * 1.0e-6) - frames * ratio;
        require(abs(error) < 300, "Variable-block jitter or clock change destabilized phase.");
        elapsed += seconds; ++block;
    }
    require(abs(error) < 10, "Jittered controller failed to converge after clock change.");
    cout << "PASS: ten-minute packet-jitter, variable-block, clock-change, and slew simulation; phase " << error << " frames.\n";
}

/*
 * Name: main
 * Input: None.
 * Process: Exercises production correction, interpolation, resets, limits, and backoff.
 * Output: Exit zero for deterministic timing checks; no audio hardware is opened.
 */
int main() {
    try {
        for (double skew : array<double, 4>{-1000, -300, 300, 1000}) { check_clock_skew(skew); }
        check_jitter_and_change();
        audiohaus::DriftController controller;
        for (uint32_t block = 0; block < 10000; ++block) { controller.update(1000000, 480); }
        require(controller.correction_ppm() <= 2000, "Positive saturation failed.");
        require(controller.update(0, 480, false) == 1 && controller.correction_ppm() == 0, "Disabling correction did not reset it.");
        require(controller.update(numeric_limits<double>::quiet_NaN(), 480) == 1, "Nonfinite phase was not contained.");
        require(abs(audiohaus::interpolate_sample(-0.5f, 0.5f, 0.25) + 0.25f) < 1.0e-6f, "Fractional interpolation is wrong.");
        require(audiohaus::recovery_backoff_ms(0) == 250 && audiohaus::recovery_backoff_ms(1) == 500 &&
            audiohaus::recovery_backoff_ms(100) == 4000, "Recovery backoff is not bounded.");
        require(audiohaus::live_target_frames(512, 0) == 2400 && audiohaus::live_history_capacity(2400) == 12000,
            "Default live buffering changed.");
        const uint32_t large_target = audiohaus::live_target_frames(48000, 96000);
        require(large_target == 144002 && audiohaus::live_history_capacity(large_target) >= large_target + 9600 &&
            audiohaus::live_history_capacity(large_target) <= 192000, "Large-device delay target cannot prime inside retained history.");
        require(audiohaus::is_recoverable_audio_error(AUDCLNT_E_DEVICE_INVALIDATED) &&
            audiohaus::is_recoverable_audio_error(HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED)) &&
            !audiohaus::is_recoverable_audio_error(E_ACCESSDENIED) && !audiohaus::is_recoverable_audio_error(E_OUTOFMEMORY) &&
            !audiohaus::is_recoverable_audio_error(HRESULT_FROM_WIN32(ERROR_INVALID_STATE)), "Fatal isolation/access failures were misclassified as recoverable.");
        cout << "PASS: correction bounds, disable/reset, nonfinite input, interpolation, and retry backoff.\n";
        return 0;
    } catch (const exception& error) { cerr << "FAIL: " << error.what() << '\n'; return 1; }
}

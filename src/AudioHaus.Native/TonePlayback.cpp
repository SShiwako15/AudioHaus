#include "TonePlayback.h"

#include <Windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <exception>
#include <memory>
#include <numbers>
#include <thread>

namespace audiohaus {
namespace {

using Microsoft::WRL::ComPtr;

constexpr uint32_t kSampleRate = 48000;
constexpr uint32_t kChannels = 2;
constexpr uint64_t kFadeFrames = 480;
constexpr DWORD kEventTimeoutMs = 2000;
constexpr uint64_t kCompletionMarginMs = 5000;
constexpr double kTwoPi = 2.0 * numbers::pi;

/*
 * Name: qpc_now
 * Input: None.
 * Process: Reads the Windows performance counter.
 * Output: Monotonic QPC ticks, without conversion to audio or wall-clock time.
 */
uint64_t qpc_now() noexcept {
    LARGE_INTEGER counter{};
    QueryPerformanceCounter(&counter);
    return static_cast<uint64_t>(counter.QuadPart);
}

/*
 * Name: MtaInitialization
 * Input: A dedicated worker thread.
 * Process: Balances multithreaded COM initialization on that thread.
 * Output: A scoped COM apartment and its initialization result.
 */
class MtaInitialization {
public:
    /*
     * Name: MtaInitialization
     * Input: None.
     * Process: Initializes COM for the playback worker.
     * Output: A scope guard containing the initialization HRESULT.
     */
    MtaInitialization() noexcept : result_(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {}

    /*
     * Name: ~MtaInitialization
     * Input: None.
     * Process: Releases this thread's successful COM initialization.
     * Output: Balanced COM lifetime without exceptions.
     */
    ~MtaInitialization() {
        if (SUCCEEDED(result_)) {
            CoUninitialize();
        }
    }

    MtaInitialization(const MtaInitialization&) = delete;
    MtaInitialization& operator=(const MtaInitialization&) = delete;

    /*
     * Name: result
     * Input: None.
     * Process: Reads the initialization status.
     * Output: The HRESULT returned by CoInitializeEx.
     */
    HRESULT result() const noexcept { return result_; }

private:
    HRESULT result_;
};

/*
 * Name: ToneOutput
 * Input: A result record owned by the playback invocation.
 * Process: Owns one event-driven renderer and its phase-continuous tone state.
 * Output: Bounded shared-mode playback with deterministic resource cleanup.
 */
class ToneOutput {
public:
    /*
     * Name: ToneOutput
     * Input: An output result record.
     * Process: Attaches the record without opening audio resources.
     * Output: An inactive output owned by the MTA worker.
     */
    explicit ToneOutput(ToneOutputResult& result) noexcept : result_(result) {}

    /*
     * Name: ~ToneOutput
     * Input: None.
     * Process: Stops any running stream before releasing COM objects and its event.
     * Output: Released resources, including when another output fails.
     */
    ~ToneOutput() {
        if (started_) {
            stop(false);
        }
        render_.Reset();
        client_.Reset();
        if (event_ != nullptr) {
            CloseHandle(event_);
        }
    }

    ToneOutput(const ToneOutput&) = delete;
    ToneOutput& operator=(const ToneOutput&) = delete;

    /*
     * Name: prepare
     * Input: A device enumerator and selected endpoint ID.
     * Process: Verifies an active render device and initializes stereo float Media audio.
     * Output: S_OK or the failing Windows HRESULT; no stream starts here.
     */
    HRESULT prepare(IMMDeviceEnumerator& enumerator, const wstring& endpoint_id) {
        ComPtr<IMMDevice> device;
        HRESULT result = enumerator.GetDevice(endpoint_id.c_str(), device.GetAddressOf());
        if (FAILED(result)) {
            return fail(result);
        }

        ComPtr<IMMEndpoint> endpoint;
        result = device.As(&endpoint);
        if (FAILED(result)) {
            return fail(result);
        }
        EDataFlow flow = eAll;
        result = endpoint->GetDataFlow(&flow);
        if (FAILED(result)) {
            return fail(result);
        }
        if (flow != eRender) {
            return fail(AUDCLNT_E_WRONG_ENDPOINT_TYPE);
        }
        DWORD state = 0;
        result = device->GetState(&state);
        if (FAILED(result)) {
            return fail(result);
        }
        if (state != DEVICE_STATE_ACTIVE) {
            return fail(AUDCLNT_E_DEVICE_INVALIDATED);
        }

        result = device->Activate(__uuidof(IAudioClient2), CLSCTX_ALL, nullptr,
                                  reinterpret_cast<void**>(client_.GetAddressOf()));
        if (FAILED(result)) {
            return fail(result);
        }

        AudioClientProperties properties{};
        properties.cbSize = sizeof(properties);
        properties.eCategory = AudioCategory_Media;
        result = client_->SetClientProperties(&properties);
        if (FAILED(result)) {
            return fail(result);
        }

        event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (event_ == nullptr) {
            return fail(HRESULT_FROM_WIN32(GetLastError()));
        }

        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
        format.nChannels = static_cast<WORD>(kChannels);
        format.nSamplesPerSec = kSampleRate;
        format.wBitsPerSample = 32;
        format.nBlockAlign = static_cast<WORD>(kChannels * sizeof(float));
        format.nAvgBytesPerSec = kSampleRate * format.nBlockAlign;
        const DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                            AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        result = client_->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, &format, nullptr);
        if (FAILED(result)) {
            return fail(result);
        }
        result = client_->SetEventHandle(event_);
        if (FAILED(result)) {
            return fail(result);
        }
        result = client_->GetBufferSize(&buffer_frames_);
        if (FAILED(result)) {
            return fail(result);
        }
        if (buffer_frames_ == 0) {
            return fail(E_UNEXPECTED);
        }
        result = client_->GetService(IID_PPV_ARGS(render_.GetAddressOf()));
        return FAILED(result) ? fail(result) : S_OK;
    }

    /*
     * Name: prime
     * Input: Tone settings and total requested frames.
     * Process: Queues the first packet before any endpoint starts.
     * Output: S_OK or the failing render HRESULT.
     */
    HRESULT prime(const TonePlaybackRequest& request, uint64_t total_frames) noexcept {
        return write_frames(request, total_frames,
                            static_cast<UINT32>(min<uint64_t>(buffer_frames_, total_frames)));
    }

    /*
     * Name: start
     * Input: A prepared and primed stream.
     * Process: Starts WASAPI and timestamps its successful return.
     * Output: S_OK or the failing HRESULT; stream ownership remains scoped.
     */
    HRESULT start() noexcept {
        const HRESULT result = client_->Start();
        if (FAILED(result)) {
            return fail(result);
        }
        started_ = true;
        result_.start_qpc = qpc_now();
        return S_OK;
    }

    /*
     * Name: service
     * Input: Tone settings and total requested frames after a render notification.
     * Process: Fills available space or stops after the final queued frame drains.
     * Output: S_OK or the failing HRESULT; completion is set only after a clean stop.
     */
    HRESULT service(const TonePlaybackRequest& request, uint64_t total_frames) noexcept {
        UINT32 padding = 0;
        HRESULT result = client_->GetCurrentPadding(&padding);
        if (FAILED(result)) {
            return fail(result);
        }
        if (padding > buffer_frames_) {
            return fail(E_UNEXPECTED);
        }
        if (result_.frames_submitted == total_frames && padding == 0) {
            return stop(true);
        }
        const uint64_t remaining = total_frames - result_.frames_submitted;
        const UINT32 available = buffer_frames_ - padding;
        const UINT32 frames = static_cast<UINT32>(min<uint64_t>(remaining, available));
        return frames == 0 ? S_OK : write_frames(request, total_frames, frames);
    }

    /*
     * Name: event_handle
     * Input: None.
     * Process: Exposes the renderer's notification event without transferring ownership.
     * Output: A valid event handle after prepare succeeds.
     */
    HANDLE event_handle() const noexcept { return event_; }

    /*
     * Name: completed
     * Input: None.
     * Process: Reads the output's drain and stop status.
     * Output: True only after all requested frames drain and Stop succeeds.
     */
    bool completed() const noexcept { return result_.completed != 0; }

    /*
     * Name: fail
     * Input: A failing HRESULT.
     * Process: Records the actual output failure.
     * Output: The unchanged HRESULT for propagation to the coordinator.
     */
    HRESULT fail(HRESULT result) noexcept {
        if (result_.hresult == static_cast<uint32_t>(E_ABORT) ||
            SUCCEEDED(static_cast<HRESULT>(result_.hresult))) {
            result_.hresult = static_cast<uint32_t>(result);
        }
        result_.completed = 0;
        return result;
    }

private:
    /*
     * Name: write_frames
     * Input: Tone settings, total requested frames, and a nonzero packet length.
     * Process: Writes equal stereo samples with continuous phase and short endpoint fades.
     * Output: S_OK with submitted-frame accounting, or a render HRESULT.
     */
    HRESULT write_frames(const TonePlaybackRequest& request, uint64_t total_frames,
                         UINT32 frames) noexcept {
        BYTE* bytes = nullptr;
        HRESULT result = render_->GetBuffer(frames, &bytes);
        if (FAILED(result)) {
            return fail(result);
        }

        float* samples = reinterpret_cast<float*>(bytes);
        const double increment = kTwoPi * request.frequency_hz / kSampleRate;
        const uint64_t fade_frames = max<uint64_t>(1, min(kFadeFrames, total_frames / 2));
        for (UINT32 index = 0; index < frames; ++index) {
            const uint64_t frame = result_.frames_submitted + index;
            const double fade_in = static_cast<double>(frame) / static_cast<double>(fade_frames);
            const double fade_out = static_cast<double>(total_frames - frame - 1) /
                                    static_cast<double>(fade_frames);
            const double envelope = min({1.0, fade_in, fade_out});
            const float sample = static_cast<float>(request.amplitude * envelope * sin(phase_));
            samples[index * kChannels] = sample;
            samples[index * kChannels + 1] = sample;
            phase_ += increment;
            if (phase_ >= kTwoPi) {
                phase_ -= kTwoPi;
            }
        }

        result = render_->ReleaseBuffer(frames, 0);
        if (FAILED(result)) {
            return fail(result);
        }
        result_.frames_submitted += frames;
        return S_OK;
    }

    /*
     * Name: stop
     * Input: Whether all requested frames have drained.
     * Process: Timestamps immediately before Stop and records successful completion.
     * Output: S_OK or Stop's HRESULT; aborted outputs retain their failure status.
     */
    HRESULT stop(bool drained) noexcept {
        const uint64_t stop_qpc = qpc_now();
        const HRESULT result = client_->Stop();
        if (FAILED(result)) {
            return fail(result);
        }
        started_ = false;
        result_.stop_qpc = stop_qpc;
        if (drained) {
            result_.hresult = static_cast<uint32_t>(S_OK);
            result_.completed = 1;
        }
        return S_OK;
    }

    ToneOutputResult& result_;
    ComPtr<IAudioClient2> client_;
    ComPtr<IAudioRenderClient> render_;
    HANDLE event_ = nullptr;
    UINT32 buffer_frames_ = 0;
    bool started_ = false;
    double phase_ = 0.0;
};

/*
 * Name: play_on_worker
 * Input: Validated tone settings and preallocated result records.
 * Process: Prepares all outputs, starts them together, and services render events.
 * Output: True only if every output drains; failed or aborted outputs retain HRESULTs.
 */
bool play_on_worker(const TonePlaybackRequest& request, vector<ToneOutputResult>& results) {
    const MtaInitialization com;
    if (FAILED(com.result())) {
        for (auto& result : results) {
            result.hresult = static_cast<uint32_t>(com.result());
        }
        return false;
    }

    ComPtr<IMMDeviceEnumerator> enumerator;
    const HRESULT creation_result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(enumerator.GetAddressOf()));
    if (FAILED(creation_result)) {
        for (auto& result : results) {
            result.hresult = static_cast<uint32_t>(creation_result);
        }
        return false;
    }

    array<unique_ptr<ToneOutput>, 2> outputs;
    const size_t count = request.endpoint_ids.size();
    const uint64_t total_frames = static_cast<uint64_t>(kSampleRate) * request.duration_ms / 1000;
    for (size_t index = 0; index < count; ++index) {
        outputs[index] = make_unique<ToneOutput>(results[index]);
        if (FAILED(outputs[index]->prepare(*enumerator.Get(), request.endpoint_ids[index])) ||
            FAILED(outputs[index]->prime(request, total_frames))) {
            return false;
        }
    }
    for (size_t index = 0; index < count; ++index) {
        if (FAILED(outputs[index]->start())) {
            return false;
        }
    }

    const uint64_t deadline = GetTickCount64() + request.duration_ms + kCompletionMarginMs;
    while (true) {
        array<HANDLE, 2> events{};
        DWORD event_count = 0;
        for (size_t index = 0; index < count; ++index) {
            if (!outputs[index]->completed()) {
                events[event_count++] = outputs[index]->event_handle();
            }
        }
        if (event_count == 0) {
            return true;
        }

        const DWORD wait_result = WaitForMultipleObjects(event_count, events.data(), FALSE,
                                                         kEventTimeoutMs);
        if (wait_result == WAIT_FAILED || wait_result == WAIT_TIMEOUT ||
            GetTickCount64() > deadline) {
            const HRESULT failure = wait_result == WAIT_FAILED
                ? HRESULT_FROM_WIN32(GetLastError())
                : HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            for (size_t index = 0; index < count; ++index) {
                if (!outputs[index]->completed()) {
                    outputs[index]->fail(failure);
                }
            }
            return false;
        }

        for (size_t index = 0; index < count; ++index) {
            if (!outputs[index]->completed() &&
                FAILED(outputs[index]->service(request, total_frames))) {
                return false;
            }
        }
    }
}

}

/*
 * Name: play_test_tone
 * Input: One or two distinct endpoint IDs and bounded tone settings.
 * Process: Initializes results and joins a dedicated MTA worker after playback ends.
 * Output: Per-output results and overall success; setup exceptions propagate to the caller.
 */
bool play_test_tone(const TonePlaybackRequest& request, vector<ToneOutputResult>& results) {
    results.assign(request.endpoint_ids.size(), ToneOutputResult{});
    const bool valid = !request.endpoint_ids.empty() && request.endpoint_ids.size() <= 2 &&
        isfinite(request.frequency_hz) && request.frequency_hz >= 20.0 &&
        request.frequency_hz <= 20000.0 && isfinite(request.amplitude) &&
        request.amplitude >= 0.0 && request.amplitude <= 0.25 &&
        request.duration_ms >= 1 && request.duration_ms <= 60000 &&
        all_of(request.endpoint_ids.begin(), request.endpoint_ids.end(),
               [](const wstring& id) { return !id.empty() && id.find(L'\0') == wstring::npos; }) &&
        (request.endpoint_ids.size() != 2 || request.endpoint_ids[0] != request.endpoint_ids[1]);
    if (!valid) {
        for (auto& result : results) {
            result.hresult = static_cast<uint32_t>(E_INVALIDARG);
        }
        return false;
    }
    for (auto& result : results) {
        result.hresult = static_cast<uint32_t>(E_ABORT);
    }

    bool success = false;
    exception_ptr worker_exception;
    thread worker([&] {
        try {
            success = play_on_worker(request, results);
        } catch (...) {
            worker_exception = current_exception();
        }
    });
    worker.join();
    if (worker_exception) {
        rethrow_exception(worker_exception);
    }
    return success;
}

}

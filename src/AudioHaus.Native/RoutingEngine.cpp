#include "RoutingEngine.h"

#include "AudioDecoder.h"
#include "MixingMath.h"
#include "ProcessCapture.h"
#include "AudioSessionDiscovery.h"
#include "TimingController.h"
#include "PreparedOutput.h"
#include "AudioResourceTask.h"
#include "AudioRecovery.h"

#include <audioclient.h>
#include <mfapi.h>
#include <mferror.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <thread>
#include <utility>
#include <vector>

namespace audiohaus {
namespace {

using Microsoft::WRL::ComPtr;

constexpr size_t kMaxSources = 8;
constexpr size_t kMaxOutputs = 8;
constexpr size_t kMaxRoutes = kMaxSources * kMaxOutputs;
constexpr uint32_t kSampleRate = 48000;
constexpr uint32_t kChannels = 2;
constexpr uint32_t kFrameBytes = kChannels * sizeof(float);
constexpr uint64_t kTicksPerSecond = 10000000;
constexpr uint64_t kCacheFrames = kSampleRate * 4;
constexpr size_t kMaxNotifications = kMaxOutputs + kMaxSources;
constexpr uint32_t kDecodeChunkFrames = 4096;
constexpr DWORD kRenderTimeoutMs = 2000;
constexpr uint32_t kStopped = 0;
constexpr uint32_t kPlaying = 1;
constexpr uint32_t kPaused = 2;
constexpr uint32_t kEnded = 3;
constexpr uint32_t kFailed = 4;
constexpr uint32_t kOutputRecovering = 2;
constexpr uint32_t kSourceRecovering = 5;

/*
 * Name: qpc_now
 * Input: None.
 * Process: Reads the Windows performance counter.
 * Output: Monotonic ticks shared by all source clocks.
 */
uint64_t qpc_now() noexcept {
    LARGE_INTEGER counter{};
    QueryPerformanceCounter(&counter);
    return static_cast<uint64_t>(counter.QuadPart);
}

/*
 * Name: ticks_to_frames
 * Input: Media time and whether the final partial frame must be included.
 * Process: Converts 100-nanosecond units without overflowing intermediate products.
 * Output: A normalized PCM frame index or total frame count.
 */
uint64_t ticks_to_frames(uint64_t ticks, bool round_up = false) noexcept {
    return ticks / kTicksPerSecond * kSampleRate +
        (ticks % kTicksPerSecond * kSampleRate + (round_up ? kTicksPerSecond - 1 : 0)) /
            kTicksPerSecond;
}

/*
 * Name: frames_to_ticks
 * Input: A normalized PCM frame index.
 * Process: Converts its quotient and remainder separately.
 * Output: The corresponding 100-nanosecond position rounded down.
 */
uint64_t frames_to_ticks(uint64_t frames) noexcept {
    return frames / kSampleRate * kTicksPerSecond +
        frames % kSampleRate * kTicksPerSecond / kSampleRate;
}

/*
 * Name: CommandKind
 * Input: A routing or transport operation.
 * Process: Identifies the worker's acknowledged command.
 * Output: An internal operation selector.
 */
enum class CommandKind {
    kAddSource, kAddOutput, kRemoveSource, kRemoveOutput, kConnect, kDisconnect,
    kGain, kMute, kTransport, kSourceStatus, kOutputStatus, kRouteStatus,
    kAddProcess, kCaptureStatus, kDelay, kDrift, kOutputTiming, kRouteTiming,
    kCaptureTiming, kRetryOutput, kRetryCapture,
    kSourceLevel, kOutputLevel, kGetSourceLevel, kGetOutputLevel, kSourceSignal
};

/*
 * Name: Command
 * Input: Arguments and caller-owned output storage valid until acknowledgment.
 * Process: Transfers plain values and borrowed references to the worker.
 * Output: An allocation-free command record without COM ownership.
 */
struct Command {
    CommandKind kind = CommandKind::kSourceStatus;
    uint32_t source_id = 0;
    uint32_t output_id = 0;
    uint32_t transport = 0;
    uint64_t position = 0;
    double gain = 1.0;
    bool muted = false;
    const wstring* text = nullptr;
    uint32_t* returned_id = nullptr;
    RoutingSourceStatus* source_status = nullptr;
    RoutingOutputStatus* output_status = nullptr;
    RoutingRouteStatus* route_status = nullptr;
    ProcessCaptureStatus* capture_status = nullptr;
    OutputTimingStatus* output_timing = nullptr;
    RouteTimingStatus* route_timing = nullptr;
    CaptureTimingStatus* capture_timing = nullptr;
    AudioLevelStatus* level = nullptr;
    SourceSignalStatus* signal = nullptr;
};

/*
 * Name: GainRamp
 * Input: Finite normalized gain targets.
 * Process: Advances a five-millisecond linear ramp independently per output clock.
 * Output: Smooth sample gains without allocation or stale muted playback.
 */
struct GainRamp {
    double current = 1.0, target = 1.0;
    uint32_t remaining = 0;
    /*
     * Name: set_target
     * Input: Finite normalized target.
     * Process: Starts a 240-frame ramp only when the target changes.
     * Output: Uninterrupted linear transition from the current gain.
     */
    void set_target(double value) noexcept {
        if (value != target) { target = value; remaining = 240; }
    }
    /*
     * Name: next
     * Input: Current ramp state.
     * Process: Advances one output-clock frame toward the target.
     * Output: Smoothed gain without allocation or failure.
     */
    double next() noexcept {
        if (remaining != 0) { current += (target - current) / remaining--; }
        return current;
    }
};

/*
 * Name: Source
 * Input: One file decoder or live process capture shared by its routes.
 * Process: Owns bounded indexed PCM history and source playback/monitor state.
 * Output: Bounded decoded storage with independent route readers.
 */
struct Source {
    uint32_t id = 0;
    AudioLevelStatus level;
    SourceSignalStatus signal;
    unique_ptr<AudioDecoder> decoder;
    unique_ptr<ProcessCapture> capture;
    ProcessCaptureStatus capture_status;
    wstring parking_id;
    RoutingSourceStatus status;
    vector<float> cache;
    uint64_t cache_begin = 0;
    uint64_t cache_end = 0;
    uint64_t duration_frames = 0;
    uint64_t clock_base = 0;
    uint64_t clock_started = 0;
    bool decoder_ended = false;
    HANDLE root_process = nullptr;
    unique_ptr<AudioResourceTask<ProcessCapture>> capture_opener;
    uint32_t resume_state = kStopped, recovery_failures = 0;
    uint32_t last_error_hresult = 0;
    uint64_t retry_at = 0, recovery_attempts = 0, recovery_count = 0;
    bool capture_opening = false;
    /*
     * Name: is_live
     * Input: None.
     * Process: Reads source identity independently of temporarily absent capture resources.
     * Output: True for process sources, including while reconnecting.
     */
    bool is_live() const noexcept { return capture_status.process_id != 0; }
    /*
     * Name: ~Source
     * Input: Final owning coordinator.
     * Process: Releases buffer services, joins pending activation, and closes the original
     *          root handle only after all workers stop using immutable source identity.
     * Output: No leaked capture worker, COM client, or process handle.
     */
    ~Source() {
        capture.reset(); capture_opener.reset();
        if (root_process != nullptr) { CloseHandle(root_process); }
    }
};

/*
 * Name: Route
 * Input: One source/output connection.
 * Process: Retains the compatible public snapshot alongside its fractional reader.
 * Output: Independent drift, delay, priming, and underrun state for each destination.
 */
struct Route : RoutingRouteStatus {
    GainRamp source_ramp;
    DriftController controller;
    RouteTimingStatus timing;
    double read_position = 0;
    uint32_t fade_remaining = 128;
    bool positioned = false, starved = false;
};

/*
 * Name: Output
 * Input: One selected render endpoint.
 * Process: Owns exactly one event-driven WASAPI stream and its stereo mix buffer.
 * Output: Mixed output with isolated recovery or a queryable terminal failure.
 */
class Output {
public:
    uint32_t id = 0;
    AudioLevelStatus level;
    GainRamp level_ramp;
    wstring endpoint_id;
    RoutingOutputStatus status;
    OutputTimingStatus timing;
    vector<float> mix;
    HANDLE event = nullptr;
    uint64_t last_event_tick = 0;
    UINT32 buffer_frames = 0;

    /*
     * Name: Output
     * Input: None.
     * Process: Creates an inactive resource owner.
     * Output: An output ready for initialization on the MTA worker.
     */
    Output() = default;

    /*
     * Name: ~Output
     * Input: None.
     * Process: Stops the stream before releasing interfaces and its notification event.
     * Output: Worker-thread resource cleanup, including partial initialization.
     */
    ~Output() { close_stream(); opener_.reset(); }

    Output(const Output&) = delete;
    Output& operator=(const Output&) = delete;

    /*
     * Name: initialize
     * Input: The selected active render endpoint ID.
     * Process: Opens Media-category stereo float rendering and primes silence.
     * Output: S_OK after Start succeeds, or its exact Windows failure.
     */
    HRESULT initialize() {
        unique_ptr<PreparedOutput> prepared;
        const HRESULT result = PreparedOutput::open(endpoint_id, prepared);
        if (FAILED(result)) { return result; }
        return adopt(move(prepared));
    }

    /*
     * Name: adopt
     * Input: Unstarted client prepared on a background MTA.
     * Process: Acquires buffer services on their owning thread and primes fresh silence.
     * Output: HRESULT; lifetime counters and configured delay survive reconnection.
     */
    HRESULT adopt(unique_ptr<PreparedOutput> prepared) {
        close_stream();
        client_ = move(prepared->client);
        buffer_frames = prepared->buffer_frames;
        timing.stream_latency_100ns = prepared->latency_100ns;
        event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (event == nullptr) { return fail(HRESULT_FROM_WIN32(GetLastError())); }
        HRESULT result = client_->SetEventHandle(event);
        if (FAILED(result)) { return fail(result); }
        result = client_->GetService(IID_PPV_ARGS(render_.GetAddressOf()));
        if (FAILED(result)) { return fail(result); }
        result = client_->GetService(IID_PPV_ARGS(clock_.GetAddressOf()));
        if (FAILED(result)) { return fail(result); }
        result = clock_->GetFrequency(&clock_frequency_);
        if (FAILED(result) || clock_frequency_ == 0) { return fail(FAILED(result) ? result : E_UNEXPECTED); }
        timing.capacity_frames = buffer_frames;
        mix.assign(static_cast<size_t>(buffer_frames) * kChannels, 0.0f);
        result = submit(buffer_frames, MixingBlockMetrics{});
        if (FAILED(result)) { return result; }
        result = client_->Start();
        if (FAILED(result)) { return fail(result); }
        started_ = true;
        if (status.start_qpc == 0) { status.start_qpc = qpc_now(); }
        status.state = 1; status.hresult = 0; status.stop_qpc = 0;
        needs_reprime = false;
        timing.hresult = 0; timing.retry_in_ms = 0;
        clock_position_ = 0; clock_timestamp_ = 0; timing.clock_rate_ppm = 0;
        last_event_tick = GetTickCount64();
        last_submit_tick_ = last_event_tick;
        return S_OK;
    }

    /*
     * Name: poll_recovery
     * Input: Current retry deadline and any prepared background result.
     * Process: Retries the same endpoint without running activation on the audio thread.
     * Output: True after successful reconnection; false while pending or terminally failed.
     */
    bool poll_recovery() noexcept {
        if (status.state != kOutputRecovering) { return false; }
        try {
            if (!opener_) { opener_ = make_unique<AudioResourceTask<PreparedOutput>>(); }
            unique_ptr<PreparedOutput> prepared;
            HRESULT result = S_OK;
            if (opener_->take(prepared, result)) {
                opening_ = false;
                if (SUCCEEDED(result) && prepared) {
                    result = adopt(move(prepared));
                    if (SUCCEEDED(result)) { ++timing.recovery_count; failures_ = 0; return true; }
                }
                if (SUCCEEDED(result)) { result = E_UNEXPECTED; }
                fail(result);
                ++failures_;
                retry_at_ = GetTickCount64() + recovery_backoff_ms(failures_);
            }
            if (status.state != kOutputRecovering) { return false; }
            const uint64_t now = GetTickCount64();
            timing.retry_in_ms = static_cast<uint32_t>(retry_at_ > now ? retry_at_ - now : 0);
            if (!opening_ && now >= retry_at_) {
                const wstring selected = endpoint_id;
                opening_ = opener_->start([selected](unique_ptr<PreparedOutput>& destination) {
                    return PreparedOutput::open(selected, destination);
                });
                if (opening_) { ++timing.recovery_attempts; }
            }
        } catch (const bad_alloc&) { fail(E_OUTOFMEMORY); }
        catch (...) { fail(E_FAIL); }
        return false;
    }

    /*
     * Name: available_frames
     * Input: A capacity output reference.
     * Process: Reads queued frames and validates the initialized endpoint capacity.
     * Output: S_OK with free frames, or an isolated output failure.
     */
    HRESULT available_frames(UINT32& frames) noexcept {
        UINT32 padding = 0;
        const HRESULT result = client_->GetCurrentPadding(&padding);
        if (FAILED(result)) { return fail(result); }
        if (padding > buffer_frames) { return fail(E_UNEXPECTED); }
        timing.padding_frames = padding;
        const uint64_t now = GetTickCount64();
        const uint64_t duration_ms = (static_cast<uint64_t>(buffer_frames) * 1000 + kSampleRate - 1) / kSampleRate;
        if (started_ && padding == 0 && now - last_submit_tick_ > duration_ms + 2) {
            ++timing.underrun_count; ++timing.rebuffer_count; needs_reprime = true;
            clock_position_ = 0; clock_timestamp_ = 0; timing.clock_rate_ppm = 0;
        }
        UINT64 position = 0, timestamp = 0;
        const HRESULT measured = clock_->GetPosition(&position, &timestamp);
        if (FAILED(measured)) { return fail(measured); }
        if (measured == S_OK && (position < clock_position_ || timestamp < clock_timestamp_)) {
            clock_position_ = 0; clock_timestamp_ = 0; timing.clock_rate_ppm = 0;
        }
        if (measured == S_OK && timestamp > clock_timestamp_ && position >= clock_position_) {
            if (clock_timestamp_ != 0 && timestamp - clock_timestamp_ >= kTicksPerSecond / 2) {
                const double actual = static_cast<double>(position - clock_position_) / static_cast<double>(clock_frequency_);
                const double elapsed = static_cast<double>(timestamp - clock_timestamp_) / kTicksPerSecond;
                const double ppm = (actual / elapsed - 1.0) * 1.0e6;
                if (isfinite(ppm) && abs(ppm) < 100000) { timing.clock_rate_ppm += (ppm - timing.clock_rate_ppm) * 0.2; }
                clock_position_ = position; clock_timestamp_ = timestamp;
            } else if (clock_timestamp_ == 0) { clock_position_ = position; clock_timestamp_ = timestamp; }
        }
        frames = buffer_frames - padding;
        return S_OK;
    }

    /*
     * Name: submit
     * Input: Finalized mix frames and their post-clamp measurements.
     * Process: Copies prepared PCM while holding WASAPI's buffer and commits counters.
     * Output: S_OK or a recorded render failure; no decoding/allocation occurs here.
     */
    HRESULT submit(UINT32 frames, const MixingBlockMetrics& metrics) noexcept {
        BYTE* bytes = nullptr;
        HRESULT result = render_->GetBuffer(frames, &bytes);
        if (FAILED(result)) { return fail(result); }
        memcpy(bytes, mix.data(), static_cast<size_t>(frames) * kFrameBytes);
        result = render_->ReleaseBuffer(frames, 0);
        if (FAILED(result)) { return fail(result); }
        status.frames_submitted += frames;
        status.non_silent_frames += metrics.non_silent_frames;
        status.clipped_samples += metrics.clipped_samples;
        status.sum_squares += metrics.sum_squares;
        status.last_peak = metrics.peak;
        last_submit_tick_ = GetTickCount64();
        if (metrics.non_silent_frames > 0 && timing.first_signal_qpc == 0) { timing.first_signal_qpc = qpc_now(); }
        return S_OK;
    }

    /*
     * Name: fail
     * Input: A failing Windows result.
     * Process: Records the error, releases this stream, and classifies recovery.
     * Output: The HRESULT; the output stays queryable while Recovering or Failed.
     */
    HRESULT fail(HRESULT result) noexcept {
        status.hresult = static_cast<uint32_t>(result);
        timing.hresult = status.hresult; timing.last_error_hresult = status.hresult;
        status.state = is_recoverable_audio_error(result) ? kOutputRecovering : kFailed;
        close_stream();
        if (status.state == kOutputRecovering) { retry_at_ = GetTickCount64() + recovery_backoff_ms(failures_); }
        return result;
    }
    bool needs_reprime = false;
    /*
     * Name: retry
     * Input: Existing selected endpoint.
     * Process: Schedules an immediate background reopen while retaining graph identity.
     * Output: No value; settings and routes remain attached to this output.
     */
    void retry() noexcept {
        fail(AUDCLNT_E_RESOURCES_INVALIDATED);
        status.state = kOutputRecovering; retry_at_ = 0; failures_ = 0;
    }

private:
    /*
     * Name: close_stream
     * Input: Partially or fully initialized endpoint resources.
     * Process: Stops and releases services/client/event on their owning coordinator.
     * Output: Empty endpoint ownership; telemetry/configuration survive recovery.
     */
    void close_stream() noexcept {
        if (started_) { stop(); }
        started_ = false;
        render_.Reset(); clock_.Reset(); client_.Reset();
        if (event != nullptr) { CloseHandle(event); event = nullptr; }
        timing.capacity_frames = 0; timing.padding_frames = 0;
    }
    /*
     * Name: stop
     * Input: A running renderer.
     * Process: Records a conservative stop boundary and retains any first failure.
     * Output: Stop's HRESULT; ownership remains safe for destruction.
     */
    HRESULT stop() noexcept {
        const uint64_t stopped = qpc_now();
        const HRESULT result = client_->Stop();
        if (SUCCEEDED(result)) {
            started_ = false;
            status.stop_qpc = stopped;
        }
        return result;
    }

    ComPtr<IAudioClient2> client_;
    ComPtr<IAudioRenderClient> render_;
    ComPtr<IAudioClock> clock_;
    uint64_t clock_frequency_ = 0, clock_position_ = 0, clock_timestamp_ = 0;
    unique_ptr<AudioResourceTask<PreparedOutput>> opener_;
    uint64_t retry_at_ = 0, last_submit_tick_ = 0;
    uint32_t failures_ = 0;
    bool opening_ = false;
    bool started_ = false;
};

/*
 * Name: RoutingSession
 * Input: Worker commands and render notifications.
 * Process: Owns shared decoders, independent route readers, and per-endpoint mixers.
 * Output: Bounded fan-out and final-clamped mixing without duplicate file players.
 */
class RoutingSession {
public:
    /*
     * Name: RoutingSession
     * Input: None.
     * Process: Reserves the bounded routing table before audio work begins.
     * Output: An empty session.
     */
    RoutingSession() { routes_.reserve(kMaxRoutes); }

    /*
     * Name: ~RoutingSession
     * Input: None.
     * Process: Releases all outputs and sources before balancing MF and COM.
     * Output: Complete cleanup on the owning worker thread.
     */
    ~RoutingSession() {
        for (auto& output : outputs_) { output.reset(); }
        for (auto& source : sources_) { source.reset(); }
        if (mf_started_) { MFShutdown(); }
        if (com_initialized_) { CoUninitialize(); }
    }

    RoutingSession(const RoutingSession&) = delete;
    RoutingSession& operator=(const RoutingSession&) = delete;

    /*
     * Name: initialize
     * Input: The dedicated worker thread.
     * Process: Initializes its MTA, Media Foundation, and common clock frequency.
     * Output: S_OK or the Windows initialization HRESULT.
     */
    HRESULT initialize() noexcept {
        HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(result)) { return result; }
        com_initialized_ = true;
        result = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (FAILED(result)) { return result; }
        mf_started_ = true;
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) { return E_FAIL; }
        qpc_frequency_ = static_cast<uint64_t>(frequency.QuadPart);
        return S_OK;
    }

    /*
     * Name: execute
     * Input: An acknowledged public command.
     * Process: Refreshes common clocks and applies only the requested resource/control change.
     * Output: HRESULT plus any caller-owned ID or status result.
     */
    HRESULT execute(const Command& command) {
        update_sources(qpc_now());
        switch (command.kind) {
        case CommandKind::kSourceSignal: {
            Source* source = find_source(command.source_id);
            if (source == nullptr) { return E_INVALIDARG; }
            *command.signal = source->signal; return S_OK;
        }
        case CommandKind::kSourceLevel:
        case CommandKind::kOutputLevel:
        case CommandKind::kGetSourceLevel:
        case CommandKind::kGetOutputLevel: {
            const bool source_kind = command.kind == CommandKind::kSourceLevel || command.kind == CommandKind::kGetSourceLevel;
            Source* source = source_kind ? find_source(command.source_id) : nullptr;
            Output* output = source_kind ? nullptr : find_output(command.output_id);
            AudioLevelStatus* level = source != nullptr ? &source->level : output != nullptr ? &output->level : nullptr;
            if (level == nullptr) { return E_INVALIDARG; }
            if (command.kind == CommandKind::kGetSourceLevel || command.kind == CommandKind::kGetOutputLevel) {
                *command.level = *level;
            } else {
                if (!valid_gain(command.gain)) { return E_INVALIDARG; }
                level->gain = command.gain; level->muted = command.muted ? 1u : 0u;
                if (output != nullptr && count_routes(output->id, false) == 0) {
                    output->level_ramp.current = command.muted ? 0.0 : command.gain;
                    output->level_ramp.target = output->level_ramp.current; output->level_ramp.remaining = 0;
                }
            }
            return S_OK;
        }
        case CommandKind::kAddSource: return add_source(*command.text, *command.returned_id);
        case CommandKind::kAddProcess: return add_process(command.source_id, *command.text, *command.returned_id);
        case CommandKind::kCaptureStatus: {
            Source* source = find_source(command.source_id);
            if (source == nullptr || !source->is_live()) { return E_INVALIDARG; }
            poll_captures();
            source->capture_status.state = source->status.state;
            source->capture_status.hresult = source->status.hresult;
            source->capture_status.buffered_frames = source->cache_end - source->cache_begin;
            *command.capture_status = source->capture_status;
            return S_OK;
        }
        case CommandKind::kAddOutput: return add_output(*command.text, *command.returned_id);
        case CommandKind::kDelay:
        case CommandKind::kDrift: {
            Output* output = find_output(command.output_id);
            if (output == nullptr || (command.kind == CommandKind::kDelay && command.position > kMaximumDelayFrames)) { return E_INVALIDARG; }
            if (command.kind == CommandKind::kDelay) {
                output->timing.delay_frames = static_cast<uint32_t>(command.position);
                output->timing.first_signal_qpc = 0;
            } else { output->timing.drift_enabled = command.muted ? 1u : 0u; }
            for (auto& route : routes_) {
                if (route.output_id == output->id) {
                    route.positioned = false; route.starved = false; route.timing.priming = 1;
                    route.controller.reset(); route.fade_remaining = 128;
                }
            }
            return S_OK;
        }
        case CommandKind::kOutputTiming: {
            Output* output = find_output(command.output_id);
            if (output == nullptr) { return E_INVALIDARG; }
            *command.output_timing = output->timing;
            return S_OK;
        }
        case CommandKind::kRouteTiming: {
            Route* route = find_route(command.source_id, command.output_id);
            if (route == nullptr) { return E_INVALIDARG; }
            Source* source = find_source(command.source_id);
            Output* output = find_output(command.output_id);
            route->timing.correction_ppm = route->controller.correction_ppm();
            route->timing.read_position_frame = route->read_position;
            if (!route->positioned) { route->timing.phase_error_frames = 0; }
            const uint64_t cursor = max(source->cache_begin, static_cast<uint64_t>(route->read_position));
            route->timing.buffered_frames = static_cast<uint32_t>(source->cache_end > cursor ? source->cache_end - cursor : 0);
            if (output->status.state != 1 || source->status.state == kSourceRecovering) { route->timing.priming = 1; }
            *command.route_timing = route->timing;
            return S_OK;
        }
        case CommandKind::kCaptureTiming: {
            Source* source = find_source(command.source_id);
            if (source == nullptr || !source->is_live()) { return E_INVALIDARG; }
            const uint64_t now = GetTickCount64();
            *command.capture_timing = { source->status.hresult,
                static_cast<uint32_t>(source->status.state == kSourceRecovering && source->retry_at > now ? source->retry_at - now : 0),
                source->status.state == kSourceRecovering ? source->resume_state : source->status.state, source->last_error_hresult,
                source->recovery_attempts, source->recovery_count, live_history_frames(*source), 0 };
            return S_OK;
        }
        case CommandKind::kRetryOutput: {
            Output* output = find_output(command.output_id);
            if (output == nullptr) { return E_INVALIDARG; }
            output->retry(); return S_OK;
        }
        case CommandKind::kRetryCapture: {
            Source* source = find_source(command.source_id);
            if (source == nullptr || !source->is_live()) { return E_INVALIDARG; }
            if (WaitForSingleObject(source->root_process, 0) != WAIT_TIMEOUT) { return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED); }
            if (source->status.state != kSourceRecovering && source->status.state != kFailed) { source->resume_state = source->status.state; }
            source->capture.reset(); source->status.state = kSourceRecovering;
            source->status.hresult = 0; source->retry_at = 0; source->recovery_failures = 0;
            rebuffer_source(source->id); return S_OK;
        }
        case CommandKind::kRemoveSource: return remove_source(command.source_id);
        case CommandKind::kRemoveOutput: return remove_output(command.output_id);
        case CommandKind::kConnect: return connect(command);
        case CommandKind::kDisconnect: return disconnect(command.source_id, command.output_id);
        case CommandKind::kGain:
        case CommandKind::kMute: {
            RoutingRouteStatus* route = find_route(command.source_id, command.output_id);
            if (route == nullptr) { return E_INVALIDARG; }
            if (command.kind == CommandKind::kGain) {
                if (!valid_gain(command.gain)) { return E_INVALIDARG; }
                route->gain = command.gain;
            } else {
                route->muted = command.muted ? 1u : 0u;
            }
            return S_OK;
        }
        case CommandKind::kTransport: return transport(command.source_id, command.transport, command.position);
        case CommandKind::kSourceStatus: {
            Source* source = find_source(command.source_id);
            if (source == nullptr) { return E_INVALIDARG; }
            source->status.route_count = count_routes(source->id, true);
            source->status.cached_frames = source->cache_end - source->cache_begin;
            *command.source_status = source->status;
            return S_OK;
        }
        case CommandKind::kOutputStatus: {
            Output* output = find_output(command.output_id);
            if (output == nullptr) { return E_INVALIDARG; }
            output->status.route_count = count_routes(output->id, false);
            *command.output_status = output->status;
            return S_OK;
        }
        case CommandKind::kRouteStatus: {
            const RoutingRouteStatus* route = find_route(command.source_id, command.output_id);
            if (route == nullptr) { return E_INVALIDARG; }
            *command.route_status = *route;
            return S_OK;
        }
        default: return E_INVALIDARG;
        }
    }

    /*
     * Name: wait_handles
     * Input: Fixed arrays with the command event already in slot zero.
     * Process: Adds active render events and computes the earliest stall deadline.
     * Output: Wait count, output lookup slots, and bounded timeout.
     */
    DWORD wait_handles(array<HANDLE, kMaxNotifications + 1>& events,
                       array<Output*, kMaxNotifications>& lookup,
                       array<Source*, kMaxNotifications>& capture_lookup, DWORD& timeout) noexcept {
        DWORD count = 1;
        timeout = INFINITE;
        poll_captures();
        for (auto& output : outputs_) {
            if (!output || output->status.state == kFailed) { continue; }
            if (output->status.state == kOutputRecovering) {
                if (output->poll_recovery()) { rebase_output(output->id); }
                timeout = min(timeout, DWORD{50});
                if (output->status.state != 1) { continue; }
            }
            const uint64_t elapsed = GetTickCount64() - output->last_event_tick;
            if (elapsed >= kRenderTimeoutMs) {
                output->fail(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                timeout = min(timeout, DWORD{50});
                continue;
            }
            events[count] = output->event;
            lookup[count - 1] = output.get();
            ++count;
            timeout = min(timeout, kRenderTimeoutMs - static_cast<DWORD>(elapsed));
        }
        for (auto& source : sources_) {
            if (source && source->status.state == kSourceRecovering) { timeout = min(timeout, DWORD{50}); }
            if (!source || !source->capture || source->status.state == kFailed || source->status.state == kEnded) { continue; }
            events[count] = source->capture->event_handle();
            capture_lookup[count - 1] = source.get();
            ++count;
            timeout = min(timeout, DWORD{50});
        }
        return count;
    }

    /*
     * Name: service_capture
     * Input: A live source with available process-loopback packets.
     * Process: Drains bounded batches into shared, delay-sized PCM history.
     * Output: Capture metrics and current audio for every output reader.
     */
    void service_capture(Source& source) noexcept {
        for (uint32_t batch = 0; batch < 64; ++batch) {
            ProcessCapturePacket packet;
            const HRESULT hr = source.capture->read_packet(packet);
            if (hr == S_FALSE) { break; }
            if (FAILED(hr)) { fail_source(source, hr); break; }
            const bool silent = (packet.flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            if (packet.samples == nullptr && !silent && packet.frames != 0) {
                source.capture->release_packet(packet.frames);
                fail_source(source, E_UNEXPECTED);
                break;
            }
            if ((packet.flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) {
                ++source.capture_status.discontinuities;
                source.cache_begin = source.cache_end;
                rebuffer_source(source.id);
            }
            double packet_peak = 0.0;
            for (uint32_t frame = 0; frame < packet.frames; ++frame) {
                const size_t destination = static_cast<size_t>((source.cache_end + frame) % kCacheFrames) * kChannels;
                bool non_silent = false;
                for (uint32_t channel = 0; channel < kChannels; ++channel) {
                    float value = silent ? 0.0f : packet.samples[static_cast<size_t>(frame) * kChannels + channel];
                    if (!isfinite(value)) { value = 0; }
                    source.cache[destination + channel] = clamp(value, -1.0f, 1.0f);
                    const double stored = source.cache[destination + channel];
                    source.capture_status.sum_squares += stored * stored;
                    packet_peak = max(packet_peak, abs(stored));
                    non_silent = non_silent || abs(value) > 1.0e-9f;
                }
                if (non_silent) { ++source.capture_status.non_silent_frames; }
            }
            source.cache_end += packet.frames;
            const uint64_t history = live_history_frames(source);
            const uint64_t oldest = source.cache_end > history ? source.cache_end - history : 0;
            uint64_t unread = source.cache_end;
            if (source.status.state == kPlaying) {
                for (const auto& route : routes_) {
                    if (route.source_id == source.id) { unread = min(unread, max(route.cursor_frame, source.cache_begin)); }
                }
            }
            if (oldest > unread) { source.capture_status.frames_dropped += oldest - unread; }
            source.cache_begin = max(source.cache_begin, oldest);
            source.status.cached_frames = source.cache_end - source.cache_begin;
            source.capture_status.frames_captured += packet.frames;
            source.capture_status.last_peak = packet_peak;
            if ((packet.flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0) {
                source.capture_status.last_packet_qpc_100ns = packet.qpc_100ns;
            }
            const HRESULT released = source.capture->release_packet(packet.frames);
            if (FAILED(released)) { fail_source(source, released); break; }
        }
    }

    /*
     * Name: service_output
     * Input: The output whose render notification fired.
     * Process: Mixes every active route from shared source caches, then submits one block.
     * Output: Isolated source/output failure state; unrelated streams keep running.
     */
    void service_output(Output& output) {
        const uint64_t now = GetTickCount64();
        output.timing.max_service_gap_ms = max(output.timing.max_service_gap_ms,
            static_cast<double>(now - output.last_event_tick));
        output.last_event_tick = now;
        update_sources(qpc_now());
        UINT32 frames = 0;
        if (FAILED(output.available_frames(frames)) || frames == 0) { return; }
        if (output.needs_reprime) { rebase_output(output.id); output.needs_reprime = false; }
        fill_n(output.mix.data(), static_cast<size_t>(frames) * kChannels, 0.0f);
        for (auto& route : routes_) {
            if (route.output_id != output.id) { continue; }
            Source* source = find_source(route.source_id);
            if (source == nullptr || source->status.state != kPlaying) { continue; }
            mix_route(*source, route, output, output.mix.data(), frames);
        }
        output.level_ramp.set_target(output.level.muted ? 0.0 : output.level.gain);
        for (uint32_t frame = 0; frame < frames; ++frame) {
            const float gain = static_cast<float>(output.level_ramp.next());
            output.mix[static_cast<size_t>(frame) * kChannels] *= gain;
            output.mix[static_cast<size_t>(frame) * kChannels + 1] *= gain;
        }
        const MixingBlockMetrics metrics = finalize_pcm(output.mix.data(), frames);
        output.submit(frames, metrics);
        update_sources(qpc_now());
    }

private:
    /*
     * Name: rebase_output
     * Input: Reconnected or starved output ID.
     * Process: Rejoins current source timelines with configured delay and a short fade.
     * Output: Fresh readers without rewinding or interrupting any other destination.
     */
    void rebase_output(uint32_t id) noexcept {
        for (auto& route : routes_) {
            if (route.output_id != id) { continue; }
            route.positioned = false; route.starved = false; route.controller.reset();
            route.fade_remaining = 128; route.timing.priming = 1; ++route.rebases;
            ++route.timing.rebuffer_count;
        }
    }
    /*
     * Name: rebuffer_source
     * Input: Discontinuous or recovered process source ID.
     * Process: Primes only its route readers from fresh history with a short fade.
     * Output: Old capture samples are not replayed after a device/packet interruption.
     */
    void rebuffer_source(uint32_t id) noexcept {
        for (auto& route : routes_) {
            if (route.source_id != id) { continue; }
            route.positioned = false; route.starved = false; route.controller.reset();
            route.timing.priming = 1; route.fade_remaining = 128;
            ++route.rebases; ++route.timing.rebuffer_count;
        }
    }

    /*
     * Name: poll_capture_recovery
     * Input: Recovering source retaining the original root process handle.
     * Process: Prepares a replacement client in a background MTA and adopts buffer
     *          services only on this coordinator, preserving the requested monitor state.
     * Output: Automatic same-PID/same-parking recovery; source failures stay queryable.
     */
    void poll_capture_recovery(Source& source) {
        if (!source.capture_opener) { source.capture_opener = make_unique<AudioResourceTask<ProcessCapture>>(); }
        unique_ptr<ProcessCapture> prepared;
        HRESULT hr = S_OK;
        if (source.capture_opener->take(prepared, hr)) {
            source.capture_opening = false;
            if (SUCCEEDED(hr) && prepared) {
                if (WaitForSingleObject(source.root_process, 0) == WAIT_OBJECT_0) { source.status.state = kEnded; source.status.hresult = 0; return; }
                hr = prepared->start();
                if (SUCCEEDED(hr)) {
                    source.capture = move(prepared);
                    source.status.state = source.resume_state;
                    source.status.hresult = 0;
                    source.clock_base = source.status.position_100ns; source.clock_started = qpc_now();
                    source.cache_begin = source.cache_end; source.status.cached_frames = 0;
                    ++source.recovery_count; source.recovery_failures = 0;
                    rebuffer_source(source.id);
                    return;
                }
            }
            if (SUCCEEDED(hr)) { hr = E_UNEXPECTED; }
            fail_source(source, hr);
            ++source.recovery_failures;
            source.retry_at = GetTickCount64() + recovery_backoff_ms(source.recovery_failures);
        }
        if (source.status.state != kSourceRecovering) { return; }
        if (!source.capture_opening && GetTickCount64() >= source.retry_at) {
            const uint32_t pid = source.capture_status.process_id;
            const wstring parking = source.parking_id;
            source.capture_opening = source.capture_opener->start([pid, parking](unique_ptr<ProcessCapture>& destination) {
                return ProcessCapture::prepare(pid, parking, destination);
            });
            if (source.capture_opening) { ++source.recovery_attempts; }
        }
    }
    /*
     * Name: live_history_frames
     * Input: A process source and its configured destinations.
     * Process: Retains the greatest reader target plus 200 ms of safety history.
     * Output: Bounded PCM history; zero-delay routes keep the previous 12000-frame cap.
     */
    uint64_t live_history_frames(const Source& source) noexcept {
        uint32_t target = 0;
        for (const auto& route : routes_) {
            if (route.source_id != source.id) { continue; }
            if (Output* output = find_output(route.output_id)) {
                target = max(target, live_target_frames(output->buffer_frames, output->timing.delay_frames));
            }
        }
        return live_history_capacity(target);
    }
    /*
     * Name: poll_captures
     * Input: Worker-owned sources.
     * Process: Detects root exit, parking escape, and packets after missed notifications.
     * Output: Isolated failures; an exited root ends its source without PID reuse.
     */
    void poll_captures() noexcept {
        for (auto& source : sources_) {
            if (!source || !source->is_live() || source->status.state == kFailed || source->status.state == kEnded) { continue; }
            try {
                if (WaitForSingleObject(source->root_process, 0) == WAIT_OBJECT_0) {
                    if (source->capture) { source->capture->stop(); }
                    source->status.state = kEnded;
                    source->status.hresult = 0;
                    continue;
                }
                if (source->status.state == kSourceRecovering) { poll_capture_recovery(*source); }
                if (!source->capture || source->status.state == kFailed || source->status.state == kEnded) { continue; }
                const HRESULT hr = source->capture->check_health();
                if (hr == S_FALSE) { source->capture->stop(); source->status.state = kEnded; }
                else if (FAILED(hr)) { fail_source(*source, hr); }
                else { service_capture(*source); }
            } catch (const bad_alloc&) { fail_source(*source, E_OUTOFMEMORY); }
            catch (...) { fail_source(*source, E_FAIL); }
        }
    }
    /*
     * Name: valid_gain
     * Input: A proposed route scalar.
     * Process: Rejects nonfinite and out-of-range values.
     * Output: True for gains in the inclusive range zero to one.
     */
    static bool valid_gain(double gain) noexcept { return isfinite(gain) && gain >= 0.0 && gain <= 1.0; }

    /*
     * Name: find_source
     * Input: A source ID.
     * Process: Searches the bounded source table.
     * Output: Its worker-owned source, or null.
     */
    Source* find_source(uint32_t id) noexcept {
        for (auto& source : sources_) {
            if (source && source->id == id) { return source.get(); }
        }
        return nullptr;
    }

    /*
     * Name: find_output
     * Input: An output ID.
     * Process: Searches the bounded output table.
     * Output: Its worker-owned output, or null.
     */
    Output* find_output(uint32_t id) noexcept {
        for (auto& output : outputs_) {
            if (output && output->id == id) { return output.get(); }
        }
        return nullptr;
    }

    /*
     * Name: find_route
     * Input: Source and output IDs.
     * Process: Searches the unique connection pairs.
     * Output: The existing route, or null.
     */
    Route* find_route(uint32_t source_id, uint32_t output_id) noexcept {
        for (auto& route : routes_) {
            if (route.source_id == source_id && route.output_id == output_id) { return &route; }
        }
        return nullptr;
    }

    /*
     * Name: count_routes
     * Input: A resource ID and whether it identifies a source.
     * Process: Counts matching connections in the bounded routing table.
     * Output: A snapshot route count.
     */
    uint32_t count_routes(uint32_t id, bool source) const noexcept {
        return static_cast<uint32_t>(count_if(routes_.begin(), routes_.end(),
            [=](const RoutingRouteStatus& route) { return (source ? route.source_id : route.output_id) == id; }));
    }

    /*
     * Name: add_source
     * Input: Local path and source-ID destination.
     * Process: Creates one decoder and a bounded PCM ring without starting its clock.
     * Output: HRESULT and a new monotonic source ID.
     */
    HRESULT add_source(const wstring& path, uint32_t& id) {
        id = 0;
        if (path.empty() || path.find(L'\0') != wstring::npos || next_source_id_ == 0) { return E_INVALIDARG; }
        auto slot = find_if(sources_.begin(), sources_.end(), [](const auto& source) { return !source; });
        if (slot == sources_.end()) { return HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES); }
        auto source = make_unique<Source>();
        HRESULT result = AudioDecoder::open(path, source->decoder);
        if (FAILED(result)) { return result; }
        source->cache.resize(static_cast<size_t>(kCacheFrames) * kChannels);
        source->status.duration_100ns = source->decoder->duration_100ns();
        source->duration_frames = ticks_to_frames(source->status.duration_100ns, true);
        source->id = next_source_id_++;
        id = source->id;
        *slot = move(source);
        return S_OK;
    }

    /*
     * Name: add_process
     * Input: Live application PID, virtual parking endpoint, and ID destination.
     * Process: Creates one capture/cache, rejecting overlapping trees and parking outputs.
     * Output: HRESULT and a stopped monitor; no external playback setting is changed.
     */
    HRESULT add_process(uint32_t process_id, const wstring& parking_id, uint32_t& id) {
        id = 0;
        if (next_source_id_ == 0) { return E_INVALIDARG; }
        auto slot = find_if(sources_.begin(), sources_.end(), [](const auto& source) { return !source; });
        if (slot == sources_.end()) { return HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES); }
        for (const auto& output : outputs_) {
            if (output && CompareStringOrdinal(output->endpoint_id.c_str(), -1, parking_id.c_str(), -1, TRUE) == CSTR_EQUAL) { return E_INVALIDARG; }
        }
        for (const auto& existing : sources_) {
            if (!existing || !existing->is_live()) { continue; }
            bool overlap = false;
            HRESULT hr = process_tree_contains(existing->capture_status.process_id, process_id, overlap);
            if (FAILED(hr)) { return hr; }
            if (overlap) { return E_INVALIDARG; }
            hr = process_tree_contains(process_id, existing->capture_status.process_id, overlap);
            if (FAILED(hr)) { return hr; }
            if (overlap) { return E_INVALIDARG; }
        }
        auto source = make_unique<Source>();
        source->cache.resize(static_cast<size_t>(kCacheFrames) * kChannels);
        source->parking_id = parking_id;
        source->capture_status.process_id = process_id;
        source->root_process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, process_id);
        if (source->root_process == nullptr) { return HRESULT_FROM_WIN32(GetLastError()); }
        const HRESULT hr = ProcessCapture::open(process_id, parking_id, source->capture);
        if (FAILED(hr)) { return hr; }
        source->id = next_source_id_++;
        id = source->id;
        *slot = move(source);
        return S_OK;
    }

    /*
     * Name: add_output
     * Input: Distinct endpoint ID and output-ID destination.
     * Process: Starts one silent shared-mode mixer without modifying other outputs.
     * Output: HRESULT and a new monotonic output ID.
     */
    HRESULT add_output(const wstring& endpoint, uint32_t& id) {
        id = 0;
        if (endpoint.empty() || endpoint.find(L'\0') != wstring::npos || next_output_id_ == 0) { return E_INVALIDARG; }
        for (const auto& source : sources_) {
            if (source && source->is_live() && CompareStringOrdinal(source->parking_id.c_str(), -1,
                endpoint.c_str(), -1, TRUE) == CSTR_EQUAL) { return E_INVALIDARG; }
        }
        for (const auto& output : outputs_) {
            if (output && CompareStringOrdinal(output->endpoint_id.c_str(), -1,
                                               endpoint.c_str(), -1, TRUE) == CSTR_EQUAL) {
                return E_INVALIDARG;
            }
        }
        auto slot = find_if(outputs_.begin(), outputs_.end(), [](const auto& output) { return !output; });
        if (slot == outputs_.end()) { return HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES); }
        auto output = make_unique<Output>();
        output->endpoint_id = endpoint;
        const HRESULT result = output->initialize();
        if (FAILED(result)) { return result; }
        output->id = next_output_id_++;
        id = output->id;
        *slot = move(output);
        return S_OK;
    }

    /*
     * Name: remove_source
     * Input: Existing source ID.
     * Process: Removes its routes before releasing the shared decoder/cache.
     * Output: S_OK, or E_INVALIDARG when the source does not exist.
     */
    HRESULT remove_source(uint32_t id) {
        for (auto& source : sources_) {
            if (source && source->id == id) {
                erase_if(routes_, [=](const auto& route) { return route.source_id == id; });
                source.reset();
                return S_OK;
            }
        }
        return E_INVALIDARG;
    }

    /*
     * Name: remove_output
     * Input: Existing output ID.
     * Process: Removes its connections and destroys only that output client.
     * Output: S_OK, or E_INVALIDARG when the output does not exist.
     */
    HRESULT remove_output(uint32_t id) {
        for (auto& output : outputs_) {
            if (output && output->id == id) {
                erase_if(routes_, [=](const auto& route) { return route.output_id == id; });
                output.reset();
                return S_OK;
            }
        }
        return E_INVALIDARG;
    }

    /*
     * Name: connect
     * Input: Existing source/output IDs and valid gain/mute settings.
     * Process: Updates an existing route or creates a reader at the current source clock.
     * Output: S_OK or an argument/capacity failure.
     */
    HRESULT connect(const Command& command) {
        Source* source = find_source(command.source_id);
        if (source == nullptr || find_output(command.output_id) == nullptr || !valid_gain(command.gain)) { return E_INVALIDARG; }
        if (RoutingRouteStatus* route = find_route(command.source_id, command.output_id)) {
            route->gain = command.gain;
            route->muted = command.muted ? 1u : 0u;
            return S_OK;
        }
        if (routes_.size() == kMaxRoutes) { return HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES); }
        Route route;
        route.source_ramp.current = source->level.muted ? 0.0 : source->level.gain;
        route.source_ramp.target = route.source_ramp.current;
        route.source_id = command.source_id;
        route.output_id = command.output_id;
        route.gain = command.gain;
        route.muted = command.muted ? 1u : 0u;
        route.cursor_frame = source->is_live() ? live_start(*source) : ticks_to_frames(source->status.position_100ns);
        routes_.push_back(route);
        return S_OK;
    }

    /*
     * Name: disconnect
     * Input: Existing route IDs.
     * Process: Removes exactly that source-to-output connection.
     * Output: S_OK or E_INVALIDARG when no route exists.
     */
    HRESULT disconnect(uint32_t source_id, uint32_t output_id) {
        const auto removed = erase_if(routes_, [=](const auto& route) {
            return route.source_id == source_id && route.output_id == output_id;
        });
        return removed != 0 ? S_OK : E_INVALIDARG;
    }

    /*
     * Name: fail_source
     * Input: A source and its decoder/capture failure.
     * Process: Preserves monitor intent and retries transient live-source errors.
     * Output: The HRESULT and isolated Recovering/Failed state; other streams continue.
     */
    HRESULT fail_source(Source& source, HRESULT result) noexcept {
        source.last_error_hresult = static_cast<uint32_t>(result);
        if (source.is_live() && source.status.state != kSourceRecovering && source.status.state != kFailed) { source.resume_state = source.status.state; }
        if (source.is_live() && is_recoverable_audio_error(result)) {
            if (source.status.state != kSourceRecovering) { source.resume_state = source.status.state; }
            source.status.state = kSourceRecovering;
            source.status.hresult = static_cast<uint32_t>(result);
            source.capture.reset();
            source.retry_at = GetTickCount64() + recovery_backoff_ms(source.recovery_failures);
            return result;
        }
        if (source.status.state != kFailed) { source.status.hresult = static_cast<uint32_t>(result); }
        source.status.state = kFailed;
        if (source.capture) { source.capture->stop(); }
        return result;
    }

    /*
     * Name: reset_readers
     * Input: Source ID and an absolute frame position.
     * Process: Repositions only the routes belonging to that source.
     * Output: Updated cursors with lifetime read counts preserved.
     */
    void reset_readers(uint32_t source_id, uint64_t frame) noexcept {
        for (auto& route : routes_) {
            if (route.source_id == source_id) {
                if (route.cursor_frame != frame) { ++route.rebases; }
                route.cursor_frame = frame;
                route.read_position = static_cast<double>(frame);
                route.positioned = false; route.starved = false;
                route.controller.reset(); route.timing.priming = 1; route.fade_remaining = 128;
            }
        }
    }

    /*
     * Name: seek_source
     * Input: A source, clamped media time, and desired transport state.
     * Process: Seeks its single decoder and resets its cache/readers without touching outputs.
     * Output: S_OK or an isolated source failure.
     */
    HRESULT seek_source(Source& source, uint64_t position, uint32_t state) {
        const HRESULT result = source.decoder->seek(position);
        if (FAILED(result)) { return fail_source(source, result); }
        const uint64_t frame = ticks_to_frames(position);
        source.cache_begin = frame;
        source.cache_end = frame;
        source.decoder_ended = position == source.status.duration_100ns;
        source.status.position_100ns = position;
        source.status.cached_frames = 0;
        source.status.state = state;
        source.clock_base = position;
        source.clock_started = qpc_now();
        reset_readers(source.id, frame);
        return S_OK;
    }

    /*
     * Name: transport
     * Input: Source ID, transport command, and optional seek time.
     * Process: Applies independent source clocks and repositions only affected readers.
     * Output: HRESULT after the operation; failed sources remain queryable.
     */
    HRESULT transport(uint32_t id, uint32_t command, uint64_t position) {
        Source* source = find_source(id);
        if (source == nullptr || command > 3) { return E_INVALIDARG; }
        if (source->status.state == kFailed) { return static_cast<HRESULT>(source->status.hresult); }
        if (source->is_live()) {
            if (command == 3) { return E_NOTIMPL; }
            if (source->status.state == kEnded) { return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED); }
            if (source->status.state == kSourceRecovering) {
                if (command == 0) { source->resume_state = kPlaying; }
                else if (command == 1 && source->resume_state == kPlaying) { source->resume_state = kPaused; }
                else if (command == 2) { source->resume_state = kStopped; source->status.position_100ns = 0; }
                return S_OK;
            }
            if (command == 0 && source->status.state != kPlaying) {
                source->clock_base = source->status.position_100ns;
                source->clock_started = qpc_now();
                source->status.state = kPlaying;
                reset_readers(id, live_start(*source));
            } else if (command == 1 && source->status.state == kPlaying) {
                source->clock_base = source->status.position_100ns;
                source->status.state = kPaused;
            } else if (command == 2) {
                source->status.state = kStopped;
                source->status.position_100ns = 0;
                source->clock_base = 0;
                reset_readers(id, source->cache_end);
            }
            return S_OK;
        }
        switch (command) {
        case 0:
            if (source->status.state == kPlaying) { return S_OK; }
            if (source->status.state == kEnded) {
                const HRESULT result = seek_source(*source, 0, kStopped);
                if (FAILED(result)) { return result; }
            }
            source->clock_base = source->status.position_100ns;
            source->clock_started = qpc_now();
            source->status.state = kPlaying;
            reset_readers(id, ticks_to_frames(source->status.position_100ns));
            return S_OK;
        case 1:
            if (source->status.state == kPlaying) {
                source->clock_base = source->status.position_100ns;
                source->status.state = kPaused;
            }
            return S_OK;
        case 2: return seek_source(*source, 0, kStopped);
        case 3: {
            const uint64_t clamped = min(position, source->status.duration_100ns);
            uint32_t state = source->status.state;
            if (state == kEnded && clamped < source->status.duration_100ns) { state = kStopped; }
            return seek_source(*source, clamped, state);
        }
        default: return E_INVALIDARG;
        }
    }

    /*
     * Name: update_sources
     * Input: One common performance-counter reading.
     * Process: Advances Playing clocks and marks EOF once all viable readers submit the end.
     * Output: Updated positions and transport states, including unrouted source progression.
     */
    void update_sources(uint64_t now) noexcept {
        for (auto& source : sources_) {
            if (!source || source->status.state != kPlaying) { continue; }
            if (source->capture) {
                const double elapsed = static_cast<double>(now - source->clock_started) *
                    static_cast<double>(kTicksPerSecond) / static_cast<double>(qpc_frequency_);
                source->status.position_100ns = source->clock_base + static_cast<uint64_t>(elapsed);
                continue;
            }
            const uint64_t remaining = source->status.duration_100ns - source->clock_base;
            const double elapsed = static_cast<double>(now - source->clock_started) *
                                   static_cast<double>(kTicksPerSecond) / static_cast<double>(qpc_frequency_);
            source->status.position_100ns = source->clock_base +
                (elapsed >= static_cast<double>(remaining) ? remaining : static_cast<uint64_t>(elapsed));
            if (source->status.position_100ns != source->status.duration_100ns) { continue; }
            bool readers_ended = true;
            for (const auto& route : routes_) {
                if (route.source_id != source->id || route.cursor_frame >= source->duration_frames) { continue; }
                Output* output = find_output(route.output_id);
                if (output != nullptr && output->status.state == 1) {
                    readers_ended = false;
                    break;
                }
            }
            if (readers_ended) { source->status.state = kEnded; }
        }
    }

    /*
     * Name: ensure_cached
     * Input: A source and the absolute PCM range needed by one reader.
     * Process: Decodes missing frames once into its ring, seeking past stale unused history.
     * Output: S_OK with available PCM, including partial EOF, or an isolated failure.
     */
    HRESULT ensure_cached(Source& source, uint64_t cursor, uint32_t frames) {
        const uint64_t end = min(source.duration_frames, cursor + frames);
        if (cursor >= end) { return S_OK; }
        if (cursor < source.cache_begin ||
            (cursor > source.cache_end &&
             (source.cache_begin == source.cache_end || cursor - source.cache_end > kDecodeChunkFrames))) {
            const HRESULT result = source.decoder->seek(frames_to_ticks(cursor));
            if (FAILED(result)) { return fail_source(source, result); }
            source.cache_begin = cursor;
            source.cache_end = cursor;
            source.decoder_ended = false;
        }
        while (source.cache_end < end && !source.decoder_ended) {
            const uint64_t ring_index = source.cache_end % kCacheFrames;
            const uint32_t request = static_cast<uint32_t>(min({
                end - source.cache_end, static_cast<uint64_t>(kDecodeChunkFrames), kCacheFrames - ring_index}));
            uint32_t written = 0;
            bool ended = false;
            const HRESULT result = source.decoder->read(
                source.cache.data() + static_cast<size_t>(ring_index) * kChannels, request, written, ended);
            if (FAILED(result)) { return fail_source(source, result); }
            if (written > request || (written == 0 && !ended)) { return fail_source(source, MF_E_INVALID_STREAM_DATA); }
            source.cache_end += written;
            if (source.cache_end - source.cache_begin > kCacheFrames) { source.cache_begin = source.cache_end - kCacheFrames; }
            source.status.frames_decoded += written;
            source.status.cached_frames = source.cache_end - source.cache_begin;
            source.decoder_ended = ended;
        }
        return S_OK;
    }

    /*
     * Name: mix_route
     * Input: A Playing source, independent route, and one output's clear mix buffer.
     * Process: Rebases stale readers, reuses shared decoded PCM, and applies gain/mute.
     * Output: Accumulated audio and advancing reader counters, including muted routes.
     */
    void mix_route(Source& source, Route& route, Output& output, float* mix, uint32_t frames) {
        if (source.capture) { mix_live(source, route, output, mix, frames); return; }
        const double elapsed = static_cast<double>(qpc_now() - source.clock_started) * kSampleRate / static_cast<double>(qpc_frequency_);
        const double origin = static_cast<double>(ticks_to_frames(source.clock_base));
        route.timing.target_frames = output.timing.delay_frames;
        if (elapsed < output.timing.delay_frames) { route.timing.priming = 1; return; }
        const double desired = min(static_cast<double>(source.duration_frames), origin + elapsed - output.timing.delay_frames);
        if (!route.positioned || route.read_position < source.cache_begin || abs(desired - route.read_position) > kCacheFrames) {
            if (route.positioned) { ++route.rebases; ++route.timing.overrun_count; }
            route.read_position = desired; route.positioned = true;
            route.controller.reset(); route.fade_remaining = 128;
        }
        route.timing.priming = 0;
        route.timing.phase_error_frames = desired - route.read_position;
        const double ratio = route.controller.update(route.timing.phase_error_frames, frames, output.timing.drift_enabled != 0);
        const uint64_t cursor = static_cast<uint64_t>(route.read_position);
        if (cursor < source.duration_frames) {
            const uint32_t needed = static_cast<uint32_t>(ceil(frames * ratio)) + 2;
            if (FAILED(ensure_cached(source, cursor, needed))) { return; }
        }
        consume_pcm(source, route, mix, frames, ratio);
        if (source.decoder_ended && route.read_position >= source.cache_end) {
            route.read_position = static_cast<double>(source.duration_frames);
            route.cursor_frame = source.duration_frames;
        }
    }

    /*
     * Name: live_start
     * Input: Live source history.
     * Process: Chooses at most 50 ms of recent preroll, bounded by available history.
     * Output: Current reader start without replaying a paused backlog.
     */
    static uint64_t live_start(const Source& source) noexcept {
        return max(source.cache_begin, source.cache_end > 2400 ? source.cache_end - 2400 : uint64_t{0});
    }

    /*
     * Name: mix_live
     * Input: Live source, route, and one output block.
     * Process: Consumes shared captured PCM, rebasing stale readers and padding underruns.
     * Output: Bounded-latency audio; missing samples remain silent in the clear mix.
     */
    void mix_live(Source& source, Route& route, Output& output, float* mix, uint32_t frames) noexcept {
        const uint64_t target = live_target_frames(output.buffer_frames, output.timing.delay_frames);
        route.timing.target_frames = static_cast<uint32_t>(target);
        if (route.positioned && (route.read_position < source.cache_begin || route.read_position > source.cache_end)) {
            route.positioned = false; route.timing.priming = 1;
            ++route.rebases; ++route.timing.overrun_count;
        }
        if (!route.positioned) {
            if (source.cache_end - source.cache_begin < max<uint64_t>(target, frames + 2)) { return; }
            route.read_position = static_cast<double>(source.cache_end - target);
            route.positioned = true; route.starved = false; route.timing.priming = 0;
            route.controller.reset(); route.fade_remaining = 128;
        }
        route.timing.phase_error_frames = static_cast<double>(source.cache_end) - route.read_position - static_cast<double>(target);
        const double ratio = route.controller.update(route.timing.phase_error_frames, frames, output.timing.drift_enabled != 0);
        const uint32_t produced = consume_pcm(source, route, mix, frames, ratio);
        if (produced < frames) {
            if (!route.starved) { ++route.timing.underrun_count; ++route.timing.rebuffer_count; }
            route.timing.underrun_frames += frames - produced;
            route.starved = true; route.positioned = false; route.timing.priming = 1;
            route.controller.reset();
        }
    }

    /*
     * Name: consume_pcm
     * Input: Shared PCM history, fractional reader, destination, frame count, and ratio.
     * Process: Interpolates stereo samples and fades starts or starvation tails.
     * Output: Produced output frames; muted readers advance and telemetry remains visible.
     */
    uint32_t consume_pcm(Source& source, Route& route, float* mix, uint32_t frames, double ratio) noexcept {
        const double before = route.read_position;
        const double usable = max(0.0, static_cast<double>(source.cache_end) - route.read_position - (source.capture ? 1.0 : 0.0));
        const uint32_t available = static_cast<uint32_t>(min<double>(frames, ceil(usable / ratio)));
        uint32_t produced = 0;
        source.signal.last_peak = 0;
        for (; produced < available; ++produced) {
            const uint64_t first = static_cast<uint64_t>(route.read_position);
            if (first >= source.cache_end || first < source.cache_begin) { break; }
            const uint64_t second = min(first + 1, source.cache_end - 1);
            const double fraction = route.read_position - static_cast<double>(first);
            route.source_ramp.set_target(source.level.muted ? 0.0 : source.level.gain);
            double gain = (route.muted != 0 ? 0.0 : route.gain) * route.source_ramp.next();
            if (route.fade_remaining > 0) { gain *= 1.0 - static_cast<double>(route.fade_remaining--) / 128.0; }
            if (source.capture && available < frames) { gain *= min(1.0, static_cast<double>(available - produced) / 128.0); }
            if (gain > 0) {
                for (uint32_t channel = 0; channel < kChannels; ++channel) {
                    const float value = interpolate_sample(source.cache[static_cast<size_t>(first % kCacheFrames) * kChannels + channel],
                        source.cache[static_cast<size_t>(second % kCacheFrames) * kChannels + channel], fraction);
                    const float contribution = static_cast<float>(value * gain);
                    mix[static_cast<size_t>(produced) * kChannels + channel] += contribution;
                    source.signal.sum_squares += static_cast<double>(contribution) * contribution;
                    source.signal.last_peak = max(source.signal.last_peak, abs(static_cast<double>(contribution)));
                }
            }
            route.read_position += ratio;
        }
        source.signal.frames += produced;
        route.frames_read += static_cast<uint64_t>(route.read_position) - static_cast<uint64_t>(before);
        route.cursor_frame = static_cast<uint64_t>(route.read_position);
        route.timing.read_position_frame = route.read_position;
        route.timing.correction_ppm = route.controller.correction_ppm();
        route.timing.buffered_frames = static_cast<uint32_t>(min<uint64_t>(source.cache_end > route.cursor_frame ? source.cache_end - route.cursor_frame : 0,
            (numeric_limits<uint32_t>::max)()));
        return produced;
    }

    array<unique_ptr<Source>, kMaxSources> sources_;
    array<unique_ptr<Output>, kMaxOutputs> outputs_;
    vector<Route> routes_;
    uint64_t qpc_frequency_ = 0;
    uint32_t next_source_id_ = 1;
    uint32_t next_output_id_ = 1;
    bool com_initialized_ = false;
    bool mf_started_ = false;
};

}

/*
 * Name: RoutingEngine::Impl
 * Input: Acknowledged routing commands from public callers.
 * Process: Serializes commands and owns the dedicated MTA routing coordinator.
 * Output: Independent persistent routing with direct wake-and-join shutdown.
 */
class RoutingEngine::Impl {
public:
    /*
     * Name: Impl
     * Input: None.
     * Process: Creates an inactive worker owner.
     * Output: An implementation ready for initialization.
     */
    Impl() = default;

    /*
     * Name: ~Impl
     * Input: None.
     * Process: Requests shutdown directly, wakes the worker, and joins without allocation.
     * Output: All worker resources released before closing its command event.
     */
    ~Impl() {
        {
            const lock_guard<mutex> lock(state_mutex_);
            exit_requested_ = true;
        }
        if (command_event_ != nullptr) { SetEvent(command_event_); }
        if (worker_.joinable()) { worker_.join(); }
        if (command_event_ != nullptr) { CloseHandle(command_event_); }
    }

    Impl(const Impl&) = delete;
    Impl& operator=(const Impl&) = delete;

    /*
     * Name: initialize
     * Input: None.
     * Process: Starts the worker and waits for its COM/MF initialization result.
     * Output: The initialization HRESULT.
     */
    HRESULT initialize() {
        command_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (command_event_ == nullptr) { return HRESULT_FROM_WIN32(GetLastError()); }
        worker_ = thread(&Impl::run, this);
        unique_lock<mutex> lock(state_mutex_);
        acknowledgment_.wait(lock, [&] { return ready_; });
        return initialization_result_;
    }

    /*
     * Name: submit
     * Input: Plain command arguments valid until this method returns.
     * Process: Serializes callers and waits for the worker's acknowledgment or exit.
     * Output: Operation HRESULT with any requested result already populated.
     */
    HRESULT submit(const Command& command) {
        const lock_guard<mutex> call_lock(call_mutex_);
        unique_lock<mutex> lock(state_mutex_);
        if (worker_done_) { return FAILED(terminal_result_) ? terminal_result_ : E_ABORT; }
        command_ = command;
        pending_ = true;
        acknowledged_ = false;
        if (!SetEvent(command_event_)) {
            pending_ = false;
            return HRESULT_FROM_WIN32(GetLastError());
        }
        acknowledgment_.wait(lock, [&] { return acknowledged_ || worker_done_; });
        return acknowledged_ ? command_result_ : (FAILED(terminal_result_) ? terminal_result_ : E_ABORT);
    }

private:
    /*
     * Name: worker_body
     * Input: Synchronized command storage and render notifications.
     * Process: Owns the session and prioritizes commands among up to eight output events.
     * Output: Returns after shutdown or a terminal wait/initialization failure.
     */
    HRESULT worker_body() {
        RoutingSession session;
        const HRESULT initialized = session.initialize();
        {
            const lock_guard<mutex> lock(state_mutex_);
            initialization_result_ = initialized;
            ready_ = true;
        }
        acknowledgment_.notify_all();
        if (FAILED(initialized)) { return initialized; }
        while (true) {
            {
                const lock_guard<mutex> lock(state_mutex_);
                if (exit_requested_) { return S_OK; }
            }
            array<HANDLE, kMaxNotifications + 1> events{};
            array<Output*, kMaxNotifications> lookup{};
            array<Source*, kMaxNotifications> capture_lookup{};
            events[0] = command_event_;
            DWORD timeout = INFINITE;
            const DWORD count = session.wait_handles(events, lookup, capture_lookup, timeout);
            const DWORD waited = WaitForMultipleObjects(count, events.data(), FALSE, timeout);
            if (waited == WAIT_OBJECT_0) {
                Command command;
                {
                    const lock_guard<mutex> lock(state_mutex_);
                    if (exit_requested_) { return S_OK; }
                    if (!pending_) { continue; }
                    command = command_;
                }
                HRESULT result = S_OK;
                try {
                    result = session.execute(command);
                } catch (const bad_alloc&) {
                    result = E_OUTOFMEMORY;
                } catch (...) {
                    result = E_FAIL;
                }
                {
                    const lock_guard<mutex> lock(state_mutex_);
                    command_result_ = result;
                    pending_ = false;
                    acknowledged_ = true;
                }
                acknowledgment_.notify_all();
            } else if (waited > WAIT_OBJECT_0 && waited < WAIT_OBJECT_0 + count) {
                if (capture_lookup[waited - WAIT_OBJECT_0 - 1] != nullptr) {
                    session.service_capture(*capture_lookup[waited - WAIT_OBJECT_0 - 1]);
                    continue;
                }
                Output& output = *lookup[waited - WAIT_OBJECT_0 - 1];
                try {
                    session.service_output(output);
                } catch (const bad_alloc&) {
                    output.fail(E_OUTOFMEMORY);
                } catch (...) {
                    output.fail(E_FAIL);
                }
            } else if (waited != WAIT_TIMEOUT) {
                return waited == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError()) : E_UNEXPECTED;
            }
        }
    }

    /*
     * Name: run
     * Input: None.
     * Process: Contains worker exceptions and always releases initialization/command waiters.
     * Output: A terminal HRESULT after every decoder/output has been destroyed.
     */
    void run() noexcept {
        HRESULT result = E_FAIL;
        try {
            result = worker_body();
        } catch (const bad_alloc&) {
            result = E_OUTOFMEMORY;
        } catch (...) {
            result = E_FAIL;
        }
        {
            const lock_guard<mutex> lock(state_mutex_);
            terminal_result_ = result;
            if (!ready_) {
                initialization_result_ = result;
                ready_ = true;
            }
            worker_done_ = true;
        }
        acknowledgment_.notify_all();
    }

    HANDLE command_event_ = nullptr;
    thread worker_;
    mutex call_mutex_;
    mutex state_mutex_;
    condition_variable acknowledgment_;
    Command command_;
    HRESULT initialization_result_ = E_FAIL;
    HRESULT terminal_result_ = E_FAIL;
    HRESULT command_result_ = E_FAIL;
    bool ready_ = false;
    bool worker_done_ = false;
    bool exit_requested_ = false;
    bool pending_ = false;
    bool acknowledged_ = false;
};

/*
 * Name: RoutingEngine::RoutingEngine
 * Input: An initialized worker implementation.
 * Process: Takes exclusive worker ownership.
 * Output: An engine created through create.
 */
RoutingEngine::RoutingEngine(unique_ptr<Impl> impl) noexcept : impl_(move(impl)) {}

/*
 * Name: RoutingEngine::~RoutingEngine
 * Input: None.
 * Process: Destroys and joins the implementation.
 * Output: Fully released routing resources.
 */
RoutingEngine::~RoutingEngine() = default;

/*
 * Name: RoutingEngine::create
 * Input: Engine ownership destination.
 * Process: Initializes the worker and translates setup exceptions to HRESULTs.
 * Output: S_OK and a live empty engine, or an empty destination on failure.
 */
HRESULT RoutingEngine::create(unique_ptr<RoutingEngine>& engine) {
    engine.reset();
    try {
        auto impl = make_unique<Impl>();
        const HRESULT result = impl->initialize();
        if (FAILED(result)) { return result; }
        engine = unique_ptr<RoutingEngine>(new RoutingEngine(move(impl)));
        return S_OK;
    } catch (const bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}

/*
 * Name: RoutingEngine::add_source
 * Input: Local file path and ID destination.
 * Process: Sends an acknowledged source-creation command.
 * Output: HRESULT and a new source ID on success.
 */
HRESULT RoutingEngine::add_source(const wstring& file_path, uint32_t& id) {
    id = 0;
    Command command;
    command.kind = CommandKind::kAddSource;
    command.text = &file_path;
    command.returned_id = &id;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::add_output
 * Input: Endpoint ID and ID destination.
 * Process: Sends an acknowledged output-creation command.
 * Output: HRESULT and a new output ID on success.
 */
HRESULT RoutingEngine::add_output(const wstring& endpoint_id, uint32_t& id) {
    id = 0;
    Command command;
    command.kind = CommandKind::kAddOutput;
    command.text = &endpoint_id;
    command.returned_id = &id;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::add_process_source
 * Input: Application PID, virtual parking endpoint, and ID destination.
 * Process: Sends an acknowledged live-capture creation command.
 * Output: HRESULT and new source ID; existing playback survives failure.
 */
HRESULT RoutingEngine::add_process_source(uint32_t process_id, const wstring& parking_id, uint32_t& id) {
    id = 0;
    Command command;
    command.kind = CommandKind::kAddProcess;
    command.source_id = process_id;
    command.text = &parking_id;
    command.returned_id = &id;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::get_capture_status
 * Input: Live source ID and status destination.
 * Process: Refreshes and queries capture telemetry through the worker.
 * Output: HRESULT and copied status.
 */
HRESULT RoutingEngine::get_capture_status(uint32_t id, ProcessCaptureStatus& status) {
    Command command;
    command.kind = CommandKind::kCaptureStatus;
    command.source_id = id;
    command.capture_status = &status;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::remove_source
 * Input: Source ID.
 * Process: Sends an acknowledged source-removal command.
 * Output: The operation HRESULT.
 */
HRESULT RoutingEngine::remove_source(uint32_t id) {
    Command command;
    command.kind = CommandKind::kRemoveSource;
    command.source_id = id;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::remove_output
 * Input: Output ID.
 * Process: Sends an acknowledged output-removal command.
 * Output: The operation HRESULT.
 */
HRESULT RoutingEngine::remove_output(uint32_t id) {
    Command command;
    command.kind = CommandKind::kRemoveOutput;
    command.output_id = id;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::connect
 * Input: Source/output IDs, gain, and mute state.
 * Process: Sends an acknowledged route upsert.
 * Output: The operation HRESULT.
 */
HRESULT RoutingEngine::connect(uint32_t source_id, uint32_t output_id, double gain, bool muted) {
    Command command;
    command.kind = CommandKind::kConnect;
    command.source_id = source_id;
    command.output_id = output_id;
    command.gain = gain;
    command.muted = muted;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::disconnect
 * Input: Existing route IDs.
 * Process: Sends an acknowledged route removal.
 * Output: The operation HRESULT.
 */
HRESULT RoutingEngine::disconnect(uint32_t source_id, uint32_t output_id) {
    Command command;
    command.kind = CommandKind::kDisconnect;
    command.source_id = source_id;
    command.output_id = output_id;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::set_route_gain
 * Input: Existing route IDs and gain.
 * Process: Sends an acknowledged route gain update.
 * Output: The operation HRESULT.
 */
HRESULT RoutingEngine::set_route_gain(uint32_t source_id, uint32_t output_id, double gain) {
    Command command;
    command.kind = CommandKind::kGain;
    command.source_id = source_id;
    command.output_id = output_id;
    command.gain = gain;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::set_route_mute
 * Input: Existing route IDs and mute state.
 * Process: Sends an acknowledged route mute update.
 * Output: The operation HRESULT.
 */
HRESULT RoutingEngine::set_route_mute(uint32_t source_id, uint32_t output_id, bool muted) {
    Command command;
    command.kind = CommandKind::kMute;
    command.source_id = source_id;
    command.output_id = output_id;
    command.muted = muted;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::source_command
 * Input: Source ID, transport selector, and seek time.
 * Process: Sends an acknowledged independent transport command.
 * Output: The operation HRESULT.
 */
HRESULT RoutingEngine::source_command(uint32_t source_id, uint32_t transport, uint64_t position_100ns) {
    Command command;
    command.kind = CommandKind::kTransport;
    command.source_id = source_id;
    command.transport = transport;
    command.position = position_100ns;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::get_source_status
 * Input: Source ID and status destination.
 * Process: Requests a fresh source snapshot from the worker.
 * Output: S_OK and status, or a missing-source/engine failure.
 */
HRESULT RoutingEngine::get_source_status(uint32_t id, RoutingSourceStatus& status) {
    status = {};
    Command command;
    command.kind = CommandKind::kSourceStatus;
    command.source_id = id;
    command.source_status = &status;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::get_output_status
 * Input: Output ID and status destination.
 * Process: Requests a fresh output snapshot from the worker.
 * Output: S_OK and status, or a missing-output/engine failure.
 */
HRESULT RoutingEngine::get_output_status(uint32_t id, RoutingOutputStatus& status) {
    status = {};
    Command command;
    command.kind = CommandKind::kOutputStatus;
    command.output_id = id;
    command.output_status = &status;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::get_route_status
 * Input: Route IDs and status destination.
 * Process: Requests the connection's settings and reader counters.
 * Output: S_OK and status, or a missing-route/engine failure.
 */
HRESULT RoutingEngine::get_route_status(uint32_t source_id, uint32_t output_id, RoutingRouteStatus& status) {
    status = {};
    Command command;
    command.kind = CommandKind::kRouteStatus;
    command.source_id = source_id;
    command.output_id = output_id;
    command.route_status = &status;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::set_output_delay
 * Input: Output ID and additional normalized PCM frames.
 * Process: Sends an acknowledged delay change.
 * Output: HRESULT; delays outside zero to two seconds are rejected.
 */
HRESULT RoutingEngine::set_output_delay(uint32_t id, uint32_t frames) {
    Command command; command.kind = CommandKind::kDelay; command.output_id = id; command.position = frames;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::set_output_drift
 * Input: Output ID and correction flag.
 * Process: Sends an acknowledged controller setting.
 * Output: Operation HRESULT.
 */
HRESULT RoutingEngine::set_output_drift(uint32_t id, bool enabled) {
    Command command; command.kind = CommandKind::kDrift; command.output_id = id; command.muted = enabled;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::get_output_timing
 * Input: Output ID and timing destination.
 * Process: Copies a fresh renderer timing snapshot.
 * Output: HRESULT and telemetry.
 */
HRESULT RoutingEngine::get_output_timing(uint32_t id, OutputTimingStatus& status) {
    Command command; command.kind = CommandKind::kOutputTiming; command.output_id = id; command.output_timing = &status;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::get_route_timing
 * Input: Source/output IDs and timing destination.
 * Process: Copies fractional-reader and buffer telemetry.
 * Output: HRESULT and telemetry.
 */
HRESULT RoutingEngine::get_route_timing(uint32_t source, uint32_t output, RouteTimingStatus& status) {
    Command command; command.kind = CommandKind::kRouteTiming; command.source_id = source; command.output_id = output; command.route_timing = &status;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::get_capture_timing
 * Input: Process source ID and timing destination.
 * Process: Queries capture retry history and adaptive history capacity.
 * Output: Operation HRESULT and snapshot.
 */
HRESULT RoutingEngine::get_capture_timing(uint32_t id, CaptureTimingStatus& status) {
    Command command; command.kind = CommandKind::kCaptureTiming; command.source_id = id; command.capture_timing = &status;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::retry_output
 * Input: Existing output ID.
 * Process: Requests immediate background reopen.
 * Output: Acknowledgment HRESULT; subsequent status reports completion.
 */
HRESULT RoutingEngine::retry_output(uint32_t id) {
    Command command; command.kind = CommandKind::kRetryOutput; command.output_id = id;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::retry_capture
 * Input: Existing process source ID.
 * Process: Requests immediate background preparation for the original root.
 * Output: Acknowledgment HRESULT; subsequent status reports completion.
 */
HRESULT RoutingEngine::retry_capture(uint32_t id) {
    Command command; command.kind = CommandKind::kRetryCapture; command.source_id = id;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::set_source_level
 * Input: Source ID, normalized gain, and mute.
 * Process: Submits a source master command to the worker.
 * Output: Acknowledged HRESULT; missing IDs or invalid gains fail.
 */
HRESULT RoutingEngine::set_source_level(uint32_t id, double gain, bool muted) {
    Command command; command.kind = CommandKind::kSourceLevel; command.source_id = id; command.gain = gain; command.muted = muted;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::set_output_level
 * Input: Output ID, normalized gain, and mute.
 * Process: Submits an output master command to the worker.
 * Output: Acknowledged HRESULT; missing IDs or invalid gains fail.
 */
HRESULT RoutingEngine::set_output_level(uint32_t id, double gain, bool muted) {
    Command command; command.kind = CommandKind::kOutputLevel; command.output_id = id; command.gain = gain; command.muted = muted;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::get_source_level
 * Input: Source ID and level destination.
 * Process: Reads the source target through a synchronous worker command.
 * Output: HRESULT and copied level; missing IDs fail.
 */
HRESULT RoutingEngine::get_source_level(uint32_t id, AudioLevelStatus& level) {
    Command command; command.kind = CommandKind::kGetSourceLevel; command.source_id = id; command.level = &level;
    return impl_->submit(command);
}
/*
 * Name: RoutingEngine::get_output_level
 * Input: Output ID and level destination.
 * Process: Reads the output target through a synchronous worker command.
 * Output: HRESULT and copied level; missing IDs fail.
 */
HRESULT RoutingEngine::get_output_level(uint32_t id, AudioLevelStatus& level) {
    Command command; command.kind = CommandKind::kGetOutputLevel; command.output_id = id; command.level = &level;
    return impl_->submit(command);
}

/*
 * Name: RoutingEngine::get_source_signal
 * Input: Source ID and caller-owned signal destination.
 * Process: Waits for the worker's post-master signal snapshot.
 * Output: HRESULT and cumulative routed PCM measurements.
 */
HRESULT RoutingEngine::get_source_signal(uint32_t id, SourceSignalStatus& signal) {
    Command command; command.kind = CommandKind::kSourceSignal; command.source_id = id; command.signal = &signal;
    return impl_->submit(command);
}

}

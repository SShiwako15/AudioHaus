#include "FilePlayer.h"

#include <audioclient.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mmdeviceapi.h>
#include <propvarutil.h>
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

constexpr uint32_t kStopped = 0;
constexpr uint32_t kPlaying = 1;
constexpr uint32_t kPaused = 2;
constexpr uint32_t kEnded = 3;
constexpr uint32_t kFailed = 4;
constexpr uint32_t kSampleRate = 48000;
constexpr uint32_t kChannels = 2;
constexpr uint32_t kFrameBytes = kChannels * sizeof(float);
constexpr uint64_t kTicksPerSecond = 10000000;
constexpr DWORD kRenderTimeoutMs = 2000;
constexpr DWORD kMaxSampleBytes = 16 * 1024 * 1024;
constexpr uint32_t kMaxEmptyReads = 256;
constexpr DWORD kAllStreams = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
constexpr DWORD kFirstAudioStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
constexpr DWORD kMediaSource = static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE);

/*
 * Name: PlayerCommand
 * Input: A public transport or status request.
 * Process: Identifies the operation dispatched to the player worker.
 * Output: An internal command value without COM ownership.
 */
enum class PlayerCommand { kPlay, kPause, kStop, kSeek, kStatus };

/*
 * Name: current_qpc
 * Input: None.
 * Process: Reads the performance counter at a successful stream boundary.
 * Output: QPC ticks describing software timing.
 */
uint64_t current_qpc() noexcept {
    LARGE_INTEGER counter{};
    QueryPerformanceCounter(&counter);
    return static_cast<uint64_t>(counter.QuadPart);
}

/*
 * Name: frames_to_ticks
 * Input: A normalized PCM frame count.
 * Process: Converts frames without multiplying the entire count by ten million.
 * Output: Duration in 100-nanosecond units, rounded down.
 */
uint64_t frames_to_ticks(uint64_t frames) noexcept {
    return frames / kSampleRate * kTicksPerSecond +
           frames % kSampleRate * kTicksPerSecond / kSampleRate;
}

/*
 * Name: ticks_to_frames
 * Input: Nonnegative media time and whether to round upward.
 * Process: Converts time using overflow-safe quotient and remainder arithmetic.
 * Output: Frames, rounded upward for seek trimming or to nearest for timestamps.
 */
uint64_t ticks_to_frames(uint64_t ticks, bool round_up) noexcept {
    const uint64_t rounding = round_up ? kTicksPerSecond - 1 : kTicksPerSecond / 2;
    return ticks / kTicksPerSecond * kSampleRate +
           (ticks % kTicksPerSecond * kSampleRate + rounding) / kTicksPerSecond;
}

/*
 * Name: PlaybackSession
 * Input: Per-file gain and initialization parameters supplied on the MTA worker.
 * Process: Streams one decoded sample through one fixed-size render staging buffer.
 * Output: Independent local-file playback with worker-owned COM resources.
 */
class PlaybackSession {
public:
    /*
     * Name: PlaybackSession
     * Input: Scalar gain already validated to 0..1.
     * Process: Stores configuration without creating COM resources.
     * Output: An unopened session.
     */
    explicit PlaybackSession(double gain) noexcept : gain_(static_cast<float>(gain)) {}

    /*
     * Name: ~PlaybackSession
     * Input: None.
     * Process: Stops audio, releases decoder/render COM objects, and balances MF/COM.
     * Output: Complete worker-thread cleanup, including partial initialization.
     */
    ~PlaybackSession() {
        if (started_) {
            client_->Stop();
        }
        release_sample();
        reader_.Reset();
        render_.Reset();
        client_.Reset();
        if (render_event_ != nullptr) {
            CloseHandle(render_event_);
        }
        if (mf_started_) {
            MFShutdown();
        }
        if (com_initialized_) {
            CoUninitialize();
        }
    }

    PlaybackSession(const PlaybackSession&) = delete;
    PlaybackSession& operator=(const PlaybackSession&) = delete;

    /*
     * Name: initialize
     * Input: Local file path and selected output endpoint ID.
     * Process: Opens seekable decoding and shared-mode rendering without starting it.
     * Output: S_OK and Stopped state, or a preserved failure HRESULT.
     */
    HRESULT initialize(const wstring& file_path, const wstring& endpoint_id) {
        HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(result)) {
            return fail(result);
        }
        com_initialized_ = true;
        result = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (FAILED(result)) {
            return fail(result);
        }
        mf_started_ = true;

        const DWORD attributes = GetFileAttributesW(file_path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) {
            return fail(HRESULT_FROM_WIN32(GetLastError()));
        }
        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
            return fail(E_INVALIDARG);
        }
        result = MFCreateSourceReaderFromURL(file_path.c_str(), nullptr, reader_.GetAddressOf());
        if (FAILED(result)) {
            return fail(result);
        }
        result = initialize_decoder();
        if (FAILED(result)) {
            return fail(result);
        }
        result = initialize_renderer(endpoint_id);
        if (FAILED(result)) {
            return fail(result);
        }
        return reset_position(0, kStopped);
    }

    /*
     * Name: execute
     * Input: Transport command and optional seek position.
     * Process: Applies the command entirely on the worker thread.
     * Output: HRESULT; status queries succeed even after an asynchronous failure.
     */
    HRESULT execute(PlayerCommand command, uint64_t position) {
        if (command == PlayerCommand::kStatus) {
            if (status_.state == kPlaying || status_.state == kPaused) {
                refresh_position();
            }
            return S_OK;
        }
        if (status_.state == kFailed) {
            return static_cast<HRESULT>(status_.hresult);
        }
        switch (command) {
        case PlayerCommand::kPlay: return play();
        case PlayerCommand::kPause: return pause();
        case PlayerCommand::kStop: return reset_position(0, kStopped);
        case PlayerCommand::kSeek: {
            const uint64_t clamped = min(position, status_.duration_100ns);
            uint32_t state = status_.state;
            if (state == kEnded && clamped < status_.duration_100ns) {
                state = kStopped;
            }
            return reset_position(clamped, state);
        }
        default: return E_INVALIDARG;
        }
    }

    /*
     * Name: service_render
     * Input: A render-event notification.
     * Process: Refreshes the stall deadline and supplies available PCM to WASAPI.
     * Output: S_OK or a failure recorded in the status snapshot.
     */
    HRESULT service_render() {
        last_event_tick_ = GetTickCount64();
        return fill_renderer();
    }

    /*
     * Name: status
     * Input: None.
     * Process: Copies the worker's current status.
     * Output: A snapshot with no retained native pointers.
     */
    FilePlayerStatus status() const noexcept { return status_; }

    /*
     * Name: render_event
     * Input: None.
     * Process: Reads the renderer notification handle without transferring ownership.
     * Output: The initialized WASAPI event.
     */
    HANDLE render_event() const noexcept { return render_event_; }

    /*
     * Name: wait_timeout
     * Input: Current playback state.
     * Process: Computes the remaining event deadline without extending it on commands.
     * Output: Remaining milliseconds, or INFINITE when playback is idle.
     */
    DWORD wait_timeout() const noexcept {
        if (status_.state != kPlaying) {
            return INFINITE;
        }
        const uint64_t elapsed = GetTickCount64() - last_event_tick_;
        return elapsed >= kRenderTimeoutMs ? 0 : kRenderTimeoutMs - static_cast<DWORD>(elapsed);
    }

    /*
     * Name: fail
     * Input: A failing HRESULT.
     * Process: Preserves the first failure and stops a running output safely.
     * Output: The failure code; Failed remains queryable until destruction.
     */
    HRESULT fail(HRESULT result) noexcept {
        if (status_.state != kFailed) {
            status_.hresult = static_cast<uint32_t>(result);
        }
        status_.state = kFailed;
        if (started_) {
            stop_renderer();
        }
        return result;
    }

private:
    /*
     * Name: initialize_decoder
     * Input: An opened synchronous source reader.
     * Process: Selects audio, negotiates float stereo PCM, and requires duration/seeking.
     * Output: S_OK or the exact initialization HRESULT.
     */
    HRESULT initialize_decoder() {
        HRESULT result = reader_->SetStreamSelection(kAllStreams, FALSE);
        if (FAILED(result)) { return result; }
        result = reader_->SetStreamSelection(kFirstAudioStream, TRUE);
        if (FAILED(result)) { return result; }

        ComPtr<IMFMediaType> media_type;
        result = MFCreateMediaType(media_type.GetAddressOf());
        if (FAILED(result)) { return result; }
        result = media_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        if (FAILED(result)) { return result; }
        result = media_type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float);
        if (FAILED(result)) { return result; }
        const array<pair<const GUID*, uint32_t>, 6> attributes = {{
            {&MF_MT_AUDIO_NUM_CHANNELS, kChannels},
            {&MF_MT_AUDIO_SAMPLES_PER_SECOND, kSampleRate},
            {&MF_MT_AUDIO_BITS_PER_SAMPLE, 32},
            {&MF_MT_AUDIO_BLOCK_ALIGNMENT, kFrameBytes},
            {&MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kSampleRate * kFrameBytes},
            {&MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE}
        }};
        for (const auto& attribute : attributes) {
            result = media_type->SetUINT32(*attribute.first, attribute.second);
            if (FAILED(result)) { return result; }
        }
        result = reader_->SetCurrentMediaType(kFirstAudioStream, nullptr,
                                               media_type.Get());
        if (FAILED(result)) { return result; }
        result = verify_media_type();
        if (FAILED(result)) { return result; }

        PROPVARIANT duration{};
        const unique_ptr<PROPVARIANT, decltype(&PropVariantClear)> clear_duration(&duration, &PropVariantClear);
        result = reader_->GetPresentationAttribute(kMediaSource,
                                                    MF_PD_DURATION, &duration);
        if (FAILED(result)) { return result; }
        if (duration.vt != VT_UI8 || duration.uhVal.QuadPart == 0 ||
            duration.uhVal.QuadPart > static_cast<uint64_t>(numeric_limits<LONGLONG>::max())) {
            return MF_E_INVALID_FILE_FORMAT;
        }
        status_.duration_100ns = duration.uhVal.QuadPart;

        PROPVARIANT characteristics{};
        const unique_ptr<PROPVARIANT, decltype(&PropVariantClear)> clear_characteristics(
            &characteristics, &PropVariantClear);
        result = reader_->GetPresentationAttribute(kMediaSource,
            MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS, &characteristics);
        if (FAILED(result)) { return result; }
        if (characteristics.vt != VT_UI4 || (characteristics.ulVal & MFMEDIASOURCE_CAN_SEEK) == 0) {
            return MF_E_INVALIDREQUEST;
        }
        return S_OK;
    }

    /*
     * Name: verify_media_type
     * Input: The source reader's current audio media type.
     * Process: Checks that decoder conversion still produces the required PCM layout.
     * Output: S_OK or a media-type/query HRESULT.
     */
    HRESULT verify_media_type() {
        ComPtr<IMFMediaType> media_type;
        HRESULT result = reader_->GetCurrentMediaType(kFirstAudioStream,
                                                       media_type.GetAddressOf());
        if (FAILED(result)) { return result; }
        GUID major{};
        GUID subtype{};
        result = media_type->GetGUID(MF_MT_MAJOR_TYPE, &major);
        if (FAILED(result)) { return result; }
        result = media_type->GetGUID(MF_MT_SUBTYPE, &subtype);
        if (FAILED(result)) { return result; }
        if (major != MFMediaType_Audio || subtype != MFAudioFormat_Float) {
            return MF_E_INVALIDMEDIATYPE;
        }
        const array<pair<const GUID*, uint32_t>, 4> required = {{
            {&MF_MT_AUDIO_NUM_CHANNELS, kChannels},
            {&MF_MT_AUDIO_SAMPLES_PER_SECOND, kSampleRate},
            {&MF_MT_AUDIO_BITS_PER_SAMPLE, 32},
            {&MF_MT_AUDIO_BLOCK_ALIGNMENT, kFrameBytes}
        }};
        for (const auto& attribute : required) {
            UINT32 value = 0;
            result = media_type->GetUINT32(*attribute.first, &value);
            if (FAILED(result)) { return result; }
            if (value != attribute.second) { return MF_E_INVALIDMEDIATYPE; }
        }
        return S_OK;
    }

    /*
     * Name: initialize_renderer
     * Input: The selected active render endpoint ID.
     * Process: Opens shared event-driven Media audio and allocates fixed staging space.
     * Output: S_OK or an endpoint, format, or allocation failure.
     */
    HRESULT initialize_renderer(const wstring& endpoint_id) {
        ComPtr<IMMDeviceEnumerator> enumerator;
        HRESULT result = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                          IID_PPV_ARGS(enumerator.GetAddressOf()));
        if (FAILED(result)) { return result; }
        ComPtr<IMMDevice> device;
        result = enumerator->GetDevice(endpoint_id.c_str(), device.GetAddressOf());
        if (FAILED(result)) { return result; }
        ComPtr<IMMEndpoint> endpoint;
        result = device.As(&endpoint);
        if (FAILED(result)) { return result; }
        EDataFlow flow = eAll;
        result = endpoint->GetDataFlow(&flow);
        if (FAILED(result)) { return result; }
        if (flow != eRender) { return AUDCLNT_E_WRONG_ENDPOINT_TYPE; }
        DWORD state = 0;
        result = device->GetState(&state);
        if (FAILED(result)) { return result; }
        if (state != DEVICE_STATE_ACTIVE) { return AUDCLNT_E_DEVICE_INVALIDATED; }
        result = device->Activate(__uuidof(IAudioClient2), CLSCTX_ALL, nullptr,
                                  reinterpret_cast<void**>(client_.GetAddressOf()));
        if (FAILED(result)) { return result; }

        AudioClientProperties properties{};
        properties.cbSize = sizeof(properties);
        properties.eCategory = AudioCategory_Media;
        result = client_->SetClientProperties(&properties);
        if (FAILED(result)) { return result; }
        render_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (render_event_ == nullptr) { return HRESULT_FROM_WIN32(GetLastError()); }

        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
        format.nChannels = static_cast<WORD>(kChannels);
        format.nSamplesPerSec = kSampleRate;
        format.wBitsPerSample = 32;
        format.nBlockAlign = static_cast<WORD>(kFrameBytes);
        format.nAvgBytesPerSec = kSampleRate * kFrameBytes;
        const DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                            AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        result = client_->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, &format, nullptr);
        if (FAILED(result)) { return result; }
        result = client_->SetEventHandle(render_event_);
        if (FAILED(result)) { return result; }
        result = client_->GetBufferSize(&buffer_frames_);
        if (FAILED(result)) { return result; }
        if (buffer_frames_ == 0) { return E_UNEXPECTED; }
        result = client_->GetService(IID_PPV_ARGS(render_.GetAddressOf()));
        if (FAILED(result)) { return result; }
        staging_.resize(static_cast<size_t>(buffer_frames_) * kChannels);
        return S_OK;
    }

    /*
     * Name: release_sample
     * Input: The currently retained decoded buffer, if any.
     * Process: Unlocks and releases decoded PCM before replacing or flushing it.
     * Output: S_OK or Unlock's HRESULT; ownership is always cleared.
     */
    HRESULT release_sample() noexcept {
        HRESULT result = S_OK;
        if (decoded_bytes_ != nullptr) {
            result = decoded_buffer_->Unlock();
        }
        decoded_bytes_ = nullptr;
        decoded_frames_ = 0;
        decoded_offset_ = 0;
        decoded_buffer_.Reset();
        return result;
    }

    /*
     * Name: load_sample
     * Input: Decoder state and the current seek-relative PCM cursor.
     * Process: Reads one bounded sample, handles flags, and trims timestamp-leading PCM.
     * Output: S_OK with available PCM, S_FALSE at EOF, or a decoding failure.
     */
    HRESULT load_sample() {
        HRESULT result = release_sample();
        if (FAILED(result)) { return result; }
        for (uint32_t attempt = 0; attempt < kMaxEmptyReads; ++attempt) {
            if (decoder_eof_) { return S_FALSE; }
            DWORD flags = 0;
            LONGLONG timestamp = 0;
            ComPtr<IMFSample> sample;
            result = reader_->ReadSample(kFirstAudioStream, 0, nullptr,
                                         &flags, &timestamp, sample.GetAddressOf());
            if (FAILED(result)) { return result; }
            if ((flags & MF_SOURCE_READERF_ERROR) != 0) { return E_FAIL; }
            if ((flags & (MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED |
                          MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED)) != 0) {
                result = verify_media_type();
                if (FAILED(result)) { return result; }
            }
            decoder_eof_ = (flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0;
            if (!sample) { continue; }

            ComPtr<IMFMediaBuffer> buffer;
            result = sample->ConvertToContiguousBuffer(buffer.GetAddressOf());
            if (FAILED(result)) { return result; }
            sample.Reset();
            DWORD length = 0;
            result = buffer->GetCurrentLength(&length);
            if (FAILED(result)) { return result; }
            if (length == 0) { continue; }
            if (length > kMaxSampleBytes || length % kFrameBytes != 0) {
                return MF_E_INVALID_STREAM_DATA;
            }
            const uint64_t sample_frames = length / kFrameBytes;
            uint64_t skip = 0;
            uint64_t sample_frame = 0;
            if (timestamp >= 0 && static_cast<uint64_t>(timestamp) >= segment_position_) {
                sample_frame = ticks_to_frames(static_cast<uint64_t>(timestamp) - segment_position_, false);
                if (sample_frame < decode_cursor_frames_) {
                    skip = decode_cursor_frames_ - sample_frame;
                }
            } else {
                const uint64_t distance = timestamp < 0
                    ? segment_position_ + static_cast<uint64_t>(-(timestamp + 1)) + 1
                    : segment_position_ - static_cast<uint64_t>(timestamp);
                skip = ticks_to_frames(distance, true);
                if (skip < sample_frames) {
                    skip += min(decode_cursor_frames_, sample_frames - skip);
                }
            }
            if (skip >= sample_frames) { continue; }
            pending_gap_frames_ = sample_frame > decode_cursor_frames_
                ? sample_frame - decode_cursor_frames_ : 0;
            BYTE* bytes = nullptr;
            result = buffer->Lock(&bytes, nullptr, nullptr);
            if (FAILED(result)) { return result; }
            decoded_buffer_ = move(buffer);
            decoded_bytes_ = bytes;
            decoded_frames_ = sample_frames;
            decoded_offset_ = skip;
            return S_OK;
        }
        return MF_E_INVALID_STREAM_DATA;
    }

    /*
     * Name: fill_staging
     * Input: Requested render capacity and an output frame count.
     * Process: Decodes ahead only into bounded staging, applying gain and timeline gaps.
     * Output: S_OK with available frames, including a final partial packet, or failure.
     */
    HRESULT fill_staging(UINT32 capacity, UINT32& written) {
        written = 0;
        while (written < capacity) {
            if (decoded_offset_ == decoded_frames_ && pending_gap_frames_ == 0) {
                const HRESULT result = load_sample();
                if (FAILED(result)) { return result; }
                if (result == S_FALSE) { break; }
            }
            const UINT32 available = capacity - written;
            if (pending_gap_frames_ != 0) {
                const UINT32 frames = static_cast<UINT32>(min<uint64_t>(available, pending_gap_frames_));
                fill_n(staging_.data() + static_cast<size_t>(written) * kChannels,
                       static_cast<size_t>(frames) * kChannels, 0.0f);
                pending_gap_frames_ -= frames;
                decode_cursor_frames_ += frames;
                written += frames;
                continue;
            }
            const UINT32 frames = static_cast<UINT32>(min<uint64_t>(
                available, decoded_frames_ - decoded_offset_));
            const BYTE* source = decoded_bytes_ + decoded_offset_ * kFrameBytes;
            for (size_t index = 0; index < static_cast<size_t>(frames) * kChannels; ++index) {
                float sample = 0.0f;
                memcpy(&sample, source + index * sizeof(float), sizeof(float));
                staging_[static_cast<size_t>(written) * kChannels + index] =
                    isfinite(sample) ? clamp(sample, -1.0f, 1.0f) * gain_ : 0.0f;
            }
            decoded_offset_ += frames;
            decode_cursor_frames_ += frames;
            written += frames;
        }
        return S_OK;
    }

    /*
     * Name: update_position
     * Input: Current queued WASAPI frames.
     * Process: Excludes queued and decoded read-ahead frames from source progress.
     * Output: Updated source position clamped to the known file duration.
     */
    void update_position(UINT32 padding) noexcept {
        const uint64_t consumed = segment_submitted_frames_ -
                                  min<uint64_t>(padding, segment_submitted_frames_);
        status_.position_100ns = min(status_.duration_100ns,
                                     segment_position_ + frames_to_ticks(consumed));
    }

    /*
     * Name: refresh_position
     * Input: An initialized renderer.
     * Process: Reads queued frames without decoding additional samples.
     * Output: S_OK and refreshed progress, or a query failure recorded as Failed.
     */
    HRESULT refresh_position() noexcept {
        UINT32 padding = 0;
        const HRESULT result = client_->GetCurrentPadding(&padding);
        if (FAILED(result)) { return fail(result); }
        if (padding > buffer_frames_) { return fail(E_UNEXPECTED); }
        update_position(padding);
        return S_OK;
    }

    /*
     * Name: fill_renderer
     * Input: Current source, staging, and renderer state.
     * Process: Decodes before acquiring a WASAPI buffer, submits PCM, and drains EOF.
     * Output: S_OK or a recorded failure; Ended occurs only after all queued PCM drains.
     */
    HRESULT fill_renderer() {
        UINT32 padding = 0;
        HRESULT result = client_->GetCurrentPadding(&padding);
        if (FAILED(result)) { return fail(result); }
        if (padding > buffer_frames_) { return fail(E_UNEXPECTED); }
        update_position(padding);
        const UINT32 available = buffer_frames_ - padding;
        UINT32 frames = 0;
        result = fill_staging(available, frames);
        if (FAILED(result)) { return fail(result); }
        if (frames != 0) {
            BYTE* bytes = nullptr;
            result = render_->GetBuffer(frames, &bytes);
            if (FAILED(result)) { return fail(result); }
            memcpy(bytes, staging_.data(), static_cast<size_t>(frames) * kFrameBytes);
            result = render_->ReleaseBuffer(frames, 0);
            if (FAILED(result)) { return fail(result); }
            segment_submitted_frames_ += frames;
            status_.frames_submitted += frames;
        } else if (padding == 0 && decoder_eof_ && decoded_offset_ == decoded_frames_ &&
                   pending_gap_frames_ == 0) {
            if (started_) {
                result = stop_renderer();
                if (FAILED(result)) { return fail(result); }
            }
            status_.state = kEnded;
        }
        return S_OK;
    }

    /*
     * Name: stop_renderer
     * Input: A running WASAPI stream.
     * Process: Stops the device while retaining its queued frames.
     * Output: S_OK and a conservative stop timestamp, or the Windows HRESULT.
     */
    HRESULT stop_renderer() noexcept {
        const uint64_t stop_qpc = current_qpc();
        const HRESULT result = client_->Stop();
        if (SUCCEEDED(result)) {
            started_ = false;
            status_.stop_qpc = stop_qpc;
        }
        return result;
    }

    /*
     * Name: play
     * Input: Current transport state.
     * Process: Primes available PCM, starts or resumes WASAPI, and restarts ended files.
     * Output: S_OK or a recorded decoder/render failure.
     */
    HRESULT play() {
        if (status_.state == kPlaying) { return S_OK; }
        if (status_.state == kEnded) {
            const HRESULT result = reset_position(0, kStopped);
            if (FAILED(result)) { return result; }
        }
        status_.state = kPlaying;
        HRESULT result = fill_renderer();
        if (FAILED(result) || status_.state == kEnded) { return result; }
        result = client_->Start();
        if (FAILED(result)) { return fail(result); }
        started_ = true;
        status_.start_qpc = current_qpc();
        last_event_tick_ = GetTickCount64();
        return S_OK;
    }

    /*
     * Name: pause
     * Input: Current transport state.
     * Process: Stops active playback and measures its final frozen padding.
     * Output: S_OK with preserved queued PCM, or a recorded device failure.
     */
    HRESULT pause() noexcept {
        if (status_.state != kPlaying) { return S_OK; }
        const HRESULT result = stop_renderer();
        if (FAILED(result)) { return fail(result); }
        status_.state = kPaused;
        return refresh_position();
    }

    /*
     * Name: reset_position
     * Input: Clamped media position and desired transport state.
     * Process: Stops/resets rendering, flushes decoding, and seeks before optional resume.
     * Output: HRESULT with position reset; lifetime submitted frames remain monotonic.
     */
    HRESULT reset_position(uint64_t position, uint32_t state) {
        HRESULT result = S_OK;
        if (started_) {
            result = stop_renderer();
            if (FAILED(result)) { return fail(result); }
        }
        result = client_->Reset();
        if (FAILED(result)) { return fail(result); }
        result = release_sample();
        if (FAILED(result)) { return fail(result); }
        result = reader_->Flush(kFirstAudioStream);
        if (FAILED(result)) { return fail(result); }
        PROPVARIANT target{};
        target.vt = VT_I8;
        target.hVal.QuadPart = static_cast<LONGLONG>(position);
        result = reader_->SetCurrentPosition(GUID_NULL, target);
        if (FAILED(result)) { return fail(result); }
        segment_position_ = position;
        segment_submitted_frames_ = 0;
        decode_cursor_frames_ = 0;
        pending_gap_frames_ = 0;
        decoder_eof_ = position == status_.duration_100ns;
        status_.position_100ns = position;
        status_.state = state == kPlaying ? kStopped : state;
        return state == kPlaying ? play() : S_OK;
    }

    FilePlayerStatus status_;
    ComPtr<IMFSourceReader> reader_;
    ComPtr<IMFMediaBuffer> decoded_buffer_;
    ComPtr<IAudioClient2> client_;
    ComPtr<IAudioRenderClient> render_;
    HANDLE render_event_ = nullptr;
    vector<float> staging_;
    BYTE* decoded_bytes_ = nullptr;
    uint64_t decoded_frames_ = 0;
    uint64_t decoded_offset_ = 0;
    uint64_t pending_gap_frames_ = 0;
    uint64_t decode_cursor_frames_ = 0;
    uint64_t segment_position_ = 0;
    uint64_t segment_submitted_frames_ = 0;
    uint64_t last_event_tick_ = 0;
    UINT32 buffer_frames_ = 0;
    float gain_ = 1.0f;
    bool com_initialized_ = false;
    bool mf_started_ = false;
    bool started_ = false;
    bool decoder_eof_ = false;
};

}

/*
 * Name: FilePlayer::Impl
 * Input: Local file settings and synchronized public commands.
 * Process: Owns one worker, one command event, and acknowledged command/status storage.
 * Output: Serialized transport operations without sharing COM interfaces across threads.
 */
class FilePlayer::Impl {
public:
    /*
     * Name: Impl
     * Input: File path, output ID, and gain.
     * Process: Copies immutable initialization settings.
     * Output: An implementation ready to start its worker.
     */
    Impl(const wstring& file_path, const wstring& endpoint_id, double gain)
        : file_path_(file_path), endpoint_id_(endpoint_id), gain_(gain) {}

    /*
     * Name: ~Impl
     * Input: None.
     * Process: Sets shutdown directly, wakes the worker, and joins without queuing commands.
     * Output: A terminated worker and released command event; no shutdown allocation.
     */
    ~Impl() {
        {
            const lock_guard<mutex> lock(state_mutex_);
            exit_requested_ = true;
        }
        if (command_event_ != nullptr) {
            SetEvent(command_event_);
        }
        if (worker_.joinable()) {
            worker_.join();
        }
        if (command_event_ != nullptr) {
            CloseHandle(command_event_);
        }
    }

    Impl(const Impl&) = delete;
    Impl& operator=(const Impl&) = delete;

    /*
     * Name: initialize
     * Input: Copied initialization settings.
     * Process: Starts the worker and waits until decoder/output initialization completes.
     * Output: The worker's initialization HRESULT.
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
     * Input: A command, seek position, and optional status destination.
     * Process: Serializes callers, wakes the worker, and waits for acknowledgment or exit.
     * Output: Command HRESULT and optional immutable snapshot.
     */
    HRESULT submit(PlayerCommand command, uint64_t position = 0, FilePlayerStatus* status = nullptr) {
        const lock_guard<mutex> call_lock(call_mutex_);
        unique_lock<mutex> lock(state_mutex_);
        if (worker_done_) {
            if (status != nullptr) { *status = cached_status_; }
            return command == PlayerCommand::kStatus ? S_OK : terminal_result();
        }
        command_ = command;
        seek_position_ = position;
        command_pending_ = true;
        command_acknowledged_ = false;
        if (!SetEvent(command_event_)) {
            command_pending_ = false;
            return HRESULT_FROM_WIN32(GetLastError());
        }
        acknowledgment_.wait(lock, [&] { return command_acknowledged_ || worker_done_; });
        if (status != nullptr) { *status = cached_status_; }
        if (command == PlayerCommand::kStatus) { return S_OK; }
        return command_acknowledged_ ? command_result_ : terminal_result();
    }

private:
    /*
     * Name: terminal_result
     * Input: Cached status while its mutex is held.
     * Process: Retains the worker's failure or describes a cleanly stopped worker.
     * Output: A failing HRESULT for commands after worker exit.
     */
    HRESULT terminal_result() const noexcept {
        const HRESULT result = static_cast<HRESULT>(cached_status_.hresult);
        return FAILED(result) ? result : E_ABORT;
    }

    /*
     * Name: cache_status
     * Input: Worker-owned session status.
     * Process: Copies the snapshot under the command/status mutex.
     * Output: Updated shared status without exposing native resources.
     */
    void cache_status(const PlaybackSession& session) {
        const lock_guard<mutex> lock(state_mutex_);
        cached_status_ = session.status();
    }

    /*
     * Name: worker_body
     * Input: Immutable settings and synchronized command storage.
     * Process: Opens a session and prioritizes command events over render notifications.
     * Output: Returns only after shutdown or initialization/wait failure.
     */
    void worker_body() {
        PlaybackSession session(gain_);
        const HRESULT initialized = session.initialize(file_path_, endpoint_id_);
        {
            const lock_guard<mutex> lock(state_mutex_);
            initialization_result_ = initialized;
            cached_status_ = session.status();
            ready_ = true;
        }
        acknowledgment_.notify_all();
        if (FAILED(initialized)) { return; }

        while (true) {
            {
                const lock_guard<mutex> lock(state_mutex_);
                if (exit_requested_) { return; }
            }
            if (session.wait_timeout() == 0) {
                session.fail(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                cache_status(session);
            }
            const bool playing = session.status().state == kPlaying;
            const array<HANDLE, 2> events = {command_event_, session.render_event()};
            const DWORD wait_result = WaitForMultipleObjects(playing ? 2 : 1,
                events.data(), FALSE, session.wait_timeout());
            if (wait_result == WAIT_OBJECT_0) {
                PlayerCommand command = PlayerCommand::kStatus;
                uint64_t position = 0;
                {
                    const lock_guard<mutex> lock(state_mutex_);
                    if (exit_requested_) { return; }
                    if (!command_pending_) { continue; }
                    command = command_;
                    position = seek_position_;
                }
                const HRESULT result = session.execute(command, position);
                {
                    const lock_guard<mutex> lock(state_mutex_);
                    cached_status_ = session.status();
                    command_result_ = result;
                    command_pending_ = false;
                    command_acknowledged_ = true;
                }
                acknowledgment_.notify_all();
            } else if (playing && wait_result == WAIT_OBJECT_0 + 1) {
                session.service_render();
                cache_status(session);
            } else if (wait_result == WAIT_TIMEOUT) {
                session.fail(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                cache_status(session);
            } else {
                session.fail(wait_result == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError()) : E_UNEXPECTED);
                cache_status(session);
                return;
            }
        }
    }

    /*
     * Name: run
     * Input: None.
     * Process: Contains worker exceptions and always wakes initialization/command waiters.
     * Output: Final worker status after all session resources are released.
     */
    void run() noexcept {
        HRESULT exception_result = S_OK;
        try {
            worker_body();
        } catch (const bad_alloc&) {
            exception_result = E_OUTOFMEMORY;
        } catch (...) {
            exception_result = E_FAIL;
        }
        {
            const lock_guard<mutex> lock(state_mutex_);
            if (FAILED(exception_result)) {
                cached_status_.state = kFailed;
                cached_status_.hresult = static_cast<uint32_t>(exception_result);
            }
            if (!ready_) {
                initialization_result_ = FAILED(exception_result) ? exception_result : E_FAIL;
                ready_ = true;
            }
            worker_done_ = true;
        }
        acknowledgment_.notify_all();
    }

    wstring file_path_;
    wstring endpoint_id_;
    double gain_;
    HANDLE command_event_ = nullptr;
    thread worker_;
    mutex call_mutex_;
    mutex state_mutex_;
    condition_variable acknowledgment_;
    FilePlayerStatus cached_status_;
    PlayerCommand command_ = PlayerCommand::kStatus;
    uint64_t seek_position_ = 0;
    HRESULT initialization_result_ = E_FAIL;
    HRESULT command_result_ = E_FAIL;
    bool ready_ = false;
    bool worker_done_ = false;
    bool exit_requested_ = false;
    bool command_pending_ = false;
    bool command_acknowledged_ = false;
};

/*
 * Name: FilePlayer::FilePlayer
 * Input: An initialized implementation.
 * Process: Takes ownership without moving COM objects between threads.
 * Output: A stopped player with a live worker.
 */
FilePlayer::FilePlayer(unique_ptr<Impl> impl) noexcept : impl_(move(impl)) {}

/*
 * Name: FilePlayer::~FilePlayer
 * Input: None.
 * Process: Destroys the implementation, waking and joining its worker.
 * Output: Fully released playback resources.
 */
FilePlayer::~FilePlayer() = default;

/*
 * Name: FilePlayer::open
 * Input: Local path, output ID, bounded gain, and a player ownership destination.
 * Process: Starts initialization and transfers ownership only after it succeeds.
 * Output: HRESULT; allocation/thread exceptions are translated and output stays empty.
 */
HRESULT FilePlayer::open(const wstring& file_path, const wstring& endpoint_id,
                         double gain, unique_ptr<FilePlayer>& player) {
    player.reset();
    if (file_path.empty() || endpoint_id.empty() || file_path.find(L'\0') != wstring::npos ||
        endpoint_id.find(L'\0') != wstring::npos || !isfinite(gain) || gain < 0.0 || gain > 1.0) {
        return E_INVALIDARG;
    }
    try {
        auto impl = make_unique<Impl>(file_path, endpoint_id, gain);
        const HRESULT result = impl->initialize();
        if (FAILED(result)) { return result; }
        player = unique_ptr<FilePlayer>(new FilePlayer(move(impl)));
        return S_OK;
    } catch (const bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}

/*
 * Name: FilePlayer::play
 * Input: None.
 * Process: Sends an acknowledged Play command to the worker.
 * Output: The transport operation's HRESULT.
 */
HRESULT FilePlayer::play() { return impl_->submit(PlayerCommand::kPlay); }

/*
 * Name: FilePlayer::pause
 * Input: None.
 * Process: Sends an acknowledged Pause command to the worker.
 * Output: The transport operation's HRESULT.
 */
HRESULT FilePlayer::pause() { return impl_->submit(PlayerCommand::kPause); }

/*
 * Name: FilePlayer::stop
 * Input: None.
 * Process: Sends an acknowledged Stop command to the worker.
 * Output: The transport operation's HRESULT.
 */
HRESULT FilePlayer::stop() { return impl_->submit(PlayerCommand::kStop); }

/*
 * Name: FilePlayer::seek
 * Input: Source position in 100-nanosecond units.
 * Process: Sends an acknowledged Seek command to the worker.
 * Output: The transport operation's HRESULT.
 */
HRESULT FilePlayer::seek(uint64_t position_100ns) {
    return impl_->submit(PlayerCommand::kSeek, position_100ns);
}

/*
 * Name: FilePlayer::get_status
 * Input: A status output reference.
 * Process: Requests fresh worker-owned progress and copies its snapshot.
 * Output: S_OK with normal or Failed state; no unmanaged resource escapes.
 */
HRESULT FilePlayer::get_status(FilePlayerStatus& status) {
    return impl_->submit(PlayerCommand::kStatus, 0, &status);
}

}

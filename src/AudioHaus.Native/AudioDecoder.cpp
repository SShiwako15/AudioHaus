#include "AudioDecoder.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <propvarutil.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace audiohaus {
namespace {

using Microsoft::WRL::ComPtr;

constexpr uint32_t kSampleRate = 48000;
constexpr uint32_t kChannels = 2;
constexpr uint32_t kFrameBytes = kChannels * sizeof(float);
constexpr uint64_t kTicksPerSecond = 10000000;
constexpr DWORD kMaxSampleBytes = 16 * 1024 * 1024;
constexpr uint32_t kMaxEmptyReads = 256;
constexpr DWORD kAllStreams = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
constexpr DWORD kFirstAudioStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
constexpr DWORD kMediaSource = static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE);

/*
 * Name: ticks_to_frames
 * Input: Nonnegative media time and whether to round upward.
 * Process: Converts 100 ns time without overflowing an intermediate multiplication.
 * Output: 48 kHz frame count, rounded upward for trimming or nearest for timestamps.
 */
uint64_t ticks_to_frames(uint64_t ticks, bool round_up) noexcept {
    const uint64_t rounding = round_up ? kTicksPerSecond - 1 : kTicksPerSecond / 2;
    return ticks / kTicksPerSecond * kSampleRate +
           (ticks % kTicksPerSecond * kSampleRate + rounding) / kTicksPerSecond;
}

}

/*
 * Name: AudioDecoder::Impl
 * Input: Media Foundation operations on the caller's owning MTA thread.
 * Process: Retains a source reader, one locked sample, and seek-relative timing.
 * Output: Bounded decoded PCM without gain, WASAPI, or background threads.
 */
class AudioDecoder::Impl {
public:
    /*
     * Name: ~Impl
     * Input: None.
     * Process: Unlocks the current sample before releasing the source reader.
     * Output: Released decoder COM resources on the caller's thread.
     */
    ~Impl() {
        release_sample();
        reader_.Reset();
    }

    /*
     * Name: initialize
     * Input: Validated local file path.
     * Process: Opens the file, configures normalized PCM, and requires seeking/duration.
     * Output: S_OK positioned at zero, or the exact initialization HRESULT.
     */
    HRESULT initialize(const wstring& file_path) {
        const DWORD attributes = GetFileAttributesW(file_path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) { return HRESULT_FROM_WIN32(GetLastError()); }
        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) { return E_INVALIDARG; }
        HRESULT result = MFCreateSourceReaderFromURL(file_path.c_str(), nullptr, reader_.GetAddressOf());
        if (FAILED(result)) { return result; }
        result = reader_->SetStreamSelection(kAllStreams, FALSE);
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
        const array<pair<const GUID*, uint32_t>, 6> format_attributes = {{
            {&MF_MT_AUDIO_NUM_CHANNELS, kChannels},
            {&MF_MT_AUDIO_SAMPLES_PER_SECOND, kSampleRate},
            {&MF_MT_AUDIO_BITS_PER_SAMPLE, 32},
            {&MF_MT_AUDIO_BLOCK_ALIGNMENT, kFrameBytes},
            {&MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kSampleRate * kFrameBytes},
            {&MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE}
        }};
        for (const auto& attribute : format_attributes) {
            result = media_type->SetUINT32(*attribute.first, attribute.second);
            if (FAILED(result)) { return result; }
        }
        result = reader_->SetCurrentMediaType(kFirstAudioStream, nullptr, media_type.Get());
        if (FAILED(result)) { return result; }
        result = verify_media_type();
        if (FAILED(result)) { return result; }

        PROPVARIANT duration{};
        const unique_ptr<PROPVARIANT, decltype(&PropVariantClear)> clear_duration(&duration, &PropVariantClear);
        result = reader_->GetPresentationAttribute(kMediaSource, MF_PD_DURATION, &duration);
        if (FAILED(result)) { return result; }
        if (duration.vt != VT_UI8 || duration.uhVal.QuadPart == 0 ||
            duration.uhVal.QuadPart > static_cast<uint64_t>((numeric_limits<LONGLONG>::max)())) {
            return MF_E_INVALID_FILE_FORMAT;
        }
        duration_100ns_ = duration.uhVal.QuadPart;

        PROPVARIANT characteristics{};
        const unique_ptr<PROPVARIANT, decltype(&PropVariantClear)> clear_characteristics(&characteristics, &PropVariantClear);
        result = reader_->GetPresentationAttribute(kMediaSource, MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS, &characteristics);
        if (FAILED(result)) { return result; }
        if (characteristics.vt != VT_UI4 || (characteristics.ulVal & MFMEDIASOURCE_CAN_SEEK) == 0) {
            return MF_E_INVALIDREQUEST;
        }
        return seek(0);
    }

    /*
     * Name: read
     * Input: Writable stereo float storage and frame capacity.
     * Process: Copies sanitized PCM and timestamp gaps from one retained sample.
     * Output: Written frames and EOF; HRESULT failures preserve already copied frames.
     */
    HRESULT read(float* destination, uint32_t requested_frames, uint32_t& written, bool& ended) {
        while (written < requested_frames) {
            if (decoded_offset_ == decoded_frames_ && pending_gap_frames_ == 0) {
                const HRESULT result = load_sample();
                if (FAILED(result)) { return result; }
                if (result == S_FALSE) { break; }
            }
            const uint32_t available = requested_frames - written;
            if (pending_gap_frames_ != 0) {
                const uint32_t frames = static_cast<uint32_t>((min<uint64_t>)(available, pending_gap_frames_));
                fill_n(destination + static_cast<size_t>(written) * kChannels,
                       static_cast<size_t>(frames) * kChannels, 0.0f);
                pending_gap_frames_ -= frames;
                cursor_frames_ += frames;
                written += frames;
                continue;
            }
            const uint32_t frames = static_cast<uint32_t>((min<uint64_t>)(available, decoded_frames_ - decoded_offset_));
            const BYTE* source = decoded_bytes_ + decoded_offset_ * kFrameBytes;
            for (size_t index = 0; index < static_cast<size_t>(frames) * kChannels; ++index) {
                float sample = 0.0f;
                memcpy(&sample, source + index * sizeof(float), sizeof(float));
                destination[static_cast<size_t>(written) * kChannels + index] =
                    isfinite(sample) ? clamp(sample, -1.0f, 1.0f) : 0.0f;
            }
            decoded_offset_ += frames;
            cursor_frames_ += frames;
            written += frames;
        }
        ended = decoder_eof_ && pending_gap_frames_ == 0 && decoded_offset_ == decoded_frames_;
        return S_OK;
    }

    /*
     * Name: seek
     * Input: Requested absolute position in 100 ns units.
     * Process: Clamps duration, drops old PCM, flushes the reader, and seeks.
     * Output: HRESULT; following reads trim leading decoder samples to the new target.
     */
    HRESULT seek(uint64_t position) {
        position = (min)(position, duration_100ns_);
        HRESULT result = release_sample();
        if (FAILED(result)) { return result; }
        result = reader_->Flush(kFirstAudioStream);
        if (FAILED(result)) { return result; }
        PROPVARIANT target{};
        target.vt = VT_I8;
        target.hVal.QuadPart = static_cast<LONGLONG>(position);
        result = reader_->SetCurrentPosition(GUID_NULL, target);
        if (FAILED(result)) { return result; }
        segment_position_ = position;
        cursor_frames_ = 0;
        pending_gap_frames_ = 0;
        decoder_eof_ = position == duration_100ns_;
        return S_OK;
    }

    /*
     * Name: duration_100ns
     * Input: None.
     * Process: Reads the validated source metadata.
     * Output: Duration in 100 ns units.
     */
    uint64_t duration_100ns() const noexcept { return duration_100ns_; }

private:
    /*
     * Name: verify_media_type
     * Input: Current source-reader media type.
     * Process: Requires float audio with the exact normalized stereo PCM layout.
     * Output: S_OK or a format/query HRESULT.
     */
    HRESULT verify_media_type() {
        ComPtr<IMFMediaType> media_type;
        HRESULT result = reader_->GetCurrentMediaType(kFirstAudioStream, media_type.GetAddressOf());
        if (FAILED(result)) { return result; }
        GUID major{};
        GUID subtype{};
        result = media_type->GetGUID(MF_MT_MAJOR_TYPE, &major);
        if (FAILED(result)) { return result; }
        result = media_type->GetGUID(MF_MT_SUBTYPE, &subtype);
        if (FAILED(result)) { return result; }
        if (major != MFMediaType_Audio || subtype != MFAudioFormat_Float) { return MF_E_INVALIDMEDIATYPE; }
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
     * Name: release_sample
     * Input: Currently retained sample, if any.
     * Process: Unlocks decoded PCM before releasing its buffer ownership.
     * Output: S_OK or Unlock's HRESULT; all retained sample fields are cleared.
     */
    HRESULT release_sample() noexcept {
        HRESULT result = S_OK;
        if (decoded_bytes_ != nullptr) { result = decoded_buffer_->Unlock(); }
        decoded_bytes_ = nullptr;
        decoded_frames_ = 0;
        decoded_offset_ = 0;
        decoded_buffer_.Reset();
        return result;
    }

    /*
     * Name: load_sample
     * Input: Source reader and the seek-relative output frame cursor.
     * Process: Reads one bounded sample, trims preroll/overlap, and identifies gaps.
     * Output: S_OK with PCM, S_FALSE at EOF, or the original decoding HRESULT.
     */
    HRESULT load_sample() {
        HRESULT result = release_sample();
        if (FAILED(result)) { return result; }
        for (uint32_t attempt = 0; attempt < kMaxEmptyReads; ++attempt) {
            if (decoder_eof_) { return S_FALSE; }
            DWORD flags = 0;
            LONGLONG timestamp = 0;
            ComPtr<IMFSample> sample;
            result = reader_->ReadSample(kFirstAudioStream, 0, nullptr, &flags, &timestamp, sample.GetAddressOf());
            if (FAILED(result)) { return result; }
            if ((flags & MF_SOURCE_READERF_ERROR) != 0) { return E_FAIL; }
            if ((flags & (MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED | MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED)) != 0) {
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
            if (length > kMaxSampleBytes || length % kFrameBytes != 0) { return MF_E_INVALID_STREAM_DATA; }

            const uint64_t sample_frames = length / kFrameBytes;
            uint64_t sample_frame = 0;
            uint64_t skip = 0;
            if (timestamp >= 0 && static_cast<uint64_t>(timestamp) >= segment_position_) {
                sample_frame = ticks_to_frames(static_cast<uint64_t>(timestamp) - segment_position_, false);
                if (sample_frame < cursor_frames_) { skip = cursor_frames_ - sample_frame; }
            } else {
                const uint64_t distance = timestamp < 0
                    ? segment_position_ + static_cast<uint64_t>(-(timestamp + 1)) + 1
                    : segment_position_ - static_cast<uint64_t>(timestamp);
                skip = ticks_to_frames(distance, true);
                if (skip < sample_frames) { skip += (min)(cursor_frames_, sample_frames - skip); }
            }
            if (skip >= sample_frames) { continue; }
            pending_gap_frames_ = sample_frame > cursor_frames_ ? sample_frame - cursor_frames_ : 0;
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

    ComPtr<IMFSourceReader> reader_;
    ComPtr<IMFMediaBuffer> decoded_buffer_;
    BYTE* decoded_bytes_ = nullptr;
    uint64_t decoded_frames_ = 0;
    uint64_t decoded_offset_ = 0;
    uint64_t pending_gap_frames_ = 0;
    uint64_t cursor_frames_ = 0;
    uint64_t segment_position_ = 0;
    uint64_t duration_100ns_ = 0;
    bool decoder_eof_ = false;
};

/*
 * Name: AudioDecoder::AudioDecoder
 * Input: An initialized implementation on the caller's MTA thread.
 * Process: Takes exclusive decoder ownership without transferring threads.
 * Output: A decoder opened through the factory.
 */
AudioDecoder::AudioDecoder(unique_ptr<Impl> impl) noexcept : impl_(move(impl)) {}

/*
 * Name: AudioDecoder::~AudioDecoder
 * Input: None.
 * Process: Releases retained sample and source-reader ownership.
 * Output: Complete same-thread cleanup before caller MF/COM shutdown.
 */
AudioDecoder::~AudioDecoder() = default;

/*
 * Name: AudioDecoder::open
 * Input: Local path and an ownership destination on an initialized MTA/MF thread.
 * Process: Initializes decoding and transfers ownership after success.
 * Output: HRESULT and decoder; exceptions become E_OUTOFMEMORY or E_FAIL.
 */
HRESULT AudioDecoder::open(const wstring& file_path, unique_ptr<AudioDecoder>& decoder) {
    decoder.reset();
    if (file_path.empty() || file_path.find(L'\0') != wstring::npos) { return E_INVALIDARG; }
    try {
        auto impl = make_unique<Impl>();
        const HRESULT result = impl->initialize(file_path);
        if (FAILED(result)) { return result; }
        decoder = unique_ptr<AudioDecoder>(new AudioDecoder(move(impl)));
        return S_OK;
    } catch (const bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}

/*
 * Name: AudioDecoder::read
 * Input: Writable stereo float storage and frame capacity.
 * Process: Validates output storage and copies the next contiguous normalized PCM.
 * Output: HRESULT, written count, and EOF; failures do not throw across the helper API.
 */
HRESULT AudioDecoder::read(float* destination, uint32_t requested_frames, uint32_t& written, bool& ended) {
    written = 0;
    ended = false;
    if (destination == nullptr && requested_frames != 0) { return E_INVALIDARG; }
    try {
        return impl_->read(destination, requested_frames, written, ended);
    } catch (const bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}

/*
 * Name: AudioDecoder::seek
 * Input: Requested absolute position in 100 ns units.
 * Process: Flushes old data and clamps the new decoding origin to source duration.
 * Output: HRESULT; allocation or other exceptions are contained.
 */
HRESULT AudioDecoder::seek(uint64_t position_100ns) {
    try {
        return impl_->seek(position_100ns);
    } catch (const bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}

/*
 * Name: AudioDecoder::duration_100ns
 * Input: None.
 * Process: Reads initialized metadata without COM work.
 * Output: Positive source duration in 100 ns units.
 */
uint64_t AudioDecoder::duration_100ns() const noexcept { return impl_->duration_100ns(); }

}

#pragma once

#include <Windows.h>

#include <cstdint>
#include <memory>
#include <string>

namespace audiohaus {

using namespace std;

/*
 * Name: AudioDecoder
 * Input: A seekable local WAV, MP3, or supported M4A file.
 * Process: Streams normalized stereo float PCM while retaining one decoded sample.
 * Output: Contiguous 48 kHz audio; caller owns COM/MF startup and thread affinity.
 */
class AudioDecoder {
public:
    /*
     * Name: open
     * Input: Local path and an ownership destination on an initialized MTA/MF thread.
     * Process: Opens audio decoding, validates metadata, and positions at the beginning.
     * Output: HRESULT and decoder; failure leaves the ownership destination empty.
     */
    static HRESULT open(const wstring& file_path, unique_ptr<AudioDecoder>& decoder);

    /*
     * Name: ~AudioDecoder
     * Input: None; destruction must run on the owning MTA thread before MF shutdown.
     * Process: Unlocks retained PCM and releases the source reader.
     * Output: Released decoder resources without changing caller COM/MF ownership.
     */
    ~AudioDecoder();

    AudioDecoder(const AudioDecoder&) = delete;
    AudioDecoder& operator=(const AudioDecoder&) = delete;

    /*
     * Name: read
     * Input: Writable interleaved stereo storage and requested frame capacity.
     * Process: Reads bounded samples, fills timeline gaps, and sanitizes float PCM.
     * Output: HRESULT, written frames, and EOF. Final PCM may accompany ended=true;
     *         subsequent reads return zero/true. Null storage requires zero capacity.
     */
    HRESULT read(float* destination, uint32_t requested_frames, uint32_t& written, bool& ended);

    /*
     * Name: seek
     * Input: Absolute nonnegative media position in 100 ns units.
     * Process: Clamps to duration, flushes old PCM, and trims decoder seek preroll.
     * Output: HRESULT; the next read starts at the requested clamped position.
     */
    HRESULT seek(uint64_t position_100ns);

    /*
     * Name: duration_100ns
     * Input: None.
     * Process: Reads validated source duration without accessing the decoder.
     * Output: Positive duration in 100 ns units.
     */
    uint64_t duration_100ns() const noexcept;

private:
    class Impl;

    /*
     * Name: AudioDecoder
     * Input: An initialized decoder implementation.
     * Process: Takes exclusive source-reader ownership on its existing thread.
     * Output: A decoder created through open.
     */
    explicit AudioDecoder(unique_ptr<Impl> impl) noexcept;

    unique_ptr<Impl> impl_;
};

}

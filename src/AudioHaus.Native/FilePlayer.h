#pragma once

#include <Windows.h>

#include <cstdint>
#include <memory>
#include <string>

namespace audiohaus {

using namespace std;

/*
 * Name: FilePlayerStatus
 * Input: Decoder and playback activity for one local file.
 * Process: Records state, source position, and lifetime submitted-frame counts.
 * Output: A snapshot; states are Stopped 0, Playing 1, Paused 2, Ended 3, Failed 4.
 */
struct FilePlayerStatus {
    uint32_t state = 0;
    uint32_t hresult = 0;
    uint32_t sample_rate = 48000;
    uint32_t channels = 2;
    uint64_t position_100ns = 0;
    uint64_t duration_100ns = 0;
    uint64_t frames_submitted = 0;
    uint64_t start_qpc = 0;
    uint64_t stop_qpc = 0;
};

/*
 * Name: FilePlayer
 * Input: A seekable local audio file, render endpoint ID, and gain.
 * Process: Owns a dedicated MTA worker with an independent decoder and renderer.
 * Output: Synchronous controls; COM resources remain exclusively on the worker.
 */
class FilePlayer {
public:
    /*
     * Name: open
     * Input: Local file path, active output ID, gain 0..1, and ownership output.
     * Process: Opens Media Foundation decoding and WASAPI without starting sound.
     * Output: HRESULT and a stopped player; failure leaves player empty.
     */
    static HRESULT open(const wstring& file_path, const wstring& endpoint_id,
                        double gain, unique_ptr<FilePlayer>& player);

    /*
     * Name: ~FilePlayer
     * Input: None; callers must finish other calls before destroying the player.
     * Process: Wakes and joins the worker, stopping playback and releasing resources.
     * Output: No background activity or retained native audio resources.
     */
    ~FilePlayer();

    FilePlayer(const FilePlayer&) = delete;
    FilePlayer& operator=(const FilePlayer&) = delete;

    /*
     * Name: play
     * Input: None.
     * Process: Starts or resumes playback; rewinds a naturally ended file.
     * Output: HRESULT after the worker acknowledges the operation.
     */
    HRESULT play();

    /*
     * Name: pause
     * Input: None.
     * Process: Stops the device clock while preserving queued PCM and position.
     * Output: HRESULT after playback is paused; idle states remain unchanged.
     */
    HRESULT pause();

    /*
     * Name: stop
     * Input: None.
     * Process: Stops and resets WASAPI, flushes decoding, and rewinds the file.
     * Output: HRESULT; position becomes zero without resetting lifetime frame counts.
     */
    HRESULT stop();

    /*
     * Name: seek
     * Input: Requested position in 100-nanosecond units.
     * Process: Clamps to duration, flushes both paths, and preserves transport state.
     * Output: HRESULT after seeking; early decoded frames are discarded before playback.
     */
    HRESULT seek(uint64_t position_100ns);

    /*
     * Name: get_status
     * Input: A status output reference.
     * Process: Reads progress from submitted PCM minus remaining WASAPI padding.
     * Output: S_OK and a snapshot, including queryable Failed state and its HRESULT.
     */
    HRESULT get_status(FilePlayerStatus& status);

private:
    class Impl;

    /*
     * Name: FilePlayer
     * Input: An initialized worker implementation.
     * Process: Takes exclusive ownership of the worker.
     * Output: A player created through open.
     */
    explicit FilePlayer(unique_ptr<Impl> impl) noexcept;

    unique_ptr<Impl> impl_;
};

}

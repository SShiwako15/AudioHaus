#pragma once

#include <Windows.h>
#include <cstdint>
#include <memory>
#include <string>

namespace audiohaus {
using namespace std;

/*
 * Name: ProcessCapturePacket
 * Input: A borrowed WASAPI stereo float capture packet.
 * Process: Describes data valid until release_packet; silent packets may be null.
 * Output: Frame count, flags, and Windows QPC timestamp in 100 ns units.
 */
struct ProcessCapturePacket {
    const float* samples = nullptr;
    uint32_t frames = 0;
    uint32_t flags = 0;
    uint64_t qpc_100ns = 0;
};

/*
 * Name: ProcessCapture
 * Input: Existing application PID and an active virtual render endpoint ID.
 * Process: Owns process-tree loopback, process lifetime, and a background parking guard.
 * Output: Event-driven 48 kHz stereo float packets on the caller's MTA thread.
 */
class ProcessCapture {
public:
    /*
     * Name: open
     * Input: PID, virtual parking endpoint, and ownership destination.
     * Process: Rejects self/ancestors, validates parking, and activates loopback.
     * Output: HRESULT and running capture; the asynchronous activation wait is at most five seconds.
     */
    static HRESULT open(uint32_t process_id, const wstring& parking_id, unique_ptr<ProcessCapture>& capture);
    /*
     * Name: prepare
     * Input: PID, parking endpoint, and ownership destination on an MTA worker.
     * Process: Performs blocking activation without acquiring capture buffer services.
     * Output: HRESULT and an unstarted capture transferable to the coordinator.
     */
    static HRESULT prepare(uint32_t process_id, const wstring& parking_id, unique_ptr<ProcessCapture>& capture);
    /*
     * Name: start
     * Input: A prepared capture on its final owning MTA coordinator.
     * Process: Acquires capture services and starts packets plus background parking checks.
     * Output: HRESULT; service acquisition/release stay on the same owning thread.
     */
    HRESULT start();
    /*
     * Name: ~ProcessCapture
     * Input: No outstanding borrowed packet.
     * Process: Stops and releases capture on its owning MTA thread.
     * Output: All owned handles and interfaces released.
     */
    ~ProcessCapture();
    ProcessCapture(const ProcessCapture&) = delete;
    ProcessCapture& operator=(const ProcessCapture&) = delete;
    /*
     * Name: read_packet
     * Input: Packet destination.
     * Process: Obtains one available packet without blocking or allocating.
     * Output: S_OK for a borrowed packet, S_FALSE when empty, or HRESULT failure.
     */
    HRESULT read_packet(ProcessCapturePacket& packet);
    /*
     * Name: release_packet
     * Input: Number of frames from the last successful read.
     * Process: Releases WASAPI's borrowed buffer.
     * Output: HRESULT; every successful read requires exactly one release.
     */
    HRESULT release_packet(uint32_t frames) noexcept;
    /*
     * Name: check_health
     * Input: None.
     * Process: Checks root lifetime and reads the background isolation result without scanning.
     * Output: S_OK, S_FALSE when root exited, or failure; idle audio is healthy.
     */
    HRESULT check_health();
    /*
     * Name: event_handle
     * Input: None.
     * Process: Reads the owned sample-ready notification handle.
     * Output: Borrowed handle valid for this capture's lifetime.
     */
    HANDLE event_handle() const noexcept;
    /*
     * Name: stop
     * Input: None.
     * Process: Stops packet production once; retains queryable ownership.
     * Output: No value; safe during failure cleanup.
     */
    void stop() noexcept;
private:
    class Impl;
    /*
     * Name: ProcessCapture
     * Input: Initialized implementation.
     * Process: Takes exclusive capture ownership.
     * Output: Instance created through open.
     */
    explicit ProcessCapture(unique_ptr<Impl> impl) noexcept;
    unique_ptr<Impl> impl_;
};
}

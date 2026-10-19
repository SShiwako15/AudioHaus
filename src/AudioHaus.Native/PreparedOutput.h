#pragma once

#include <Windows.h>
#include <audioclient.h>
#include <wrl/client.h>
#include <cstdint>
#include <memory>
#include <string>

namespace audiohaus {
using namespace std;

/*
 * Name: PreparedOutput
 * Input: Exact render endpoint ID on a COM-initialized MTA.
 * Process: Activates and initializes a shared client without acquiring buffer services.
 * Output: Transferable MTA client; its adopting thread acquires/releases render services.
 */
struct PreparedOutput {
    Microsoft::WRL::ComPtr<IAudioClient2> client;
    uint32_t buffer_frames = 0;
    uint64_t latency_100ns = 0;
    /*
     * Name: open
     * Input: Endpoint ID and ownership destination.
     * Process: Performs potentially blocking device activation away from the renderer.
     * Output: HRESULT and unstarted 48 kHz stereo client, or empty destination on failure.
     */
    static HRESULT open(const wstring& endpoint_id, unique_ptr<PreparedOutput>& output);
};
}

#include "PreparedOutput.h"

#include <mmdeviceapi.h>
#include <algorithm>
#include <new>
#include <utility>

namespace audiohaus {
using Microsoft::WRL::ComPtr;

/*
 * Name: PreparedOutput::open
 * Input: Exact endpoint ID and ownership destination on an initialized MTA.
 * Process: Opens Media-category shared stereo float rendering without buffer services.
 * Output: Original Windows HRESULT; partial resources remain locally owned on failure.
 */
HRESULT PreparedOutput::open(const wstring& endpoint_id, unique_ptr<PreparedOutput>& output) {
    output.reset();
    auto prepared = make_unique<PreparedOutput>();
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(enumerator.GetAddressOf()));
    if (FAILED(hr)) { return hr; }
    ComPtr<IMMDevice> device;
    hr = enumerator->GetDevice(endpoint_id.c_str(), &device);
    if (FAILED(hr)) { return hr; }
    ComPtr<IMMEndpoint> endpoint;
    hr = device.As(&endpoint);
    if (FAILED(hr)) { return hr; }
    EDataFlow flow = eAll;
    hr = endpoint->GetDataFlow(&flow);
    if (FAILED(hr)) { return hr; }
    if (flow != eRender) { return AUDCLNT_E_WRONG_ENDPOINT_TYPE; }
    DWORD state = 0;
    hr = device->GetState(&state);
    if (FAILED(hr)) { return hr; }
    if (state != DEVICE_STATE_ACTIVE) { return AUDCLNT_E_DEVICE_INVALIDATED; }
    hr = device->Activate(__uuidof(IAudioClient2), CLSCTX_ALL, nullptr,
        reinterpret_cast<void**>(prepared->client.GetAddressOf()));
    if (FAILED(hr)) { return hr; }
    AudioClientProperties properties{};
    properties.cbSize = sizeof(properties); properties.eCategory = AudioCategory_Media;
    hr = prepared->client->SetClientProperties(&properties);
    if (FAILED(hr)) { return hr; }
    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT; format.nChannels = 2;
    format.nSamplesPerSec = 48000; format.wBitsPerSample = 32;
    format.nBlockAlign = 8; format.nAvgBytesPerSec = 48000 * 8;
    hr = prepared->client->Initialize(AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
        AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 0, 0, &format, nullptr);
    if (FAILED(hr)) { return hr; }
    UINT32 frames = 0;
    hr = prepared->client->GetBufferSize(&frames);
    if (FAILED(hr)) { return hr; }
    if (frames == 0 || frames > 48000) { return E_UNEXPECTED; }
    prepared->buffer_frames = frames;
    REFERENCE_TIME latency = 0;
    hr = prepared->client->GetStreamLatency(&latency);
    if (FAILED(hr)) { return hr; }
    prepared->latency_100ns = static_cast<uint64_t>(max<REFERENCE_TIME>(latency, 0));
    output = move(prepared);
    return S_OK;
}
}

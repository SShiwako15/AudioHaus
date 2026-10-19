#include "AudioEndpointEnumerator.h"

#include <Windows.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propvarutil.h>
#include <wrl/client.h>

#include <iomanip>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace audiohaus {
namespace {

using Microsoft::WRL::ComPtr;

/*
 * Name: check_result
 * Input: Windows operation result and operation name.
 * Process: Checks for failure and formats its HRESULT in hexadecimal.
 * Output: Nothing on success; throws runtime_error on failure.
 */
void check_result(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        ostringstream message;
        message << operation << " failed (HRESULT 0x" << hex << uppercase
                << setw(8) << setfill('0') << static_cast<unsigned long>(result) << ").";
        throw runtime_error(message.str());
    }
}

/*
 * Name: ComInitialization
 * Input: The calling thread's current COM apartment.
 * Process: Balances owned COM initialization, preserving existing apartments.
 * Output: Scoped COM availability for endpoint discovery.
 */
class ComInitialization {
public:
    /*
     * Name: ComInitialization
     * Input: None.
     * Process: Initializes MTA COM or uses the thread's existing apartment.
     * Output: A scope guard; throws runtime_error on initialization failure.
     */
    ComInitialization() : result_(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {
        if (result_ != RPC_E_CHANGED_MODE) {
            check_result(result_, "CoInitializeEx");
        }
    }

    /*
     * Name: ~ComInitialization
     * Input: None.
     * Process: Uninitializes COM only when this guard initialized it successfully.
     * Output: Releases this scope's COM initialization without throwing.
     */
    ~ComInitialization() {
        if (SUCCEEDED(result_)) {
            CoUninitialize();
        }
    }

    ComInitialization(const ComInitialization&) = delete;
    ComInitialization& operator=(const ComInitialization&) = delete;

private:
    HRESULT result_;
};

/*
 * Name: get_endpoint_id
 * Input: A Windows endpoint interface.
 * Process: Copies its opaque ID and releases the Windows-allocated string.
 * Output: Endpoint ID; throws runtime_error if querying fails.
 */
wstring get_endpoint_id(IMMDevice& device) {
    wchar_t* raw_id = nullptr;
    const HRESULT result = device.GetId(&raw_id);
    const unique_ptr<wchar_t, decltype(&CoTaskMemFree)> id(raw_id, &CoTaskMemFree);
    check_result(result, "IMMDevice::GetId");
    if (!id) {
        throw runtime_error("IMMDevice::GetId returned no endpoint ID.");
    }
    return wstring(id.get());
}

/*
 * Name: get_display_name
 * Input: A Windows endpoint interface and fallback ID.
 * Process: Reads the friendly name and releases its property value.
 * Output: Display name, or fallback ID when absent; throws on query failure.
 */
wstring get_display_name(IMMDevice& device, const wstring& fallback_id) {
    ComPtr<IPropertyStore> properties;
    check_result(device.OpenPropertyStore(STGM_READ, properties.GetAddressOf()),
                 "IMMDevice::OpenPropertyStore");

    PROPVARIANT value;
    PropVariantInit(&value);
    const unique_ptr<PROPVARIANT, decltype(&PropVariantClear)> cleanup(&value, &PropVariantClear);
    check_result(properties->GetValue(PKEY_Device_FriendlyName, &value),
                 "IPropertyStore::GetValue");
    if (value.vt == VT_LPWSTR && value.pwszVal != nullptr && value.pwszVal[0] != L'\0') {
        return wstring(value.pwszVal);
    }
    return fallback_id;
}

/*
 * Name: to_endpoint_state
 * Input: Windows endpoint state.
 * Process: Maps Windows state constants to the application model.
 * Output: Named endpoint state, or unknown for an unrecognized value.
 */
AudioEndpointState to_endpoint_state(DWORD state) {
    switch (state) {
    case DEVICE_STATE_ACTIVE: return AudioEndpointState::kActive;
    case DEVICE_STATE_DISABLED: return AudioEndpointState::kDisabled;
    case DEVICE_STATE_NOTPRESENT: return AudioEndpointState::kNotPresent;
    case DEVICE_STATE_UNPLUGGED: return AudioEndpointState::kUnplugged;
    default: return AudioEndpointState::kUnknown;
    }
}

}

/*
 * Name: AudioEndpointEnumerator::enumerate
 * Input: None.
 * Process: Queries all playback endpoints and identifies the multimedia default.
 * Output: Fresh endpoint snapshots; throws runtime_error on Windows query failure.
 */
vector<AudioEndpoint> AudioEndpointEnumerator::enumerate() const {
    const ComInitialization com;
    ComPtr<IMMDeviceEnumerator> enumerator;
    check_result(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                 IID_PPV_ARGS(enumerator.GetAddressOf())),
                 "CoCreateInstance(MMDeviceEnumerator)");

    wstring default_id;
    ComPtr<IMMDevice> default_device;
    const HRESULT default_result = enumerator->GetDefaultAudioEndpoint(
        eRender, eMultimedia, default_device.GetAddressOf());
    if (default_result != HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) {
        check_result(default_result, "IMMDeviceEnumerator::GetDefaultAudioEndpoint");
        default_id = get_endpoint_id(*default_device.Get());
    }

    ComPtr<IMMDeviceCollection> devices;
    check_result(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATEMASK_ALL,
                                               devices.GetAddressOf()),
                 "IMMDeviceEnumerator::EnumAudioEndpoints");
    UINT count = 0;
    check_result(devices->GetCount(&count), "IMMDeviceCollection::GetCount");

    vector<AudioEndpoint> endpoints;
    endpoints.reserve(count);
    for (UINT index = 0; index < count; ++index) {
        ComPtr<IMMDevice> device;
        check_result(devices->Item(index, device.GetAddressOf()), "IMMDeviceCollection::Item");

        AudioEndpoint endpoint;
        endpoint.id = get_endpoint_id(*device.Get());
        endpoint.display_name = get_display_name(*device.Get(), endpoint.id);
        DWORD state = 0;
        check_result(device->GetState(&state), "IMMDevice::GetState");
        endpoint.state = to_endpoint_state(state);
        endpoint.is_default_multimedia = !default_id.empty() && endpoint.id == default_id;
        endpoints.push_back(move(endpoint));
    }
    return endpoints;
}

}

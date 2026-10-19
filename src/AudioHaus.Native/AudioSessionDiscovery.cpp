#include "AudioSessionDiscovery.h"

#include <audiopolicy.h>
#include <mmdeviceapi.h>
#include <tlhelp32.h>
#include <wrl/client.h>
#include <algorithm>
#include <utility>

namespace audiohaus {
using Microsoft::WRL::ComPtr;

/*
 * Name: enumerate_audio_sessions
 * Input: Destination on a COM-initialized thread.
 * Process: Copies active session PIDs and their actual render endpoint IDs.
 * Output: HRESULT; a failed snapshot must not be used as isolation evidence.
 */
HRESULT enumerate_audio_sessions(vector<AudioSessionProcess>& sessions) {
    sessions.clear();
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(enumerator.GetAddressOf()));
    if (FAILED(hr)) { return hr; }
    ComPtr<IMMDeviceCollection> devices;
    hr = enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices);
    if (FAILED(hr)) { return hr; }
    UINT count = 0;
    hr = devices->GetCount(&count);
    if (FAILED(hr)) { return hr; }
    for (UINT index = 0; index < count; ++index) {
        ComPtr<IMMDevice> device;
        hr = devices->Item(index, &device);
        if (FAILED(hr)) { return hr; }
        LPWSTR text = nullptr;
        hr = device->GetId(&text);
        if (FAILED(hr)) { return hr; }
        wstring endpoint;
        try { endpoint = text; } catch (...) { CoTaskMemFree(text); throw; }
        CoTaskMemFree(text);
        ComPtr<IAudioSessionManager2> manager;
        hr = device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr,
            reinterpret_cast<void**>(manager.GetAddressOf()));
        if (FAILED(hr)) { return hr; }
        ComPtr<IAudioSessionEnumerator> active;
        hr = manager->GetSessionEnumerator(&active);
        if (FAILED(hr)) { return hr; }
        int session_count = 0;
        hr = active->GetCount(&session_count);
        if (FAILED(hr)) { return hr; }
        for (int session_index = 0; session_index < session_count; ++session_index) {
            ComPtr<IAudioSessionControl> control;
            hr = active->GetSession(session_index, &control);
            if (FAILED(hr)) { return hr; }
            AudioSessionState state{};
            hr = control->GetState(&state);
            if (FAILED(hr)) { return hr; }
            if (state != AudioSessionStateActive) { continue; }
            ComPtr<IAudioSessionControl2> details;
            hr = control.As(&details);
            if (FAILED(hr)) { return hr; }
            DWORD pid = 0;
            hr = details->GetProcessId(&pid);
            if (FAILED(hr)) { return hr; }
            if (pid != 0) { sessions.push_back({pid, endpoint}); }
        }
    }
    return S_OK;
}

/*
 * Name: process_tree_contains
 * Input: Root and candidate PIDs.
 * Process: Bounds ancestry traversal by the snapshot size to reject cycles.
 * Output: HRESULT and membership without retaining process resources.
 */
HRESULT process_tree_contains(uint32_t root, uint32_t candidate, bool& contains) {
    contains = root == candidate;
    if (contains) { return S_OK; }
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) { return HRESULT_FROM_WIN32(GetLastError()); }
    vector<pair<DWORD, DWORD>> parents;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    BOOL found = Process32FirstW(snapshot, &entry);
    if (!found) {
        const DWORD error = GetLastError();
        CloseHandle(snapshot);
        return HRESULT_FROM_WIN32(error);
    }
    try {
        do { parents.emplace_back(entry.th32ProcessID, entry.th32ParentProcessID); }
        while (Process32NextW(snapshot, &entry));
    } catch (...) { CloseHandle(snapshot); throw; }
    const DWORD error = GetLastError();
    CloseHandle(snapshot);
    if (error != ERROR_NO_MORE_FILES) { return HRESULT_FROM_WIN32(error); }
    for (size_t step = 0; candidate != 0 && step < parents.size(); ++step) {
        const auto item = find_if(parents.begin(), parents.end(),
            [=](const auto& parent) { return parent.first == candidate; });
        if (item == parents.end() || item->second == candidate) { break; }
        candidate = item->second;
        if (candidate == root) { contains = true; break; }
    }
    return S_OK;
}

/*
 * Name: check_process_parking
 * Input: Target PID and an exact parking endpoint ID.
 * Process: Checks active sessions, including child render processes.
 * Output: S_OK for isolation/idle, or a discovery/endpoint-escape HRESULT.
 */
HRESULT check_process_parking(uint32_t process_id, const wstring& parking_id) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(enumerator.GetAddressOf()));
    if (FAILED(hr)) { return hr; }
    ComPtr<IMMDevice> parking;
    hr = enumerator->GetDevice(parking_id.c_str(), &parking);
    if (FAILED(hr)) { return hr; }
    DWORD state = 0;
    hr = parking->GetState(&state);
    if (FAILED(hr)) { return hr; }
    if (state != DEVICE_STATE_ACTIVE) { return HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED); }
    vector<AudioSessionProcess> sessions;
    hr = enumerate_audio_sessions(sessions);
    if (FAILED(hr)) { return hr; }
    for (const auto& session : sessions) {
        bool included = false;
        hr = process_tree_contains(process_id, session.process_id, included);
        if (FAILED(hr)) { return hr; }
        if (included && CompareStringOrdinal(parking_id.c_str(), -1,
            session.endpoint_id.c_str(), -1, TRUE) != CSTR_EQUAL) {
            return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        }
    }
    return S_OK;
}
}

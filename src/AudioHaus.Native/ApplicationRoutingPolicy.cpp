#include "ApplicationRoutingPolicy.h"
#include "AudioSessionDiscovery.h"

#include <Windows.h>
#include <appmodel.h>
#include <inspectable.h>
#include <mmdeviceapi.h>
#include <roapi.h>
#include <winstring.h>
#include <wrl/client.h>
#include <array>
#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <vector>

using namespace std;
using Microsoft::WRL::ComPtr;

namespace {

/*
 * Name: AudioPolicyConfig
 * Input: Windows 11 AudioPolicyConfig activation factory.
 * Process: Isolates the internal ABI; the nineteen preceding slots are never called.
 * Output: Per-process render preferences, as used by EarTrumpet's 21H2 adapter.
 */
struct __declspec(uuid("ab3d4648-e242-459f-b02f-541c70306324")) AudioPolicyConfig : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE unused_01() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_02() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_03() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_04() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_05() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_06() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_07() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_08() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_09() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_10() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_11() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_12() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_13() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_14() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_15() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_16() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_17() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_18() = 0;
    virtual HRESULT STDMETHODCALLTYPE unused_19() = 0;
    virtual HRESULT STDMETHODCALLTYPE set_preference(uint32_t pid, EDataFlow flow, ERole role, HSTRING value) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_preference(uint32_t pid, EDataFlow flow, ERole role, HSTRING* value) = 0;
};

/*
 * Name: ComScope
 * Input: Calling apartment.
 * Process: Balances COM initialization while allowing a pre-existing STA.
 * Output: An initialized apartment or a failing HRESULT.
 */
struct ComScope {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~ComScope() { if (SUCCEEDED(result)) { CoUninitialize(); } }
};

/*
 * Name: StringHandle
 * Input: Optional owned HSTRING.
 * Process: Releases Windows string ownership on all return paths.
 * Output: Balanced string lifetime.
 */
struct StringHandle {
    HSTRING value = nullptr;
    ~StringHandle() { WindowsDeleteString(value); }
};

/*
 * Name: activate_policy
 * Input: An interface destination on a COM-initialized thread.
 * Process: Activates only the known Windows 11 ABI.
 * Output: HRESULT and interface; no Windows settings are changed.
 */
HRESULT activate_policy(ComPtr<AudioPolicyConfig>& policy) {
    constexpr wchar_t kClass[] = L"Windows.Media.Internal.AudioPolicyConfig";
    StringHandle name;
    HRESULT hr = WindowsCreateString(kClass, static_cast<UINT32>(size(kClass) - 1), &name.value);
    if (FAILED(hr)) { return hr; }
    return RoGetActivationFactory(name.value, __uuidof(AudioPolicyConfig), reinterpret_cast<void**>(policy.GetAddressOf()));
}

/*
 * Name: invoke
 * Input: HRESULT destination and a COM operation.
 * Process: Contains C++ exceptions and balances COM initialization.
 * Output: C status and original HRESULT; no exception crosses the ABI.
 */
template<typename Action>
AudioHausResult invoke(uint32_t* error, Action action) {
    if (error == nullptr) { return kAudioHausInvalidArgument; }
    HRESULT hr = E_FAIL;
    try {
        ComScope apartment;
        hr = apartment.result;
        if (SUCCEEDED(hr) || hr == RPC_E_CHANGED_MODE) { hr = action(); }
    } catch (const bad_alloc&) { hr = E_OUTOFMEMORY; }
    catch (...) { hr = E_FAIL; }
    *error = static_cast<uint32_t>(hr);
    return SUCCEEDED(hr) ? kAudioHausSuccess : hr == E_INVALIDARG ? kAudioHausInvalidArgument :
        hr == E_OUTOFMEMORY ? kAudioHausOutOfMemory : kAudioHausDiscoveryFailed;
}

/*
 * Name: SessionRow
 * Input: One active render session and a live process handle.
 * Process: Stores lifetime identity independently of a PID's later reuse.
 * Output: Copied process, endpoint, and application identity.
 */
struct SessionRow {
    uint32_t pid = 0;
    uint64_t created = 0;
    wstring endpoint, identity;
};
}

/*
 * Name: AudioHausSessionSnapshotOpaque
 * Input: Copied session rows.
 * Process: Owns immutable discovery storage within the native DLL.
 * Output: Snapshot handle consumed by the C ABI.
 */
struct AudioHausSessionSnapshotOpaque { vector<SessionRow> rows; };

/*
 * Name: audiohaus_policy_probe
 * Input: HRESULT destination.
 * Process: Activates the isolated Windows 11 internal policy interface without changes.
 * Output: Status and exact HRESULT; unsupported Windows interfaces fail explicitly.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_policy_probe(uint32_t* error) {
    return invoke(error, [] { ComPtr<AudioPolicyConfig> policy; return activate_policy(policy); });
}

/*
 * Name: audiohaus_policy_get
 * Input: PID, render role 0..2, and optional UTF-16 buffer.
 * Process: Reads the opaque persisted per-application output preference.
 * Output: Status, required capacity, HRESULT; empty text means follow default.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_policy_get(uint32_t pid, uint32_t role, wchar_t* text,
    uint32_t capacity, uint32_t* required, uint32_t* error) {
    if (required != nullptr) { *required = 0; }
    bool buffer_small = false;
    const auto result = invoke(error, [&] {
        if (pid == 0 || role > 2 || required == nullptr || (text == nullptr && capacity != 0)) { return E_INVALIDARG; }
        ComPtr<AudioPolicyConfig> policy;
        HRESULT hr = activate_policy(policy);
        if (FAILED(hr)) { return hr; }
        StringHandle preference;
        hr = policy->get_preference(pid, eRender, static_cast<ERole>(role), &preference.value);
        if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) { hr = S_OK; }
        if (FAILED(hr)) { return hr; }
        UINT32 length = 0;
        const wchar_t* raw = WindowsGetStringRawBuffer(preference.value, &length);
        *required = length + 1;
        if (capacity < *required) { buffer_small = true; return S_OK; }
        if (length != 0) { memcpy(text, raw, length * sizeof(wchar_t)); }
        text[length] = L'\0';
        return S_OK;
    });
    return result == kAudioHausSuccess && buffer_small ? kAudioHausBufferTooSmall : result;
}

/*
 * Name: audiohaus_policy_set
 * Input: PID, render role 0..2, and opaque preference or null for default.
 * Process: Changes only that application's render preference through Windows policy.
 * Output: Status and exact HRESULT; no capture or global default changes.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_policy_set(uint32_t pid, uint32_t role,
    const wchar_t* preference, uint32_t* error) {
    return invoke(error, [&] {
        if (pid == 0 || role > 2) { return E_INVALIDARG; }
        ComPtr<AudioPolicyConfig> policy;
        HRESULT hr = activate_policy(policy);
        if (FAILED(hr)) { return hr; }
        StringHandle value;
        if (preference != nullptr && *preference != L'\0') {
            hr = WindowsCreateString(preference, static_cast<UINT32>(wcslen(preference)), &value.value);
            if (FAILED(hr)) { return hr; }
        }
        return policy->set_preference(pid, eRender, static_cast<ERole>(role), value.value);
    });
}

/*
 * Name: audiohaus_policy_check_parking
 * Input: Root PID, exact cable endpoint, and HRESULT destination.
 * Process: Checks active original streams throughout the process tree.
 * Output: Success for parked/idle streams; endpoint escape and discovery failures fail.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_policy_check_parking(uint32_t pid, const wchar_t* parking, uint32_t* error) {
    return invoke(error, [&] { return pid == 0 || parking == nullptr || *parking == L'\0' ? E_INVALIDARG :
        audiohaus::check_process_parking(pid, parking); });
}

/*
 * Name: audiohaus_process_tree_contains
 * Input: Root PID, candidate PID, membership and HRESULT destinations.
 * Process: Tests ancestry using a fresh Windows process snapshot.
 * Output: Membership 0/1, status and HRESULT; snapshot failures fail explicitly.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_process_tree_contains(uint32_t root, uint32_t candidate, uint32_t* contains, uint32_t* error) {
    if (contains != nullptr) { *contains = 0; }
    return invoke(error, [&] {
        if (root == 0 || candidate == 0 || contains == nullptr) { return E_INVALIDARG; }
        bool included = false;
        const HRESULT hr = audiohaus::process_tree_contains(root, candidate, included);
        *contains = included ? 1u : 0u; return hr;
    });
}

/*
 * Name: audiohaus_create_session_snapshot
 * Input: Snapshot/count/HRESULT destinations.
 * Process: Enumerates active sessions once and copies process lifetime/identity data.
 * Output: Immutable owned snapshot or null/zero on failure; exited processes are skipped.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_create_session_snapshot(AudioHausSessionSnapshot* snapshot,
    uint32_t* count, uint32_t* error) {
    if (snapshot != nullptr) { *snapshot = nullptr; }
    if (count != nullptr) { *count = 0; }
    return invoke(error, [&] {
        if (snapshot == nullptr || count == nullptr) { return E_INVALIDARG; }
        vector<audiohaus::AudioSessionProcess> sessions;
        const HRESULT hr = audiohaus::enumerate_audio_sessions(sessions);
        if (FAILED(hr)) { return hr; }
        auto owned = make_unique<AudioHausSessionSnapshotOpaque>();
        for (const auto& session : sessions) {
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, session.process_id);
            if (process == nullptr) { continue; }
            FILETIME created{}, exited{}, kernel{}, user{};
            array<wchar_t, 32768> image{};
            DWORD length = static_cast<DWORD>(image.size());
            const bool valid = GetProcessTimes(process, &created, &exited, &kernel, &user) &&
                QueryFullProcessImageNameW(process, 0, image.data(), &length) && WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
            wstring identity;
            try {
                if (valid) {
                    identity.assign(image.data(), length);
                    UINT32 app_length = 0;
                    if (GetApplicationUserModelId(process, &app_length, nullptr) == ERROR_INSUFFICIENT_BUFFER) {
                        vector<wchar_t> app(app_length);
                        if (GetApplicationUserModelId(process, &app_length, app.data()) == ERROR_SUCCESS) {
                            identity += L"|"; identity += app.data();
                        }
                    }
                }
            } catch (...) { CloseHandle(process); throw; }
            CloseHandle(process);
            if (!valid) { continue; }
            const uint64_t time = (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
            owned->rows.push_back({session.process_id, time, session.endpoint_id, move(identity)});
        }
        *count = static_cast<uint32_t>(owned->rows.size());
        *snapshot = owned.release();
        return S_OK;
    });
}

/*
 * Name: audiohaus_get_session
 * Input: Live snapshot, row index, metadata, and separate UTF-16 buffers.
 * Process: Copies a row only when both buffers are sufficient.
 * Output: Required capacities on BufferTooSmall; invalid arguments fail without writes.
 */
AudioHausResult AUDIOHAUS_CALL audiohaus_get_session(AudioHausSessionSnapshot snapshot, uint32_t index,
    AudioHausSessionInfo* info, wchar_t* endpoint, uint32_t endpoint_capacity, wchar_t* identity, uint32_t identity_capacity) {
    if (info != nullptr) { *info = {}; }
    if (snapshot == nullptr || info == nullptr || index >= snapshot->rows.size() ||
        (endpoint == nullptr && endpoint_capacity != 0) || (identity == nullptr && identity_capacity != 0)) { return kAudioHausInvalidArgument; }
    const auto& row = snapshot->rows[index];
    info->process_id = row.pid; info->creation_time = row.created;
    info->endpoint_capacity = static_cast<uint32_t>(row.endpoint.size() + 1);
    info->identity_capacity = static_cast<uint32_t>(row.identity.size() + 1);
    if (endpoint_capacity < info->endpoint_capacity || identity_capacity < info->identity_capacity) { return kAudioHausBufferTooSmall; }
    memcpy(endpoint, row.endpoint.c_str(), info->endpoint_capacity * sizeof(wchar_t));
    memcpy(identity, row.identity.c_str(), info->identity_capacity * sizeof(wchar_t));
    return kAudioHausSuccess;
}

/*
 * Name: audiohaus_destroy_session_snapshot
 * Input: Owned snapshot or null, without active readers.
 * Process: Releases copied session storage.
 * Output: No value; null is harmless.
 */
void AUDIOHAUS_CALL audiohaus_destroy_session_snapshot(AudioHausSessionSnapshot snapshot) { delete snapshot; }

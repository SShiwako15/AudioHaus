#include "ProcessCapture.h"
#include "AudioSessionDiscovery.h"

#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <new>
#include <atomic>
#include <thread>
#include <utility>

namespace audiohaus {
namespace {
using Microsoft::WRL::ComPtr;
using namespace Microsoft::WRL;

/*
 * Name: CaptureActivation
 * Input: Target PID; Windows retains the COM callback until completion.
 * Process: Keeps async parameters/event/results independent of router lifetime.
 * Output: Bounded activation wait; a late callback never touches destroyed owners.
 */
class CaptureActivation final : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
    FtmBase, IActivateAudioInterfaceCompletionHandler> {
public:
    HANDLE completed = nullptr;
    HRESULT result = E_PENDING;
    ComPtr<IAudioClient> client;
    AUDIOCLIENT_ACTIVATION_PARAMS parameters{};
    PROPVARIANT blob{};
    /*
     * Name: ~CaptureActivation
     * Input: Completed/released Windows callback ownership.
     * Process: Closes the callback-owned completion event.
     * Output: Safe cleanup even after the caller timed out.
     */
    ~CaptureActivation() { if (completed != nullptr) { CloseHandle(completed); } }
    /*
     * Name: ActivateCompleted
     * Input: Windows async activation operation.
     * Process: Retrieves an MTA audio client and signals the waiting caller.
     * Output: S_OK to Windows; result contains the exact activation HRESULT.
     */
    STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) override {
        ComPtr<IUnknown> activated;
        HRESULT activation = E_FAIL;
        result = operation->GetActivateResult(&activation, &activated);
        if (SUCCEEDED(result)) { result = activation; }
        if (SUCCEEDED(result)) { result = activated.As(&client); }
        SetEvent(completed);
        return S_OK;
    }
};
}

/*
 * Name: ProcessCapture::Impl
 * Input: PID and parking endpoint on an initialized MTA.
 * Process: Owns normalized loopback interfaces, sample event, and root process handle.
 * Output: Bounded, nonblocking capture with an explicit isolation failure path.
 */
class ProcessCapture::Impl {
public:
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> reader;
    HANDLE ready = nullptr;
    HANDLE process = nullptr;
    HANDLE guard_stop = nullptr;
    thread guard_worker;
    atomic<HRESULT> guard_result{S_OK};
    uint32_t process_id = 0;
    wstring parking_id;
    bool started = false;
    /*
     * Name: ~Impl
     * Input: Owning MTA thread.
     * Process: Stops capture before releasing interfaces and kernel handles.
     * Output: Partial or complete initialization cleaned up.
     */
    ~Impl() {
        if (started) { client->Stop(); }
        if (guard_stop != nullptr) { SetEvent(guard_stop); }
        if (guard_worker.joinable()) { guard_worker.join(); }
        reader.Reset(); client.Reset();
        if (ready != nullptr) { CloseHandle(ready); }
        if (guard_stop != nullptr) { CloseHandle(guard_stop); }
        if (process != nullptr) { CloseHandle(process); }
    }
    /*
     * Name: monitor_parking
     * Input: Immutable PID/endpoint and a dedicated shutdown event.
     * Process: Scans Windows sessions on a separate MTA so discovery cannot block
     *          capture/render callbacks; publishes the first isolation failure.
     * Output: Atomic HRESULT; every COM object is released on this background thread.
     */
    void monitor_parking() noexcept {
        const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(initialized)) { guard_result.store(initialized, memory_order_release); return; }
        HRESULT result = S_OK;
        while (true) {
            const DWORD waited = WaitForSingleObject(guard_stop, 250);
            if (waited == WAIT_OBJECT_0) { break; }
            if (waited != WAIT_TIMEOUT) { result = HRESULT_FROM_WIN32(GetLastError()); break; }
            try { result = check_process_parking(process_id, parking_id); }
            catch (const bad_alloc&) { result = E_OUTOFMEMORY; }
            catch (...) { result = E_FAIL; }
            if (FAILED(result)) { break; }
        }
        guard_result.store(result, memory_order_release);
        CoUninitialize();
    }
    /*
     * Name: prepare
     * Input: Set process_id and parking_id.
     * Process: Verifies isolation and activates an include-process-tree loopback client.
     * Output: S_OK with an unstarted client, or exact validation/activation HRESULT.
     */
    HRESULT prepare() {
        bool includes_self = false;
        HRESULT hr = process_tree_contains(process_id, GetCurrentProcessId(), includes_self);
        if (FAILED(hr)) { return hr; }
        if (process_id == 0 || includes_self) { return E_INVALIDARG; }
        process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, process_id);
        if (process == nullptr) { return HRESULT_FROM_WIN32(GetLastError()); }
        if (WaitForSingleObject(process, 0) != WAIT_TIMEOUT) { return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED); }
        ComPtr<IMMDeviceEnumerator> enumerator;
        hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            IID_PPV_ARGS(enumerator.GetAddressOf()));
        if (FAILED(hr)) { return hr; }
        ComPtr<IMMDevice> parking;
        hr = enumerator->GetDevice(parking_id.c_str(), &parking);
        if (FAILED(hr)) { return hr; }
        ComPtr<IMMEndpoint> endpoint;
        hr = parking.As(&endpoint);
        if (FAILED(hr)) { return hr; }
        EDataFlow flow = eAll;
        hr = endpoint->GetDataFlow(&flow);
        if (FAILED(hr)) { return hr; }
        DWORD state = 0;
        hr = parking->GetState(&state);
        if (FAILED(hr)) { return hr; }
        if (flow != eRender) { return AUDCLNT_E_WRONG_ENDPOINT_TYPE; }
        if (state != DEVICE_STATE_ACTIVE) { return AUDCLNT_E_DEVICE_INVALIDATED; }
        hr = check_process_parking(process_id, parking_id);
        if (FAILED(hr)) { return hr; }
        auto activation = Make<CaptureActivation>();
        if (!activation) { return E_OUTOFMEMORY; }
        activation->completed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (activation->completed == nullptr) { return HRESULT_FROM_WIN32(GetLastError()); }
        activation->parameters.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        activation->parameters.ProcessLoopbackParams.TargetProcessId = process_id;
        activation->parameters.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
        activation->blob.vt = VT_BLOB;
        activation->blob.blob.cbSize = sizeof(activation->parameters);
        activation->blob.blob.pBlobData = reinterpret_cast<BYTE*>(&activation->parameters);
        ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
        hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            __uuidof(IAudioClient), &activation->blob, activation.Get(), &operation);
        if (FAILED(hr)) { return hr; }
        const DWORD waited = WaitForSingleObject(activation->completed, 5000);
        if (waited != WAIT_OBJECT_0) {
            return HRESULT_FROM_WIN32(waited == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError());
        }
        if (FAILED(activation->result)) { return activation->result; }
        client = activation->client;
        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
        format.nChannels = 2;
        format.nSamplesPerSec = 48000;
        format.wBitsPerSample = 32;
        format.nBlockAlign = 8;
        format.nAvgBytesPerSec = 48000 * 8;
        hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
            AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
            0, 0, &format, nullptr);
        if (FAILED(hr)) { return hr; }
        ready = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (ready == nullptr) { return HRESULT_FROM_WIN32(GetLastError()); }
        hr = client->SetEventHandle(ready);
        if (FAILED(hr)) { return hr; }
        return S_OK;
    }
    /*
     * Name: start
     * Input: Prepared client on its final buffer-service owning thread.
     * Process: Acquires the reader, starts capture, and launches parking monitoring.
     * Output: HRESULT; failures remain safely owned for destruction on this thread.
     */
    HRESULT start() {
        HRESULT hr = client->GetService(IID_PPV_ARGS(reader.GetAddressOf()));
        if (FAILED(hr)) { return hr; }
        guard_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (guard_stop == nullptr) { return HRESULT_FROM_WIN32(GetLastError()); }
        hr = client->Start();
        if (SUCCEEDED(hr)) { started = true; guard_worker = thread(&Impl::monitor_parking, this); }
        return hr;
    }
};

/*
 * Name: ProcessCapture::ProcessCapture
 * Input: Initialized implementation.
 * Process: Takes ownership without moving COM resources between apartments.
 * Output: An open capture.
 */
ProcessCapture::ProcessCapture(unique_ptr<Impl> impl) noexcept : impl_(move(impl)) {}
/*
 * Name: ProcessCapture::~ProcessCapture
 * Input: Owning MTA thread.
 * Process: Releases the implementation.
 * Output: Capture stopped and resources released.
 */
ProcessCapture::~ProcessCapture() = default;
/*
 * Name: ProcessCapture::open
 * Input: PID, virtual endpoint ID, and ownership destination.
 * Process: Initializes capture and contains setup exceptions.
 * Output: HRESULT; destination remains empty on failure.
 */
HRESULT ProcessCapture::open(uint32_t process_id, const wstring& parking_id, unique_ptr<ProcessCapture>& capture) {
    const HRESULT prepared = prepare(process_id, parking_id, capture);
    if (FAILED(prepared)) { return prepared; }
    const HRESULT result = capture->start();
    if (FAILED(result)) { capture.reset(); }
    return result;
}
/*
 * Name: ProcessCapture::prepare
 * Input: PID, parking endpoint, and ownership destination on a background MTA.
 * Process: Performs blocking activation without acquiring capture buffer services.
 * Output: HRESULT and prepared capture; exceptions are contained.
 */
HRESULT ProcessCapture::prepare(uint32_t process_id, const wstring& parking_id, unique_ptr<ProcessCapture>& capture) {
    capture.reset();
    if (parking_id.empty() || parking_id.find(L'\0') != wstring::npos) { return E_INVALIDARG; }
    try {
        auto impl = make_unique<Impl>();
        impl->process_id = process_id; impl->parking_id = parking_id;
        const HRESULT hr = impl->prepare();
        if (FAILED(hr)) { return hr; }
        capture = unique_ptr<ProcessCapture>(new ProcessCapture(move(impl)));
        return S_OK;
    } catch (const bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
/*
 * Name: ProcessCapture::start
 * Input: Prepared capture on the final owning coordinator.
 * Process: Starts its buffer reader and background parking monitor.
 * Output: HRESULT; allocation/thread exceptions are contained.
 */
HRESULT ProcessCapture::start() {
    try { return impl_->started ? S_OK : impl_->start(); }
    catch (const bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
/*
 * Name: ProcessCapture::read_packet
 * Input: Packet destination.
 * Process: Borrows the next available normalized WASAPI packet.
 * Output: S_FALSE if empty, S_OK if borrowed, or capture failure.
 */
HRESULT ProcessCapture::read_packet(ProcessCapturePacket& packet) {
    packet = {};
    if (!impl_->reader || !impl_->started) { return E_ABORT; }
    UINT32 available = 0;
    HRESULT hr = impl_->reader->GetNextPacketSize(&available);
    if (FAILED(hr)) { return hr; }
    if (available == 0) { return S_FALSE; }
    BYTE* bytes = nullptr;
    UINT64 position = 0;
    DWORD flags = 0;
    hr = impl_->reader->GetBuffer(&bytes, &packet.frames, &flags, &position, &packet.qpc_100ns);
    packet.flags = flags;
    packet.samples = reinterpret_cast<const float*>(bytes);
    return hr;
}
/*
 * Name: ProcessCapture::release_packet
 * Input: Borrowed packet frame count.
 * Process: Releases the capture buffer.
 * Output: WASAPI HRESULT.
 */
HRESULT ProcessCapture::release_packet(uint32_t frames) noexcept { return impl_->reader->ReleaseBuffer(frames); }
/*
 * Name: ProcessCapture::check_health
 * Input: None.
 * Process: Checks root exit and reads the background parking monitor's result.
 * Output: S_FALSE for exit, S_OK for health, or isolation/discovery failure.
 */
HRESULT ProcessCapture::check_health() {
    const DWORD waited = WaitForSingleObject(impl_->process, 0);
    if (waited == WAIT_OBJECT_0) { return S_FALSE; }
    if (waited != WAIT_TIMEOUT) { return HRESULT_FROM_WIN32(GetLastError()); }
    return impl_->guard_result.load(memory_order_acquire);
}
/*
 * Name: ProcessCapture::event_handle
 * Input: None.
 * Process: Reads the sample notification handle.
 * Output: Borrowed handle.
 */
HANDLE ProcessCapture::event_handle() const noexcept { return impl_->ready; }
/*
 * Name: ProcessCapture::stop
 * Input: None.
 * Process: Stops sample production once.
 * Output: No value; cleanup remains valid after failure.
 */
void ProcessCapture::stop() noexcept {
    if (impl_->started) { impl_->client->Stop(); impl_->started = false; }
    if (impl_->guard_stop != nullptr) { SetEvent(impl_->guard_stop); }
}
}

#include <Windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/implements.h>
#include <wrl/client.h>
#include <atomic>
#include <cmath>
#include <iostream>
#include <string>
#include <thread>

using namespace std;
using namespace Microsoft::WRL;

/*
 * Name: DefaultActivation
 * Input: Windows' default render interface activation.
 * Process: Owns completion state independently of the waiting thread.
 * Output: An automatically routed audio client or HRESULT.
 */
class DefaultActivation final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, FtmBase, IActivateAudioInterfaceCompletionHandler> {
public:
    HANDLE completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HRESULT result = E_PENDING;
    ComPtr<IAudioClient> client;
    ~DefaultActivation() { if (completed != nullptr) { CloseHandle(completed); } }
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

/*
 * Name: render
 * Input: Stop/pause flags and a quiet sine frequency.
 * Process: Uses DEVINTERFACE_AUDIO_RENDER activation, never a resolved endpoint ID.
 * Output: Continuous default-policy-following PCM; prints READY or exact failure.
 */
int render(atomic<bool>& stop, atomic<bool>& paused, double frequency) {
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) { return 1; }
    int exit_code = 0;
    {
        auto activation = Make<DefaultActivation>();
        ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
        HRESULT hr = ActivateAudioInterfaceAsync(L"{e6327cad-dcec-4949-ae8a-991e976a79d2}",
            __uuidof(IAudioClient), nullptr, activation.Get(), &operation);
        if (SUCCEEDED(hr)) { hr = WaitForSingleObject(activation->completed, 5000) == WAIT_OBJECT_0 ? activation->result : HRESULT_FROM_WIN32(ERROR_TIMEOUT); }
        HANDLE ready = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        ComPtr<IAudioRenderClient> writer;
        WAVEFORMATEX format{WAVE_FORMAT_IEEE_FLOAT, 2, 48000, 48000 * 8, 8, 32, 0};
        if (SUCCEEDED(hr)) { hr = activation->client->Initialize(AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
            0, 0, &format, nullptr); }
        if (SUCCEEDED(hr)) { hr = activation->client->SetEventHandle(ready); }
        if (SUCCEEDED(hr)) { hr = activation->client->GetService(IID_PPV_ARGS(writer.GetAddressOf())); }
        UINT32 capacity = 0;
        if (SUCCEEDED(hr)) { hr = activation->client->GetBufferSize(&capacity); }
        if (SUCCEEDED(hr)) { hr = activation->client->Start(); }
        if (SUCCEEDED(hr)) { cout << "READY " << GetCurrentProcessId() << endl; }
        bool started = SUCCEEDED(hr);
        uint64_t position = 0;
        while (SUCCEEDED(hr) && !stop.load()) {
            if (paused.load()) {
                if (started) { hr = activation->client->Stop(); started = false; }
                Sleep(10); continue;
            }
            if (!started) { hr = activation->client->Start(); started = SUCCEEDED(hr); }
            if (FAILED(hr)) { break; }
            WaitForSingleObject(ready, 100);
            UINT32 padding = 0;
            hr = activation->client->GetCurrentPadding(&padding);
            if (FAILED(hr)) { break; }
            if (padding >= capacity) { continue; }
            const UINT32 frames = capacity - padding;
            BYTE* bytes = nullptr;
            hr = writer->GetBuffer(frames, &bytes);
            if (FAILED(hr)) { break; }
            auto samples = reinterpret_cast<float*>(bytes);
            for (UINT32 index = 0; index < frames; ++index) {
                const float value = static_cast<float>(sin(static_cast<double>(position++) * frequency * 6.283185307179586 / 48000) * 0.02);
                samples[index * 2] = value; samples[index * 2 + 1] = value;
            }
            hr = writer->ReleaseBuffer(frames, 0);
        }
        if (started) { activation->client->Stop(); }
        CloseHandle(ready);
        if (FAILED(hr)) { cerr << "Render HRESULT 0x" << hex << static_cast<unsigned long>(hr) << endl; exit_code = 1; }
    }
    CoUninitialize();
    return exit_code;
}

/*
 * Name: main
 * Input: Optional frequency; stdin commands play, pause, and quit.
 * Process: Owns one policy-following renderer and joins it before exiting.
 * Output: Quiet test audio and deterministic readiness/cleanup evidence.
 */
int main(int argc, char** argv) {
    atomic<bool> stop{false}, paused{false};
    const double frequency = argc == 2 ? stod(argv[1]) : 330;
    int result = 0;
    thread worker([&] { result = render(stop, paused, frequency); });
    string command;
    while (getline(cin, command) && command != "quit") {
        if (command == "pause") { paused.store(true); }
        if (command == "play") { paused.store(false); }
    }
    stop.store(true);
    worker.join();
    return result;
}

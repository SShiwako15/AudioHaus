#pragma once

#include <Windows.h>
#include <objbase.h>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <new>
#include <thread>
#include <utility>

namespace audiohaus {
using namespace std;

/*
 * Name: AudioResourceTask
 * Input: A factory for an unstarted audio resource without acquired buffer services.
 * Process: Runs blocking activation on a dedicated MTA and transfers the result on poll.
 * Output: Nonblocking recovery progress; destruction joins before releasing task state.
 */
template<typename Resource>
class AudioResourceTask {
public:
    /*
     * Name: AudioResourceTask
     * Input: None.
     * Process: Starts an idle background MTA resource opener.
     * Output: A task; thread allocation failures propagate to the containing API boundary.
     */
    AudioResourceTask() : worker_(&AudioResourceTask::run, this) {}
    /*
     * Name: ~AudioResourceTask
     * Input: No concurrent callers.
     * Process: Wakes and joins activation before discarding an unadopted resource.
     * Output: No detached worker or callback into destroyed router state.
     */
    ~AudioResourceTask() {
        { const lock_guard<mutex> lock(mutex_); stopping_ = true; }
        changed_.notify_all();
        if (worker_.joinable()) { worker_.join(); }
    }
    AudioResourceTask(const AudioResourceTask&) = delete;
    AudioResourceTask& operator=(const AudioResourceTask&) = delete;
    /*
     * Name: start
     * Input: Factory capturing immutable arguments only.
     * Process: Queues a single activation without waiting for Windows device operations.
     * Output: True if queued; false while an earlier result is still outstanding.
     */
    bool start(function<HRESULT(unique_ptr<Resource>&)> factory) {
        const lock_guard<mutex> lock(mutex_);
        if (pending_ || running_ || completed_ || stopping_) { return false; }
        factory_ = move(factory); pending_ = true;
        changed_.notify_one();
        return true;
    }
    /*
     * Name: take
     * Input: Resource and HRESULT destinations on the owning coordinator.
     * Process: Transfers a completed result under a short lock.
     * Output: False while pending; true once, with no blocking activation on the caller.
     */
    bool take(unique_ptr<Resource>& resource, HRESULT& result) {
        const lock_guard<mutex> lock(mutex_);
        if (!completed_) { return false; }
        resource = move(resource_); result = result_; completed_ = false;
        return true;
    }
private:
    /*
     * Name: run
     * Input: Synchronized factory requests.
     * Process: Initializes COM once, contains exceptions, and publishes prepared resources.
     * Output: Prepared-resource results; any unadopted resource is released on this MTA.
     */
    void run() noexcept {
        const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        while (true) {
            function<HRESULT(unique_ptr<Resource>&)> factory;
            {
                unique_lock<mutex> lock(mutex_);
                changed_.wait(lock, [&] { return stopping_ || pending_; });
                if (stopping_) { resource_.reset(); break; }
                factory = move(factory_); pending_ = false; running_ = true;
            }
            HRESULT result = initialized;
            unique_ptr<Resource> resource;
            if (SUCCEEDED(initialized)) {
                try { result = factory(resource); }
                catch (const bad_alloc&) { result = E_OUTOFMEMORY; }
                catch (...) { result = E_FAIL; }
            }
            {
                const lock_guard<mutex> lock(mutex_);
                if (stopping_) { resource.reset(); running_ = false; break; }
                resource_ = move(resource); result_ = result; completed_ = true; running_ = false;
            }
        }
        if (SUCCEEDED(initialized)) { CoUninitialize(); }
    }
    mutex mutex_;
    condition_variable changed_;
    function<HRESULT(unique_ptr<Resource>&)> factory_;
    unique_ptr<Resource> resource_;
    HRESULT result_ = E_PENDING;
    bool stopping_ = false, pending_ = false, running_ = false, completed_ = false;
    thread worker_;
};
}

#pragma once

#include <Windows.h>
#include <audioclient.h>

namespace audiohaus {
/*
 * Name: is_recoverable_audio_error
 * Input: Windows audio/device HRESULT.
 * Process: Distinguishes transient endpoint/service loss from argument, allocation,
 *          access, and parking-isolation failures.
 * Output: True only when automatic retry can restore the same selected endpoint.
 */
inline bool is_recoverable_audio_error(HRESULT error) noexcept {
    return error == AUDCLNT_E_DEVICE_INVALIDATED || error == AUDCLNT_E_RESOURCES_INVALIDATED ||
        error == AUDCLNT_E_SERVICE_NOT_RUNNING || error == AUDCLNT_E_ENDPOINT_CREATE_FAILED ||
        error == AUDCLNT_E_DEVICE_IN_USE || error == AUDCLNT_E_BUFFER_ERROR || error == RPC_E_DISCONNECTED ||
        error == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) || error == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ||
        error == HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED) || error == HRESULT_FROM_WIN32(ERROR_TIMEOUT) ||
        error == HRESULT_FROM_WIN32(RPC_S_SERVER_UNAVAILABLE);
}
}

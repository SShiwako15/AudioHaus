#pragma once

#include <Windows.h>
#include <cstdint>
#include <string>
#include <vector>

namespace audiohaus {
using namespace std;

/*
 * Name: AudioSessionProcess
 * Input: One active Windows render session.
 * Process: Associates its owning PID with an opaque playback endpoint ID.
 * Output: Discovery data; names and PID lifetimes are not stable identifiers.
 */
struct AudioSessionProcess { uint32_t process_id = 0; wstring endpoint_id; };

/*
 * Name: enumerate_audio_sessions
 * Input: Destination on a COM-initialized thread.
 * Process: Enumerates active render sessions across active endpoints.
 * Output: HRESULT and current PID/endpoint pairs; system-sound PID zero is omitted.
 */
HRESULT enumerate_audio_sessions(vector<AudioSessionProcess>& sessions);

/*
 * Name: process_tree_contains
 * Input: Root PID and candidate PID.
 * Process: Walks a fresh Toolhelp process snapshot, including the root itself.
 * Output: HRESULT and membership; inaccessible snapshots fail explicitly.
 */
HRESULT process_tree_contains(uint32_t root, uint32_t candidate, bool& contains);

/*
 * Name: check_process_parking
 * Input: Target process tree and its configured virtual playback endpoint.
 * Process: Rejects active target sessions on any other endpoint.
 * Output: S_OK when isolated or currently idle; ERROR_INVALID_STATE on escape.
 */
HRESULT check_process_parking(uint32_t process_id, const wstring& parking_id);
}

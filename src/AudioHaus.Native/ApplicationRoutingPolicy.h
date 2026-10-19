#pragma once

#include "AudioHausApi.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Name: AudioHausSessionSnapshot
 * Input: One successful audio-session enumeration.
 * Process: Owns an immutable batch of active session identities and endpoints.
 * Output: Opaque storage, released after its readers finish.
 */
typedef struct AudioHausSessionSnapshotOpaque* AudioHausSessionSnapshot;

/*
 * Name: AudioHausSessionInfo
 * Input: A row in a session snapshot.
 * Process: Describes process lifetime and required UTF-16 buffer capacities.
 * Output: A 24-byte layout; creation_time is the Windows FILETIME value.
 */
typedef struct AudioHausSessionInfo {
    uint32_t process_id, endpoint_capacity;
    uint64_t creation_time;
    uint32_t identity_capacity, reserved;
} AudioHausSessionInfo;

/*
 * Name: audiohaus_policy_probe
 * Input: HRESULT destination.
 * Process: Activates the isolated Windows 11 internal policy interface without changes.
 * Output: Status and exact HRESULT; unsupported Windows interfaces fail explicitly.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_policy_probe(uint32_t* error_hresult);

/*
 * Name: audiohaus_policy_get
 * Input: PID, render role 0..2, and optional UTF-16 buffer.
 * Process: Reads the opaque persisted per-application output preference.
 * Output: Status, required capacity, HRESULT; empty text means follow default.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_policy_get(uint32_t process_id, uint32_t role,
    wchar_t* text, uint32_t capacity, uint32_t* required, uint32_t* error_hresult);

/*
 * Name: audiohaus_policy_set
 * Input: PID, render role 0..2, and opaque preference or null for default.
 * Process: Changes only that application's render preference through Windows policy.
 * Output: Status and exact HRESULT; no capture or global default changes.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_policy_set(uint32_t process_id, uint32_t role,
    const wchar_t* preference, uint32_t* error_hresult);

/*
 * Name: audiohaus_policy_check_parking
 * Input: Root PID, exact cable endpoint, and HRESULT destination.
 * Process: Checks active original streams throughout the process tree.
 * Output: Success for parked/idle streams; endpoint escape and discovery failures fail.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_policy_check_parking(uint32_t process_id,
    const wchar_t* parking_id, uint32_t* error_hresult);

/*
 * Name: audiohaus_process_tree_contains
 * Input: Root PID, candidate PID, membership and HRESULT destinations.
 * Process: Tests ancestry using a fresh Windows process snapshot.
 * Output: Membership 0/1, status and HRESULT; snapshot failures fail explicitly.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_process_tree_contains(uint32_t root,
    uint32_t candidate, uint32_t* contains, uint32_t* error_hresult);

/*
 * Name: audiohaus_create_session_snapshot
 * Input: Snapshot/count/HRESULT destinations.
 * Process: Enumerates active sessions once and copies process lifetime/identity data.
 * Output: Immutable owned snapshot or null/zero on failure; exited processes are skipped.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_create_session_snapshot(
    AudioHausSessionSnapshot* snapshot, uint32_t* count, uint32_t* error_hresult);

/*
 * Name: audiohaus_get_session
 * Input: Live snapshot, row index, metadata, and separate UTF-16 buffers.
 * Process: Copies a row only when both buffers are sufficient.
 * Output: Required capacities on BufferTooSmall; invalid arguments fail without writes.
 */
AUDIOHAUS_API AudioHausResult AUDIOHAUS_CALL audiohaus_get_session(AudioHausSessionSnapshot snapshot,
    uint32_t index, AudioHausSessionInfo* info, wchar_t* endpoint, uint32_t endpoint_capacity,
    wchar_t* identity, uint32_t identity_capacity);

/*
 * Name: audiohaus_destroy_session_snapshot
 * Input: Owned snapshot or null, without active readers.
 * Process: Releases copied session storage.
 * Output: No value; null is harmless.
 */
AUDIOHAUS_API void AUDIOHAUS_CALL audiohaus_destroy_session_snapshot(AudioHausSessionSnapshot snapshot);

#ifdef __cplusplus
}
#endif

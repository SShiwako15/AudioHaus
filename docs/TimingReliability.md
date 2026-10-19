# Timing and reliability

The `route` engine supports output delay, gradual clock correction, buffer
telemetry, and automatic recovery. These features apply to file and process
sources through the same native mixer and C# controller. The older `files` and
`tone` commands retain their independent playback implementations.

## Controls

Inside an existing routing session, substitute the printed IDs:

```text
delay <output-id> 125.5
drift <output-id> on
timing
retry-output <output-id>
retry-capture <process-source-id>
```

`delay` accepts finite milliseconds from 0 through 2000, rounded to a 48 kHz
frame. It affects every route feeding that output. Changing it reprimes those
readers with a short fade while preserving source transport, gain, and mute.
Already submitted device audio must drain before any control is fully audible.

For one source playing through a fast wired output and slower Bluetooth output,
add delay to the faster output. Start small and adjust by listening or measuring
with microphones. This is additional software delay; Bluetooth and device
buffering contribute their own latency. Negative delay and automatic acoustic
calibration are not implemented.

C# uses the same settings without parsing console text:

```csharp
router.SetOutputDelay(speakerOutput, TimeSpan.FromMilliseconds(125.5));
router.SetDriftCorrection(speakerOutput, true);
OutputTimingStatus output = router.GetOutputTiming(speakerOutput);
RouteTimingStatus route = router.GetRouteTiming(source, speakerOutput);
CaptureTimingStatus capture = router.GetCaptureTiming(processSource);
```

Snapshots are immutable and valid after router disposal. Calls on a disposed
router throw. Invalid ranges/IDs are rejected without altering another route.
Retry calls acknowledge scheduling; subsequent status reports completion.

## Clocks and buffers

All PCM remains stereo float at 48 kHz. Each source owns one bounded
four-second ring; each route has an independent fractional reader. File readers
follow the source's QPC timeline minus the output delay. File EOF waits for
running delayed readers to submit the tail; unavailable outputs cannot strand
a source indefinitely. An output that returns joins the current timeline.

Process readers target 50 ms of capture history plus the configured delay.
For device buffers larger than 50 ms, the base target expands to a complete
output block plus two interpolation frames. History retention grows to the
largest destination target plus 200 ms safety margin, bounded by the allocated
four-second ring. Typical zero-delay routes retain 250 ms. All captured history
continues to advance while monitoring is paused/stopped; resuming joins recent
audio instead of replaying a paused backlog.

`DriftController` filters phase error and uses a bounded PI controller to adjust
source frames consumed per output frame. Positive correction consumes source
PCM faster. Correction is limited to ±2000 ppm and changes by at most 1000 ppm
per second. Stereo samples use linear interpolation. This is a small adaptive
sample-rate conversion; it can slightly change pitch. It is not a high-order,
transparent resampler or a guarantee that independent speakers are acoustically
synchronized. Errors beyond the controller's range require rebuffering.

File timing uses the common QPC reference; process timing uses each captured
source's history head. A separate `IAudioClock` measurement estimates the device
rate for diagnostics. Windows clock position units are divided by that clock's
frequency, and its correlated QPC timestamp is already in 100 ns units. These
rules follow [IAudioClock::GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition).
Clock-rate estimation restarts after starvation, clock regression, or reopening.
Values near recovery can reflect driver settling rather than steady clock skew.

## Monitoring

`status` includes `timing`; the separate `timing` command prints only timing.

| Snapshot | Values and units |
| --- | --- |
| Output | Additional delay, sampled device padding/capacity in frames, correction enable, clock ppm estimate, largest render service gap, driver-reported stream latency, retry deadline, underrun/rebuffer/recovery counts, last error. |
| Route | Priming state, retained unread frames, target frames, signed phase error, correction ppm, fractional read position, underrun/missing-frame/overrun/rebuffer counts. |
| Capture | Dynamic retained-history capacity, desired monitor state, retry deadline, attempt/success counts, last error. |

Counters accumulate across recovery. Current HRESULT returns to zero after a
successful recovery; last-error HRESULT remains available. Padding is the last
sample before a render fill, rather than an acoustic-latency estimate. Route
buffer depth is refreshed when queried. Priming deliberately submits silence
until sufficient fresh history exists; it is separate from underrun counts.

`FirstSignalQpc` records the first nonzero PCM submission after output creation
or a delay change, using raw performance-counter ticks. It is useful for testing
software scheduling. It does not record when a speaker physically emits sound.

The new C ABI types are additive: output timing 96 bytes, route timing 80 bytes,
capture timing 48 bytes. Older source/output/route/capture layouts remain
56/80/48/80 bytes. C# bindings use Cdecl and SafeHandle ownership. See
`TimingApi.h` and `NativeTimingMethods.cs` for exact fields and exports.

## Recovery and ownership

Output state is `Running` (1), `Recovering` (2), or `Failed` (4). Process sources
also support `Recovering` (5) alongside stopped/playing/paused/ended/failed.
During capture recovery, Play/Pause/Stop update the desired state to restore.

An empty device queue after a missed service deadline increments the output
underrun counter. The output rejoins the current source timelines with a short
fade. A live reader that exhausts history counts missing frames and primes
again. Capture discontinuity flags discard old history before priming. Recovery
cannot reconstruct audio lost while the process was unscheduled or disconnected.

Transient device invalidation, service unavailability, lost RPC connections,
missing endpoints, and bounded activation timeouts enter automatic recovery.
Retries use 250 ms initially, then 500/1000/2000/4000 ms backoff, capped at
4000 ms. A failed endpoint is always reopened by its exact selected ID. The
router does not substitute the Windows default output. Gain/mute, delay,
correction policy, graph IDs, and lifetime counters survive reconnection.

Access errors, invalid arguments, allocation failures, and the parking-isolation
error remain terminal and queryable. Moving original playback away from the
cable is an isolation failure; it must not be silently retried as normal device
loss. Fix the application's output first, then use `retry-capture` if the
original root process is still alive. Retrying an exited process is rejected.

Blocking endpoint/client preparation runs on background MTA workers during
recovery, so other output mixers continue. The coordinator acquires and releases
capture/render buffer services on their owning thread. This follows Windows'
[device invalidation recovery guidance](https://learn.microsoft.com/en-us/windows/win32/coreaudio/recovering-from-an-invalid-device-error)
and the [IAudioRenderClient ownership rule](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudiorenderclient).

Process recovery retains a handle to the original root. Its exit ends capture;
PID reuse cannot select another application's audio. Pending resource factories
capture immutable identity values. Destruction stops streams, joins activation
and guard workers, releases services/clients, and closes events/process handles
before destroying their owners. No recovery worker is detached. Process
activation waits at most five seconds, with a heap-owned callback safe for late
completion. Disposal waits for an in-flight Windows preparation call; output
activation has no hard cancellation deadline supplied by Windows.

Initial AddSource/AddOutput/AddProcess calls and file decoding still run on the
coordinator. Slow file access or initial graph construction can cause an audible
gap. Recovery limits the damage and reports it; fully isolating file decoding
and initial graph construction remains a further architecture improvement.

## Repeating validation

Build `AudioHaus.sln` using Visual Studio MSBuild for x64, then build/run the
matching checks. Set IDs to current active endpoints and `$diagnostics` to the
built diagnostics executable:

```powershell
dotnet build tests/AudioHaus.TimingChecks/AudioHaus.TimingChecks.csproj -c Release -p:Platform=x64
$checks = '.\artifacts\bin\x64\Release\AudioHaus.TimingChecks\AudioHaus.TimingChecks.exe'
& $checks
& $checks --hardware $diagnostics $cableInput $realtek $headphones
& $checks --disconnect $realtek $headphones
& $checks --cable-disconnect $diagnostics $cableInput $realtek $headphones
& $checks --starvation $diagnostics $cableInput $realtek
```

Default checks open no audio output. Hardware mode uses quiet generated WAV
fixtures, tests actual first-signal submission delay, delayed capture history,
drift toggling, independent retries, monitor-state preservation, and repeated
disposal with pending work. It forces 400 ms scheduler outages in test-owned
child routers for file and live sources. Only those child threads are suspended
and they are resumed in `finally`; existing user processes are never suspended.

The two disconnection modes wait for deliberate user actions and time out if no
physical/configuration outage occurs. For Bluetooth, turn off and reconnect the
headphones when prompted. For the cable, disable/re-enable only stereo CABLE
Input under `mmsys.cpl` → Playback. Do not disable a driver or change the default
output. Right-click the empty playback list and enable Show Disabled Devices if
needed to find the cable again. Its recording endpoint's Listen option must
remain off.

`tests/AudioHaus.TimingMathChecks.cpp` runs deterministic clock-skew, jitter,
clock-change, slew, delay-capacity, interpolation, and retry-limit checks against
the production timing helpers. Compile it with C++20 and `/W4 /WX` from the x64
Developer Command Prompt. Also run the earlier routing/capture regression
consoles and listening sequence. The evidence and remaining limits are in
[TimingReliabilityValidation.md](TimingReliabilityValidation.md).

For a longer music check, build `AudioHaus.CaptureChecks` in the matching
configuration and run:

```powershell
& '.\artifacts\bin\x64\Release\AudioHaus.CaptureChecks\AudioHaus.CaptureChecks.exe' --listen-long $diagnostics $cableInput $realtek $headphones $fileA $fileB
```

This adds a 15-second lead-in, then uses 8/45/15/15/10/20/12-second windows for
initial silence, separate tracks, speaker mute, quieter headphones, monitor
Stop, live resume, and final silence. Include at least 130 seconds of remaining
audio in file A after the check's 15-second seek, and in file B after its
30-second seek. The user's HUMBLE. and In My head files meet this condition.

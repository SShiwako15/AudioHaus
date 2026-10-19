# Timing and reliability validation

Date: 2026-10-17. Development machine: Windows x64, Realtek speakers,
WH-1000XM6 Bluetooth headphones, and installed stereo VB-CABLE. Measurements
describe software/device-stream behavior unless explicitly marked listening.

## Automated evidence

The production controller passed five-minute simulations for consumer clocks
at −1000, −300, +300, and +1000 ppm. Final correction canceled each skew within
two ppm, phase stayed within 200 frames throughout, and converged within two
frames after settling. A further ten-minute simulation used variable 240/720
frame blocks, 17 Hz packet-timing noise, and a +700 to −900 ppm clock change.
Final phase error was 0.044 frames. Correction remained within ±2000 ppm and
obeyed its 1000 ppm/second slew bound. Disable/reset, nonfinite-input handling,
interpolation, retry backoff, and large-buffer maximum-delay capacity passed.

Native/managed ABI checks verify all timing field offsets and sizes 96/80/48,
guard bytes surrounding raw snapshot writes, null handles/storage, missing IDs,
delay-range rejection, invalid native drift flags, and disposed ownership.

The first Release hardware run on Realtek/WH-1000XM6 passed:

| Check | Observed result |
| --- | --- |
| 500 ms file delay | First-signal submissions separated by 499.84 ms; delayed destination silent during initial 250 ms. |
| Runtime delay | 125.5 ms became 6024 frames on the selected output only. |
| Drift policy | Disabling one destination returned its correction to zero; the other remained enabled. |
| Output reopening | Three asynchronous reopens; other output continued; route gain/mute, delay, and drift policy retained. |
| File EOF | 150 ms remaining file audio with 2 s output delay finished after the delayed tail was submitted. |
| Live delay/history | 2400/26400-frame targets; history expanded to at least 36000 frames for the delayed reader. |
| Stable live interval | Six seconds with bounded buffer depth/correction and zero new reader underruns. |
| Capture reopening | Three asynchronous reopens preserving Paused, Stopped, and Playing monitoring states; new captured signal resumed. |
| Forced file starvation | 400 ms owned-child scheduler stall; output underrun counted; signal resumed and process exited cleanly. |
| Cleanup | Five dispose-during-output-and-capture-recovery cycles; all completed within the seven-second check bound; helpers closed cleanly. |

Logs are retained locally in ignored `artifacts/TimingHardware.Release.log` and
the unique temporary fixture directory printed by the check. Generated fixtures
contain synthetic PCM; user music is never copied.

## Physical Bluetooth outage

The user turned WH-1000XM6 off during a bounded test, then turned it on and
reconnected it. The headphone output entered Recovering and attempted the same
endpoint repeatedly. Realtek continued submitting nonzero audio during the
outage. The headphone output automatically returned to Running and submitted new
signal with the original route, gain 0.01, and 125 ms delay retained. The run
recorded 28 recovery attempts and closed successfully.

Local log: `artifacts/TimingDisconnect.Release.log`. This is an actual Bluetooth
power-off/reconnect test, rather than only a manual RetryOutput command.

## Virtual-cable outage

The user disabled stereo CABLE Input in Windows Playback while an owned renderer
remained pinned to that endpoint. Its process capture entered Recovering,
continued transient retries while the cable was disabled, and automatically
resumed Playing after it was re-enabled. The independent file/headphone output
continued submitting signal during the outage. Recovery used the original PID
and retained route gain 0.1, 100 ms output delay, and desired Playing state. New
captured signal and new routed signal were both measured. The run recorded 15
capture recovery attempts and all helper/router resources closed cleanly.

The first cable check timed out before the user changed the setting and did not
verify recovery. The repeated check used a longer window and passed. Evidence:
`artifacts/TimingCableDisconnect.Release.Retry.log`. The cable was enabled again
at completion.

## Final regression pass

The final Release timing run repeated the hardware checks with the completed
ABI and console validation. A requested 500 ms delay measured 503.30 ms at first
PCM submission. Both file and process-capture child routers recovered after
forced 400 ms scheduler stalls, with increasing underrun counters and resumed
nonzero PCM. Twelve invalid console delay/flag/ID cases were contained; valid
settings survived, and case-insensitive drift controls worked. All five
dispose-during-recovery cycles passed again. Log:
`artifacts/TimingHardware.Release.Final.log`.

Debug and Release solution builds completed with warnings treated as errors.
Timing ABI/guarded-storage checks passed in both configurations. The earlier
routing software checks and 51 routing console assertions passed again, including
native-free help, quoted paths, graph ownership, and 40 invalid command cases.
File regression checks passed normalized WAV playback and the user's read-only
MP3/M4A transport/decoding cases. Capture regressions passed PID isolation,
mix/gain/mute/pause/resume, initial/runtime parking escape, root exit, guarded
session buffers, and cleanup against the actual cable.

The Debug hardware suite passed the same delay, live-buffering, output/capture
reopening, file/live scheduler-stall, pending-disposal, and command checks.
Its 500 ms first-signal separation measured 493.10 ms. The Release twenty-second
two-process/two-output stability check measured 47997.0 and 48045.0 submitted
frames/second, with zero new unread loss, discontinuities, or reader rebases on
either stream. Logs: `artifacts/TimingHardware.Debug.log` and
`artifacts/TimingStability.Release.log`.

After resetting the clock estimator on starvation/regression, targeted file and
process scheduler-outage checks passed again against the final native code.
Evidence: `artifacts/TimingStarvation.Final.log`. Clock ppm near a driver restart
or underrun remains a transient estimate; buffer-phase feedback, rather than
that diagnostic estimate, controls the resampling ratio.

## Final music run

The completed timing engine repeated the approximately seventy-second
application-capture sequence using HUMBLE. on Realtek and In My head on
WH-1000XM6. The two source applications continued playing into the cable while
AudioHaus independently muted, reduced gain, stopped, resumed, and disposed its
monitors. Both helpers and the router exited successfully.

| Final pre-disposal snapshot | Frames | Loss/continuity/clipping | HRESULT |
| --- | ---: | --- | --- |
| HUMBLE. capture | 2459520 captured | 0 unread dropped, 0 discontinuities | `0x00000000` |
| In My head capture | 2457600 captured | 0 unread dropped, 0 discontinuities | `0x00000000` |
| Realtek | 2456268 submitted | 0 clipped samples | `0x00000000` |
| WH-1000XM6 | 2455776 submitted | 0 clipped samples | `0x00000000` |

Muted Realtek and both stopped monitors measured exactly zero submitted RMS.
Log: `artifacts/TimingMusic.Final.log`. Audible quality, initial/final physical
silence, and source assignment were confirmed in the longer repeat below.

## Extended listening acceptance

The `--listen-long` repeat provided a 15-second lead-in and
8/45/15/15/10/20/12-second phase windows. It used separate source application
PIDs 14144 (HUMBLE.) and 20360 (In My head). Both monitor outputs submitted new
nonzero signal after Stop/resume, and both source helpers closed successfully.

| Final pre-disposal snapshot | Frames | Loss/continuity/clipping | HRESULT |
| --- | ---: | --- | --- |
| HUMBLE. capture | 5147520 captured | 0 unread dropped, 0 discontinuities | `0x00000000` |
| In My head capture | 5146080 captured | 0 unread dropped, 0 discontinuities | `0x00000000` |
| Realtek | 5145292 submitted | 0 clipped samples | `0x00000000` |
| WH-1000XM6 | 5143776 submitted | 0 clipped samples | `0x00000000` |

Muted Realtek and both stopped monitors again measured zero submitted RMS.
The user confirmed clean playback, HUMBLE. only on Realtek, In My head only on
WH-1000XM6, independent mute/volume, and physical silence before routing,
during Stop, and after closure. Final listening acceptance passed. Evidence:
`artifacts/TimingMusic.Extended.log` and the user's explicit confirmation.

## Completion audit

| Requirement | Current implementation and direct evidence |
| --- | --- |
| Drift correction | Production `TimingController.h` feeds each fractional reader in `RoutingEngine.cpp`; deterministic skew/jitter/clock-change checks plus live bounded-reader checks passed. |
| Adjustable output delay | C API, managed API, and console expose 0–96000 additional frames; two actual output clients measured approximately 500 ms first-submission separation, runtime 6024-frame setting, and maximum-delay EOF. |
| Buffer monitoring | Additive output/route/capture snapshots expose device padding/capacity, live targets/history, priming, corrections, gaps, and cumulative recovery counters; ABI/guard-byte and live-queue checks passed. |
| Underrun recovery | Actual 400 ms scheduler stalls in owned file and capture routers incremented render underruns and resumed fresh nonzero PCM; `TimingStarvation.Final.log`. |
| Disconnected outputs | Actual WH-1000XM6 power-off/reconnect recovered the same endpoint and retained settings while Realtek continued; `TimingDisconnect.Release.log`. |
| Unavailable capture endpoint | Actual stereo cable disable/enable recovered the original PID and monitor intent while an independent file output continued; `TimingCableDisconnect.Release.Retry.log`. |
| Cleanup and error handling | Five pending-recovery disposal cycles, native argument/storage validation, terminal parking-escape isolation, original-process exit, and earlier file/routing/capture regressions passed; workers join and COM services release on their owning MTA. |
| Audible acceptance | The extended repeat passed stream checks and the user confirmed correct source assignment, clean playback, independent controls, and initial/Stop/final physical silence. |

## Remaining validation limits

The earlier application-capture listening checkpoint passed on this same output
pair: HUMBLE. on Realtek and In My head on WH-1000XM6, independent monitoring
controls, clean playback, and silence before routing/during Stop/after disposal.
See [ApplicationCaptureValidation.md](ApplicationCaptureValidation.md).

Submitted-PCM delay measurements do not establish acoustic alignment. The
Bluetooth codec/driver adds latency outside the software's control. Two
simultaneous Bluetooth outputs, protected third-party content, long endurance,
and a desktop interface remain outside this evidence. Test applications are
separate AudioHaus diagnostics processes; actual YouTube, Spotify, and VLC
instances have not yet been exercised.

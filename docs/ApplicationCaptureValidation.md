# Application capture validation

Implementation and tests run on October 17, 2026, on Windows 11 x64 with the
existing Realtek speakers and WH-1000XM6 Bluetooth headphones.

## Passed

- Debug and Release solution builds, warnings treated as errors.
- Actual Windows process-loopback activation through the async API, including an
  idle target without capturing other applications.
- 80-byte capture ABI and every field offset; existing 56/80/48-byte routing
  layouts retained.
- Invalid/duplicate/self PID rejection, malformed/missing parking endpoint,
  parking/output exclusion, live monitor pause/frozen time, Stop/reset, no seek,
  root exit/Ended state, disposal, and calls after disposal.
- Two helper instances of the same executable rendered to the same Realtek
  endpoint, yet remained individually captured by PID. With A playing and B
  stopped, only A accumulated non-silent captured frames. With A paused and B
  playing, only B accumulated non-silent captured frames.
- Both live sources mixed through one headphone output; route mute, monitor
  pause/resume, and gain scaling matched captured input PCM measurements.
- Initial attachment rejected original audio outside the selected parking
  endpoint. A later additional original stream on another endpoint failed only
  that capture with `0x8007139F`; independent capture/output resources survived.
- Native session discovery reported required PID capacity and left guarded
  buffers untouched on insufficient-capacity queries; invalid input cleared
  metadata and reported `E_INVALIDARG`.
- Existing file-routing hardware checks, local-file silent transport/codec
  fixture checks, tone ABI/buffer checks, and 51 routing-console assertions.

Representative Debug measurements:

| Condition | Captured input RMS | AudioHaus route gain | Submitted output RMS |
| --- | ---: | ---: | ---: |
| A only | 0.02891562 | 0.10 | 0.00262871 |
| A and B mixed | A 0.03441064; B 0.03813834 | 0.10 each | 0.00446698 |
| B route muted | A 0.03663032 | 0.10 | 0.00365060 |
| A monitor paused; B route muted | — | — | 0.00000000 |
| A resumed at live audio | 0.03776140 | 0.10 | 0.00377511 |
| A gain halved | 0.03790995 | 0.05 | 0.00189237 |

Input levels varied on the tested endpoint, so gain/mix checks compare output
energy to simultaneously measured captured input energy rather than assuming a
constant input level across separate windows. Capture/output windows and queued
audio are not sample-aligned. The native mixing math has separate deterministic
sample-value checks. These measurements precede physical device processing.

## Installed virtual-cable checks

At initial inspection Windows exposed no active VB-CABLE playback endpoint.
The signed official Pack45 package was downloaded and extracted, and its x64
installer was opened with administrator elevation after approval. Details and
the observed package hash are in [ApplicationCapture.md](ApplicationCapture.md).
The user confirmed installation and Windows restart. After restart, Windows
exposed CABLE Input and CABLE In 16ch alongside both physical outputs. The current
stereo CABLE Input ID is
`{0.0.0.00000000}.{77b0ae97-9c51-4b22-be6f-4bfcbbc984e4}`; it differs from the
temporary ID seen before restart. The installer made the cable the default output;
the user restored a physical default and confirmed that recording-side cable
monitoring is disabled. A fresh endpoint snapshot confirmed WH-1000XM6 as the
multimedia default again.

The automated capture checks then passed with the actual cable as the shared
parking target and WH-1000XM6 as the destination. Representative measurements:

| Condition | Captured input RMS | Route gain | Submitted output RMS |
| --- | ---: | ---: | ---: |
| A only | 0.00282808 | 0.10 | 0.00028199 |
| A and B mixed | A 0.00282938; B 0.00282807 | 0.10 each | 0.00039963 |
| B route muted | A 0.00282808 | 0.10 | 0.00028282 |
| A monitor paused; B muted | - | - | 0.00000000 |
| A resumed | 0.00282808 | 0.10 | 0.00028276 |
| A gain halved | 0.00282808 | 0.05 | 0.00014136 |

Both process filters, bounded buffers, initial/runtime parking checks, and cleanup
passed using the installed cable. These are submitted-PCM measurements; the
separate audible acceptance is recorded below.

An additional console smoke check attached an idle application PID to the actual
cable through `capture`, connected it to a physical output at zero gain, and
exercised live status/transport, seek rejection, source/output removal, and clean
closure. It passed without rendering audio. The case runner was temporary.

## Application listening checkpoint

The user requested the listening check after completing Windows sound setup.
Two independent player processes opened the user's existing MP3 and M4A files,
rendered only into CABLE Input at source gain 1, and started at 15 and 30 seconds.
AudioHaus captured each process tree and routed HUMBLE. to Realtek and In My head
to WH-1000XM6 at route gain 0.15. The test has these phases:

| Phase | Approximate seconds | Expected physical result |
| --- | ---: | --- |
| 0 | 8 | Both source apps play into the cable with no router; both outputs silent. |
| 1 | 15 | HUMBLE. on Realtek; In My head on WH-1000XM6. |
| 2 | 8 | Realtek route muted; only In My head remains audible on headphones. |
| 3 | 8 | HUMBLE. restored; headphone route reduced to gain 0.05. |
| 4 | 8 | Both monitors stopped; outputs silent while source apps keep playing. |
| 5 | 10 | Both routes restored at gain 0.15 and rejoin the live audio. |
| 6 | 8 | Router closed; outputs silent while source apps still play into the cable. |

Submitted-PCM silence measurements add a short interval to phases 2 and 4.
The first session returned exit code 0, healthy HRESULTs, zero clipping, and zero
submitted PCM during mute/Stop. The user nevertheless heard clicks, stuttering,
or brief gaps in the assigned tracks. Listening acceptance failed. By the last
snapshot, source A had lost 6,084 unread frames and source B 5,088; there were no
capture discontinuity flags. Healthy HRESULTs alone did not establish continuity.

Periodic Windows session scans and process snapshots were running on the same
thread as rendering. They have been moved to background MTA workers with atomic
health results. Initial/runtime parking escape checks still pass. Both solution
configurations build successfully with the fix.

The new 20-second silent stability check passed with two application captures,
two physical output clients, and background isolation checks active throughout:

| Output | Submitted frames/second | New lost unread frames | New discontinuities | New reader rebases |
| --- | ---: | ---: | ---: | ---: |
| Realtek | 47,999.8 | 0 | 0 | 0 |
| WH-1000XM6 | 48,023.8 | 0 | 0 | 0 |

The repeated music check used source application PIDs 5364 and 10964 with the
same routes and silence phases. At the final pre-disposal snapshot:

| Stream | Frames | Lost unread frames | Discontinuities or clipped samples | HRESULT |
| --- | ---: | ---: | ---: | --- |
| HUMBLE. capture | 2,459,040 captured | 0 | 0 discontinuities | `0x00000000` |
| In My head capture | 2,457,600 captured | 0 | 0 discontinuities | `0x00000000` |
| Realtek output | 2,456,780 submitted | - | 0 clipped | `0x00000000` |
| WH-1000XM6 output | 2,455,296 submitted | - | 0 clipped | `0x00000000` |

The muted Realtek route and both stopped monitors measured zero submitted RMS.
Both application captures continued advancing during monitor Stop. The router
and source applications closed successfully with no standard-error diagnostics.

The user confirmed the repeat had clean playback without clicks, stutters, or
gaps; correct HUMBLE./Realtek and In My head/WH-1000XM6 assignment; independent
controls; and silence before routing, during Stop, and after router disposal
while the source applications continued playing into the cable. The process
capture and no-duplicate-original-playback listening checkpoint passed.

The earlier helper tests used a physical endpoint as their diagnostic parking
target; the post-restart tests used the actual virtual cable. These player
processes are AudioHaus diagnostics applications, rather than VLC, Spotify, or a
browser. The specific third-party application examples remain untested.

AudioHaus does not create/install a custom kernel driver or programmatically
change undocumented per-app Windows routing settings. The setup assistant and
guide use the official VB-CABLE installer and Windows/app output controls.

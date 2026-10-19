# Simultaneous playback checkpoint

Validated on October 17, 2026, using Windows 11 25H2, build 26200.9711, x64.

## Hardware and result

- Speakers (Realtek(R) Audio).
- Headphones (WH-1000XM6), identified by the user as the Bluetooth output.
- Both endpoints were active in Windows during the dual tests.

The Release diagnostics command played a 440 Hz test tone at 0.05 digital peak
amplitude on each output individually, then on both outputs for five seconds.
Both outputs returned HRESULT `0x00000000`, completed successfully, and submitted
240,000 stereo frames at 48 kHz during the dual run. The conservative native
running intervals overlapped for **4.987 seconds**.

The user confirmed that the tone was audible from both the speakers and headphones
together during that run. This satisfies the current mixed-output hardware checkpoint.

```powershell
$speaker = '{0.0.0.00000000}.{d607b8e4-57c2-43f2-a0a8-80fb4a9e3fc7}'
$headphones = '{0.0.0.00000000}.{6657aafe-8f28-416c-839d-45d01af5aff7}'
.\artifacts\bin\x64\Release\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe tone --output $speaker --seconds 1
.\artifacts\bin\x64\Release\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe tone --output $headphones --seconds 1
.\artifacts\bin\x64\Release\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe tone --output $speaker --output $headphones --seconds 5
```

These IDs belong to this PC's discovery snapshot. Obtain current IDs with `list`
when repeating the test on another PC or after Windows recreates an endpoint.

## Validation evidence

| Check | Result |
| --- | --- |
| Debug and Release solution builds | Passed with warnings treated as errors. |
| CLI behavior | 57 cases per configuration passed: help, listing, option parsing, numeric boundaries, and invalid/inactive/duplicate selections. |
| Managed argument validation | 23 nonplaying cases passed, including non-finite values, duplicate IDs, and embedded null characters. |
| C/C# ABI | 40-byte output report and all seven field offsets verified. |
| Native invalid requests | Status codes, result clearing, capacity checks, and guard-buffer boundaries passed. |
| Realtek single output | 48,000 frames submitted and drained; successful completion. |
| WH-1000XM6 single output | 48,000 frames submitted and drained; successful completion. |
| Release dual output | 240,000 frames per output; 4.987 seconds overlap; user confirmed both audible. |
| Debug automated dual output | 96,000 frames per output; 1.974 seconds overlap; all stream assertions passed. |
| Failed second-output preparation | First output was primed but neither stream started; failed HRESULT and aborted sibling reported. |
| Cleanup after failed preparation | A silent retry completed, submitting 4,848 frames for 100.5 ms rounded to 101 ms. |
| Independent signal check | A temporary WASAPI loopback fixture detected the generated 440 Hz tone in the Realtek endpoint mix. |

The repeatable checks are in `tests/AudioHaus.PlaybackChecks`; usage is documented
in the [README](../README.md#repeatable-playback-checks). The independent loopback
fixture and CLI case runner were temporary verification programs, not shipped tools.

## Interpretation

QPC timestamps bracket running WASAPI streams; they do not measure sound arrival
at the listener. Exact submitted frame counts and drained buffers establish audio
engine delivery, while the user's listening confirmation supplies the acoustic
evidence for this hardware pair. This milestone does not measure synchronization
error or certify two simultaneous Bluetooth transports. A two-Bluetooth-device
test and longer endurance/disconnection tests remain future validation scenarios.

The renderer follows Microsoft's [shared-mode rendering guidance](https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream)
and [WASAPI stream-flag documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants).

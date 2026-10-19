# Desktop validation

Validation date: 2026-10-18. This record covers the WPF interface over the
existing C++ engine and C# interop. Earlier engine recovery and listening
results remain in [TimingReliabilityValidation.md](TimingReliabilityValidation.md)
and [ApplicationCaptureValidation.md](ApplicationCaptureValidation.md).

## Environment and boundaries

Windows x64, .NET 10 SDK, Windows SDK 10.0.26100.0, MSVC v145. Physical destinations
were Realtek speakers and Bluetooth WH-1000XM6 headphones. Stereo VB-CABLE parked
the original application streams; WH-1000XM6 remained the normal default.

Application tests used two separate, test-owned AudioHaus.Diagnostics processes.
Their identical executable names and different PIDs exercise process selection
and isolation. Spotify, YouTube, and VLC were not tested in this milestone.
Browser process-tree grouping and protected content need separate validation.

## Completed automated checks

| Method | Evidence | Result |
| --- | --- | --- |
| Build both configurations | `msbuild AudioHaus.sln /restore /p:Configuration=Release /p:Platform=x64`, repeated with Debug | Passed |
| Native-backed view-model checks | `AudioHaus.ViewModelChecks`, Debug and Release | Passed |
| Real WPF software controls | Normal Windows file picker, Unicode/space-containing filename, setup guide, play/pause/seek/Stop, invalid-container error, dismissal, removal, refresh | Passed |
| Physical file routing through WPF | One source on both outputs, two independent files, route gain/mute, pause/seek/Stop, removal isolation | Passed |
| WPF output settings | Actual PCM meters and buffer telemetry, delay, drift toggle, retry with nonzero recovery counters | Passed |
| WPF application capture | Two selected PIDs, different physical outputs, capture input meters, independent controls, monitor Stop/live resume, close while originals remain parked | Passed |
| Longer WPF music session | Two existing MP3/M4A tracks, independent controls, Stop/resume, normal closure, original players left parked for final silence | Passed; listener confirmed |

The view-model checks also verify dispatcher ownership of property changes,
idempotent shutdown, disabled commands after shutdown, and three shutdowns while
file loading is pending. They use real native decoding without opening outputs.

The UI checks drive the shipped window using Windows UI Automation. They do not
substitute a mock audio engine or call the router directly. Assertions observe
visible source state, media time, real submitted-PCM meters, and recovery counters.
Application processes and the desktop app exit normally after successful runs.
Failed runs clean up only processes created by that run.

Local evidence under the ignored `artifacts` directory:

- `Desktop.Hardware.log` and `Desktop.Hardware.png`: software controls and two-output file session.
- `Desktop.Capture.Retry.log` and `Desktop.Capture.png`: successful two-process capture session.
- `Desktop.Listening.log` and `Desktop.Listening.png`: longer user-media session; human result recorded below.

The rendered capture screenshot was reviewed for contrast, readable controls,
scrolling, and initial window fit at the desktop's current scaling. The initial
window now fits the Windows work area. Both card columns scroll independently.
Other DPI settings, multiple monitors, and assistive technologies need further
manual coverage.

Desktop testing found and fixed read-only telemetry bindings that otherwise
caused a runtime WPF exception when a source card appeared. Progress and meter
bindings now explicitly use `OneWay`. Unexpected layout errors also detach the
failed visual tree before showing a single fatal-error dialog and closing audio.

## Listening session

The longer WPF session routes HUMBLE. to Realtek and In My head to WH-1000XM6,
then exercises mute, lower gain, monitor pause, both monitors stopped, live resume,
and closure while both original applications still render into the cable.

The session ran from 09:57:38 through approximately 10:00:35 in the local test
log, including lead-in and final silence. The user confirmed: "Yes, everything
sounded correct" in response to the complete checklist: assigned tracks, clean
playback, independent mute/volume/pause, and silence before routing, during Stop,
and after the window closed. All three test-owned audio applications exited;
the final process check found no remaining AudioHaus processes.

This is acoustic confirmation for this hardware/session. Automated meter activity
alone does not establish acoustic routing, physical silence, clean Bluetooth
sound, or acoustic synchronization. No exact acoustic alignment was measured.

## Repeat validation

Build the solution before test projects so the matching native DLL is present:

```powershell
msbuild AudioHaus.sln /restore /p:Configuration=Release /p:Platform=x64
dotnet build tests/AudioHaus.ViewModelChecks/AudioHaus.ViewModelChecks.csproj -c Release -p:Platform=x64
.\artifacts\bin\x64\Release\AudioHaus.ViewModelChecks\AudioHaus.ViewModelChecks.exe
dotnet build tests/AudioHaus.DesktopChecks/AudioHaus.DesktopChecks.csproj -c Release -p:Platform=x64
```

Use [DesktopChecks commands](../tests/AudioHaus.DesktopChecks/README.md) for
software-only, hardware, application-capture, and longer listening modes. Run GUI
checks on an unlocked interactive desktop. The hardware modes produce sound.
Use [DesktopSetup.md](DesktopSetup.md) for cable isolation and normal app operation.

For a release candidate, repeat these methods:

1. Build and native-backed checks for state, errors, thread ownership, and shutdown.
2. Real UI automation for commands, bindings, selectors, and visible failures.
3. Hardware tests for actual concurrent WASAPI outputs and independent route PCM.
4. Human listening for track assignment, glitches, controls, and original-path silence.
5. Device/cable outage tests for automatic recovery while unrelated routes continue.
6. Extended playback, DPI/window-size, accessibility, and actual target-application tests.

Outage recovery was previously exercised in the native/console milestone with
real headphone off/on and cable disable/enable. This desktop session exercises
the Retry control; it does not claim a new physical outage test through WPF.
Delay is manual compensation, and different Bluetooth devices can still have
different latency. No acoustic synchronization guarantee is established.

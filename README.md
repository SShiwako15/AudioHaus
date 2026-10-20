# AudioHaus

AudioHaus routes active Windows applications to selected playback devices using
C++ audio processing and a C# WPF interface. Send a browser to speakers and VLC
to headphones, send one application to several outputs, or mix several apps on
one device. Application and output volume/mute controls affect the AudioHaus
mix without changing Windows/device volume.

## Release and demonstration

For the packaged installation workflow and end-user prerequisites, read
[Release and prerequisites](docs/Release.md). For a short, repeatable
walkthrough of routing, fan-out, controls, and cleanup, read
[Short demonstration](docs/Demo.md).

## Desktop app

Build the solution for Release/x64, then launch:

```powershell
msbuild AudioHaus.sln /restore /p:Configuration=Release /p:Platform=x64
.\artifacts\bin\x64\Release\AudioHaus.App\AudioHaus.App.exe
```

Install VB-CABLE separately first and disable **Listen to this device** on CABLE Output.
Keep your normal Windows default on a physical output. Start audio in your
applications, select a source on the right and an output on the left, then click
**Connect**. Available devices and applications refresh automatically. AudioHaus
switches original playback to the cable, verifies the move, and starts routing;
final disconnection or normal closure restores the previous output preference.

The desktop app has no local-file player or media transport. Play media in its
original application. Apps that explicitly pin an output may reject automatic
switching; AudioHaus reports the failure and rolls back the connection.

Read [DesktopSetup.md](docs/DesktopSetup.md) for setup and a full listening
session, and [the desktop UI checks](tests/AudioHaus.DesktopChecks/README.md) for
repeatable validation through the real window. The console tools remain available
for diagnostics and earlier milestones.

## Requirements

- Windows 11 x64.
- Visual Studio 2026 with the Desktop development with C++ workload and MSVC v145.
- Windows SDK 10.0.26100.0 and .NET 10 SDK.
- Installed stereo VB-CABLE for application routing.

## Build and run diagnostics

Open `AudioHaus.sln` in Visual Studio, select **Debug / x64**, and build the solution.
Set **AudioHaus.Diagnostics** as the startup project and use **Ctrl+F5** to run it.

Alternatively, from Developer PowerShell for Visual Studio, run these commands
from the repository root:

```powershell
msbuild AudioHaus.sln /restore /p:Configuration=Debug /p:Platform=x64
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe
```

Use `Release` in place of `Debug` in both commands for an optimized build.
Build the solution first: building the C# project alone does not build the native
DLL. The solution builds dependencies in order and copies the native DLL into
the diagnostics output folder.

## Output and options

With no arguments, diagnostics prints the total and active endpoint counts,
followed by each endpoint's display name, opaque Windows ID, state, and default
multimedia status. Active endpoints appear first, with the active multimedia
default first; names and IDs provide a consistent order within each group.

All playback endpoint states are included, so disconnected or previously paired
devices may appear. Display names need not be unique; use the ID to distinguish
endpoints. Availability is a snapshot taken when the command runs.

```powershell
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe --help
```

`-h` also displays help. `list` explicitly selects endpoint listing, and
`tone --help`, `files --help`, and `route --help` display command usage. The tool
does not pair devices or change Windows audio settings. A paired Bluetooth device is not necessarily an active
Windows playback endpoint; listing two devices does not prove concurrent playback.

## Routing, fan-out, and mixing

The `route` session builds a native graph of up to eight sources, eight outputs,
and 64 source/output connections. Each source has one decoder shared by its
destinations. Each output has one WASAPI mixer that sums its connected sources;
adding another route does not open a second stream on that output. Route gain and
mute are independent, while source play/pause/stop/seek affect all its routes.

## Timing and device recovery

The `route` engine supports per-output delay (0–2000 ms), gradual drift
correction, buffer monitoring, and automatic recovery from output/capture device
loss. Settings and graph IDs survive reopening. Use `timing` to inspect queues,
correction ppm, underruns, and retries. Disconnected outputs reopen by their
selected endpoint ID while other destinations continue.

See [the timing architecture and commands](docs/TimingReliability.md) and
[the validation record](docs/TimingReliabilityValidation.md) for test commands,
measured results, and remaining limits. Additional software delay and clock
correction do not automatically calibrate Bluetooth acoustic latency.

## Individual application capture

Use `applications` to discover active audio PIDs/endpoints, `processes <name>` to
distinguish instances, and `cable-setup` for installation/configuration guidance.
Inside `route`, `capture <pid> <CABLE-Input-endpoint-id>` adds a live source that
supports existing connections, gain, mute, fan-out, and mixing. Pause/Stop control
AudioHaus monitoring; seek and media transport remain in the source app.

Original app playback must go to an unmonitored virtual cable before capture.
Process loopback alone does not silence it. A shared cable can park two apps or
two VLC instances while their PIDs remain independently captured. The router
checks for original streams escaping that parking endpoint and isolates failures.

Follow [ApplicationCapture.md](docs/ApplicationCapture.md) for the YouTube/Spotify
and two-VLC examples, Windows setup, managed API, and repeatable checks. The
[capture validation record](docs/ApplicationCaptureValidation.md) includes passed
virtual-cable listening acceptance on Realtek and WH-1000XM6 using two independent
player processes, and the background-monitoring fix for initial audio glitches.
The specific browser/Spotify/VLC examples remain untested. Installing the cable
driver requires administrator access and restart.

Hardware checks use quiet generated WAV tones. Measurements describe submitted
PCM; listening confirms which physical outputs are audible. The
[routing validation record](docs/RoutingValidation.md) contains architecture,
measured signal levels, device-independent C++ mixing checks, and the listening
sequence. It also records the current latency, drift, and hardware limitations.
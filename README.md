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
See [AutomaticRoutingValidation.md](docs/AutomaticRoutingValidation.md) for the
current checks. Earlier playback/capture milestones below remain developer
diagnostics; they do not describe the current desktop workflow.

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

## Test tone playback

First test a single selected output. This example resolves the current Windows
multimedia default and plays a three-second, 440 Hz tone at 0.05 digital peak
amplitude. Start with a comfortable Windows/device volume; digital amplitude does
not control the device's volume setting.

```powershell
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe tone --output default
```

For another output, copy its exact `ID:` value from the endpoint listing and quote
it. Names are not accepted as identifiers. The endpoint must be `Active` when the
command runs. For a simultaneous test, supply two different active IDs:

```powershell
$outputA = '<paste the first active endpoint ID>'
$outputB = '<paste the second active endpoint ID>'
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe tone --output $outputA --seconds 3
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe tone --output $outputB --seconds 3
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe tone --output $outputA --output $outputB --seconds 5 --frequency 440 --level 0.05
```

Replace the placeholder strings before running these commands. `default` may also
be one selection, provided it resolves to a different endpoint from the other
selection. The tool never chooses a second output automatically.

| Tone option | Default | Allowed value |
| --- | --- | --- |
| `--output` | Required | One or two distinct active endpoint IDs, or `default`. |
| `--seconds` | `3` | `0.1` through `60` seconds. |
| `--frequency` | `440` | `20` through `20000` Hz. |
| `--level` | `0.05` | `0` through `0.25` digital peak amplitude. |

Numbers use an invariant decimal point. Non-finite numbers, missing values,
unsupported arguments, and repeated options other than `--output` are rejected.
Playback ends after the requested duration; Ctrl+C terminates the process early.

Each output reports its sample rate, channel count, submitted frame count, WASAPI
HRESULT, and completion state. With two outputs, the tool also reports the overlap
between their native stream start/stop timestamps. Positive overlap and completed
reports establish concurrent WASAPI streams; they do not establish audible output,
Bluetooth transport compatibility, or synchronized sound at the speakers.

## Independent local file playback

The `files` command opens one or two local files with independent native players.
WAV, MP3, and M4A decoding uses Windows Media Foundation; an installed codec and
an unprotected, valid file are required. A file extension alone does not guarantee
decoding support. Each file has its own output, playback state, and seek position.

For a single file, replace this example path with an existing file:

```powershell
$fileA = 'C:\Users\you\Music\background.mp3'
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe files --file $fileA --output default
```

For two independent files, use the exact IDs of two different active endpoints.
For example, choose Realtek speakers as `$outputA` and WH-1000XM6 headphones as
`$outputB` when both are active. Copy IDs from a fresh listing rather than using
the device names:

```powershell
$fileA = 'C:\Users\you\Music\background.mp3'
$fileB = 'C:\Users\you\Music\another song.m4a'
$outputA = '<paste the first active endpoint ID>'
$outputB = '<paste the second active endpoint ID>'
.\artifacts\bin\x64\Debug\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe files --file $fileA --output $outputA --file $fileB --output $outputB --gain 0.15
```

Replace the paths and IDs before running. Provide each `--file` before its matching
`--output`. One or two pairs are accepted; two pairs must resolve to different
active endpoints. `default` resolves the current Windows multimedia default.
Optional `--gain` applies to both files, defaults to `0.15`, accepts finite values
from `0` through `1`, and may appear only once. It changes digital samples without
changing Windows or device volume.

Both files open **stopped**. Enter console commands to begin or control playback:

| Command | Effect |
| --- | --- |
| `play 1`, `play 2`, `play all` | Start or resume the selected files. |
| `pause 1`, `pause 2`, `pause all` | Pause selected files while retaining their positions. |
| `stop 1`, `stop 2`, `stop all` | Stop selected files and reset their positions to zero. |
| `seek 1 30`, `seek 2 12.5` | Set one file's position in seconds; values past its end clamp to its duration. |
| `status` | Display filenames, outputs, states, positions/durations, frame counts, and HRESULTs. |
| `help` | Display control syntax. |
| `quit` | Close the session and release every player. |

Player numbers follow the command-line file order. Controls for one player do
not change the other. `play all` starts both through consecutive independent
controls and does not promise synchronized acoustic starts. Seek accepts finite,
nonnegative seconds using a decimal point. Invalid console commands report an
error and leave the session open. Native playback failures also leave status and
quit available; they cause session exit code `1`. End of input closes all players,
including when commands are piped from another process.

Validate a file session by playing both, pausing player 1 while player 2 continues,
seeking player 1, resuming it, then stopping player 2 and verifying its position
returns to zero while player 1 continues. Finally, let a short file reach its end,
check its state and duration, and quit. Confirm by listening that each file stays
on its assigned physical output; frame counts alone do not prove audible isolation.

## Routing, fan-out, and mixing

The `route` session builds a native graph of up to eight sources, eight outputs,
and 64 source/output connections. Each source has one decoder shared by its
destinations. Each output has one WASAPI mixer that sums its connected sources;
adding another route does not open a second stream on that output. Route gain and
mute are independent, while source play/pause/stop/seek affect all its routes.

Start an empty graph:

```powershell
.\artifacts\bin\x64\Release\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe route
```

Add files and physical outputs inside this console. Paths containing spaces use
double quotes. Windows backslashes are literal; no escape sequences apply.
The example files and endpoint IDs below are specific to the development PC;
replace them with existing paths and current active IDs on another machine.

For **one source on Realtek speakers and WH-1000XM6 headphones**, enter:

```text
source "C:\Users\Legion\Music\Kendrick Lamar - HUMBLE..mp3"
output "{0.0.0.00000000}.{d607b8e4-57c2-43f2-a0a8-80fb4a9e3fc7}"
output "{0.0.0.00000000}.{6657aafe-8f28-416c-839d-45d01af5aff7}"
```

Each command prints a native ID. Replace the angle-bracket tokens in the following
commands with those numeric IDs; IDs need not be consecutive:

```text
connect <humble-source-id> <speaker-output-id> 0.15
connect <humble-source-id> <headphone-output-id> 0.15
play <humble-source-id>
mute <humble-source-id> <speaker-output-id> on
mute <humble-source-id> <speaker-output-id> off
gain <humble-source-id> <headphone-output-id> 0.10
status
stop <humble-source-id>
quit
```

Muting the speaker route leaves the source and headphone route playing. Gain/mute
changes affect newly mixed audio; audio already queued in Windows or the Bluetooth
device can delay the audible change. Source Stop resets the shared source position
for every destination.

For **two sources mixed onto one Realtek output**, start a new `route` session:

```text
source "C:\Users\Legion\Music\Kendrick Lamar - HUMBLE..mp3"
source "C:\Users\Legion\Music\Bedroom - In My head.m4a"
output "{0.0.0.00000000}.{d607b8e4-57c2-43f2-a0a8-80fb4a9e3fc7}"
```

Substitute the printed source/output IDs in these commands:

```text
connect <humble-source-id> <speaker-output-id> 0.15
connect <bedroom-source-id> <speaker-output-id> 0.15
play all
gain <humble-source-id> <speaker-output-id> 0.05
mute <bedroom-source-id> <speaker-output-id> on
mute <bedroom-source-id> <speaker-output-id> off
pause <humble-source-id>
seek <humble-source-id> 15
play <humble-source-id>
status
stop all
quit
```

Pausing one source leaves the other audible through the same output. Native mixing
clamps final samples to the valid range. Excessive combined gain can still sound
distorted; reduce route gains if the clipped-sample counter increases.

| Routing command | Effect |
| --- | --- |
| `source "<file-path>"` | Add a stopped WAV/MP3/M4A source and print its ID. |
| `output default` or `output "<endpoint-id>"` | Add an active output mixer, initially silent, and print its ID. |
| `connect <source-id> <output-id> [gain]` | Add or update a route; default gain `0.15`, mute off. |
| `disconnect <source-id> <output-id>` | Remove one connection while other routes continue. |
| `gain <source-id> <output-id> <value>` | Change one route's finite gain, from `0` through `1`. |
| `mute <source-id> <output-id> on` or `off` | Silence one route while its source continues advancing. |
| `play`, `pause`, or `stop` followed by `<source-id>` or `all` | Control source transports. |
| `seek <source-id> <seconds>` | Seek one source using finite nonnegative seconds, clamped to duration. |
| `remove-source <source-id>` | Remove a source and all its routes. |
| `remove-output <output-id>` | Close an output and remove all its routes. |
| `delay <output-id> <milliseconds>` | Add 0–2000 ms delay to that destination; re-prime its readers. |
| `drift <output-id> on` or `off` | Enable/disable gradual clock correction for that destination. |
| `timing` | Inspect device queues, reader buffers, correction, underruns, and recovery counters. |
| `retry-output <output-id>` | Request asynchronous reopening of the same selected output. |
| `retry-capture <source-id>` | Retry capture of the original live process after fixing a terminal error. |
| `status`, `help`, `quit` | Inspect the graph, show commands, or release the entire graph. |

`status` reads source state/position and decoding counters, output frame/peak/
clipping counters and HRESULTs, and each route's gain, mute, read cursor, and
rebase count. Repeating `connect` updates the existing connection rather than
duplicating it, and sets mute off. A physical endpoint can be added once per graph.
No source plays until a `play` command. End of input also closes the session.
Command mistakes report an error and allow more commands without failing the
session; native action or playback failures are retained as exit code `1`.

Use both listening and telemetry to validate fan-out, per-route gain/mute, and
two-source mixing. Bluetooth buffer latency and independent device clocks remain
best effort; concurrent streams and matching source positions do not prove acoustic
synchronization. The `files` and `tone` commands remain available for their original
independent-player and hardware checks. This milestone uses console controls.

## Timing and device recovery

The `route` engine now supports per-output delay (0–2000 ms), gradual drift
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

## Repeatable routing checks

After building the solution, run software/ABI checks without opening outputs:

```powershell
dotnet run --project tests/AudioHaus.RoutingChecks/AudioHaus.RoutingChecks.csproj -c Release
```

To measure fan-out, mixing, gain, mute, transport, and live graph changes on two
active outputs, set `$outputA` and `$outputB` to their exact IDs and run:

```powershell
dotnet run --project tests/AudioHaus.RoutingChecks/AudioHaus.RoutingChecks.csproj -c Debug -- --hardware --output-a $outputA --output-b $outputB
```

Hardware checks use quiet generated WAV tones. Measurements describe submitted
PCM; listening confirms which physical outputs are audible. The
[routing validation record](docs/RoutingValidation.md) contains architecture,
measured signal levels, device-independent C++ mixing checks, and the listening
sequence. It also records the current latency, drift, and hardware limitations.

## Repeatable file playback checks

The local-file milestone has a separate repeatable validation console:

```powershell
dotnet run --project tests/AudioHaus.FilePlaybackChecks/AudioHaus.FilePlaybackChecks.csproj -c Release
dotnet run --project tests/AudioHaus.FilePlaybackChecks/AudioHaus.FilePlaybackChecks.csproj -c Release -- --file-a $fileA --file-b $fileB
dotnet run --project tests/AudioHaus.FilePlaybackChecks/AudioHaus.FilePlaybackChecks.csproj -c Release -- --hardware --file-a $fileA --file-b $fileB --output-a $outputA --output-b $outputB
```

Set the file paths and output IDs using the example above. Default checks use
generated WAV fixtures and gain zero on a real endpoint to validate format
conversion, play/pause/seek/stop, end-of-file restart, cleanup, and native errors.
Optional file arguments silently validate real MP3/M4A decoding. Hardware mode
plays the two files at gain `0.15` and requires independent state and frame
progression while controlling one player. User files are read only; generated
fixtures are retained in a unique temporary folder. Exit code `2` means required
hardware is unavailable; acoustic routing requires listening confirmation.

The [local-file validation record](docs/FilePlaybackValidation.md) includes the
tested Realtek/WH-1000XM6 routes, actual MP3/M4A files, and a ready-to-run example.

## Tone hardware validation

1. Connect and enable the intended devices in Windows, then list endpoints again.
   Record the exact IDs and confirm both states are `Active`.
2. Run each single-output test above. Confirm the tone is audible on the selected
   device, and that the submitted frame count is nonzero with a completed report.
3. Run the dual-output test. Confirm both devices remain audible at the same time,
   both reports complete successfully, and native stream overlap is positive.
4. Repeat with two Bluetooth devices for the Bluetooth hardware checkpoint. Record
   the speaker/headphone models, adapter, Windows version, command, and output.
   Successful USB/wired playback alone does not validate Bluetooth concurrency.
5. Listen for dropouts and timing differences. Repeat with `--seconds 60` if the
   short test passes; record any failure HRESULT or disconnect. Test device loss
   during a bounded run and confirm the command reports failure and exits.

The current goal is best-effort simultaneous playback. Bluetooth buffering and
independent device clocks can introduce delay differences; this checkpoint does
not promise acoustic synchronization. If either device is inaudible despite a
successful stream report, the hardware checkpoint is still unverified. A missing
or inactive second output requires connecting/enabling hardware before it can be
validated.

## Repeatable playback checks

After building the solution, run the dependency-free validation console:

```powershell
dotnet run --project tests/AudioHaus.PlaybackChecks/AudioHaus.PlaybackChecks.csproj -c Release
```

The default checks argument validation, native report layout, and C API buffer
boundaries without playing audio. Hardware checks are explicit:

```powershell
dotnet run --project tests/AudioHaus.PlaybackChecks/AudioHaus.PlaybackChecks.csproj -c Release -- --single default
dotnet run --project tests/AudioHaus.PlaybackChecks/AudioHaus.PlaybackChecks.csproj -c Release -- --dual $outputA $outputB
dotnet run --project tests/AudioHaus.PlaybackChecks/AudioHaus.PlaybackChecks.csproj -c Release -- --failure-recovery default
```

Set `$outputA` and `$outputB` to actual endpoint IDs as shown above. Single/dual
checks require exact frame counts, successful completion, and positive native
stream intervals; the dual check also requires positive overlap. Failure recovery
checks that an invalid second endpoint prevents either stream from starting, then
opens a short silent stream to verify cleanup and duration rounding.

The validation console also accepts `--hardware`, which chooses the active default
(or first active endpoint), tests it, and then tests two active endpoints when
available. It returns 2 when the requested hardware is unavailable, 1 on failure,
and 0 when all requested checks pass. Listening remains part of hardware acceptance.

See [the recorded playback checkpoint](docs/PlaybackValidation.md) for the measured
Realtek/WH-1000XM6 results and listening confirmation.

## Exit codes and troubleshooting

| Exit code | Meaning |
| --- | --- |
| 0 | Listing/help completed, tone streams completed with positive overlap when applicable, or a file/routing session closed without native failures. |
| 1 | Discovery, library loading, file opening, routing actions, or playback failed; a tone stream was incomplete; or dual tone streams did not overlap. Details are written to standard error. |
| 2 | Invalid command-line arguments, missing files, unresolved/inactive outputs, or duplicate output selections. |

If the native DLL cannot load or is incompatible, rebuild the solution for x64
and keep the generated output files together. A missing native DLL during build
is reported as `AH0001`; build the complete solution with the matching configuration.
If a tone fails, retain its hexadecimal HRESULT and rerun endpoint listing; a
device may have disconnected after selection. Windows controls Bluetooth codecs
and profiles, so concurrent Bluetooth operation depends on the radio, drivers,
and devices. Avoid activating Bluetooth microphones during this playback test.

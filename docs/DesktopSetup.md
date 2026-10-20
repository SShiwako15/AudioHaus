# AudioHaus application router

AudioHaus routes sound from running applications. It is not a music player:
start your game, browser, VLC, or music app normally, then choose where its audio
is heard. The C# WPF interface controls the C++ capture/mixing engine.

## Run

On this development machine, open
`S:\AudioHaus\artifacts\bin\x64\Release\AudioHaus.App\AudioHaus.App.exe`.
For an installed release, use the Start Menu or desktop shortcut created by
the installer described in [Release and prerequisites](Release.md).

To build from Developer PowerShell:

```powershell
msbuild AudioHaus.sln /restore /p:Configuration=Release /p:Platform=x64
.\artifacts\bin\x64\Release\AudioHaus.App\AudioHaus.App.exe
```

Build prerequisites remain Windows 11 x64, Visual Studio 2026/MSVC v145,
Windows SDK 10.0.26100.0, and .NET 10 SDK. Only one desktop instance runs at a time.

## Route an application

1. Connect the headphones/speakers and start audio in the source application.
2. Select its card on the **right** and a playback device on the **left**.
3. Click **Connect**. AudioHaus switches the original app stream to the silent
   cable, verifies isolation, and starts the selected route automatically.
4. Select another source/device pair and Connect to create a separate route.

For example, connect a game to headphones and a music app to speakers. Connect
one source to two devices for fan-out. Connect two sources to one device to mix
them. A source and its output share a connection color; fan-out adds several
labeled color badges. Selection has a separate background and “Selected” label.

**Source volume/mute** affects all routed copies of that source. **Output
volume/mute** affects the AudioHaus mix sent to that destination. Both start at
100% without amplification. Existing Windows/device volume settings are not
written by these controls. Controls are enabled after a route exists.

Select an existing pair and click **Disconnect**, or use its row under
**Connections**. Removing one fan-out route leaves its other destinations active.
The final controlled connection releases the app's Windows output preference.

## Discovery and recovery

Available cards refresh automatically about once per second; meters update about
four times per second. Windows audio-session activity determines source presence,
not whether the current audio samples are silent. Paused apps may disappear if
they stop their render session. Resume audio and their cards return.

Disconnected Bluetooth devices disappear from the available output column.
Saved/paired devices are not treated as connected. The configured parking cable
is hidden from that column; other active Windows virtual outputs can still appear.

Connections remain accessible when a source becomes inactive or a device goes
away. A lost destination waits for that same endpoint to return; AudioHaus does
not choose another speaker automatically. Native recovery retains its routing
and volume/mute settings. Device details contain delay, drift correction, buffer,
and recovery controls/telemetry.

Source meters measure routed contributions after source volume/mute and before
output volume/mute. Output meters measure the AudioHaus mix before hardware
volume/effects. Muting an output leaves its source meter active; muting a source
silences that source's routed meter and all its destinations.
Meter activity does not prove that a physical speaker is audible.

## One-time cable setup

VB-CABLE must be installed separately from its publisher, with a restart when
required. It is already installed on this development machine. AudioHaus selects
a unique active stereo **CABLE Input** automatically. If it cannot, use
**Settings → Parking cable → Use selected cable**. Disconnect all routes before
changing this infrastructure setting.

Keep a physical device as the normal Windows default. In `mmsys.cpl`:

1. Open **Recording → CABLE Output → Properties → Listen**.
2. Leave **Listen to this device** unchecked.
3. Close any other program monitoring the cable.

There is no per-connection Windows Volume mixer step. Connect performs the
application switch. If an app is pinned to an explicit device and refuses to
move, the operation fails after five seconds and restores changes. Restarting
that app's playback or selecting its normal/default output may help; some apps,
exclusive/protected streams, or elevated processes remain unsupported.

## Restoration and limits

Normal window closure stops AudioHaus output and restores previous app render
preferences, including “follow Windows default.” The source apps continue running.
An external Windows preference change is preserved rather than overwritten by
cleanup. Microphone preferences and global Windows defaults are not changed.

Windows can share an output preference across instances of one executable.
AudioHaus reference-counts that shared ownership and restores it when the last
managed instance releases it. Disconnecting one instance must not unpark another
that is still routed. Two source PIDs still have independent captured audio and
source controls.

Windows rejects preference writes through an already-exited PID. If no live
instance remains, or AudioHaus exits unexpectedly, its saved restoration record
is recovered when the app is active again while AudioHaus is running. Reopen
AudioHaus if an app remains silently parked after a crash. Pending recovery is
shown in the workspace. Recovery does not automatically route a new process.

Settings and outstanding restoration intent are stored under
`%LOCALAPPDATA%\AudioHaus\routing.json`. Do not delete outstanding restoration
records while apps are parked. No future-launch routing rules are created.

The current engine manages up to eight sources and eight outputs simultaneously;
all available candidates can still be discovered. Browser tabs sharing an audio
process cannot be separated by this version. Bluetooth latency and acoustic
synchronization remain device dependent; delay compensation is manual.

See [AutomaticRoutingValidation.md](AutomaticRoutingValidation.md) for current
evidence. Older desktop/player validation records describe earlier milestones.

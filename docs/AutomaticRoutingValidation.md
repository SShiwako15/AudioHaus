# Automatic application routing validation

Date: 2026-10-18. Windows 11 build 26200, x64; .NET 10 and MSVC v145.
Destinations: Realtek speakers and WH-1000XM6 Bluetooth headphones. Parking:
stereo CABLE Input from the installed VB-CABLE driver.

## Completed checks

| Method | Result |
| --- | --- |
| Release and Debug solution builds | Passed with warnings treated as errors |
| Additive control/signal ABI | Existing layout checks, new 16/32-byte layouts, invalid gain/ID rejection, and acknowledged targets: passed |
| Native-backed view-model checks (Release/Debug) | Active-only endpoints, reserved-cable exclusion, process lifetimes, stable card identity/selection, control defaults, dispatcher ownership, shutdown during initialization: passed |
| Real Windows switching | Policy capability, actual stream movement, shared application preference, exact role restoration: passed |
| Coordinator with automatic renderers | Fan-out, independent captures/mixing, measured source/output master multiplication, mute isolation, shared ownership, normal restoration: passed |
| Exited-process recovery | Durable restoration record, new-lifetime recovery, no inherited route rule: passed |
| Explicit-output failure | Five-second refusal, graph/preference rollback, shutdown cancellation during Connect: passed |
| Initially muted connections | Source mute and output mute produce zero submitted startup energy; routed source meter follows source controls and remains active during output mute: passed |
| Real WPF automation | Left/right layout, no player/manual capture UI, selection, Connect/Disconnect, shared colors/badges, source/output controls and meters, automatic inactive-source updates, retained connections, normal closure: passed |

RMS measurements for one source on two physical outputs were approximately
0.01414/0.01414 at unity, 0.00707/0.00707 at source gain 0.5, and
0.003536/0.00707 after additionally setting the first output gain to 0.5.
Source mute produced zero PCM on both outputs. Output mute produced zero only
on that destination. Two independent source frequencies mixed to approximately
0.01998 RMS on the shared destination.

Local evidence in ignored build artifacts:

- `RouterChecks.Final.log`: coordinator, PCM, mute startup, failure, and restoration evidence.
- `AutomaticRouting.Desktop.Final.log` and `AutomaticRouting.Desktop.Final.png`:
  successful visible desktop automation.
- `AutomaticRouting.EdgeVlc.log` and `AutomaticRouting.EdgeVlc.png`: real-app
  controls and listening sequence.
- `AutomaticRouting.EdgeVlc.Recovery.log` and `.png`: completed focused physical
  device-recovery check and normal closure.

The native factory ABI is isolated in ApplicationRoutingPolicy, following the
Windows 11 interface shape used by
[EarTrumpet](https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/DataModel/WindowsAudio/Internal/AudioPolicyConfigService.cs).
This is an internal Windows interface, so capability detection and actual-session
verification remain necessary.

## Real application session

The user selected Microsoft Edge (`msedge`) and VLC (`vlc`). Both original
streams moved successfully through the WPF Connect action: Edge to WH-1000XM6,
VLC to Realtek. Automated checks observed independent source mute, source
volume, output mute, and fan-out/mixing. Turning the headphones off removed their
available card while retaining the route, and VLC continued on Realtek. Turning
them back on restored the same endpoint and routed audio automatically. Normal
closure completed with an empty restoration journal.

The user confirmed correct physical destinations, clean sound, independent
controls, fan-out/mixing, recovered Edge playback after reconnecting, and both
apps returning to their original Windows outputs after closure. The first
combined test timed out waiting for reconnection; the focused repeat passed
with a longer hardware response window.

No game has been validated in this milestone. Results for Edge and VLC must
not be generalized to games, protected content, or every browser process model.

## Repeat

Build the solution, then the test renderer and managed checks:

```powershell
msbuild AudioHaus.sln /restore /p:Configuration=Release /p:Platform=x64
msbuild tests/AudioHaus.AutomaticRenderer/AudioHaus.AutomaticRenderer.vcxproj /p:Configuration=Release /p:Platform=x64
dotnet build tests/AudioHaus.ViewModelChecks/AudioHaus.ViewModelChecks.csproj -c Release -p:Platform=x64
dotnet build tests/AudioHaus.SwitchingChecks/AudioHaus.SwitchingChecks.csproj -c Release -p:Platform=x64
dotnet build tests/AudioHaus.RouterChecks/AudioHaus.RouterChecks.csproj -c Release -p:Platform=x64
dotnet build tests/AudioHaus.DesktopChecks/AudioHaus.DesktopChecks.csproj -c Release -p:Platform=x64
```

ViewModelChecks requires no window, output routing, or policy writes. SwitchingChecks
requires the renderer path and cable ID. RouterChecks requires renderer,
Diagnostics, cable, and two physical endpoint IDs. DesktopChecks accepts the
actual app executable and optional router/application mode; see its README.

Synthetic hardware checks produce sound and modify test-owned application
preferences, then restore them. Real-app modes temporarily change the selected
user applications and restore them on normal closure. Run policy-changing checks as the interactive Windows user;
a restricted execution sandbox can reject Windows output-policy writes. GUI
checks require the unlocked interactive desktop.

The renderer uses Microsoft's
[automatic stream-routing activation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/automatic-stream-routing).
It does not resolve “default” into a fixed endpoint. The existing fixed-output
Diagnostics renderer is used specifically to prove refusal and rollback.

Five-millisecond gain ramps, ABI compatibility, physical listening, and external
Windows/device volume have different meanings: submitted PCM ratios do not
establish exact acoustic loudness or synchronized Bluetooth output.

Source signal counters measure contributions after source gain/mute and before
output gain, aggregated across route readers. Their RMS therefore remains at
the source level when only an output is muted. Output counters measure the
complete mix after its master and final clamp. Existing telemetry layouts and
ABI version remain unchanged; level and source-signal queries are additive.

# AudioHaus desktop integration checks

The harness launches the real WPF executable and operates its UI Automation controls, using the production native engine and Windows audio policy. Keep the interactive desktop unlocked.

Build the solution, the automatic renderer, and this project for the same x64 configuration:

```powershell
msbuild AudioHaus.sln /restore /p:Configuration=Release /p:Platform=x64
msbuild tests/AudioHaus.AutomaticRenderer/AudioHaus.AutomaticRenderer.vcxproj /p:Configuration=Release /p:Platform=x64
dotnet build tests/AudioHaus.DesktopChecks/AudioHaus.DesktopChecks.csproj -c Release -p:Platform=x64
$checks = 'artifacts/bin/x64/Release/AudioHaus.DesktopChecks/AudioHaus.DesktopChecks.exe'
$app = 'artifacts/bin/x64/Release/AudioHaus.App/AudioHaus.App.exe'
$renderer = 'artifacts/bin/x64/Release/AudioHaus.AutomaticRenderer/AudioHaus.AutomaticRenderer.exe'
```

Basic layout/settings checks create no routes:

```powershell
& $checks $app --screenshot artifacts/AutomaticRouting.Software.png
```

For routing, supply the stereo cable and two distinct active physical endpoint IDs, obtainable from Diagnostics `list`. This mode creates two quiet test-owned automatic renderers and closes them afterward:

```powershell
& $checks $app --router $renderer $cable_id $output_a $output_b --screenshot artifacts/AutomaticRouting.Desktop.png
```

It checks discovery, selection, Connect/Disconnect, fan-out, matching colors and destination labels, source/output controls, mixing, retained connections when a source becomes inactive, selection preservation across refresh, and normal closure. The renderer activates Microsoft's default-policy audio interface; it does not pin the current default endpoint.

To check existing applications, start their audio normally and pass process names without `.exe`. This mode leaves those applications running:

```powershell
& $checks $app --applications msedge vlc $headphones_id $speakers_id --screenshot artifacts/AutomaticRouting.EdgeVlc.png
```

The listening sequence connects the first app to output A and the second to output B, tests independent source mute/volume and output mute, mixes both apps on output B, then closes AudioHaus and allows listening to the restored original outputs. Human confirmation is required for physical routing and clean sound.

Use `--applications-outage` instead of `--applications` to add device recovery, or `--recovery` to check only connection, device recovery, and closure. Wait for the printed instruction before turning output A off. Reconnect it after detection within the ten-minute timeout. Its card must disappear, its connection remain, output B continue, and output A recover with audio.

Failure cleanup requests normal closure before terminating an unresponsive test-owned process. Screenshots and failure UI trees support diagnosis. Source meters measure contributions after source gain/mute and before output gain; output meters measure submitted AudioHaus PCM. Neither proves acoustic loudness, Bluetooth synchronization, or clean sound. Keep the normal Windows default on a physical output and **Listen to this device** off for CABLE Output.

# Release and prerequisites

AudioHaus is distributed as a Windows x64 installer. The application routes
audio from running applications; it does not replace a music player and does
not install a virtual audio driver.

## End-user prerequisites

- Windows 11 x64 with current audio drivers.
- A physical playback device such as speakers or headphones.
- [VB-CABLE](https://vb-audio.com/Cable/) installed separately from its
  publisher. Choose the publisher's package for the Windows version in use.
- A restart if the VB-CABLE installer requests one.

VB-CABLE is third-party software. AudioHaus does not silently install,
update, or remove it. Review its publisher's license and installer prompts.
Administrator approval is normally required for driver installation.

After installing VB-CABLE, keep a physical device as the normal Windows
default. In `mmsys.cpl`, open **Recording → CABLE Output → Properties →
Listen** and leave **Listen to this device** unchecked. Close any other
program monitoring the cable.

## Install

Run `AudioHaus-<version>-x64-Setup.exe`, accept the installer prompts, and
launch AudioHaus from the Start Menu or optional desktop shortcut. The
installer places the application under `C:\Program Files\AudioHaus` by
default. It installs only AudioHaus files and does not change global Windows
defaults.

On first launch, allow AudioHaus to discover active sessions and playback
devices. If the parking cable is not selected automatically, use **Settings →
Parking cable → Use selected cable** after disconnecting all routes.

## Build and package from source

Install the Visual Studio C++ workload with MSVC v145 and Windows SDK
10.0.26100.0, .NET 10 SDK, and Inno Setup 6. From Developer PowerShell:

```powershell
.\packaging\Build-Release.ps1 -Version 1.0.0
```

The script restores and builds `AudioHaus.sln` for `Release|x64`, verifies the
runtime files, stages a clean tree under `artifacts\release\AudioHaus`, and
writes the installer under `artifacts\release`. To validate staging without
Inno Setup:

```powershell
.\packaging\Build-Release.ps1 -SkipInstaller
```

When packaging an already-built, verified output on a machine without Visual
Studio, use both `-SkipBuild -SkipInstaller`. The normal release command still
builds first; `-SkipBuild` is only for local packaging validation.

The script fails rather than producing a success-shaped package when MSBuild,
the application output, a required native DLL, or Inno Setup is unavailable.

## Uninstall and recovery

Uninstall AudioHaus from **Installed apps** or the Start Menu uninstaller.
Uninstalling removes the application files and shortcuts; it does not remove
VB-CABLE or alter the normal Windows default device.

AudioHaus stores settings and outstanding restoration intent in
`%LOCALAPPDATA%\AudioHaus\routing.json`. Close AudioHaus normally before
uninstalling so parked application preferences can be restored. If an app
remains silent after an unexpected exit, reopen AudioHaus while that app is
active and follow the pending recovery notice. Do not delete that file while
restoration records are outstanding.

See [DesktopSetup.md](DesktopSetup.md) for detailed operation and
[DesktopValidation.md](DesktopValidation.md) for the current validation
boundaries and known limitations.

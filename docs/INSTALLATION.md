# Installation

## Requirements

- Windows 10 or Windows 11, 64-bit
- PowerShell 5.1 or later
- .NET 10 SDK for source builds
- An internet connection for radio features and the first NuGet restore

Check the SDK:

```powershell
dotnet --info
```

## Recommended installation

Not technical? Just double-click `Install.bat` (see the README). For developers:

Clone the repository and run the installation helper:

```powershell
git clone https://github.com/debattistacliff-commits/Titanium-Orbital-Launcher.git
cd Titanium-Orbital-Launcher
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\install.ps1
```

The script:

1. Restores and publishes a Release build for `win-x64`.
2. Places the build in `publish\win-x64`.
3. Creates a Desktop shortcut with the Orbital Launcher icon.

Run the application from the shortcut or directly:

```text
publish\win-x64\OrbitalLauncher.exe
```

## Manual development build

```powershell
dotnet restore .\DesktopOrbit.csproj
dotnet build .\DesktopOrbit.csproj -c Debug
dotnet run --project .\DesktopOrbit.csproj
```

## Manual Release publish

Framework-dependent build (smaller; requires the .NET Desktop Runtime):

```powershell
dotnet publish .\DesktopOrbit.csproj -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

Self-contained build (larger; carries the runtime):

```powershell
.\scripts\install.ps1 -SelfContained
```

## Updating

```powershell
git pull
.\scripts\install.ps1
```

The shortcut is refreshed to the newly published executable.

## Uninstalling

```powershell
.\scripts\uninstall.ps1
```

By default this removes the Desktop shortcut and published binaries. It deliberately preserves:

- `%LOCALAPPDATA%\DesktopOrbit\settings.json`
- `%USERPROFILE%\Documents\Desktop Orbit Library`

To remove local preferences as well:

```powershell
.\scripts\uninstall.ps1 -RemoveSettings
```

The managed library is never deleted by the script because it may contain personal files.

## Troubleshooting

### Windows blocks the script

Use the process-scoped policy shown above. It affects only the current PowerShell window.

### Radio stations do not play

Try another station. Public stream URLs can go offline, reject some players, require a codec, or block a region. Confirm that Windows audio is not muted and that your firewall allows the application to access the network.

### The shortcut shows an old icon

Refresh the Desktop or restart Windows Explorer. Windows sometimes retains icons in its shell cache.

### Search indexing takes time

The launcher scans accessible fixed drives for supported files and folders. Large drives can take time. Permission-denied locations are skipped.

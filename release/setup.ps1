# Installs Orbital Launcher for the current user (no admin rights needed).
param(
    [Parameter(Mandatory)] [string]$Source,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Orbital Launcher'),
    [switch]$NoShortcuts,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$exeName = 'OrbitalLauncher.exe'
if (-not (Test-Path -LiteralPath (Join-Path $Source $exeName))) {
    throw "The app files are missing. Extract the whole ZIP first (right-click it, Extract All), then run the installer from the extracted folder."
}

# An older copy (or the old Titanium Orbital) must be closed before its files are replaced.
Get-Process OrbitalLauncher, TitaniumOrbitalLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
Get-ChildItem -LiteralPath $Destination -Recurse -File | Unblock-File   # downloaded files: don't warn again on every start

$exe = Join-Path $Destination $exeName
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $places = @(
        [Environment]::GetFolderPath('DesktopDirectory'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) '')
    )
    foreach ($place in $places) {
        $link = $shell.CreateShortcut((Join-Path $place 'Orbital Launcher.lnk'))
        $link.TargetPath = $exe
        $link.WorkingDirectory = $Destination
        $link.IconLocation = "$exe,0"
        $link.Description = 'Open the Orbital Launcher ring (Alt+Space)'
        $link.Save()
    }

    # Shows up in Settings > Apps > Installed apps, with an Uninstall button.
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OrbitalLauncher'
    New-Item -Path $key -Force | Out-Null
    $uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$Destination\uninstall.ps1`""
    $values = @{
        DisplayName = 'Orbital Launcher'; DisplayVersion = $version; Publisher = 'debattistacliff-commits'
        DisplayIcon = "$exe,0"; InstallLocation = $Destination; UninstallString = $uninstall
        URLInfoAbout = 'https://github.com/debattistacliff-commits/Orbital-Launcher'
    }
    foreach ($name in $values.Keys) { Set-ItemProperty -Path $key -Name $name -Value $values[$name] }
    Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord
}

Write-Host "Orbital Launcher $version installed in $Destination"
if (-not $NoLaunch) {
    Start-Process -FilePath $exe
}

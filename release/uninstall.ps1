# Removes Orbital Launcher for the current user. Your settings are kept unless you choose to delete them.
param([switch]$RemoveSettings, [switch]$Quiet)

$installDir = $PSScriptRoot
Get-Process OrbitalLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

foreach ($place in [Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs')) {
    Remove-Item -LiteralPath (Join-Path $place 'Orbital Launcher.lnk') -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath 'HKCU:\Software\Classes\DesktopBackground\Shell\OrbitalLauncher' -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'OrbitalLauncher' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OrbitalLauncher' -Recurse -Force -ErrorAction SilentlyContinue

if (-not $RemoveSettings -and -not $Quiet) {
    $answer = Read-Host 'Also delete your settings (pinned apps, places, favourite stations)? Y/N'
    $RemoveSettings = $answer -match '^[Yy]'
}
if ($RemoveSettings) {
    Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'DesktopOrbit') -Recurse -Force -ErrorAction SilentlyContinue
}

# Only delete the folder if it really is an Orbital Launcher install.
if (Test-Path -LiteralPath (Join-Path $installDir 'OrbitalLauncher.exe')) {
    Set-Location $env:TEMP
    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Orbital Launcher has been removed.'

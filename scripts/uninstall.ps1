param(
    [switch]$RemoveSettings
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $projectRoot 'publish\win-x64'
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktop 'Orbital Launcher.lnk'

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

Get-Process OrbitalLauncher -ErrorAction SilentlyContinue | Stop-Process -Force

# Desktop right-click entry and "Start with Windows" (both per-user, added by v2).
foreach ($name in 'OrbitalLauncher', 'TitaniumOrbital') {
    Remove-Item -LiteralPath "HKCU:\Software\Classes\DesktopBackground\Shell\$name" -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'OrbitalLauncher' -ErrorAction SilentlyContinue
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TitaniumOrbitalLauncher' -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

if ($RemoveSettings) {
    $settingsDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'DesktopOrbit'
    if (Test-Path -LiteralPath $settingsDirectory) {
        Remove-Item -LiteralPath $settingsDirectory -Recurse -Force
    }
}

Write-Host 'Orbital Launcher shortcut and published files were removed.'
Write-Host 'The Desktop Orbit Library was preserved because it may contain personal files.'

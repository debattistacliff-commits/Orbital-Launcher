param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'DesktopOrbit.csproj'
$publishDirectory = Join-Path $projectRoot 'publish\win-x64'
$iconPath = Join-Path $projectRoot 'Assets\orbital-launcher.ico'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0'
}

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish $projectFile -c Release -r win-x64 --self-contained $selfContainedValue -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. Close Orbital Launcher if its installed files are in use, then run the installer again."
}

$executable = Join-Path $publishDirectory 'OrbitalLauncher.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Publish completed without the expected executable: $executable"
}

$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktop 'Orbital Launcher.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $publishDirectory
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Description = 'Open the Orbital Launcher ring (Alt+Space)'
$shortcut.Save()

Write-Host "Orbital Launcher installed."
Write-Host "Application: $executable"
Write-Host "Shortcut:    $shortcutPath"

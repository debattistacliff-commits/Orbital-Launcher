# Builds the ready-to-install ZIP for a GitHub release: artifacts\OrbitalLauncher-<version>-Setup.zip
# Nothing to build or download for the person installing it (.NET is included).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'DesktopOrbit.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "OrbitalLauncher-$version-Setup"
$staging = Join-Path $root "artifacts\$name"
$zip = Join-Path $root "artifacts\$name.zip"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish (Join-Path $root 'DesktopOrbit.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o (Join-Path $staging 'app')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $root 'release\setup.ps1'), (Join-Path $root 'release\uninstall.ps1') (Join-Path $staging 'app')
Copy-Item (Join-Path $root 'release\Install Orbital Launcher.bat'), (Join-Path $root 'release\READ ME.txt') $staging
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $staging 'LICENSE.txt')
Copy-Item (Join-Path $root 'Assets\flags\LICENSE-flag-icons.txt') $staging

Compress-Archive -Path $staging -DestinationPath $zip
Write-Host "Release package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

@echo off
setlocal
title Orbital Launcher - Install
cd /d "%~dp0"
set "PATH=%ProgramFiles%\dotnet;%PATH%"

echo.
echo  ==========================================
echo    Orbital Launcher - Installer
echo  ==========================================
echo.

rem --- 1. The app is built with Microsoft .NET 10 (free). Get it if it's missing.
dotnet --list-sdks 2>nul | findstr /b "10." >nul
if errorlevel 1 (
    echo  This app needs Microsoft .NET 10, a free download from Microsoft.
    echo.
    where winget >nul 2>nul
    if errorlevel 1 (
        echo  Please install the ".NET 10 SDK" from the page that opens now,
        echo  then double-click Install.bat again.
        start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
        echo.
        pause
        exit /b 1
    )
    choice /c YN /m " Download and install .NET 10 now"
    if errorlevel 2 (
        echo  OK, nothing was installed.
        pause
        exit /b 1
    )
    winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
    dotnet --list-sdks 2>nul | findstr /b "10." >nul
    if errorlevel 1 (
        echo.
        echo  .NET 10 did not finish installing. Restart the PC and run Install.bat again.
        pause
        exit /b 1
    )
)

rem --- 2. Close a copy that's already running from this folder, then build and add the desktop shortcut.
powershell -NoProfile -Command "Get-Process OrbitalLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%~dp0publish\*' } | Stop-Process -Force"
echo  Installing (this takes about a minute the first time)...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"
if errorlevel 1 (
    echo.
    echo  Something went wrong. Please read the message above.
    pause
    exit /b 1
)

echo.
echo  Done! Press Alt+Space anywhere to open the ring.
echo  You can also use the "Orbital Launcher" icon on your desktop.
echo.
if not defined ORBITAL_NO_LAUNCH start "" "%~dp0publish\win-x64\OrbitalLauncher.exe"
pause

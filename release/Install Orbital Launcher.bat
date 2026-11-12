@echo off
setlocal
title Orbital Launcher - Install
echo.
echo  ==========================================
echo    Orbital Launcher - Installer
echo  ==========================================
echo.
if not exist "%~dp0app\OrbitalLauncher.exe" (
    echo  Please extract the ZIP first: right-click it and choose "Extract All",
    echo  then open the extracted folder and double-click this file again.
    echo.
    pause
    exit /b 1
)
set "OPTIONS="
if defined ORBITAL_INSTALL_DIR set OPTIONS=-Destination "%ORBITAL_INSTALL_DIR%" -NoShortcuts
if defined ORBITAL_NO_LAUNCH set OPTIONS=%OPTIONS% -NoLaunch
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\setup.ps1" -Source "%~dp0app" %OPTIONS%
if errorlevel 1 (
    echo.
    echo  Something went wrong. Please read the message above.
    pause
    exit /b 1
)
echo.
echo  Done! Orbital Launcher is on your desktop and in the Start menu.
echo  Press Alt+Space anywhere to open the ring.
echo  To remove it later: Settings, Apps, Installed apps, Orbital Launcher, Uninstall.
echo.
pause

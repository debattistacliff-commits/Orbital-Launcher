@echo off
setlocal
title Orbital Launcher - Uninstall
cd /d "%~dp0"

echo.
echo  This removes Orbital Launcher, its desktop icon,
echo  the desktop right-click entry and "Start with Windows".
echo.
choice /c YN /m " Uninstall now"
if errorlevel 2 exit /b 0

choice /c YN /m " Also delete your settings (pinned apps, places, favourite stations)"
if errorlevel 2 (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1" -RemoveSettings
)

echo.
echo  Orbital Launcher has been removed. You can delete this folder now.
pause

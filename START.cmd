@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
title DeskPods 0.8.46
cd /d "%~dp0"
set "EXPECTED=0.8.46"
echo.
echo   DeskPods 0.8.46
echo   -------------------------------
echo   1  Start DeskPods
echo   2  Install / create desktop shortcut
echo   3  Roll back local update
echo   4  Roll back installed update
echo   5  Install .NET 8 SDK
echo   6  Uninstall installed app
echo   0  Exit
echo.
choice /c 1234560 /n /m "Choose an action: "
set "PICK=%ERRORLEVEL%"
if "%PICK%"=="7" exit /b 0
if "%PICK%"=="0" exit /b 1
if "%PICK%"=="255" exit /b 1
set "ACTION="
if "%PICK%"=="1" set "ACTION=start"
if "%PICK%"=="2" set "ACTION=install"
if "%PICK%"=="3" set "ACTION=rollback"
if "%PICK%"=="4" set "ACTION=rollback-install"
if "%PICK%"=="5" set "ACTION=sdk"
if "%PICK%"=="6" set "ACTION=uninstall"
if not defined ACTION exit /b 1
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\launcher.ps1" -Action "%ACTION%" -ExpectedVersion "%EXPECTED%"
set "RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %RESULT%

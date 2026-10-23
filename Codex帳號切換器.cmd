@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "SWITCHER_APP=%~dp0dist-v1.1.0\CodexAccountSwitcher.exe"
if not exist "%SWITCHER_APP%" goto build
if not exist "%SWITCHER_APP%.config" goto build
goto launch

:build
echo Building Codex Account Switcher...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 goto build_failed
if not exist "%SWITCHER_APP%" goto build_failed
if not exist "%SWITCHER_APP%.config" goto build_failed

:launch
start "" "%SWITCHER_APP%"
if errorlevel 1 goto launch_failed
exit /b 0

:build_failed
echo.
echo Build failed. Please review the error above and try again.
pause
exit /b 1

:launch_failed
echo.
echo Could not start Codex Account Switcher.
pause
exit /b 1

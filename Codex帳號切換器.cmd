@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "SWITCHER_BUILD_ONLY="
if "%1"=="" goto arguments_ready
if /i not "%~1"=="--build-only" goto invalid_arguments
if not "%2"=="" goto invalid_arguments
set "SWITCHER_BUILD_ONLY=1"

:arguments_ready
set "SWITCHER_APP=%~dp0dist-v1.5.1\CodexAccountSwitcher.exe"
if not exist "%SWITCHER_APP%" goto build
if not exist "%SWITCHER_APP%.config" goto build
if not exist "%~dp0dist-v1.5.1\ManagedBridge.exe" goto build
if not exist "%~dp0dist-v1.5.1\ManagedBridge.exe.config" goto build
if not exist "%~dp0dist-v1.5.1\codex-account-switcher-vscode.vsix" goto build
if not exist "%~dp0dist-v1.5.1\DesktopRuntimeHost.exe" goto build
if not exist "%~dp0dist-v1.5.1\DesktopRuntimeHost.exe.config" goto build
if not exist "%~dp0dist-v1.5.1\DesktopAppRelay.exe" goto build
if not exist "%~dp0dist-v1.5.1\DesktopAppRelay.exe.config" goto build
if not exist "%~dp0dist-v1.5.1\desktop-start.ps1" goto build
goto launch

:build
echo Building Codex Account Switcher...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 goto build_failed
if not exist "%SWITCHER_APP%" goto build_failed
if not exist "%SWITCHER_APP%.config" goto build_failed

:launch
if defined SWITCHER_BUILD_ONLY exit /b 0
start "" "%SWITCHER_APP%"
if errorlevel 1 goto launch_failed
exit /b 0

:build_failed
echo.
echo Build failed. Please review the error above and try again.
if defined SWITCHER_BUILD_ONLY exit /b 1
pause
exit /b 1

:invalid_arguments
echo Usage: launcher [--build-only]
exit /b 2

:launch_failed
echo.
echo Could not start Codex Account Switcher.
pause
exit /b 1

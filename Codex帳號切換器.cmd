@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "SWITCHER_BUILD_ONLY="
set "SWITCHER_BUILT="
if "%1"=="" goto arguments_ready
if /i not "%~1"=="--build-only" goto invalid_arguments
if not "%2"=="" goto invalid_arguments
set "SWITCHER_BUILD_ONLY=1"

:arguments_ready
set "SWITCHER_RELEASE=%~dp0release\v1.6.28"
set "SWITCHER_APP=%SWITCHER_RELEASE%\CodexRoster.exe"
if not exist "%SWITCHER_APP%" goto missing_dependency
if not exist "%SWITCHER_APP%.config" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\ManagedBridge.exe" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\ManagedBridge.exe.config" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\codex-account-switcher-vscode.vsix" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\DesktopRuntimeHost.exe" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\DesktopRuntimeHost.exe.config" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\DesktopAppRelay.exe" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\DesktopAppRelay.exe.config" goto missing_dependency
if not exist "%SWITCHER_RELEASE%\desktop-start.ps1" goto missing_dependency
goto launch

:missing_dependency
if defined SWITCHER_BUILT goto build_failed

:build
echo Building Codex Roster...
set "SWITCHER_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "SWITCHER_POWERSHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%SWITCHER_POWERSHELL%" goto powershell_missing
"%SWITCHER_POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 goto build_failed
set "SWITCHER_BUILT=1"
goto arguments_ready

:launch
if defined SWITCHER_BUILD_ONLY exit /b 0
start "" "%SWITCHER_APP%"
if errorlevel 1 goto launch_failed
exit /b 0

:powershell_missing
echo Windows PowerShell 5.1 was not found.
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
echo Could not start Codex Roster.
pause
exit /b 1

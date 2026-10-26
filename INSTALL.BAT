@echo off
rem =============================================================================
rem  Navisworks MCP Bridge - installer
rem =============================================================================
rem  Installs MCPBridge.bundle (one DLL per Navisworks release, see its
rem  PackageContents.xml) into
rem
rem    %ProgramData%\Autodesk\ApplicationPlugins\MCPBridge.bundle\
rem
rem  Navisworks Manage / Simulate 2026 and 2027 load it from there on startup
rem  and pick the DLL built for their own API.
rem
rem  The bundle is looked up next to this file in release\MCPBridge.bundle\
rem  (source tree) or MCPBridge.bundle\ (unpacked release zip).
rem
rem  Usage:
rem    INSTALL.BAT               install / update
rem    INSTALL.BAT /uninstall    remove the plugin
rem =============================================================================
setlocal EnableExtensions

set "HERE=%~dp0"
set "MODE=install"
if /I "%~1"=="/uninstall" set "MODE=uninstall"
if /I "%~1"=="-uninstall" set "MODE=uninstall"
rem Pause at the end only when started by double-click (no arguments).
set "PAUSE_AT_END="
if "%~1"=="" set "PAUSE_AT_END=1"

set "DEST=%ProgramData%\Autodesk\ApplicationPlugins\MCPBridge.bundle"
set "SRC="
if exist "%HERE%release\MCPBridge.bundle\PackageContents.xml" set "SRC=%HERE%release\MCPBridge.bundle"
if not defined SRC if exist "%HERE%MCPBridge.bundle\PackageContents.xml" set "SRC=%HERE%MCPBridge.bundle"

echo === Navisworks MCP Bridge: %MODE% ===
echo.

rem Navisworks locks the DLL while running - a copy over it would fail.
tasklist /FI "IMAGENAME eq Roamer.exe" 2>nul | find /I "Roamer.exe" >nul
if not errorlevel 1 (
    echo ERROR: Navisworks is running. Close it and run INSTALL.BAT again.
    goto :fail
)

call :remove_old_installs || goto :fail

if "%MODE%"=="uninstall" (
    if exist "%DEST%" (
        rmdir /S /Q "%DEST%"
        if exist "%DEST%" (echo [FAIL] cannot remove %DEST% & goto :fail)
        echo [ ok ] removed %DEST%
    ) else (
        echo [skip] %DEST% not installed
    )
    goto :done
)

if not defined SRC (
    echo ERROR: MCPBridge.bundle not found in "%HERE%release\" or "%HERE%".
    goto :fail
)
rem /MIR keeps the installed bundle an exact copy (drops files removed from a newer build).
robocopy "%SRC%" "%DEST%" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo [FAIL] copy to %DEST%
    echo        Run INSTALL.BAT as administrator.
    goto :fail
)
rem Strip Mark-of-the-Web, otherwise .NET refuses to load a DLL from a downloaded zip.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%DEST%' -Recurse -File | Unblock-File" >nul 2>&1
echo [ ok ] installed %DEST%

set "FOUND="
for %%Y in (2026 2027) do for %%P in (Manage Simulate) do (
    if exist "%ProgramFiles%\Autodesk\Navisworks %%P %%Y\Roamer.exe" (
        echo        Navisworks %%P %%Y will load it
        set "FOUND=1"
    )
)
if not defined FOUND echo [warn] no Navisworks Manage/Simulate 2026 or 2027 found on this PC

:done
echo.
if "%MODE%"=="install" (
    echo Done. Restart Navisworks to load the plugin.
    echo.
    echo MCP server for Claude:  pip install mcp, then in claude_desktop_config.json use
    echo   "command": "python", "args": ["%DEST:\=\\%\\Contents\\server_http.py"]
) else (
    echo Done.
)
echo.
if defined PAUSE_AT_END pause
exit /b 0

rem -----------------------------------------------------------------------------
rem Earlier versions installed the same assembly (MCPBridge, plugin id
rem MCPBridge.ADSK) as a loose plugin folder. Next to the bundle it would load
rem twice and fight over port 8765, so remove every such copy.
:remove_old_installs
set "OLD_FAILED="
for %%Y in (2026 2027) do for %%P in (Manage Simulate) do for %%N in (MCPBridge NavisworksMcpAddin) do (
    call :remove_dir "%APPDATA%\Autodesk\Navisworks %%P %%Y\Plugins\%%N"
    call :remove_dir "%ProgramFiles%\Autodesk\Navisworks %%P %%Y\Plugins\%%N"
)
if defined OLD_FAILED exit /b 1
exit /b 0

:remove_dir
if not exist "%~1" exit /b 0
rmdir /S /Q "%~1" >nul 2>&1
if exist "%~1" (
    echo [FAIL] cannot remove old copy %~1
    echo        Run INSTALL.BAT as administrator, or delete that folder by hand.
    set "OLD_FAILED=1"
) else (
    echo [ ok ] removed old copy %~1
)
exit /b 0

rem -----------------------------------------------------------------------------
:fail
echo.
if defined PAUSE_AT_END pause
exit /b 1

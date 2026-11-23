@echo off
rem Navisworks MCP Bridge installer. Run from the unpacked package folder, next to MCPBridge.bundle.
rem   INSTALL.BAT              - interactive: language, install or uninstall, AI clients
rem   INSTALL.BAT I [EN|RU]    - install / update without questions
rem   INSTALL.BAT U [EN|RU]    - uninstall without questions; /uninstall works too
rem The bundle goes to %ProgramData%\Autodesk\ApplicationPlugins\MCPBridge.bundle; Navisworks Manage and
rem Simulate 2026 / 2027 load it from there and pick the DLL built for their API.
rem The MCP server is Contents\Server\navis_mcp_server.dll, started by the AI client as
rem "dotnet.exe navis_mcp_server.dll". It registers itself: --register claude-desktop / claude-code / codex.
rem Versions up to 2.2.x used a Python server, Contents\server_http.py: it is removed here and the
rem "navisworks" entry of each AI client is pointed at the new server.
rem Overrides, for tests and development:
rem   NAVMCP_TARGET            bundle folder,           default %ProgramData%\Autodesk\ApplicationPlugins\MCPBridge.bundle
rem   NAVMCP_PROGRAMFILES      where Navisworks is,     default %ProgramFiles%
rem   NAVMCP_APPDATA           roaming profile,         default %APPDATA% - old plugin copies live under it
rem   NAVMCP_CLAUDE_CONFIG     Claude Desktop config,   default %APPDATA%\Claude\claude_desktop_config.json
rem   NAVMCP_CODEX_CONFIG      Codex config,            default %CODEX_HOME% or %USERPROFILE%\.codex, file config.toml
rem   NAVMCP_NO_CLIENTS=1      do not register the server in any AI client
rem   NAVMCP_NO_CLAUDE_CODE=1  leave Claude Code alone
rem   NAVMCP_NO_RUNNING_CHECK=1  do not refuse while Navisworks is running
rem The file is UTF-8 without BOM with CRLF line ends: the first command switches the console to UTF-8.
rem Messages must not contain parentheses: some are expanded inside ( ) blocks.
chcp 65001 >nul
setlocal EnableExtensions DisableDelayedExpansion

set "YEARS=2026 2027"
set "SOURCE=%~dp0MCPBridge.bundle"
if defined NAVMCP_TARGET (set "TARGET=%NAVMCP_TARGET%") else set "TARGET=%ProgramData%\Autodesk\ApplicationPlugins\MCPBridge.bundle"
if defined NAVMCP_PROGRAMFILES (set "PF=%NAVMCP_PROGRAMFILES%") else set "PF=%ProgramFiles%"
if defined NAVMCP_APPDATA (set "AD=%NAVMCP_APPDATA%") else set "AD=%APPDATA%"
if defined NAVMCP_CLAUDE_CONFIG (set "CLAUDE_CONFIG=%NAVMCP_CLAUDE_CONFIG%") else set "CLAUDE_CONFIG=%APPDATA%\Claude\claude_desktop_config.json"
if defined CODEX_HOME (set "CODEX_DIR=%CODEX_HOME%") else set "CODEX_DIR=%USERPROFILE%\.codex"
if defined NAVMCP_CODEX_CONFIG (set "CODEX_CONFIG=%NAVMCP_CODEX_CONFIG%") else set "CODEX_CONFIG=%CODEX_DIR%\config.toml"
set "SERVER_DLL=%TARGET%\Contents\Server\navis_mcp_server.dll"
set "INTERACTIVE=1"
if not "%~1"=="" set "INTERACTIVE=0"
set "RC=0"

title Navisworks MCP Bridge

rem ---------------------------------------------------------------- language
set "UI_LANG=%~2"
if "%INTERACTIVE%"=="0" goto :lang_selected
echo.
echo   1 - English
echo   2 - Русский
set "UI_LANG="
set /p "UI_LANG=Select installer language [1/2]: "
if "%UI_LANG%"=="2" (set "UI_LANG=RU") else set "UI_LANG=EN"
:lang_selected
if /i "%UI_LANG%"=="RU" (call :messages_ru) else call :messages_en

rem ---------------------------------------------------------------- what is here
echo.
echo ================================================================
echo   %M_TITLE%
echo ================================================================
echo.
echo %M_FOUND_HEADER%
set "FOUND="
for %%Y in (%YEARS%) do for %%P in (Manage Simulate) do call :detect %%P %%Y
if not defined FOUND echo   %M_NONE_FOUND%
call :show_installed

set "CHOICE=%~1"
if "%INTERACTIVE%"=="0" goto :dispatch
echo.
echo   I - %M_PROMPT_INSTALL%
echo   U - %M_PROMPT_UNINSTALL%
echo   Q - %M_PROMPT_QUIT%
:ask_choice
rem Empty answer: ask again. End of input, e.g. a closed pipe: quit.
set "CHOICE="
set /p "CHOICE=%M_PROMPT_CHOICE% " || goto :end
if not defined CHOICE goto :ask_choice

:dispatch
if /i "%CHOICE%"=="Q" goto :end
if /i "%CHOICE%"=="I" goto :install
if /i "%CHOICE%"=="U" goto :uninstall
if /i "%CHOICE%"=="/uninstall" goto :uninstall
if /i "%CHOICE%"=="-uninstall" goto :uninstall
echo.
call echo %M_BAD_INPUT%
goto :fail

rem ---------------------------------------------------------------- install
:install
call :check_navis_closed || goto :fail
if not exist "%SOURCE%\PackageContents.xml" goto :no_package
if not exist "%SOURCE%\Contents\2026\MCPBridge.dll" goto :no_package
if not exist "%SOURCE%\Contents\2027\MCPBridge.dll" goto :no_package
if not exist "%SOURCE%\Contents\Server\navis_mcp_server.dll" goto :no_package

call :remove_old_installs || goto :fail
set "OLD_PYTHON="
if exist "%TARGET%\Contents\server_http.py" set "OLD_PYTHON=1"

echo.
echo %M_COPYING%
echo   %TARGET%
call :free_server_dll
rem /MIR also removes the Python server of versions up to 2.2.x.
robocopy "%SOURCE%" "%TARGET%" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo %M_COPY_FAILED%
    goto :fail
)
if defined SERVER_IN_USE echo %M_SERVER_IN_USE%
rem Remove the "downloaded from the internet" mark so .NET loads the DLLs.
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%TARGET%' -Recurse -File | Unblock-File" >nul 2>&1
if defined OLD_PYTHON echo %M_OLD_PYTHON_REMOVED%
echo %M_DONE%
call :show_installed

call :register_clients

echo.
echo ================================================================
echo %M_RUN_HINT_1%
echo %M_RUN_HINT_2%
echo %M_RUN_HINT_3%
goto :end

rem ---------------------------------------------------------------- running MCP server
rem An open AI client keeps navis_mcp_server.dll loaded, and a loaded DLL cannot be overwritten. It can be
rem moved within the drive, though: the running server goes on with the moved copy, the client starts the
rem new one after a restart. Copies moved by earlier installs are deleted once no server uses them.
:free_server_dll
set "SERVER_IN_USE="
del /q "%TEMP%\navis_mcp_server_*.dll.old" >nul 2>&1
if not exist "%SERVER_DLL%" exit /b 0
fc /b "%SERVER_DLL%" "%SOURCE%\Contents\Server\navis_mcp_server.dll" >nul 2>&1 && exit /b 0
set "OLD_DLL=%TEMP%\navis_mcp_server_%RANDOM%%RANDOM%.dll.old"
move /y "%SERVER_DLL%" "%OLD_DLL%" >nul 2>&1 || exit /b 0
del /q "%OLD_DLL%" >nul 2>&1
if exist "%OLD_DLL%" set "SERVER_IN_USE=1"
exit /b 0

rem ---------------------------------------------------------------- old plugin copies
rem Earlier versions installed the same plugin as a loose folder. Next to the bundle it would load
rem twice and fight over port 8765, so every such copy goes.
:remove_old_installs
set "OLD_FAILED="
for %%Y in (%YEARS%) do for %%P in (Manage Simulate) do for %%N in (MCPBridge NavisworksMcpAddin) do (
    call :remove_dir "%AD%\Autodesk\Navisworks %%P %%Y\Plugins\%%N"
    call :remove_dir "%PF%\Autodesk\Navisworks %%P %%Y\Plugins\%%N"
)
if defined OLD_FAILED exit /b 1
exit /b 0

:remove_dir
if not exist "%~1" exit /b 0
rmdir /s /q "%~1" >nul 2>&1
if exist "%~1" (
    echo %M_OLD_COPY_FAILED%
    echo   %~1
    echo %M_RUN_AS_ADMIN%
    set "OLD_FAILED=1"
) else (
    echo %M_OLD_COPY_REMOVED%
    echo   %~1
)
exit /b 0

rem ---------------------------------------------------------------- AI clients
:register_clients
if "%NAVMCP_NO_CLIENTS%"=="1" exit /b 0
echo.
echo %M_CLIENTS_STEP%
call :find_dotnet
if not defined DOTNET (
    echo %M_NO_DOTNET_1%
    echo %M_NO_DOTNET_2%
    exit /b 0
)
"%DOTNET%" "%SERVER_DLL%" --version >nul 2>&1 || (
    echo %M_SERVER_FAILED%
    exit /b 0
)
set "ANY_CLIENT="

rem Claude Desktop: its settings folder exists.
set "C_FOUND=" & set "C_HAS="
for %%F in ("%CLAUDE_CONFIG%") do if exist "%%~dpF" set "C_FOUND=1"
if exist "%CLAUDE_CONFIG%" findstr /c:"\"navisworks\"" "%CLAUDE_CONFIG%" >nul 2>&1 && set "C_HAS=1"
if defined C_FOUND call :offer claude-desktop "Claude Desktop" "%CLAUDE_CONFIG%"

rem Codex - CLI, IDE extension or app: its settings folder exists or the codex command is on PATH.
set "C_FOUND=" & set "C_HAS="
for %%F in ("%CODEX_CONFIG%") do if exist "%%~dpF" set "C_FOUND=1"
if not defined NAVMCP_CODEX_CONFIG where codex >nul 2>&1 && set "C_FOUND=1"
if exist "%CODEX_CONFIG%" findstr /c:"[mcp_servers.navisworks]" "%CODEX_CONFIG%" >nul 2>&1 && set "C_HAS=1"
if defined C_FOUND call :offer codex "Codex" "%CODEX_CONFIG%"

rem Claude Code: the claude command is on PATH.
set "C_FOUND=" & set "C_HAS="
if not "%NAVMCP_NO_CLAUDE_CODE%"=="1" where claude >nul 2>&1 && set "C_FOUND=1"
if defined C_FOUND call :offer claude-code "Claude Code" ""

if not defined ANY_CLIENT echo %M_NO_CLIENTS%
echo.
echo %M_CLIENT_MANUAL%
echo   "%DOTNET%" "%SERVER_DLL%" --register claude-desktop
echo   "%DOTNET%" "%SERVER_DLL%" --register claude-code
echo   "%DOTNET%" "%SERVER_DLL%" --register codex
exit /b 0

rem :offer <client id> <name> <config path or "">. A client that already has a "navisworks" server -
rem the Python one of 2.2.x or an earlier .NET one - is updated without asking.
:offer
set "ANY_CLIENT=1"
if defined C_HAS goto :offer_register
if "%INTERACTIVE%"=="0" goto :offer_register
call set "ASK=%M_CLIENT_ASK%"
choice /c YN /m "%ASK% %~2"
if errorlevel 2 exit /b 0
:offer_register
"%DOTNET%" "%SERVER_DLL%" --register %~1 --config "%~3" >nul 2>&1
if errorlevel 1 (echo   %~2: %M_CLIENT_FAILED%) else echo   %~2: %M_CLIENT_DONE%
exit /b 0

:find_dotnet
set "DOTNET="
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if defined DOTNET exit /b 0
for /f "delims=" %%D in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%D"
exit /b 0

rem ---------------------------------------------------------------- uninstall
:uninstall
call :check_navis_closed || goto :fail
if not exist "%TARGET%" (
    echo.
    echo %M_NOT_INSTALLED%
    call :remove_old_installs
    goto :end
)
if "%NAVMCP_NO_CLIENTS%"=="1" goto :remove_files
rem 2.2.x has no server DLL; then unregister with the one of this package.
set "UNREG_DLL=%SERVER_DLL%"
if not exist "%UNREG_DLL%" set "UNREG_DLL=%SOURCE%\Contents\Server\navis_mcp_server.dll"
if not exist "%UNREG_DLL%" goto :remove_files
call :find_dotnet
if not defined DOTNET goto :remove_files
echo.
"%DOTNET%" "%UNREG_DLL%" --unregister claude-desktop --config "%CLAUDE_CONFIG%" >nul 2>&1
if not errorlevel 1 echo   Claude Desktop: %M_CLIENT_REMOVED%
"%DOTNET%" "%UNREG_DLL%" --unregister codex --config "%CODEX_CONFIG%" >nul 2>&1
if not errorlevel 1 echo   Codex: %M_CLIENT_REMOVED%
if "%NAVMCP_NO_CLAUDE_CODE%"=="1" goto :remove_files
where claude >nul 2>&1 || goto :remove_files
"%DOTNET%" "%UNREG_DLL%" --unregister claude-code >nul 2>&1
if not errorlevel 1 echo   Claude Code: %M_CLIENT_REMOVED%
:remove_files
call :remove_old_installs
rem A server still running in an AI client keeps its DLL loaded: move it out so the folder can go.
if exist "%SERVER_DLL%" move /y "%SERVER_DLL%" "%TEMP%\navis_mcp_server_%RANDOM%%RANDOM%.dll.old" >nul 2>&1
rmdir /s /q "%TARGET%" >nul 2>&1
if exist "%TARGET%" (
    echo %M_REMOVE_FAILED%
    echo   %TARGET%
    goto :fail
)
echo.
echo %M_REMOVED%
goto :end

rem ---------------------------------------------------------------- errors and end
:no_package
echo.
echo %M_NO_PACKAGE_1%
echo   %SOURCE%
echo %M_NO_PACKAGE_2%
goto :fail

:fail
set "RC=1"
goto :end

:end
echo.
if "%INTERACTIVE%"=="0" exit /b %RC%
echo %M_PRESS_KEY%
pause >nul
exit /b %RC%

rem ---------------------------------------------------------------- subroutines
:detect
if not exist "%PF%\Autodesk\Navisworks %1 %2\Roamer.exe" exit /b 0
set "FOUND=1"
echo   Navisworks %1 %2
exit /b 0

:show_installed
set "INSTALLED_VER="
if exist "%TARGET%\PackageContents.xml" for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "[xml]$m = Get-Content -LiteralPath '%TARGET%\PackageContents.xml'; $m.ApplicationPackage.AppVersion" 2^>nul`) do set "INSTALLED_VER=%%V"
echo.
if defined INSTALLED_VER (echo %M_INSTALLED% %INSTALLED_VER%) else echo %M_NOT_YET%
set "PACKAGE_VER="
if exist "%SOURCE%\PackageContents.xml" for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "[xml]$m = Get-Content -LiteralPath '%SOURCE%\PackageContents.xml'; $m.ApplicationPackage.AppVersion" 2^>nul`) do set "PACKAGE_VER=%%V"
if defined PACKAGE_VER echo %M_PACKAGE% %PACKAGE_VER%
exit /b 0

:check_navis_closed
if "%NAVMCP_NO_RUNNING_CHECK%"=="1" exit /b 0
tasklist /fi "imagename eq Roamer.exe" 2>nul | find /i "Roamer.exe" >nul || exit /b 0
echo.
echo %M_CLOSE_NAVIS%
exit /b 1

rem ---------------------------------------------------------------- messages
rem %%CHOICE%% is stored as %CHOICE% and expanded later by "call echo".
:messages_en
set "M_TITLE=Navisworks MCP Bridge - installer"
set "M_FOUND_HEADER=Navisworks found on this computer:"
set "M_NONE_FOUND=none - Navisworks Manage or Simulate 2026 / 2027 is needed"
set "M_INSTALLED=Installed version:"
set "M_NOT_YET=Not installed yet."
set "M_PACKAGE=Version in this package:"
set "M_PROMPT_INSTALL=install or update"
set "M_PROMPT_UNINSTALL=uninstall"
set "M_PROMPT_QUIT=quit"
set "M_PROMPT_CHOICE=Your choice [I/U/Q]:"
set "M_BAD_INPUT=Unknown choice: %%CHOICE%%. Use I, U or Q."
set "M_CLOSE_NAVIS=Navisworks is running. Close it and run INSTALL.BAT again."
set "M_NO_PACKAGE_1=The plugin files were not found next to INSTALL.BAT:"
set "M_NO_PACKAGE_2=Unpack the whole package and run INSTALL.BAT from its folder."
set "M_COPYING=Installing the plugin to:"
set "M_COPY_FAILED=Copying failed. Run INSTALL.BAT as administrator."
set "M_SERVER_IN_USE=The MCP server is running in an AI client: restart the client to use the new version."
set "M_DONE=Plugin installed."
set "M_OLD_PYTHON_REMOVED=The old Python server server_http.py was removed."
set "M_OLD_COPY_REMOVED=Removed an old copy of the plugin:"
set "M_OLD_COPY_FAILED=Could not remove an old copy of the plugin:"
set "M_RUN_AS_ADMIN=Run INSTALL.BAT as administrator, or delete that folder by hand."
set "M_CLIENTS_STEP=Connecting the MCP server to AI clients:"
set "M_NO_DOTNET_1=.NET was not found, so the MCP server cannot run."
set "M_NO_DOTNET_2=Navisworks 2026 / 2027 normally installs it; otherwise install the .NET 8 Runtime from dotnet.microsoft.com."
set "M_SERVER_FAILED=The MCP server does not start: .NET 8 or newer is needed. AI clients were not changed."
set "M_CLIENT_ASK=Connect Navisworks to"
set "M_CLIENT_DONE=connected"
set "M_CLIENT_FAILED=could not be connected - see the commands below"
set "M_CLIENT_REMOVED=disconnected"
set "M_NO_CLIENTS=No AI client found: Claude Desktop, Claude Code or Codex."
set "M_CLIENT_MANUAL=To connect a client later, run:"
set "M_RUN_HINT_1=Restart your AI client so it starts the new MCP server."
set "M_RUN_HINT_2=Start Navisworks and click MCP Bridge on the AI companion ribbon tab:"
set "M_RUN_HINT_3=the bridge starts only by this button, after every Navisworks start."
set "M_NOT_INSTALLED=The plugin is not installed."
set "M_REMOVE_FAILED=Could not remove:"
set "M_REMOVED=The plugin was removed."
set "M_PRESS_KEY=Press any key to close this window."
exit /b 0

:messages_ru
set "M_TITLE=Navisworks MCP Bridge - установка"
set "M_FOUND_HEADER=Navisworks на этом компьютере:"
set "M_NONE_FOUND=не найден - нужен Navisworks Manage или Simulate 2026 / 2027"
set "M_INSTALLED=Установлена версия:"
set "M_NOT_YET=Плагин ещё не установлен."
set "M_PACKAGE=Версия в этом пакете:"
set "M_PROMPT_INSTALL=установить или обновить"
set "M_PROMPT_UNINSTALL=удалить"
set "M_PROMPT_QUIT=выйти"
set "M_PROMPT_CHOICE=Ваш выбор [I/U/Q]:"
set "M_BAD_INPUT=Неизвестный выбор: %%CHOICE%%. Введите I, U или Q."
set "M_CLOSE_NAVIS=Navisworks запущен. Закройте его и запустите INSTALL.BAT снова."
set "M_NO_PACKAGE_1=Рядом с INSTALL.BAT не найдены файлы плагина:"
set "M_NO_PACKAGE_2=Распакуйте пакет целиком и запустите INSTALL.BAT из его папки."
set "M_COPYING=Устанавливаю плагин в папку:"
set "M_COPY_FAILED=Не удалось скопировать файлы. Запустите INSTALL.BAT от имени администратора."
set "M_SERVER_IN_USE=MCP-сервер сейчас запущен ИИ-клиентом: перезапустите клиент, чтобы работала новая версия."
set "M_DONE=Плагин установлен."
set "M_OLD_PYTHON_REMOVED=Старый сервер на Python, server_http.py, удалён."
set "M_OLD_COPY_REMOVED=Удалена старая копия плагина:"
set "M_OLD_COPY_FAILED=Не удалось удалить старую копию плагина:"
set "M_RUN_AS_ADMIN=Запустите INSTALL.BAT от имени администратора или удалите эту папку вручную."
set "M_CLIENTS_STEP=Подключение MCP-сервера к ИИ-клиентам:"
set "M_NO_DOTNET_1=.NET не найден, поэтому MCP-сервер не сможет работать."
set "M_NO_DOTNET_2=Обычно его ставит Navisworks 2026 / 2027; иначе установите .NET 8 Runtime с dotnet.microsoft.com."
set "M_SERVER_FAILED=MCP-сервер не запускается: нужен .NET 8 или новее. Настройки ИИ-клиентов не изменены."
set "M_CLIENT_ASK=Подключить Navisworks к"
set "M_CLIENT_DONE=подключён"
set "M_CLIENT_FAILED=не удалось подключить - см. команды ниже"
set "M_CLIENT_REMOVED=отключён"
set "M_NO_CLIENTS=ИИ-клиенты не найдены: Claude Desktop, Claude Code или Codex."
set "M_CLIENT_MANUAL=Чтобы подключить клиент позже, выполните:"
set "M_RUN_HINT_1=Перезапустите ИИ-клиент, чтобы он запустил новый MCP-сервер."
set "M_RUN_HINT_2=Запустите Navisworks и нажмите MCP Bridge на вкладке ленты AI companion:"
set "M_RUN_HINT_3=мост запускается только этой кнопкой, после каждого запуска Navisworks."
set "M_NOT_INSTALLED=Плагин не установлен."
set "M_REMOVE_FAILED=Не удалось удалить:"
set "M_REMOVED=Плагин удалён."
set "M_PRESS_KEY=Нажмите любую клавишу, чтобы закрыть окно."
exit /b 0

@echo off
setlocal EnableExtensions EnableDelayedExpansion

if not defined MCSTUDIO_EXE set "MCSTUDIO_EXE=D:\MCStudio\MCStudio.exe"
set "BRIDGE_ROOT=%~dp0mcstudio_bridge"
set "INJECTOR=%BRIDGE_ROOT%\bin\Allurix.MCStudio.Injector.exe"
set "BOOTSTRAP=%BRIDGE_ROOT%\bin\BRegister.dll"
set "BRIDGE=%BRIDGE_ROOT%\bin\allurix-mcs-bridge.dll"
set "BOOTSTRAP_LOG=%BRIDGE_ROOT%\bin\allurix_bootstrap.log"
set "NEW_LOG=%TEMP%\allurix_bridge_inject_%RANDOM%_%RANDOM%.log"

if not exist "%INJECTOR%" goto :missing_injector
if not exist "%BOOTSTRAP%" goto :missing_bootstrap
if not exist "%BRIDGE%" goto :missing_bridge

tasklist /FI "IMAGENAME eq MCStudio.exe" 2>nul | find /I "MCStudio.exe" >nul
if errorlevel 1 (
    if not exist "%MCSTUDIO_EXE%" goto :missing_mcstudio
    echo Starting MCStudio...
    start "" "%MCSTUDIO_EXE%"
) else (
    echo MCStudio is already running.
)

set /a WAIT_COUNT=0
:wait_for_mcstudio
tasklist /FI "IMAGENAME eq MCStudio.exe" 2>nul | find /I "MCStudio.exe" >nul
if not errorlevel 1 goto :mcstudio_found
set /a WAIT_COUNT+=1
if !WAIT_COUNT! geq 60 goto :mcstudio_timeout
>nul ping 127.0.0.1 -n 2
goto :wait_for_mcstudio

:mcstudio_found
echo Waiting for the native MCP server on port 19131...
set /a MCP_WAIT_COUNT=0
:wait_for_mcp
netstat -ano -p tcp 2>nul | findstr /R /C:":19131 .*LISTENING" >nul
if not errorlevel 1 goto :mcp_ready
set /a MCP_WAIT_COUNT+=1
if !MCP_WAIT_COUNT! geq 60 goto :mcp_timeout
>nul ping 127.0.0.1 -n 2
goto :wait_for_mcp

:mcp_ready
set "MCP_PID="
for /f "tokens=5" %%P in ('netstat -ano -p tcp 2^>nul ^| findstr /R /C:"127.0.0.1:19131 .*LISTENING"') do if not defined MCP_PID set "MCP_PID=%%P"
if not defined MCP_PID goto :mcp_timeout

set "OLD_LINES=0"
if exist "%BOOTSTRAP_LOG%" (
    for /f %%N in ('find /v /c "" ^< "%BOOTSTRAP_LOG%"') do set "OLD_LINES=%%N"
)

echo Injecting Allurix bridge...
"%INJECTOR%" "%BOOTSTRAP%" --pid %MCP_PID%
if errorlevel 1 goto :injector_failed

set /a VERIFY_COUNT=0
:verify_injection
if exist "%BOOTSTRAP_LOG%" (
    more +!OLD_LINES! "%BOOTSTRAP_LOG%" > "%NEW_LOG%"
    findstr /C:"ERR:" /C:"EX:" "%NEW_LOG%" >nul
    if not errorlevel 1 goto :bootstrap_failed
    findstr /C:"=== DONE! Status:" "%NEW_LOG%" >nul
    if not errorlevel 1 goto :success
)
set /a VERIFY_COUNT+=1
if !VERIFY_COUNT! geq 20 goto :verify_timeout
>nul ping 127.0.0.1 -n 2
goto :verify_injection

:success
for /f "usebackq tokens=*" %%L in (`findstr /C:"=== DONE! Status:" "%NEW_LOG%"`) do echo %%L
del /q "%NEW_LOG%" 2>nul
echo Allurix MCP bridge is ready.
exit /b 0

:missing_injector
echo ERROR: Injector not found: "%INJECTOR%"
exit /b 1

:missing_bootstrap
echo ERROR: Bootstrap DLL not found: "%BOOTSTRAP%"
exit /b 1

:missing_bridge
echo ERROR: Bridge DLL not found: "%BRIDGE%"
exit /b 1

:missing_mcstudio
echo ERROR: MCStudio not found: "%MCSTUDIO_EXE%"
exit /b 1

:mcstudio_timeout
echo ERROR: MCStudio did not start within 60 seconds.
exit /b 1

:mcp_timeout
echo ERROR: MCStudio MCP did not listen on port 19131 within 60 seconds.
echo Enable or start MCP in MCStudio, then run this script again.
exit /b 1

:injector_failed
echo ERROR: Injector failed.
exit /b 1

:bootstrap_failed
echo ERROR: Bridge bootstrap reported an error:
type "%NEW_LOG%"
del /q "%NEW_LOG%" 2>nul
exit /b 1

:verify_timeout
del /q "%NEW_LOG%" 2>nul
echo ERROR: Injection returned, but BRegister did not report completion within 20 seconds.
echo Check "%BOOTSTRAP_LOG%". If the DLL was already loaded, restart MCStudio first.
exit /b 1

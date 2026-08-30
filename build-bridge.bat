@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not defined MCSTUDIO_DIR set "MCSTUDIO_DIR=D:\MCStudio"
set "MCP_ASSEMBLY=%MCSTUDIO_DIR%\mcp_csharp_bridge.dll"
set "OUT=mcstudio_bridge\bin"
set "TOOLS=%OUT%\tools"

if not exist "%MCP_ASSEMBLY%" (
    echo ERROR: mcp_csharp_bridge.dll not found. Set MCSTUDIO_DIR first.
    exit /b 1
)
where csc >nul 2>nul || (
    echo ERROR: csc.exe is required. Use a .NET Framework Developer Command Prompt.
    exit /b 1
)
where cl >nul 2>nul || (
    echo ERROR: cl.exe is required to build BRegister.dll. Use a Visual Studio Developer Command Prompt.
    exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"
if not exist "%TOOLS%" mkdir "%TOOLS%"

csc /nologo /target:library /platform:x86 /reference:"%MCP_ASSEMBLY%" /out:"%OUT%\allurix-mcs-bridge.dll" mcstudio_bridge\AllurixBridge.cs || exit /b 1
csc /nologo /platform:x86 /out:"%OUT%\Allurix.MCStudio.Injector.exe" mcstudio_bridge\Injector.cs || exit /b 1
cl /nologo /utf-8 /LD /clr /Fe:"%OUT%\BRegister.dll" mcstudio_bridge\BRegister.cpp || exit /b 1

call :tool ClearTool clear
call :tool ClientLogsTool client_logs
call :uia_tool ConfirmRedeployTool confirm_redeploy
call :tool DeployLogsTool deploy_logs
call :tool DevelopmentTestTool development_test
call :tool HotfixTool hotfix
call :tool LiveLogsTool live_logs
call :tool LogsTool logs
call :tool ProjectsTool projects
call :tool RedeployTool redeploy

echo Build complete: %OUT%
exit /b 0

:tool
csc /nologo /target:library /platform:x86 /reference:"%MCP_ASSEMBLY%" /out:"%TOOLS%\%~2.dll" "mcstudio_bridge\tools\%~1.cs" "mcstudio_bridge\tools\ApolloToolHelpers.cs" || exit /b 1
exit /b 0

:uia_tool
if not defined UIA_REF_DIR set "UIA_REF_DIR=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"
if not exist "%UIA_REF_DIR%\UIAutomationClient.dll" (
    echo ERROR: UIAutomationClient.dll not found. Set UIA_REF_DIR first.
    exit /b 1
)
csc /nologo /target:library /platform:x86 /reference:"%MCP_ASSEMBLY%" /reference:"%UIA_REF_DIR%\UIAutomationClient.dll" /reference:"%UIA_REF_DIR%\UIAutomationTypes.dll" /out:"%TOOLS%\%~2.dll" "mcstudio_bridge\tools\%~1.cs" "mcstudio_bridge\tools\ApolloToolHelpers.cs" || exit /b 1
exit /b 0

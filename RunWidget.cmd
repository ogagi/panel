@echo off
set "APP=%~dp0artifacts\publish\winui-fx-menu\AiCoreMonitor.exe"
if exist "%APP%" (
    start "" "%APP%"
    exit /b 0
)

set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
set "PROJECT=%~dp0src\AiCoreMonitor.WinUI\AiCoreMonitor.WinUI.csproj"
if exist "%DOTNET%" goto run_from_source

set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if exist "%DOTNET%" goto run_from_source

set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if exist "%DOTNET%" goto run_from_source

where dotnet.exe >nul 2>&1
if not errorlevel 1 (
    set "DOTNET=dotnet.exe"
    goto run_from_source
)

echo .NET 10 was not found.
echo Install the .NET 10 SDK and run this launcher again.
exit /b 1

:run_from_source
start "" "%DOTNET%" run --project "%PROJECT%" -c Release -p:Platform=x64
exit /b 0

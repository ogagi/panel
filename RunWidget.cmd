@echo off
set "APP=%~dp0artifacts\publish\winui-fx-menu\AiCoreMonitor.exe"
if exist "%APP%" (
    start "" "%APP%"
    exit /b 0
)

set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
set "PROJECT=%~dp0src\AiCoreMonitor.WinUI\AiCoreMonitor.WinUI.csproj"
if exist "%DOTNET%" (
    start "" "%DOTNET%" run --project "%PROJECT%" -c Release -p:Platform=x64
    exit /b 0
)

echo .NET 10 was not found at "%DOTNET%".
echo Install the .NET 10 SDK and run this launcher again.
exit /b 1

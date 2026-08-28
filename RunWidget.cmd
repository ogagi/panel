@echo off
set "BUILD=%~dp0artifacts\publish\winui-current"
set "APP=%BUILD%\AiCoreMonitor.exe"
set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
set "PROJECT=%~dp0src\AiCoreMonitor.WinUI\AiCoreMonitor.WinUI.csproj"

if exist "%DOTNET%" (
    "%DOTNET%" publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false -p:EnableMsixTooling=true -o "%BUILD%" --nologo
    if errorlevel 1 exit /b 1

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0SignBuild.ps1" -BuildDirectory "%BUILD%"
    if errorlevel 1 exit /b 1

    start "" "%APP%"
    exit /b 0
)

echo .NET 10 was not found at "%DOTNET%".
echo Install the .NET 10 SDK and run this launcher again.
exit /b 1

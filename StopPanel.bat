@echo off
setlocal

tasklist /FI "IMAGENAME eq AiCoreMonitor.exe" 2>nul | find /I "AiCoreMonitor.exe" >nul
if errorlevel 1 (
    echo AI Core Monitor is already stopped.
    exit /b 0
)

taskkill /F /T /IM AiCoreMonitor.exe >nul 2>&1
if errorlevel 1 (
    echo Failed to stop AI Core Monitor.
    exit /b 1
)

echo AI Core Monitor stopped.
exit /b 0

@echo off
setlocal

tasklist /FI "IMAGENAME eq AiCoreMonitor.exe" 2>nul | find /I "AiCoreMonitor.exe" >nul
if not errorlevel 1 (
    echo AI Core Monitor is already running.
    exit /b 0
)

call "%~dp0RunWidget.cmd"
exit /b %errorlevel%

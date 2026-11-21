@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\Copy-CodingAssistant-Token.ps1"
if errorlevel 1 (
    echo.
    echo The token-copy helper failed. Review the message above.
    pause
)
endlocal

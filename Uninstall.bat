@echo off
rem MSC Multiplayer - double-click to remove the mod (your own save is put back if needed).
setlocal
set "S=%~dp0uninstall.ps1"
if not exist "%S%" set "S=%~dp0scripts\uninstall.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%S%" %*
endlocal

@echo off
rem MSC Multiplayer - double-click to install (or update) the mod.
setlocal
set "S=%~dp0install.ps1"
if not exist "%S%" set "S=%~dp0scripts\install.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%S%" %*
endlocal

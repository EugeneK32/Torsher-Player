@echo off
setlocal
if "%~1"=="" (
  echo Usage: run-test.cmd "http://host/stream/file.mkv" [ui-scale]
  echo Example: run-test.cmd "http://127.0.0.1:8090/stream/test.mkv" 1.9
  exit /b 2
)
set "SCALE=%~2"
if "%SCALE%"=="" set "SCALE=1.8"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-test.ps1" -Url "%~1" -UiScale %SCALE%
exit /b %ERRORLEVEL%

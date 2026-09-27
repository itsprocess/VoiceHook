@echo off
setlocal
cd /d "%~dp0"
if not exist "artifacts\com.voicehook.ptt.streamDeckPlugin" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts\Build.ps1"
  if errorlevel 1 (pause & exit /b 1)
)
start "" "artifacts\com.voicehook.ptt.streamDeckPlugin"

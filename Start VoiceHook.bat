@echo off
setlocal
cd /d "%~dp0"
if not exist "artifacts\VoiceHook-win-x64\VoiceHook.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts\Build.ps1" -SkipStreamDeck
  if errorlevel 1 (pause & exit /b 1)
)
start "" "artifacts\VoiceHook-win-x64\VoiceHook.exe"

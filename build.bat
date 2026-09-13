@echo off
rem Build QianwenSwitcher.exe with the in-box .NET Framework csc.exe
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 pause

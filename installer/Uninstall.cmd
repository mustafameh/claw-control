@echo off
rem Removes Claw Control (installed from the zip). Windows asks for admin rights once.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1"

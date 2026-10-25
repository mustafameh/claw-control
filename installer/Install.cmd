@echo off
rem Installs Claw Control from this folder. Windows asks for admin rights once.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"

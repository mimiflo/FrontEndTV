@echo off
rem FrontEndTV : lance l'installateur (installer.ps1, dans ce dossier).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1" %*

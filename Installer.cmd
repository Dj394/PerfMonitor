@echo off
:: PerfMonitor Live - installation en un double-clic (lance install-live.ps1 : tache planifiee, raccourcis, PawnIO si besoin)
title Installation de PerfMonitor Live
cd /d "%~dp0"
if not exist "PerfMonitorLive.exe" (
  echo PerfMonitorLive.exe est introuvable a cote de ce fichier.
  echo Decompressez d'abord tout le contenu du zip dans un dossier, puis relancez Installer.cmd.
  pause & exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-live.ps1"
echo.
echo Termine.
pause

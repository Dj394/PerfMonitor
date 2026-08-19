@echo off
:: PerfMonitor Live - desinstallation complete : tache planifiee, raccourcis, exclusion Defender, puis (au choix) le dossier et PawnIO
title Desinstallation de PerfMonitor Live
set "DIR=%~dp0"
if "%DIR:~-1%"=="\" set "DIR=%DIR:~0,-1%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%DIR%\desinstaller.ps1" -Dir "%DIR%"
if exist "%DIR%\.supprimer-dossier" (
    del /q "%DIR%\.supprimer-dossier" >nul 2>&1
    echo Suppression du dossier %DIR% ...
    start "" /min cmd /c "timeout /t 2 /nobreak >nul & rd /s /q "%DIR%""
    exit
)
pause

# Desinstallation de PerfMonitor Live (appele par Desinstaller.cmd, qui supprime ensuite le dossier si demande)
param([string]$Dir = $PSScriptRoot)
$ErrorActionPreference = 'SilentlyContinue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "Desinstallation de PerfMonitor Live ($Dir)" -ForegroundColor Cyan
$delDir  = (Read-Host "Supprimer aussi le dossier complet (exe, donnees data\, settings.json) ? [o/N]") -match '^[oOyY]'
$pawnPresent = (Test-Path (Join-Path $env:ProgramFiles 'PawnIO')) -or [bool](Get-Service PawnIO)
$delPawn = $false
if ($pawnPresent) { $delPawn = (Read-Host "Desinstaller aussi le pilote PawnIO (capteurs ; inutile si un autre logiciel l'utilise) ? [o/N]") -match '^[oOyY]' }

# 1) Partie elevee : arret + tache planifiee + exclusion Defender + PawnIO
$elev = @"
Stop-ScheduledTask PerfMonitorLive -EA SilentlyContinue
Get-Process PerfMonitorLive -EA SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask PerfMonitorLive -Confirm:`$false -EA SilentlyContinue
foreach(`$e in (Get-MpPreference -EA SilentlyContinue).ExclusionPath){ if(`$e -and `$e.TrimEnd('\') -ieq '$($Dir.TrimEnd('\'))'){ Remove-MpPreference -ExclusionPath `$e -EA SilentlyContinue } }
"@
if ($delPawn) {
    $elev += @"
`$k = Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*,HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -EA SilentlyContinue | Where-Object DisplayName -match '^PawnIO' | Select -First 1
`$u = Join-Path `$env:ProgramFiles 'PawnIO\uninstall.exe'
if (`$k.QuietUninstallString) { Start-Process cmd.exe "/c `$(`$k.QuietUninstallString)" -Wait } elseif (Test-Path `$u) { Start-Process `$u '-uninstall -silent' -Wait }
if (`$k) { Remove-Item `$k.PSPath -Force -EA SilentlyContinue }
`$inf = (pnputil /enum-drivers | Out-String) -split "`r?`n"; `$i = (`$inf | Select-String 'pawnio.inf').LineNumber
if (`$i) { `$oem = (`$inf[`$i-2] -replace '.*:\s*','').Trim(); pnputil /delete-driver `$oem /uninstall /force | Out-Null }
sc.exe stop PawnIO | Out-Null; sc.exe delete PawnIO | Out-Null
Remove-Item (Join-Path `$env:ProgramFiles 'PawnIO') -Recurse -Force -EA SilentlyContinue
"@
}
if ($isAdmin) { Invoke-Expression $elev }
else {
    $tmp = Join-Path $env:TEMP 'perfmonitor-desinstall.ps1'; Set-Content $tmp $elev -Encoding UTF8
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$tmp`""
    Remove-Item $tmp -Force
}
Start-Sleep -Milliseconds 500

# 2) Raccourcis
foreach ($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { Remove-Item (Join-Path $d 'PerfMonitor Live.lnk') -Force }

# 3) Bilan
$ok = -not (Get-ScheduledTask PerfMonitorLive) -and -not (Get-Process PerfMonitorLive)
if ($ok) { Write-Host "Tache planifiee, processus et raccourcis supprimes." -ForegroundColor Green } else { Write-Warning "La tache ou le processus PerfMonitorLive existe encore (droits admin refuses ?)." }
if ($delPawn) { if (Get-Service PawnIO) { Write-Warning "PawnIO n'a pas pu etre supprime completement." } else { Write-Host "PawnIO desinstalle." -ForegroundColor Green } }
if ($delDir) { New-Item -ItemType File (Join-Path $Dir '.supprimer-dossier') -Force | Out-Null; Write-Host "Le dossier $Dir va etre supprime a la fermeture de cette fenetre." -ForegroundColor Green }
else { Write-Host "Le dossier $Dir est conserve (donnees et reglages). Vous pouvez le supprimer a la main." }

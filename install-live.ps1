# Installe PerfMonitorLive : tache planifiee elevee a l'ouverture de session (necessaire pour les temperatures),
# raccourci sur le Bureau et dans le menu Demarrer. Se relance en administrateur si besoin (une seule invite UAC).
$exe = Join-Path $PSScriptRoot 'PerfMonitorLive.exe'
if (-not (Test-Path $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevation requise (tache planifiee en mode administrateur) : validez l'invite UAC..."
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}
# 1) nettoyage : ancien collecteur, cle Run, instances en cours
if (Get-ScheduledTask PerfMonitor -ErrorAction SilentlyContinue) { Unregister-ScheduledTask PerfMonitor -Confirm:$false; Write-Host "Ancienne tache 'PerfMonitor' retiree." }
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*collect.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PerfMonitorLive' -ErrorAction SilentlyContinue
Get-Process PerfMonitorLive -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
# 2) tache planifiee elevee, a l'ouverture de session de l'utilisateur
$action   = New-ScheduledTaskAction -Execute $exe -Argument '--tray'
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName 'PerfMonitorLive' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Write-Host "Tache planifiee 'PerfMonitorLive' (admin, a l'ouverture de session) installee."
# 3) raccourcis (Bureau + menu Demarrer) -> l'exe (qui reveille/lance l'instance elevee)
$ws = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('Programs')) ''))) {
    $lnk = $ws.CreateShortcut((Join-Path $dir 'PerfMonitor Live.lnk'))
    $lnk.TargetPath = $exe; $lnk.WorkingDirectory = $PSScriptRoot; $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Supervision temps reel du PC (CPU, RAM, disques, temperatures) avec alertes'; $lnk.Save()
}
Write-Host "Raccourci 'PerfMonitor Live' cree sur le Bureau et dans le menu Demarrer."
# 4) lancer maintenant (instance elevee, fenetre affichee)
Start-ScheduledTask -TaskName 'PerfMonitorLive'
Start-Sleep 3
Start-Process $exe   # reveille l'instance pour afficher la fenetre
Write-Host "PerfMonitor Live lance."

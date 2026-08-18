# Installe le collecteur en tache planifiee (demarre a l'ouverture de session, tourne en arriere-plan)
$script = Join-Path $PSScriptRoot 'collect.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'PerfMonitor' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'PerfMonitor'
Write-Host "Tache 'PerfMonitor' installee et demarree. Pour la retirer : Unregister-ScheduledTask PerfMonitor -Confirm:`$false"

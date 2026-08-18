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
# 0a) Runtime .NET 10 Desktop (x64) : requis par l'exe standard (l'exe "portable" l'embarque). Installe en silencieux s'il manque.
$isPortable = (Get-Item $exe).Length -gt 50MB
$net10 = Test-Path (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App.*')
if (-not $isPortable -and -not $net10) {
    Write-Host ""
    Write-Host "Le runtime .NET 10 Desktop n'est pas installe : installation automatique (Microsoft, silencieuse, ~60 Mo)..."
    $done = $false
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        try { & winget install --id Microsoft.DotNet.DesktopRuntime.10 --exact --silent --accept-source-agreements --accept-package-agreements | Out-Null; $done = Test-Path (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App.*') } catch { }
    }
    if (-not $done) {
        $tmp = Join-Path $env:TEMP 'windowsdesktop-runtime-10-win-x64.exe'
        try {
            Invoke-WebRequest -Uri 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe' -OutFile $tmp -UseBasicParsing
            $sig = Get-AuthenticodeSignature $tmp
            if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft') { Write-Warning "Signature $($sig.Status) : installation annulee. Fichier conserve : $tmp" }
            else {
                $p = Start-Process $tmp -ArgumentList '/install /quiet /norestart' -Wait -PassThru
                $done = Test-Path (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App.*')
                if (-not $done) { Write-Warning "L'installeur .NET a renvoye le code $($p.ExitCode)." }
            }
        } catch { Write-Warning "Telechargement impossible ($($_.Exception.Message))." }
    }
    if ($done) { Write-Host ".NET 10 Desktop Runtime installe." }
    else { Write-Warning "Runtime .NET 10 absent : PerfMonitor ne pourra pas demarrer. Installez-le depuis https://dotnet.microsoft.com/download/dotnet/10.0 (Run desktop apps, x64) ou utilisez le zip 'portable'."; Read-Host "Entree pour continuer quand meme"; }
}
# 0b) PawnIO : depuis la version 0.9.5, LibreHardwareMonitor n'embarque plus de pilote noyau et passe par lui.
# Sans PawnIO : ni temperature, ni frequence, ni consommation CPU, ni ventilateurs (le GPU et le SMART restent lisibles).
$pawn = (Test-Path (Join-Path $env:ProgramFiles 'PawnIO\PawnIOLib.dll')) -or [bool](Get-Service PawnIO -ErrorAction SilentlyContinue)
if (-not $pawn) {
    Write-Host ""
    Write-Warning "PawnIO n'est pas installe : les temperatures, frequences et consommations CPU ainsi que les ventilateurs resteront indisponibles."
    $rep = Read-Host "Telecharger et lancer l'installeur officiel (signe) maintenant ? [O/n]"
    if ($rep -eq '' -or $rep -match '^[oy]') {
        $tmp = Join-Path $env:TEMP 'PawnIO_setup.exe'
        try {
            Invoke-WebRequest -Uri 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe' -OutFile $tmp -UseBasicParsing
            $sig = Get-AuthenticodeSignature $tmp
            if ($sig.Status -ne 'Valid') { Write-Warning "Signature $($sig.Status) : installation annulee. Fichier conserve : $tmp" }
            else {
                Write-Host "Signature verifiee : $($sig.SignerCertificate.Subject)"
                Start-Process $tmp -Wait
                Write-Host "PawnIO installe (choisir l'edition officielle dans l'assistant)."
            }
        } catch { Write-Warning "Telechargement impossible ($($_.Exception.Message)). A installer manuellement depuis https://pawnio.eu" }
    } else { Write-Host "Ignore : installable plus tard depuis https://pawnio.eu" }
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

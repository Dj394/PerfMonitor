# Installe PerfMonitor Live en une ligne (sans navigateur, donc sans avertissement SmartScreen) :
#   powershell -NoProfile -ExecutionPolicy Bypass -c "irm https://github.com/Dj394/PerfMonitor/releases/latest/download/Get-PerfMonitor.ps1 | iex"
# Telecharge le zip de la derniere version dans %LOCALAPPDATA%\PerfMonitor (ou -Dir), le decompresse et lance install-live.ps1.
param([string]$Dir = (Join-Path $env:LOCALAPPDATA 'PerfMonitor'), [switch]$Portable)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$name = if ($Portable) { 'PerfMonitor-portable.zip' } else { 'PerfMonitor.zip' }
$url = "https://github.com/Dj394/PerfMonitor/releases/latest/download/$name"
New-Item -ItemType Directory -Force $Dir | Out-Null
$zip = Join-Path $env:TEMP $name
Write-Host "Telechargement de $name..."
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
Get-Process PerfMonitorLive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Expand-Archive -Path $zip -DestinationPath $Dir -Force
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Get-ChildItem $Dir -File | Unblock-File -ErrorAction SilentlyContinue
Write-Host "Fichiers dans $Dir. Installation..."
& (Join-Path $Dir 'install-live.ps1')

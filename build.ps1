# Compile PerfMonitorLive et copie l'exe dans ce dossier
# -Portable : exe autonome (runtime .NET inclus, ~150 Mo) pour une machine sans .NET 10 Desktop Runtime
param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'app'
Stop-ScheduledTask PerfMonitorLive -ErrorAction SilentlyContinue
Get-Process PerfMonitorLive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$sc = if ($Portable) { 'true' } else { 'false' }
dotnet publish "$app\PerfMonitorLive.csproj" -c Release -r win-x64 "-p:SelfContained=$sc" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$app\publish" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Echec de la publication" }
Copy-Item "$app\publish\PerfMonitorLive.exe" $PSScriptRoot -Force
Write-Host "OK -> $PSScriptRoot\PerfMonitorLive.exe"



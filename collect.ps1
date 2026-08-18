# PerfMonitor - collecteur (toutes les 5 s, sortie JSONL par jour dans .\data)
param([int]$IntervalSec = 5)
$ErrorActionPreference = 'SilentlyContinue'
$dataDir = Join-Path $PSScriptRoot 'data'
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory $dataDir | Out-Null }
$totalMB = [math]::Round((Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize / 1024)
$cores = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
Write-Host "PerfMonitor demarre ($totalMB MB RAM, $cores threads). Ctrl+C pour arreter."

while ($true) {
    $t0 = Get-Date
    $cpu  = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
    $mem  = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
    $memUsed = $totalMB - $mem.AvailableMBytes
    $disks = Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk | Where-Object { $_.Name -ne '_Total' } | ForEach-Object {
        @{ n = $_.Name; pct = [int]$_.PercentDiskTime; r = [math]::Round($_.DiskReadBytesPersec/1MB,2); w = [math]::Round($_.DiskWriteBytesPersec/1MB,2); q = [math]::Round($_.AvgDiskQueueLength,2); lat = [math]::Round($_.AvgDisksecPerTransfer*1000,1) }
    }
    $net = Get-CimInstance Win32_PerfFormattedData_Tcpip_NetworkInterface | Where-Object { $_.Name -notmatch 'isatap|Loopback|Teredo' }
    $netRx = [math]::Round(($net | Measure-Object BytesReceivedPersec -Sum).Sum/1KB,1)
    $netTx = [math]::Round(($net | Measure-Object BytesSentPersec -Sum).Sum/1KB,1)
    $procs = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Where-Object { $_.Name -notin '_Total','Idle' } |
        Sort-Object PercentProcessorTime -Descending | Select-Object -First 6 | ForEach-Object {
        @{ n = ($_.Name -replace '#\d+$',''); cpu = [math]::Round($_.PercentProcessorTime / $cores, 1); mem = [math]::Round($_.WorkingSetPrivate/1MB) }
    }
    $temp = $null
    $tz = Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature | Select-Object -First 1
    if ($tz) { $temp = [math]::Round($tz.CurrentTemperature/10 - 273.15, 1) }
    $sample = [ordered]@{
        ts = $t0.ToString('yyyy-MM-ddTHH:mm:ss'); cpu = $cpu; memMB = $memUsed; memPct = [math]::Round($memUsed*100/$totalMB,1)
        pageIn = $mem.PagesInputPersec; disks = @($disks); rx = $netRx; tx = $netTx; procs = @($procs); temp = $temp
    }
    $file = Join-Path $dataDir ("perf-" + $t0.ToString('yyyy-MM-dd') + ".jsonl")
    Add-Content -Path $file -Value ($sample | ConvertTo-Json -Compress -Depth 4) -Encoding UTF8
    $elapsed = ((Get-Date) - $t0).TotalSeconds
    if ($elapsed -lt $IntervalSec) { Start-Sleep -Seconds ($IntervalSec - $elapsed) }
}

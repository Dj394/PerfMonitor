# Genere dashboard.html a partir des donnees et l'ouvre. Ex: .\report.ps1 -Hours 6
param([double]$Hours = 24, [switch]$NoOpen)
$dataDir = Join-Path $PSScriptRoot 'data'
$since = (Get-Date).AddHours(-$Hours)
$lines = Get-ChildItem $dataDir -Filter 'perf-*.jsonl' | Where-Object { $_.LastWriteTime -ge $since.AddDays(-1) } |
    Sort-Object Name | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 }
$sinceStr = $since.ToString('yyyy-MM-ddTHH:mm:ss')
$lines = @($lines | Where-Object { $_.Length -gt 30 -and $_.Substring(7,19) -ge $sinceStr })
$notes = if (Test-Path "$dataDir\notes.jsonl") { @(Get-Content "$dataDir\notes.jsonl" -Encoding UTF8) } else { @() }
$dataJson  = '[' + ($lines -join ',') + ']'
$notesJson = '[' + ($notes -join ',') + ']'
$tpl = Get-Content (Join-Path $PSScriptRoot 'template.html') -Raw -Encoding UTF8
$html = $tpl.Replace('/*__DATA__*/[]', $dataJson).Replace('/*__NOTES__*/[]', $notesJson).Replace('__HOURS__', "$Hours").Replace('__GEN__', (Get-Date).ToString('dd/MM/yyyy HH:mm'))
$out = Join-Path $PSScriptRoot 'dashboard.html'
[IO.File]::WriteAllText($out, $html, (New-Object Text.UTF8Encoding $false))
Write-Host "$($lines.Count) echantillons -> $out"
if (-not $NoOpen) { Start-Process $out }

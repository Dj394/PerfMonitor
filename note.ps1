# Ajoute un marqueur (ex: .\note.ps1 "Desactive OneDrive au demarrage") - visible sur les graphiques
param([Parameter(Mandatory=$true)][string]$Text)
$f = Join-Path $PSScriptRoot 'data\notes.jsonl'
Add-Content $f (@{ ts = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss'); text = $Text } | ConvertTo-Json -Compress) -Encoding UTF8
Write-Host "Marqueur ajoute : $Text"

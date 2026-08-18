# Genere app.ico (16/24/32/48/64/128/256 px, entrees PNG) : pastille degradee bleu -> violet avec une courbe qui monte
Add-Type -AssemblyName System.Drawing
$sizes = 16,24,32,48,64,128,256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s,$s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $r = [int]($s*0.28)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rect = New-Object System.Drawing.Rectangle 0,0,($s-1),($s-1)
    $d = $r*2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90); $path.AddArc($rect.Right-$d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right-$d, $rect.Bottom-$d, $d, $d, 0, 90); $path.AddArc($rect.X, $rect.Bottom-$d, $d, $d, 90, 90); $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)), ([System.Drawing.Point]::new($s,$s)), ([System.Drawing.Color]::FromArgb(255,0x3B,0x8B,0xFF)), ([System.Drawing.Color]::FromArgb(255,0x8A,0x5C,0xFF))
    $g.FillPath($grad, $path)
    # courbe
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, $s*0.11))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @(
        [System.Drawing.PointF]::new($s*0.20, $s*0.70), [System.Drawing.PointF]::new($s*0.38, $s*0.52),
        [System.Drawing.PointF]::new($s*0.52, $s*0.62), [System.Drawing.PointF]::new($s*0.80, $s*0.30))
    $g.DrawLines($pen, $pts)
    # point final
    $dot = [Math]::Max(2, $s*0.16)
    $g.FillEllipse([System.Drawing.Brushes]::White, [float]($s*0.80-$dot/2), [float]($s*0.30-$dot/2), [float]$dot, [float]$dot)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += ,($ms.ToArray()); $bmp.Dispose()
}
# ecriture ICO
$out = Join-Path $PSScriptRoot 'app.ico'
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
# PNG 256 pour l'entete de la fenetre
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app-icon.png'), $pngs[-1])
Write-Host "OK -> $out"

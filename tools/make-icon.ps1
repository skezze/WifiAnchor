# Генерирует app.ico: тот же рисунок, что и значок в трее (круг с «Wi-Fi»).
# Запуск: powershell -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'app.ico'

function New-Png([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46, 160, 67))
    $g.FillEllipse($bg, 0, 0, $size - 1, $size - 1)

    $cx = $size / 2.0
    $baseY = $size * 0.72
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.4, $size / 10.0))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    foreach ($k in 0.20, 0.34, 0.48) {
        $r = $size * $k
        $g.DrawArc($pen, [single]($cx - $r), [single]($baseY - $r), [single]($r * 2), [single]($r * 2), 225, 90)
    }

    $dot = [Math]::Max(1.2, $size / 12.0)
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($cx - $dot), [single]($baseY - $dot), [single]($dot * 2), [single]($dot * 2))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$frames = foreach ($s in $sizes) { , (New-Png $s) }

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0)
$w.Write([uint16]1)
$w.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim)
    $w.Write([byte]$dim)
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}

foreach ($f in $frames) { $w.Write($f) }
$w.Dispose()
$fs.Dispose()

Write-Host "Создан $out"

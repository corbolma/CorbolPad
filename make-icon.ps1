# Genera corbolpad.ico (multi-risoluzione, PNG interni) e un'anteprima icon-preview.png
# Disegno: un foglio con righe di testo e due linguette di schede in alto.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Documento con l'angolo in alto a destra piegato
function New-DocPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$fold) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine($x, $y, $x + $w - $fold, $y)
    $p.AddLine($x + $w - $fold, $y, $x + $w, $y + $fold)
    $p.AddLine($x + $w, $y + $fold, $x + $w, $y + $h)
    $p.AddLine($x + $w, $y + $h, $x, $y + $h)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.03)
    $w = $s - 2 * $m
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $bg = New-RoundRect $m $m $w $w ($s * 0.22)

    # Sfondo: gradiente diagonale indaco -> blu
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 79, 70, 229)),
        ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), 45.0
    $g.FillPath($grad, $bg)
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)),
        ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.FillPath($hl, $bg)

    # Foglio con due linguette (schede): quella attiva bianca, l'altra semitrasparente
    $px = $s * 0.18; $pw = $s * 0.64; $tabH = $s * 0.11; $py = $s * 0.20 + $tabH; $ph = $s * 0.62 - $tabH
    if ($s -lt 24) { $px = $s * 0.14; $pw = $s * 0.72; $py = $s * 0.24 + $tabH; $ph = $s * 0.62 - $tabH }
    $tw = $pw * 0.42
    $r = [Math]::Max(1.0, $s * 0.04)
    $white = [System.Drawing.Brushes]::White
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(140, 255, 255, 255))), (New-RoundRect ($px + $tw + $s * 0.02) ($py - $tabH) $tw ($tabH + $r * 2) $r))
    $g.FillPath($white, (New-RoundRect $px ($py - $tabH) $tw ($tabH + $r * 2) $r))
    $g.FillPath($white, (New-RoundRect $px $py $pw $ph $r))

    # Righe di testo
    $lc = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 70, 229))
    $lh = [Math]::Max(1.0, $s * 0.045)
    $n = 4; if ($s -lt 24) { $n = 2 }
    $lens = 0.70, 0.52, 0.64, 0.38
    for ($i = 0; $i -lt $n; $i++) {
        $ly = $py + $ph * (0.20 + $i * (0.62 / [Math]::Max(1, $n - 1))) - $lh / 2
        if ($n -eq 2) { $ly = $py + $ph * (0.32 + $i * 0.34) - $lh / 2 }
        $g.FillRectangle($lc, ($px + $pw * 0.14), $ly, ($pw * $lens[$i]), $lh)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $b.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir 'corbolpad.ico'), $out.ToArray())

$pw = 20; foreach ($s in $sizes) { $pw += $s + 20 }
$prev = New-Object System.Drawing.Bitmap $pw, 620
$pg = [System.Drawing.Graphics]::FromImage($prev)
$pg.Clear([System.Drawing.Color]::FromArgb(245, 245, 245))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 310, $pw, 310)
$x = 20
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $pg.DrawImage($b, $x, 290 - $s); $pg.DrawImage($b, $x, 600 - $s)
    $x += $s + 20; $b.Dispose()
}
$prev.Save((Join-Path $dir 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "Creati corbolpad.ico e icon-preview.png"

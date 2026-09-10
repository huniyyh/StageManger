# Draws the Stage Manager icon (a stage on the left, a strip of cards on the right)
# and writes a multi-size, PNG-compressed .ico. Re-run after changing the drawing.
param([string]$Output = (Join-Path $PSScriptRoot "..\src\StageManager.App\Assets\stagemanager.ico"))

Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    function Rounded([double]$x, [double]$y, [double]$w, [double]$h, [double]$r, [System.Drawing.Color]$c) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $x *= $s; $y *= $s; $w *= $s; $h *= $s; $r *= $s; $d = $r * 2
        $p.AddArc($x, $y, $d, $d, 180, 90)
        $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure()
        $b = New-Object System.Drawing.SolidBrush $c
        $g.FillPath($b, $p)
        $b.Dispose(); $p.Dispose()
    }

    Rounded 8 8 240 240 52 ([System.Drawing.Color]::FromArgb(255, 36, 41, 56))       # background
    Rounded 36 60 128 136 16 ([System.Drawing.Color]::FromArgb(255, 88, 150, 255))    # the stage
    Rounded 48 72 104 14 7 ([System.Drawing.Color]::FromArgb(255, 200, 224, 255))     # its title bar
    foreach ($i in 0..2) { Rounded 184 (60 + $i * 48) 40 36 8 ([System.Drawing.Color]::FromArgb(255, 226, 230, 240)) } # the strip

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes   # keep the byte[] in one piece instead of unrolling it into the pipeline
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object System.Collections.ArrayList
foreach ($sz in $sizes) { [void]$images.Add([byte[]](Draw $sz)) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {                                     # ICONDIRENTRY per image
    $sz = $sizes[$i]; [byte[]]$img = $images[$i]
    $dim = [byte]$(if ($sz -ge 256) { 0 } else { $sz })
    $w.Write($dim); $w.Write($dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$img.Length); $w.Write([uint32]$offset)
    $offset += $img.Length
}
for ($i = 0; $i -lt $sizes.Count; $i++) { [byte[]]$img = $images[$i]; $w.Write($img, 0, $img.Length) }
$w.Flush()

$dir = (Resolve-Path (Split-Path $Output -Parent)).Path
$path = Join-Path $dir (Split-Path $Output -Leaf)
[System.IO.File]::WriteAllBytes($path, $out.ToArray())
"wrote $path ($($out.Length) bytes, sizes $($sizes -join ','))"

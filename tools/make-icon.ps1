# Draws the ShadowPlayer icon (a TV showing an eclipse on a night tile) and writes the multi-size .ico the app builds with.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1
param([string]$Ico = (Join-Path $PSScriptRoot '..\src\M3uPlayer.App\Assets\ShadowPlayer.ico'), [string]$PreviewDir)
Add-Type -AssemblyName System.Drawing
$S = 1024

function C($a, $r, $g, $b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function Solid($a, $r, $g, $b) { New-Object System.Drawing.SolidBrush((C $a $r $g $b)) }

function RoundRect($x, $y, $w, $h, $rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $rad
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Grad($c1, $c2, $angle) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0, 0, $S, $S)), $c1, $c2, [single]$angle)
}

function Disc($g, $brush, $cx, $cy, $r) { $g.FillEllipse($brush, (($cx - $r) * $S), (($cy - $r) * $S), (2 * $r * $S), (2 * $r * $S)) }

function Master() {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $tile = RoundRect 0 0 $S $S (0.22 * $S)
    $g.FillPath((Grad (C 255 22 30 58) (C 255 8 10 24) 70), $tile)
    $g.SetClip($tile)

    # Amber, because a dark set would vanish on the night tile.
    $amber = C 255 255 167 38
    $pen = New-Object System.Drawing.Pen($amber, (0.042 * $S))
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, (0.5 * $S), (0.32 * $S), (0.33 * $S), (0.13 * $S))
    $g.DrawLine($pen, (0.5 * $S), (0.32 * $S), (0.67 * $S), (0.13 * $S))
    $body = RoundRect (0.13 * $S) (0.30 * $S) (0.74 * $S) (0.56 * $S) (0.09 * $S)
    $g.FillPath((Grad (C 255 255 183 77) (C 255 245 124 0) 60), $body)

    $screen = RoundRect (0.20 * $S) (0.37 * $S) (0.60 * $S) (0.42 * $S) (0.05 * $S)
    $g.FillPath((Grad (C 255 16 24 48) (C 255 6 8 20) 70), $screen)
    $g.SetClip($screen, [System.Drawing.Drawing2D.CombineMode]::Intersect)
    $cx = 0.5; $cy = 0.58; $r = 0.145
    for ($i = 16; $i -ge 1; $i--) { Disc $g (Solid 14 255 170 70) $cx $cy ($r + $i * 0.0075) }
    Disc $g (Solid 255 255 196 92) $cx $cy $r
    Disc $g (Solid 255 6 8 20) ($cx - 0.06 * $r) ($cy + 0.06 * $r) ($r * 0.90)
    Disc $g (Solid 70 255 230 170) ($cx + 0.70 * $r) ($cy - 0.70 * $r) ($r * 0.32)
    Disc $g (Solid 255 255 250 225) ($cx + 0.70 * $r) ($cy - 0.70 * $r) ($r * 0.17)
    $g.Dispose(); $bmp
}

function Scaled($bmp, $size) {
    $r = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($r)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($bmp, 0, 0, $size, $size)
    $g.Dispose(); $r
}

$master = Master
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $small = Scaled $master $s
    $ms = New-Object System.IO.MemoryStream
    $small.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose()
    , $ms.ToArray()
}

# An .ico is a small directory followed by the images; PNG entries are valid from Windows Vista on.
$dir = Split-Path -Parent $Ico
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$fs = [System.IO.File]::Create($Ico)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] } # 0 means 256 in this format
    $w.Write([byte]$side); $w.Write([byte]$side); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Dispose()
"wrote $Ico ($((Get-Item $Ico).Length) bytes)"

if ($PreviewDir) {
    $rowH = 300
    $sheet = New-Object System.Drawing.Bitmap(860, $rowH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($sheet)
    $sg.Clear((C 255 38 38 44))
    $big = Scaled $master 256
    $sg.DrawImageUnscaled($big, 22, 22)
    $sg.FillRectangle((Solid 255 25 25 28), 300, 36, 530, 110)
    $sg.FillRectangle((Solid 255 243 243 243), 300, 160, 530, 110)
    $x = 318
    foreach ($s in 64, 48, 32, 24, 16) {
        $small = Scaled $master $s
        $sg.DrawImageUnscaled($small, $x, (36 + (110 - $s) / 2))
        $sg.DrawImageUnscaled($small, $x, (160 + (110 - $s) / 2))
        $small.Dispose()
        $x += $s + 42
    }
    $sg.Dispose()
    $sheet.Save((Join-Path $PreviewDir 'final.png'), [System.Drawing.Imaging.ImageFormat]::Png)
}

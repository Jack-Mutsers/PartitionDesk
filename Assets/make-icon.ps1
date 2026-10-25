# Generates PartitionDesk.ico (16-256 px) next to this script.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    function RR($x, $y, $w, $h, $r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [Math]::Max(1, $r * 2)
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); return $p
    }
    # Dark slate rounded tile
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(51, 65, 85)), ([System.Drawing.Color]::FromArgb(15, 23, 42))
    $g.FillPath($bg, (RR (8 * $k) (8 * $k) (240 * $k) (240 * $k) (52 * $k)))
    # Disk bar split into three partitions
    $colors = @(
        [System.Drawing.Color]::FromArgb(59, 130, 246),
        [System.Drawing.Color]::FromArgb(16, 185, 129),
        [System.Drawing.Color]::FromArgb(245, 158, 11)
    )
    $x = 40; $widths = 70, 96, 40
    for ($i = 0; $i -lt 3; $i++) {
        $b = New-Object System.Drawing.SolidBrush $colors[$i]
        $g.FillPath($b, (RR ($x * $k) (88 * $k) (($widths[$i] - 6) * $k) (80 * $k) (10 * $k)))
        $x += $widths[$i]
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'PartitionDesk.ico'), $out.ToArray())

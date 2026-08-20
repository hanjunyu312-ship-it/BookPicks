# Generate app icon: violet gradient rounded square + white book (matches desktop brand)
# NOTE: keep this file pure ASCII (Windows PowerShell 5.1 reads .ps1 as ANSI)
Add-Type -AssemblyName System.Drawing

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

$size = 192
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

# Background: diagonal gradient (violet -> blue)
$bg = New-RoundRect 0 0 192 192 46
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(192, 192)),
  [System.Drawing.Color]::FromArgb(255, 124, 92, 240),
  [System.Drawing.Color]::FromArgb(255, 79, 143, 247))
$g.FillPath($bgBrush, $bg)

# Top-left highlight (glass feel)
$hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, 110)),
  [System.Drawing.Color]::FromArgb(90, 255, 255, 255),
  [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
$g.FillPath($hl, $bg)

# White book body
$book = New-RoundRect 46 36 100 120 20
$g.FillPath([System.Drawing.Brushes]::White, $book)

# Spine (violet gradient bar)
$spine = New-RoundRect 88 36 16 120 8
$spineBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.Point(88, 36)), (New-Object System.Drawing.Point(104, 156)),
  [System.Drawing.Color]::FromArgb(255, 124, 92, 240),
  [System.Drawing.Color]::FromArgb(255, 79, 143, 247))
$g.FillPath($spineBrush, $spine)

# Page texture lines
$linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, 124, 92, 240), 3)
$linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($linePen, 58, 78, 78, 78)
$g.DrawLine($linePen, 58, 98, 78, 98)
$g.DrawLine($linePen, 58, 118, 78, 118)
$g.DrawLine($linePen, 114, 78, 134, 78)
$g.DrawLine($linePen, 114, 98, 134, 98)
$g.DrawLine($linePen, 114, 118, 134, 118)

# Bottom shadow
$sh = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.Point(0, 170)), (New-Object System.Drawing.Point(0, 192)),
  [System.Drawing.Color]::FromArgb(60, 20, 20, 60),
  [System.Drawing.Color]::FromArgb(0, 20, 20, 60))
$g.FillPath($sh, $bg)

$out = Join-Path $PSScriptRoot "res\mipmap-xxxhdpi\ic_launcher.png"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "Icon generated: $out"

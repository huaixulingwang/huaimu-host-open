# Build PC-side icons (app icon + tray normal/error)
# Source art: tablet brand assets (app_icon.png / brand_mark.png), transparent background.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = '<remote-repo>\AppScope\resources\base\media\app_icon.png'
$mark = 'e:\lingwang-remote\entry\build\default\intermediates\res\default\resources\base\media\brand_mark.png'
if (-not (Test-Path $src))  { throw "missing app icon: $src" }
if (-not (Test-Path $mark)) { throw "missing brand mark: $mark" }

$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-IconBitmap($source, $size, $tint) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(1, [int]($size * 0.06))
    $side = $size - $pad * 2
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, $side, $side)
    $g.DrawImage($source, $rect)
    $g.Dispose()
    if ($null -ne $tint) {
        for ($y = 0; $y -lt $size; $y++) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                if ($c.A -gt 0) {
                    $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, $tint.R, $tint.G, $tint.B))
                }
            }
        }
    }
    return $bmp
}

function Get-DibBytes($bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $bw.Write([UInt32]40)
    $bw.Write([Int32]$w)
    $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]($w * $h * 4))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    # XOR bitmap, bottom-up BGRA
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    # AND mask, bottom-up, 1bpp padded to 4 bytes per row
    $rowBytes = [int]([Math]::Floor(($w + 31) / 32) * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $rowBytes
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.A -eq 0) {
                $idx = [int][Math]::Floor($x / 8)
                $row[$idx] = $row[$idx] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

function Write-Ico($bitmaps, $path) {
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    $count = $bitmaps.Count
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$count)

    # 256 uses PNG (compressed); smaller sizes use DIB so System.Drawing.Icon can parse them
    $blobs = New-Object System.Collections.ArrayList
    foreach ($b in $bitmaps) {
        if ($b.Width -ge 256) {
            $s = New-Object IO.MemoryStream
            $b.Save($s, [System.Drawing.Imaging.ImageFormat]::Png)
            [void]$blobs.Add($s.ToArray())
            $s.Dispose()
        } else {
            [void]$blobs.Add((Get-DibBytes $b))
        }
    }

    $offset = 6 + 16 * $count
    for ($i = 0; $i -lt $count; $i++) {
        $w = $bitmaps[$i].Width
        $h = $bitmaps[$i].Height
        $data = $blobs[$i]
        $wb = 0; if ($w -lt 256) { $wb = $w }
        $hb = 0; if ($h -lt 256) { $hb = $h }
        $bw.Write([byte]$wb); $bw.Write([byte]$hb)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($d in $blobs) { $bw.Write([byte[]]$d) }
    $bw.Flush()
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

$appImage  = [System.Drawing.Image]::FromFile($src)
$markImage = [System.Drawing.Image]::FromFile($mark)

# app icon: full brand artwork (white tile), used by window/taskbar/installer
$appBmps = New-Object System.Collections.ArrayList
foreach ($s in $sizes) { [void]$appBmps.Add((New-IconBitmap $appImage $s $null)) }
Write-Ico $appBmps (Join-Path $here 'app.ico')

# tray icons: transparent brand stroke, green = healthy, red = problem
$okBmps  = New-Object System.Collections.ArrayList
$errBmps = New-Object System.Collections.ArrayList
$redTint = [System.Drawing.Color]::FromArgb(255, 214, 88, 76)
foreach ($s in @(16, 20, 24, 32, 48, 64)) {
    [void]$okBmps.Add((New-IconBitmap $markImage $s $null))
    [void]$errBmps.Add((New-IconBitmap $markImage $s $redTint))
}
Write-Ico $okBmps  (Join-Path $here 'tray_ok.ico')
Write-Ico $errBmps (Join-Path $here 'tray_error.ico')

$appImage.Dispose(); $markImage.Dispose()
foreach ($b in $appBmps) { $b.Dispose() }
foreach ($b in $okBmps)  { $b.Dispose() }
foreach ($b in $errBmps) { $b.Dispose() }

foreach ($f in 'app.ico', 'tray_ok.ico', 'tray_error.ico') {
    $fi = Get-Item (Join-Path $here $f)
    Write-Host ('built {0}  {1:N1} KB' -f $fi.Name, ($fi.Length / 1KB)) -ForegroundColor Green
}

# 从 assets/icon.png 生成多尺寸 app.ico（PNG-in-ICO，Windows 10/11 原生支持）
# 用法：powershell -ExecutionPolicy Bypass -File scripts/generate-app-icon.ps1
# 输出：src/DesktopPet.App/Assets/app.ico（16/24/32/48/64/128/256）

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$srcPng = Join-Path $repoRoot 'assets\icon.png'
$outIco = Join-Path $repoRoot 'windows-native\src\DesktopPet.App\Assets\app.ico'

if (-not (Test-Path $srcPng)) { throw "源图不存在: $srcPng" }

Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Image]::FromFile($srcPng)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBytes = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes += ,$ms.ToArray()
    $bmp.Dispose()
    $ms.Dispose()
}
$source.Dispose()

# ICONDIR + ICONDIRENTRY + PNG 数据
$count = $sizes.Count
$headerSize = 6 + 16 * $count
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)

$bw.Write([UInt16]0)          # reserved
$bw.Write([UInt16]1)          # type: icon
$bw.Write([UInt16]$count)     # image count

$offset = $headerSize
for ($i = 0; $i -lt $count; $i++) {
    $size = $sizes[$i]
    $bw.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))  # width (0 = 256)
    $bw.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))  # height
    $bw.Write([Byte]0)        # color count
    $bw.Write([Byte]0)        # reserved
    $bw.Write([UInt16]1)      # planes
    $bw.Write([UInt16]32)     # bpp
    $bw.Write([UInt32]$pngBytes[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngBytes[$i].Length
}

foreach ($data in $pngBytes) { $bw.Write($data) }
$bw.Flush()

$outDir = Split-Path -Parent $outIco
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[System.IO.File]::WriteAllBytes($outIco, $ico.ToArray())
$bw.Dispose()
$ico.Dispose()

Write-Host "OK: $outIco ($([math]::Round((Get-Item $outIco).Length / 1KB, 1)) KB, $count sizes)"

<#
.SYNOPSIS
    生成程序图标 app.ico（多尺寸）+ 预览图。
.DESCRIPTION
    设计：圆角蓝色渐变底 + 白色键帽（3D 侧面）+ 键帽内三根递增蓝柱（统计）
          + 右下角经典鼠标指针（白底深藏青描边）。
    要点：每个尺寸都**按原生尺寸绘制**（不是从大图缩），小尺寸（<=40）用加粗
          简化版（去掉指针与高光、键帽更大、柱子更粗），保证 16/24/32 清晰。
    产物：
      src\KeyMouseCounter.App\app.ico      → 编进 exe（ApplicationIcon）
      docs\screenshots\00-icon.png         → 深/浅底色下的多尺寸预览 + 小尺寸放大
.NOTES
    纯 System.Drawing 绘制，不依赖外部素材；改了设计重跑本脚本即可。
    文件必须是 **UTF-8 with BOM**：Windows PowerShell 5.1 否则按 ANSI 读中文，
    后续字节会把紧跟的 ASCII（如 } ）当双字节尾字节吞掉，导致语法错误。
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$Out = '',
    [string]$Preview = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $Root)    { $Root    = Split-Path -Parent $PSScriptRoot }
if (-not $Out)     { $Out     = Join-Path $Root 'src\KeyMouseCounter.App\app.ico' }
if (-not $Preview) { $Preview = Join-Path $Root 'docs\screenshots\00-icon.png' }

# ── 调色（与 App 主题一致：蓝主色 + 近黑）──
$cBlueLight = [System.Drawing.Color]::FromArgb(255, 112, 163, 255)
$cBlueDeep  = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
$cBlueDark  = [System.Drawing.Color]::FromArgb(255, 29, 78, 190)
$cKeyTopA   = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$cKeyTopB   = [System.Drawing.Color]::FromArgb(255, 216, 226, 242)
$cKeySide   = [System.Drawing.Color]::FromArgb(255, 150, 170, 202)
$cNavy      = [System.Drawing.Color]::FromArgb(255, 16, 32, 66)
$cBarA      = [System.Drawing.Color]::FromArgb(255, 58, 122, 246)
$cBarB      = [System.Drawing.Color]::FromArgb(255, 30, 72, 175)

function New-RoundPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    if ($d -gt $W) { $d = $W }
    if ($d -gt $H) { $d = $H }
    if ($d -le 0.01) { $p.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $W, $H))); return $p }
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-SolidBrush {
    param([System.Drawing.Color]$Color)
    return New-Object System.Drawing.SolidBrush($Color)
}

# 按目标尺寸原生绘制一张图标位图
function New-IconBitmap {
    param([int]$Size)

    $n = [float]$Size
    $bold = ($Size -le 40)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 1) 圆角底板（留 2.8% 透明边）
    $inset = 0.028 * $n
    $tw = $n - 2 * $inset
    $tile = New-RoundPath $inset $inset $tw $tw (0.22 * $n)
    $tileRect = New-Object System.Drawing.RectangleF($inset, $inset, $tw, $tw)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($tileRect, $cBlueLight, $cBlueDeep, 45.0)
    $g.FillPath($grad, $tile)
    $grad.Dispose()

    # 1b) 底部压暗，做出一点体积
    $shadeH = 0.46 * $tw
    $shadePath = New-RoundPath $inset ($inset + $tw - $shadeH) $tw $shadeH (0.20 * $n)
    $shadeRect = New-Object System.Drawing.RectangleF($inset, ($inset + $tw - $shadeH), $tw, $shadeH)
    $shade = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $shadeRect,
        [System.Drawing.Color]::FromArgb(0, $cBlueDark.R, $cBlueDark.G, $cBlueDark.B),
        [System.Drawing.Color]::FromArgb(90, $cBlueDark.R, $cBlueDark.G, $cBlueDark.B),
        90.0)
    $g.FillPath($shade, $shadePath)
    $shade.Dispose(); $shadePath.Dispose()

    # 1c) 左上高光（裁剪到底板内，小尺寸不画，否则糊）
    if (-not $bold) {
        $oldClip = $g.Clip
        $g.SetClip($tile, [System.Drawing.Drawing2D.CombineMode]::Replace)
        $hlRect = New-Object System.Drawing.RectangleF($inset, $inset, $tw, $tw)
        $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $hlRect,
            [System.Drawing.Color]::FromArgb(72, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
            55.0)
        $g.FillRectangle($hl, $hlRect)
        $hl.Dispose()
        $g.SetClip($oldClip, [System.Drawing.Drawing2D.CombineMode]::Replace)
        $oldClip.Dispose()
    }

    # 1d) 内描边（浅色，突出圆角边缘）
    $edgeW = [float][math]::Max(1.0, 0.0075 * $n)
    $edgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), $edgeW)
    $g.DrawPath($edgePen, $tile)
    $edgePen.Dispose()
    $tile.Dispose()

    # 2) 键帽几何：小尺寸放大占位，大尺寸留出指针空间
    if ($bold) {
        $kx = 0.19 * $n; $ky = 0.165 * $n
        $kw = 0.62 * $n;  $kh = 0.545 * $n
        $kr = 0.105 * $n; $sideOffset = 0.05 * $n
    } else {
        $kx = 0.185 * $n; $ky = 0.175 * $n
        $kw = 0.485 * $n; $kh = 0.425 * $n
        $kr = 0.082 * $n; $sideOffset = 0.052 * $n
    }

    # 键帽投影
    $shadowOff = [float][math]::Max(1.0, 0.012 * $n)
    $shadowPath = New-RoundPath ($kx + $shadowOff) ($ky + $sideOffset + $shadowOff) $kw $kh $kr
    $shadowBrush = New-SolidBrush ([System.Drawing.Color]::FromArgb(70, 6, 14, 34))
    $g.FillPath($shadowBrush, $shadowPath)
    $shadowBrush.Dispose(); $shadowPath.Dispose()

    # 2a) 键帽侧面（3D 厚度）
    $sidePath = New-RoundPath $kx ($ky + $sideOffset) $kw $kh $kr
    $sideBrush = New-SolidBrush $cKeySide
    $g.FillPath($sideBrush, $sidePath)
    $sideBrush.Dispose(); $sidePath.Dispose()

    # 2b) 键帽顶面
    $topY = $ky + 0.012 * $n
    $topPath = New-RoundPath $kx $topY $kw $kh $kr
    $topRect = New-Object System.Drawing.RectangleF($kx, $topY, $kw, $kh)
    $topGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($topRect, $cKeyTopA, $cKeyTopB, 90.0)
    $g.FillPath($topGrad, $topPath)
    $topGrad.Dispose()

    # 2c) 键帽边缘细描边（白键帽与蓝底之间加一点层次）
    if (-not $bold) {
        $capPenW = [float][math]::Max(1.0, 0.0055 * $n)
        $capPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(110, 130, 150, 185), $capPenW)
        $g.DrawPath($capPen, $topPath)
        $capPen.Dispose()
    }
    $topPath.Dispose()

    # 3) 键帽内三根递增蓝柱（统计）
    if ($bold) {
        $barW = 0.115 * $n; $gap = 0.062 * $n
        $heights = @((0.16 * $n), (0.255 * $n), (0.35 * $n))
        $padBottom = 0.10 * $n
    } else {
        $barW = 0.058 * $n; $gap = 0.032 * $n
        $heights = @((0.092 * $n), (0.148 * $n), (0.204 * $n))
        $padBottom = 0.078 * $n
    }
    $baseY = $topY + $kh - $padBottom
    $totalW = $barW * 3 + $gap * 2
    $bx = $kx + ($kw - $totalW) / 2
    $barR = [float][math]::Max(0.5, $barW * 0.32)
    $barGradTop = $cBarA
    $barGradBottom = $cBarB
    for ($i = 0; $i -lt 3; $i++) {
        $h = $heights[$i]
        $barPath = New-RoundPath ($bx + $i * ($barW + $gap)) ($baseY - $h) $barW $h $barR
        $barRect = New-Object System.Drawing.RectangleF(($bx + $i * ($barW + $gap)), ($baseY - $h), $barW, $h)
        $barGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($barRect, $barGradTop, $barGradBottom, 90.0)
        $g.FillPath($barGrad, $barPath)
        $barGrad.Dispose(); $barPath.Dispose()
    }

    # 4) 右下角鼠标指针（小尺寸不画，避免糊成一团）
    if (-not $bold) {
        $cx = 0.600 * $n; $cy = 0.520 * $n; $cs = 0.300 * $n
        $arrowPts = @(
            @(0.00, 0.00), @(0.00, 0.74), @(0.17, 0.60), @(0.28, 0.86),
            @(0.40, 0.81), @(0.30, 0.55), @(0.50, 0.55)
        )
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $pts = [System.Drawing.PointF[]]::new($arrowPts.Count)
        for ($i = 0; $i -lt $arrowPts.Count; $i++) {
            $pts[$i] = New-Object System.Drawing.PointF(($cx + $arrowPts[$i][0] * $cs), ($cy + $arrowPts[$i][1] * $cs))
        }
        $path.AddPolygon($pts)
        $path.CloseFigure()

        # 指针投影
        $so = [float][math]::Max(1.0, 0.013 * $n)
        $shadowMatrix = New-Object System.Drawing.Drawing2D.Matrix
        $shadowMatrix.Translate($so, ($so * 1.3))
        $arrowShadow = $path.Clone()
        $arrowShadow.Transform($shadowMatrix)
        $arrowShadowBrush = New-SolidBrush ([System.Drawing.Color]::FromArgb(80, 8, 16, 38))
        $g.FillPath($arrowShadowBrush, $arrowShadow)
        $arrowShadowBrush.Dispose(); $arrowShadow.Dispose(); $shadowMatrix.Dispose()

        $arrowFill = New-SolidBrush ([System.Drawing.Color]::White)
        $g.FillPath($arrowFill, $path)
        $arrowFill.Dispose()

        $arrowPenW = [float][math]::Max(1.0, 0.0125 * $n)
        $arrowPen = New-Object System.Drawing.Pen($cNavy, $arrowPenW)
        $arrowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($arrowPen, $path)
        $arrowPen.Dispose(); $path.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# ── ICO 写出：<64 用 BMP 条目，>=64 用 PNG 条目（Vista+ 支持）──
function Get-BmpEntry {
    param([System.Drawing.Bitmap]$Bmp)
    $size = $Bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER（高度 = 2×，XOR 图 + AND 掩码）
    $maskRow = [int]([math]::Floor(($size + 31) / 32) * 4)
    $xorSize = $size * $size * 4
    $andSize = $maskRow * $size
    $bw.Write([UInt32]40); $bw.Write([Int32]$size); $bw.Write([Int32]($size * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($xorSize + $andSize)); $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    # 像素：底行到顶行，BGRA
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $p = $Bmp.GetPixel($x, $y)
            $bw.Write([byte]$p.B); $bw.Write([byte]$p.G); $bw.Write([byte]$p.R); $bw.Write([byte]$p.A)
        }
    }

    # AND 掩码：透明处为 1（32bpp 下实际用 alpha，仅为兼容）
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskRow
        for ($x = 0; $x -lt $size; $x++) {
            if ($Bmp.GetPixel($x, $y).A -lt 128) {
                $byteIdx = [int](($x - ($x % 8)) / 8)
                $row[$byteIdx] = $row[$byteIdx] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row)
    }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

function Get-PngEntry {
    param([System.Drawing.Bitmap]$Bmp)
    $ms = New-Object System.IO.MemoryStream
    $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

function Write-Ico {
    param([int[]]$Sizes, [string]$Path)
    $images = @()
    foreach ($s in $Sizes) {
        $bmp = New-IconBitmap -Size $s
        try {
            if ($s -ge 64) { $payload = Get-PngEntry $bmp } else { $payload = Get-BmpEntry $bmp }
        } finally { $bmp.Dispose() }
        $images += ,@{ Size = $s; Data = $payload }
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $s = [int]$img.Size
        $w = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$w); $bw.Write([byte]$w); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$img.Data.Length); $bw.Write([UInt32]$offset)
        $offset += $img.Data.Length
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img.Data) }
    $bw.Flush()

    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
    $kb = [math]::Round((Get-Item $Path).Length / 1KB, 1)
    Write-Host ("  app.ico: {0} 个尺寸 ({1})，{2} KB" -f $images.Count, ($Sizes -join '/'), $kb)
}

function Write-Preview {
    param([int[]]$Sizes, [string]$Path)
    $sizesAll = @(16, 24, 32, 48, 64, 128, 256)
    $cell = 150
    $pad = 20
    $labelH = 30
    $rows = 2
    $zoomH = 230
    $w = $pad + $sizesAll.Count * ($cell + $pad)
    $h = $pad + $rows * ($cell + $labelH + $pad) + $zoomH + $pad
    $sheet = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::FromArgb(255, 15, 17, 21))

    $bgDark = New-SolidBrush ([System.Drawing.Color]::FromArgb(255, 15, 17, 21))
    $bgLight = New-SolidBrush ([System.Drawing.Color]::FromArgb(255, 243, 245, 248))
    $textDark = New-SolidBrush ([System.Drawing.Color]::FromArgb(255, 200, 208, 220))
    $textLight = New-SolidBrush ([System.Drawing.Color]::FromArgb(255, 90, 98, 112))
    $font = New-Object System.Drawing.Font('Segoe UI', 10)

    for ($row = 0; $row -lt 2; $row++) {
        $y0 = $pad + $row * ($cell + $labelH + $pad)
        if ($row -eq 1) {
            $g.FillRectangle($bgLight, 0, ($y0 - $pad), $w, ($cell + $labelH + $pad))
        }
        for ($i = 0; $i -lt $sizesAll.Count; $i++) {
            $s = $sizesAll[$i]
            $bmp = New-IconBitmap -Size $s
            try {
                # 256 超出单元格 → 等比缩到单元格内显示（标注实际尺寸）
                $drawSize = $s
                if ($drawSize -gt ($cell - 8)) { $drawSize = $cell - 8 }
                $x = $pad + $i * ($cell + $pad) + [int](($cell - $drawSize) / 2)
                $y = $y0 + [int](($cell - $drawSize) / 2)
                $g.DrawImage($bmp, $x, $y, $drawSize, $drawSize)
            } finally { $bmp.Dispose() }
            $brush = if ($row -eq 0) { $textDark } else { $textLight }
            $label = if ($s -gt ($cell - 8)) { "$s px (缩放)" } else { "$s px" }
            $sz = $g.MeasureString($label, $font)
            $lx = $pad + $i * ($cell + $pad) + ($cell - $sz.Width) / 2
            $g.DrawString($label, $font, $brush, $lx, ($y0 + $cell + 6))
        }
    }

    # 小尺寸最近邻放大，检查像素清晰度
    $zy = $pad + 2 * ($cell + $labelH + $pad) + 26
    $g.DrawString('16px ×8 / 24px ×5 / 32px ×4（最近邻放大，检查小尺寸）', $font, $textDark, $pad, ($zy - 26))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $bx = $pad
    foreach ($pair in @(@(16, 8), @(24, 5), @(32, 4))) {
        $s = $pair[0]; $f = $pair[1]
        $bmp = New-IconBitmap -Size $s
        try { $g.DrawImage($bmp, $bx, $zy, ($s * $f), ($s * $f)) } finally { $bmp.Dispose() }
        $bx += $s * $f + $pad
    }
    $g.Dispose()

    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $sheet.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    $textDark.Dispose(); $textLight.Dispose(); $bgDark.Dispose(); $bgLight.Dispose(); $font.Dispose()
    Write-Host ("  预览图: {0}" -f $Path)
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
Write-Host '生成图标…'
Write-Ico -Sizes $sizes -Path $Out
Write-Preview -Sizes $sizes -Path $Preview
Write-Host '完成。'

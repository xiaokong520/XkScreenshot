<#
.SYNOPSIS
    把一组各尺寸的 PNG 打成一个多档 .ico。

.DESCRIPTION
    托盘图标的尺寸跟 DPI 走：100% 要 16、125% 要 20、150% 要 24、200% 要 32。
    .ico 里缺哪一档，系统就拿邻近档拉伸，十几像素的图标糊起来格外明显 ——
    所以 20、24、48 这几档即便没有现成的 PNG，也要从更大的那档重采样补出来。

    512 用不上：.ico 目录项的宽、高各只占一个字节，256 已经是上限（写 0 表示）。

    各档一律以 PNG 存放。Vista 起系统就认这种压缩帧，比未压缩 DIB 小很多，
    而这个项目的下限本来就是 Windows 10。

.PARAMETER Source
    PNG 所在目录，文件名形如 screenshot_icon_256x256.png。

.PARAMETER Output
    生成的 .ico 路径。默认写进 App 项目的 Assets。

.EXAMPLE
    .\tools\build-ico.ps1 -Source C:\path\to\pngs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Source,

    [string]$Output = "$PSScriptRoot\..\src\XkScreenshot.App\Assets\XkScreenshot.ico",

    [string]$Pattern = 'screenshot_icon_{0}x{0}.png'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# From 为 $null 表示直接用现成的 PNG，否则从那一档重采样
$plan = @(
    @{ Size = 16;  From = $null },
    @{ Size = 20;  From = 64 },
    @{ Size = 24;  From = 64 },
    @{ Size = 32;  From = $null },
    @{ Size = 48;  From = 128 },
    @{ Size = 64;  From = $null },
    @{ Size = 128; From = $null },
    @{ Size = 256; From = $null }
)

function Get-SourcePath([int]$size) {
    Join-Path $Source ($Pattern -f $size)
}

function Get-PngBytes([int]$size, $from) {
    if ($null -eq $from) { return [IO.File]::ReadAllBytes((Get-SourcePath $size)) }

    $source = [Drawing.Image]::FromFile((Get-SourcePath $from))
    try {
        $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($bmp)
        try {
            # SourceCopy + 高质量双三次：目标位图是全透明的，默认的 SourceOver
            # 会把缩下来的半透明边缘和它混在一起，边上糊出一圈更淡的东西
            $g.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.DrawImage($source, (New-Object Drawing.Rectangle 0, 0, $size, $size))
        } finally { $g.Dispose() }

        $ms = New-Object IO.MemoryStream
        try {
            $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
            return $ms.ToArray()
        } finally { $ms.Dispose(); $bmp.Dispose() }
    } finally { $source.Dispose() }
}

# 定型成 byte[]：不这么写，PowerShell 会把数组摊进管道再收成 Object[]，
# 后面 BinaryWriter.Write 就挑不中 Write(byte[]) 那个重载，写出来的文件只有目录没有数据
$frames = foreach ($p in $plan) {
    [byte[]]$bytes = Get-PngBytes $p.Size $p.From
    [pscustomobject]@{ Size = $p.Size; Data = $bytes }
}

New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
$fs = [IO.File]::Create($Output)
$w = New-Object IO.BinaryWriter $fs
try {
    # ICONDIR
    $w.Write([uint16]0)                # reserved
    $w.Write([uint16]1)                # type: 1 = 图标
    $w.Write([uint16]$frames.Count)

    # 图像数据紧跟在目录后面，先把每一档的偏移算出来
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        # 256 那一档宽高字节写 0 —— 一个字节装不下 256
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $w.Write([byte]$dim)           # width
        $w.Write([byte]$dim)           # height
        $w.Write([byte]0)              # 调色板颜色数，真彩色为 0
        $w.Write([byte]0)              # reserved
        $w.Write([uint16]1)            # planes
        $w.Write([uint16]32)           # 位深
        $w.Write([uint32]$f.Data.Length)
        $w.Write([uint32]$offset)
        $offset += $f.Data.Length
    }

    foreach ($f in $frames) { $w.Write([byte[]]$f.Data, 0, $f.Data.Length) }
} finally { $w.Dispose(); $fs.Dispose() }

$path = (Resolve-Path $Output).Path
"{0}`n{1} 字节，{2} 档：{3}" -f $path, (Get-Item $path).Length, $frames.Count, ($frames.Size -join ', ')

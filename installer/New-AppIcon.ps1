<#
.SYNOPSIS
    Generates Resources/app.ico from the same artwork the app draws for its tray icon.

.DESCRIPTION
    App.xaml.cs CreateAppIcon() renders the brand mark procedurally at 32x32:
    a rounded rectangle filled with a purple->blue gradient plus a white 4-point
    sparkle. This script reproduces that artwork at multiple sizes and packs the
    results into a single multi-resolution .ico, so the tray icon, the window
    icon, Explorer shortcuts and the installer all show the same mark.

    Entries are written as 32bpp BGRA DIBs (the classic ICO payload) rather than
    PNG-compressed entries, because every consumer in our chain - the C# compiler
    embedding Win32 resources, Inno Setup's SetupIconFile, and Explorer - accepts
    DIBs without exception.

    ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less files as ANSI,
    which would mangle non-ASCII characters.

.PARAMETER OutputPath
    Destination .ico path. Defaults to the client project's Resources/app.ico.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\New-AppIcon.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'OhMyAgent.AiAgent.Client\Resources\app.ico'
}

# Sizes Windows actually asks for: tray/menu (16-24), desktop (32-48),
# large icons (64-128), and the 256 used by Explorer's extra-large view.
$sizes = @(16, 24, 32, 48, 64, 128, 256)

# Brand colors, kept in sync with App.xaml.cs CreateAppIcon().
$colorFrom = [System.Drawing.Color]::FromArgb(0x7C, 0x5C, 0xFF)  # purple
$colorTo   = [System.Drawing.Color]::FromArgb(0x38, 0x8B, 0xFD)  # blue

function New-RoundedRectPath {
    param([single] $X, [single] $Y, [single] $W, [single] $H, [single] $Radius)

    $d = $Radius * 2.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)
    $path.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90)
    $path.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-SparklePath {
    param([single] $Cx, [single] $Cy, [single] $R)

    # 4-point sparkle: cardinal points at radius R, diagonals pulled in to 34%
    # so the closed curve bows inward and reads as a star rather than a circle.
    $ri = $R * 0.34
    $d  = $ri * 0.7071
    $pts = @(
        (New-Object System.Drawing.PointF($Cx, ($Cy - $R))),
        (New-Object System.Drawing.PointF(($Cx + $d), ($Cy - $d))),
        (New-Object System.Drawing.PointF(($Cx + $R), $Cy)),
        (New-Object System.Drawing.PointF(($Cx + $d), ($Cy + $d))),
        (New-Object System.Drawing.PointF($Cx, ($Cy + $R))),
        (New-Object System.Drawing.PointF(($Cx - $d), ($Cy + $d))),
        (New-Object System.Drawing.PointF(($Cx - $R), $Cy)),
        (New-Object System.Drawing.PointF(($Cx - $d), ($Cy - $d)))
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddClosedCurve($pts, 0.25)
    return $path
}

function New-IconBitmap {
    param([int] $Size)

    # The reference artwork is authored on a 32x32 canvas; everything scales from it.
    $s = $Size / 32.0

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.Clear([System.Drawing.Color]::Transparent)

        # Inset by 1 device pixel like the original (0,0,31,31 on a 32px canvas)
        # so the antialiased edge is not clipped by the bitmap boundary.
        $inset  = [single](1.0 * $s)
        $extent = [single]($Size - $inset)
        $radius = [single](8.0 * $s)

        $bgPath = New-RoundedRectPath -X 0 -Y 0 -W $extent -H $extent -Radius $radius
        $gradientRect = New-Object System.Drawing.RectangleF(0, 0, [single]$Size, [single]$Size)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $gradientRect, $colorFrom, $colorTo, [single]55.0)
        try {
            $g.FillPath($brush, $bgPath)
        } finally {
            $brush.Dispose(); $bgPath.Dispose()
        }

        $sparkle = New-SparklePath -Cx ([single]($Size / 2.0)) -Cy ([single]($Size / 2.0)) -R ([single](11.0 * $s))
        $white   = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        try {
            $g.FillPath($white, $sparkle)
        } finally {
            $white.Dispose(); $sparkle.Dispose()
        }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function Get-DibPayload {
    <#
        Builds one ICO image entry: BITMAPINFOHEADER + bottom-up BGRA pixels +
        a 1bpp AND mask. The mask is all zeros because 32bpp entries carry their
        own alpha channel; Windows still requires the mask to be present.
    #>
    param([System.Drawing.Bitmap] $Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect,
                             [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $raw    = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    } finally {
        $Bitmap.UnlockBits($data)
    }

    $rowBytes  = $w * 4
    $pixels    = New-Object byte[] ($rowBytes * $h)
    for ($y = 0; $y -lt $h; $y++) {
        # GDI+ hands us top-down rows; DIB wants bottom-up.
        [Array]::Copy($raw, (($h - 1 - $y) * $stride), $pixels, ($y * $rowBytes), $rowBytes)
    }

    # AND mask rows are padded to a 4-byte boundary.
    $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4
    $maskBytes  = New-Object byte[] ($maskStride * $h)

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint32] 40)                             # biSize
        $writer.Write([int32]  $w)                             # biWidth
        $writer.Write([int32]  ($h * 2))                       # biHeight = XOR + AND
        $writer.Write([uint16] 1)                              # biPlanes
        $writer.Write([uint16] 32)                             # biBitCount
        $writer.Write([uint32] 0)                              # biCompression = BI_RGB
        $writer.Write([uint32] ($pixels.Length + $maskBytes.Length))  # biSizeImage
        $writer.Write([int32]  0)                              # biXPelsPerMeter
        $writer.Write([int32]  0)                              # biYPelsPerMeter
        $writer.Write([uint32] 0)                              # biClrUsed
        $writer.Write([uint32] 0)                              # biClrImportant
        $writer.Write($pixels)
        $writer.Write($maskBytes)
        $writer.Flush()
        # Leading comma stops PowerShell from unrolling the byte[] into the
        # output stream one element at a time (which loses it as an array).
        return ,$stream.ToArray()
    } finally {
        $writer.Dispose(); $stream.Dispose()
    }
}

Write-Host "Rendering brand mark at $($sizes.Count) sizes: $($sizes -join ', ')"

$payloads = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -Size $size
    try {
        $payload = [byte[]](Get-DibPayload -Bitmap $bmp)
        if ($payload.Length -eq 0) { throw "Empty payload rendered for ${size}x${size}" }
        $payloads.Add($payload)
        Write-Host ("  {0,3}x{0,-3} {1,8:N0} bytes" -f $size, $payload.Length)
    } finally {
        $bmp.Dispose()
    }
}

$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$fs     = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter($fs)
try {
    $writer.Write([uint16] 0)                 # reserved
    $writer.Write([uint16] 1)                 # type: 1 = icon
    $writer.Write([uint16] $sizes.Count)

    # Image data starts after the directory: 6-byte header + 16 bytes per entry.
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        # 256 is encoded as 0 in the single-byte width/height fields.
        $dim = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([byte]   $dim)
        $writer.Write([byte]   $dim)
        $writer.Write([byte]   0)             # palette entries (0 = no palette)
        $writer.Write([byte]   0)             # reserved
        $writer.Write([uint16] 1)             # color planes
        $writer.Write([uint16] 32)            # bits per pixel
        $writer.Write([uint32] $payloads[$i].Length)
        $writer.Write([uint32] $offset)
        $offset += $payloads[$i].Length
    }
    foreach ($payload in $payloads) { $writer.Write($payload) }
    $writer.Flush()
} finally {
    $writer.Dispose(); $fs.Dispose()
}

$sizeKb = [Math]::Round((Get-Item $OutputPath).Length / 1KB, 1)
Write-Host "Wrote $OutputPath ($sizeKb KB)"

# Prove the result is a loadable icon rather than trusting the byte math.
$probe = New-Object System.Drawing.Icon($OutputPath)
try {
    Write-Host "Verified: loads as an Icon, default size $($probe.Width)x$($probe.Height)"
} finally {
    $probe.Dispose()
}

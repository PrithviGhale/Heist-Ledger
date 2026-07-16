param(
    [Parameter(Mandatory = $false)]
    [string]$SourcePath = "src\HeistLedger\Assets\AppIcon.png",

    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "src\HeistLedger\Assets\AppIcon.ico"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$sourceFile = (Resolve-Path -LiteralPath $SourcePath).Path
$outputFile = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($outputFile)

if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$frames = [System.Collections.Generic.List[byte[]]]::new()
$sourceImage = [System.Drawing.Image]::FromFile($sourceFile)

try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new(
            $size,
            $size,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

                $scale = [Math]::Min(
                    $size / [double]$sourceImage.Width,
                    $size / [double]$sourceImage.Height)
                $drawWidth = [Math]::Max(1, [int][Math]::Round($sourceImage.Width * $scale))
                $drawHeight = [Math]::Max(1, [int][Math]::Round($sourceImage.Height * $scale))
                $drawX = [int][Math]::Floor(($size - $drawWidth) / 2)
                $drawY = [int][Math]::Floor(($size - $drawHeight) / 2)

                $destination = [System.Drawing.Rectangle]::new(
                    $drawX,
                    $drawY,
                    $drawWidth,
                    $drawHeight)
                $graphics.DrawImage(
                    $sourceImage,
                    $destination,
                    0,
                    0,
                    $sourceImage.Width,
                    $sourceImage.Height,
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }

            $memoryStream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($memoryStream, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($memoryStream.ToArray())
            }
            finally {
                $memoryStream.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $sourceImage.Dispose()
}

$fileStream = [System.IO.File]::Open(
    $outputFile,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($fileStream)

try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $frame = $frames[$index]
        $dimensionByte = if ($size -eq 256) { [byte]0 } else { [byte]$size }

        $writer.Write($dimensionByte)
        $writer.Write($dimensionByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Output "Created $outputFile with sizes: $($sizes -join ', ')"

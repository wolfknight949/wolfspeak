# Builds the multi-size Windows icon from the WolfSpeak artwork.
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\src\WolfSpeak\assets\wolfspeak-icon.png"),
    [string]$Out = (Join-Path $PSScriptRoot "..\src\WolfSpeak\assets\wolfspeak.ico")
)
Add-Type -AssemblyName System.Drawing

function Render([System.Drawing.Image]$source, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
        $width = [int][Math]::Round($source.Width * $scale)
        $height = [int][Math]::Round($source.Height * $scale)
        $left = [int][Math]::Floor(($size - $width) / 2)
        $top = [int][Math]::Floor(($size - $height) / 2)
        $g.DrawImage($source, (New-Object System.Drawing.Rectangle $left, $top, $width, $height))
        $ms = New-Object System.IO.MemoryStream
        try {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$ms.ToArray()
        }
        finally { $ms.Dispose() }
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

$sourceImage = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source))
try {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = foreach ($sz in $sizes) { ,(Render $sourceImage $sz) }
}
finally { $sourceImage.Dispose() }

$target = [System.IO.Path]::GetFullPath($Out)
$stream = [System.IO.File]::Create($target)
$writer = New-Object System.IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]
        $len = $images[$i].Length
        $writer.Write([byte]($sz % 256))
        $writer.Write([byte]($sz % 256))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$len)
        $writer.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $writer.Write($img) }
}
finally { $writer.Dispose() }

[System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($target, ".png"), $images[-1])
"wrote $target"

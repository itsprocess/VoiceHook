$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$repo=Split-Path $PSScriptRoot -Parent
$assets=Join-Path $repo 'assets'
$images=Join-Path $repo 'streamdeck/com.voicehook.ptt.sdPlugin/images'
New-Item -ItemType Directory -Force $assets,$images | Out-Null
function New-MicImage([int]$size,[string]$file) {
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode='AntiAlias'
    $graphics.Clear([Drawing.Color]::FromArgb(24,32,48))
    $graphics.ScaleTransform($size/144.0,$size/144.0)
    $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(105,223,202))
    $pen=[Drawing.Pen]::new($brush,7)
    $pen.StartCap='Round'; $pen.EndCap='Round'
    $graphics.FillEllipse($brush,58,20,28,28)
    $graphics.FillRectangle($brush,58,34,28,29)
    $graphics.FillEllipse($brush,58,49,28,28)
    $graphics.DrawArc($pen,44,41,56,54,0,180)
    $graphics.DrawLine($pen,72,95,72,109)
    $graphics.DrawLine($pen,56,109,88,109)
    $bitmap.Save($file,[Drawing.Imaging.ImageFormat]::Png)
    $pen.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
New-MicImage 20 (Join-Path $images 'icon.png')
New-MicImage 40 (Join-Path $images 'icon@2x.png')
New-MicImage 72 (Join-Path $images 'key.png')
New-MicImage 144 (Join-Path $images 'key@2x.png')
New-MicImage 64 (Join-Path $assets 'voicehook.png')
# Write a PNG-backed ICO directly: no unmanaged icon handles to leak.
$png=[IO.File]::ReadAllBytes((Join-Path $assets 'voicehook.png'))
$stream=[IO.File]::Create((Join-Path $assets 'voicehook.ico'))
$writer=[IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]1)
$writer.Write([byte]64);$writer.Write([byte]64);$writer.Write([byte]0);$writer.Write([byte]0)
$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$png.Length);$writer.Write([uint32]22)
$writer.Write($png);$writer.Dispose();$stream.Dispose()

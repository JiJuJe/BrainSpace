# Run in a single-threaded apartment so WPF can render the vector mark.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework

$project = Split-Path -Parent $PSScriptRoot
$source = Get-Content -LiteralPath (Join-Path $project 'BrandMark.xaml') -Raw
$mark = [Windows.Markup.XamlReader]::Parse($source)['BrainSpaceMark']
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = [System.Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $drawing.DrawImage($mark, [Windows.Rect]::new(0, 0, $size, $size))
    } finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $bytes = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($bytes)
        $frames.Add($bytes.ToArray())
    } finally { $bytes.Dispose() }
}

$icon = [System.IO.MemoryStream]::new()
$header = [System.IO.BinaryWriter]::new($icon)
try {
    $header.Write([uint16]0)
    $header.Write([uint16]1)
    $header.Write([uint16]$frames.Count)
    $offset = 6 + $frames.Count * 16
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $dimension = [byte]($sizes[$index] % 256)
        $header.Write($dimension)
        $header.Write($dimension)
        $header.Write([uint16]0)
        $header.Write([uint16]1)
        $header.Write([uint16]32)
        $header.Write([uint32]$frames[$index].Length)
        $header.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $header.Write($frame) }
    $header.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $project 'BrainSpace.ico'), $icon.ToArray())
} finally { $header.Dispose() }

$previews = Join-Path $project 'dist\verification'
[void][System.IO.Directory]::CreateDirectory($previews)
[System.IO.File]::WriteAllBytes((Join-Path $previews 'logo.png'), $frames[$frames.Count - 1])
[System.IO.File]::WriteAllBytes((Join-Path $previews 'logo-16px.png'), $frames[0])

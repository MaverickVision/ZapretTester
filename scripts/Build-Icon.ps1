param([string]$Source, [string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$assets = Join-Path $PSScriptRoot '../src/ZapretTester.App/Assets'
if (-not $Source) { $Source = Join-Path $assets 'app-icon.png' }
if (-not $Destination) { $Destination = Join-Path $assets 'app.ico' }
$sourceImage = [Windows.Media.Imaging.BitmapImage]::new([Uri][IO.Path]::GetFullPath($Source))
$frames = @(foreach ($size in @(16,24,32,48,64,128,256)) {
    $scaled = [Windows.Media.Imaging.TransformedBitmap]::new($sourceImage,
        [Windows.Media.ScaleTransform]::new($size/$sourceImage.PixelWidth, $size/$sourceImage.PixelHeight))
    $bitmap = [Windows.Media.Imaging.FormatConvertedBitmap]::new($scaled, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    if ($bitmap.PixelWidth -ne $size -or $bitmap.PixelHeight -ne $size) { throw 'Unexpected frame dimensions.' }
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $bitmap.CopyPixels($pixels, $stride, 0)
    if ($pixels[3] -ne 0) { throw 'The icon must have genuinely transparent corners.' }
    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $mask = [byte[]]::new($maskStride * $size)
    $buffer = [IO.MemoryStream]::new()
    $frameWriter = [IO.BinaryWriter]::new($buffer)
    try {
        $frameWriter.Write([uint32]40)
        $frameWriter.Write([int]$size)
        $frameWriter.Write([int]($size * 2))
        $frameWriter.Write([uint16]1)
        $frameWriter.Write([uint16]32)
        $frameWriter.Write([uint32]0)
        $frameWriter.Write([uint32]($pixels.Length + $mask.Length))
        foreach ($unused in 1..4) { $frameWriter.Write([int]0) }
        for ($row = $size - 1; $row -ge 0; $row--) {
            $frameWriter.Write($pixels, $row * $stride, $stride)
            for ($column = 0; $column -lt $size; $column++) {
                if ($pixels[$row * $stride + $column * 4 + 3] -eq 0) {
                    $index = ($size - 1 - $row) * $maskStride + [int][math]::Floor($column / 8)
                    $mask[$index] = $mask[$index] -bor (128 -shr ($column % 8))
                }
            }
        }
        $frameWriter.Write($mask)
        $frameWriter.Flush()
        [pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray() }
    } finally { $frameWriter.Dispose() }
})
$writer = [IO.BinaryWriter]::new([IO.File]::Create([IO.Path]::GetFullPath($Destination)))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
$decoded = [Windows.Media.Imaging.IconBitmapDecoder]::new([Uri][IO.Path]::GetFullPath($Destination),
    [Windows.Media.Imaging.BitmapCreateOptions]::None, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
if ($decoded.Frames.Count -ne 7) { throw 'Incomplete icon.' }
Write-Output 'Built seven transparent ICO frames (16–256 px).'

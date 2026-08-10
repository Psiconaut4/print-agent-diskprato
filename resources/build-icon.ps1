#Requires -Version 5
# Gera icon-16-256.ico (usado pela ARP, pelo atalho do menu Iniciar e pelo
# ApplicationIcon dos dois .exe) a partir do frame 256x256 de icon-256.ico,
# que e a arte de origem. Rodar de novo so quando a arte mudar.
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcPath = Join-Path $here 'icon-256.ico'
$outPath = Join-Path $here 'icon-16-256.ico'

# O frame 256 esta em PNG dentro do .ico; extrai os bytes crus pelo ICONDIR.
$raw = [IO.File]::ReadAllBytes($srcPath)
$offset = [BitConverter]::ToUInt32($raw, 6 + 12)
$length = [BitConverter]::ToUInt32($raw, 6 + 8)
$pngBytes = New-Object byte[] $length
[Array]::Copy($raw, $offset, $pngBytes, 0, $length)
$ms = New-Object IO.MemoryStream(, $pngBytes)
$src = [Drawing.Image]::FromStream($ms)

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = @()

foreach ($size in $sizes) {
    $bmp = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([Drawing.Color]::Transparent)
    $g.DrawImage($src, (New-Object Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose()

    if ($size -ge 128) {
        # >=128: PNG comprimido (suportado desde o Vista, evita .ico gigante).
        $pms = New-Object IO.MemoryStream
        $bmp.Save($pms, [Drawing.Imaging.ImageFormat]::Png)
        $data = $pms.ToArray()
        $pms.Dispose()
    }
    else {
        # <128: DIB classico (BITMAPINFOHEADER + XOR BGRA bottom-up + mascara AND).
        $hdr = New-Object IO.MemoryStream
        $w = New-Object IO.BinaryWriter($hdr)
        $w.Write([uint32]40)          # biSize
        $w.Write([int32]$size)        # biWidth
        $w.Write([int32]($size * 2))  # biHeight = XOR + AND
        $w.Write([uint16]1)           # biPlanes
        $w.Write([uint16]32)          # biBitCount
        $w.Write([uint32]0)           # biCompression = BI_RGB
        $w.Write([uint32]($size * $size * 4))
        $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)

        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $p = $bmp.GetPixel($x, $y)
                $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
            }
        }
        # Mascara AND: 1 bit por pixel, linhas alinhadas em 4 bytes. Com alfa de
        # 32bpp o Windows ignora a mascara, mas o formato exige que ela exista.
        $strideBits = [math]::Ceiling($size / 32) * 32
        $strideBytes = $strideBits / 8
        for ($y = 0; $y -lt $size; $y++) {
            for ($b = 0; $b -lt $strideBytes; $b++) { $w.Write([byte]0) }
        }
        $w.Flush()
        $data = $hdr.ToArray()
        $w.Dispose()
    }

    $entries += [pscustomobject]@{ Size = $size; Data = $data }
    $bmp.Dispose()
}

$src.Dispose()
$ms.Dispose()

$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$dataOffset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e.Data.Length)
    $bw.Write([uint32]$dataOffset)
    $dataOffset += $e.Data.Length
}
foreach ($e in $entries) { $bw.Write($e.Data) }
$bw.Flush()
[IO.File]::WriteAllBytes($outPath, $out.ToArray())
$bw.Dispose()

Write-Output ("gerado {0} ({1} frames, {2} bytes)" -f $outPath, $entries.Count, (Get-Item $outPath).Length)

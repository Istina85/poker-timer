# Сборка Poker Night для Windows.
# Запуск (из любой папки):  powershell -ExecutionPolicy Bypass -File windows\build.ps1
# Результат: dist\Poker Night\  (Poker Night.exe + index.html + Read_me.txt) и dist\Poker-Night-Windows.zip
# Нужны только компоненты Windows: .NET Framework 4 (csc.exe), System.Drawing. Установка ничего не требует.

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $PSScriptRoot
$src   = Join-Path $root 'windows'
$dist  = Join-Path $root 'dist'
$out   = Join-Path $dist 'Poker Night'
$csc   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'Не найден csc.exe (.NET Framework 4).' }

# 1. Значок: тёмный круг с бирюзовым ободом и буквой P; упаковка PNG в ICO
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::Transparent)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 14, 34))), 8, 8, 240, 240)
$g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 61, 224, 255), 14)), 15, 15, 226, 226)
$font = New-Object System.Drawing.Font 'Bahnschrift', 130, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$g.DrawString('P', $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 61, 224, 255))), (New-Object System.Drawing.RectangleF 0, 0, 256, 256), $sf)
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$g.Dispose(); $bmp.Dispose()
$ico = Join-Path $env:TEMP 'poker-night.ico'
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)          # ICONDIR: 1 изображение
$bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)  # 256x256, без палитры
$bw.Write([uint16]1); $bw.Write([uint16]32)                              # плоскости, 32 бита
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22)                    # размер и смещение PNG
$bw.Write($png); $bw.Flush(); $bw.Close(); $fs.Close()

# 2. Сборка exe
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null
& $csc /nologo /target:winexe /optimize+ /win32icon:$ico `
    /r:System.Windows.Forms.dll /r:System.dll `
    /out:"$out\Poker Night.exe" "$src\PokerNight.cs"
if ($LASTEXITCODE -ne 0) { throw "csc завершился с кодом $LASTEXITCODE" }

# 3. Файлы для флешки
Copy-Item (Join-Path $root 'index.html') $out
Copy-Item (Join-Path $src 'Read_me.txt') $out -ErrorAction SilentlyContinue

# 4. Архив для выкладки в GitHub Releases
$zip = Join-Path $dist 'Poker-Night-Windows.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip

$exe = Get-Item "$out\Poker Night.exe"
Write-Host ("exe: {0} ({1:N0} байт)" -f $exe.FullName, $exe.Length)
Write-Host ("zip: {0} ({1:N0} байт)" -f $zip, (Get-Item $zip).Length)

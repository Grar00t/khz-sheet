param([string]$OutputDir = "$PSScriptRoot\..\src\KHZ.Sheet.Desktop\Assets")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class KhzIconNative {
  [DllImport("user32.dll", SetLastError=true)]
  public static extern bool DestroyIcon(IntPtr hIcon);
}
'@
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$bmp = [System.Drawing.Bitmap]::new(256,256)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(13,17,23))
$dark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(31,38,48))
$green = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(63,185,80))
$white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(230,237,243))
$gridPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(48,54,61),5)
$g.FillRectangle($dark,30,30,196,196)
for($i=0;$i -lt 4;$i++) {
  for($j=0;$j -lt 4;$j++) {
    $brush = if($i -eq 0 -or ($i -eq 1 -and $j -eq 0)) { $green } else { $white }
    $g.FillRectangle($brush,46 + 42*$i,46 + 42*$j,32,32)
  }
}
$g.DrawRectangle($gridPen,30,30,196,196)
$slash = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(63,185,80),18)
$slash.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$slash.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($slash,58,205,198,48)
$g.FillEllipse($green,178,148,30,30)
$g.FillEllipse($green,178,195,30,30)
$frac = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(63,185,80),9)
$frac.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$frac.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($frac,167,188,219,188)
$png = Join-Path $OutputDir 'khz-sheet.png'
$ico = Join-Path $OutputDir 'khz-sheet.ico'
$bmp.Save($png,[System.Drawing.Imaging.ImageFormat]::Png)
$hIcon = $bmp.GetHicon()
try {
  $icon = [System.Drawing.Icon]::FromHandle($hIcon)
  $stream = [System.IO.File]::Create($ico)
  try { $icon.Save($stream) } finally { $stream.Dispose() }
} finally {
  [KhzIconNative]::DestroyIcon($hIcon) | Out-Null
  $g.Dispose(); $bmp.Dispose(); $dark.Dispose(); $green.Dispose(); $white.Dispose()
  $gridPen.Dispose(); $slash.Dispose(); $frac.Dispose()
}
Get-FileHash $png,$ico -Algorithm SHA256 | Format-Table -AutoSize

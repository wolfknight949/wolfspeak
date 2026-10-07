# Captures README screenshots of the real app running in demo mode (fake friends, fake call,
# no network, your settings untouched).
#
#   .\scripts\screenshots.ps1                  # all scenes → docs\screenshots\*.png
#   .\scripts\screenshots.ps1 -Scenes call     # just one
param(
    [string[]]$Scenes = @("home", "ringing", "call", "settings"),
    [int]$SettleMs = 2500
)
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\WolfSpeak.csproj"
$outDir = Join-Path $root "docs\screenshots"
$buildDir = Join-Path $root "publish\screenshot-build"

dotnet build $project -c Release -o $buildDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
New-Item -ItemType Directory -Force $outDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
}
"@
[Win]::SetProcessDPIAware() | Out-Null

foreach ($scene in $Scenes) {
    $proc = Start-Process (Join-Path $buildDir "WolfSpeak.exe") -ArgumentList "--demo", $scene -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(15)
        while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $proc.Refresh() }
        if ($proc.MainWindowHandle -eq 0) { throw "WolfSpeak demo window for '$scene' never appeared" }
        Start-Sleep -Milliseconds $SettleMs # let fades, pulses and level meters get going

        $hwnd = $proc.MainWindowHandle
        $r = New-Object Win+RECT
        [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
        $bmp = New-Object Drawing.Bitmap $w, $h
        $g = [Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [Win]::PrintWindow($hwnd, $hdc, 2) | Out-Null # PW_RENDERFULLCONTENT: includes WPF/DWM content
        $g.ReleaseHdc($hdc); $g.Dispose()

        # Windows 11 rounds window corners (8 px at 100 %); PrintWindow returns a square — round it with transparency.
        $radius = [int](8 * [Win]::GetDpiForWindow($hwnd) / 96)
        $out = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($out)
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $path = New-Object Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d - 1, 0, $d, $d, 270, 90)
        $path.AddArc($w - $d - 1, $h - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $h - $d - 1, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = New-Object Drawing.TextureBrush $bmp
        $g.FillPath($brush, $path)
        $g.Dispose(); $brush.Dispose(); $bmp.Dispose()

        $file = Join-Path $outDir "$scene.png"
        $out.Save($file, [Drawing.Imaging.ImageFormat]::Png); $out.Dispose()
        Write-Host "  $file  (${w}x${h})" -ForegroundColor Green
    }
    finally {
        if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
    }
}

# 1280x640 card for GitHub's Settings → Social preview, rendered from docs\social-preview.html with headless Edge.
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (Test-Path $edge) {
    $html = (Join-Path $root "docs\social-preview.html") -replace '\\', '/'
    $card = Join-Path $root "docs\social-preview.png"
    $profileDir = Join-Path $env:TEMP "wolfspeak-edge-profile"
    Start-Process $edge -Wait -WindowStyle Hidden -ArgumentList @(
        "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
        "--user-data-dir=`"$profileDir`"", "--allow-file-access-from-files", "--window-size=1280,640",
        "--screenshot=`"$card`"", "file:///$html")
    Write-Host "  $card  (1280x640)" -ForegroundColor Green
}

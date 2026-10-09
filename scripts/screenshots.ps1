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

# Render the WPF visual tree directly; PrintWindow is blank on hidden GPU surfaces.
foreach ($scene in $Scenes) {
    $file = Join-Path $outDir "$scene.png"
    $proc = Start-Process (Join-Path $buildDir "WolfSpeak.exe") -ArgumentList @("--demo", $scene, "--snapshot", "`"$file`"", "--snapshot-delay", $SettleMs) -WindowStyle Hidden -PassThru
    try {
        if (-not $proc.WaitForExit(15000)) { throw "Snapshot timed out for '$scene'" }
        if ($proc.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $file)) { throw "Snapshot failed for '$scene'" }
        Write-Host "  $file" -ForegroundColor Green
    }
    finally { if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() } }
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

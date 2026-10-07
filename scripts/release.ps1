# Builds an installable, auto-updating release with Velopack (Setup.exe + update packages).
#
#   .\scripts\release.ps1                         # build release into publish\releases (version from .csproj)
#   .\scripts\release.ps1 -Version 1.3.0          # override the version
#   .\scripts\release.ps1 -Upload                 # also publish it as a GitHub Release (needs $env:GITHUB_TOKEN)
#   .\scripts\release.ps1 -SelfContained          # bundle .NET (~85 MB setup instead of ~11 MB)
#
# Teammates install once with WolfSpeak-win-Setup.exe from the GitHub Release (it installs the
# .NET 10 Desktop Runtime too if it's missing); after that every installed copy updates itself.
# Normally CI does this for you: push a tag like `v1.3.0`.
param(
    [string]$Version,
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$Upload,
    [string]$RepoUrl = "https://github.com/wolfknight949/wolfspeak",
    [string]$Token = $env:GITHUB_TOKEN
)
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\WolfSpeak.csproj"
$icon = Join-Path $root "src\assets\wolfspeak.ico"
$appDir = Join-Path $root "publish\release-app"
$releaseDir = Join-Path $root "publish\releases"
$arch = $Runtime.Split("-")[-1]

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.TrimStart("v")
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')" }

# Release notes = this version's section of CHANGELOG.md ("## [1.2.3] ..." up to the next "## ").
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Encoding UTF8
$start = ($changelog | Select-String -Pattern "^## \[?v?$([regex]::Escape($Version))\]?(\s|$)" | Select-Object -First 1).LineNumber
if (-not $start) { throw "CHANGELOG.md has no '## [$Version]' section. Add one before releasing." }
$notes = foreach ($line in $changelog[$start..($changelog.Count - 1)]) { if ($line -match '^## ') { break }; $line }
$notes = ($notes -join "`n").Trim()
if (-not $notes) { throw "CHANGELOG.md section for $Version is empty." }
$notesFile = Join-Path $root "publish\release-notes.md"
New-Item -ItemType Directory -Force (Split-Path $notesFile) | Out-Null
[IO.File]::WriteAllText($notesFile, $notes + "`n")

function Invoke-Checked {
    & $args[0] $args[1..($args.Count - 1)]
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $($args -join ' ')" }
}

Push-Location $root
try {
    Invoke-Checked dotnet tool restore

    $flavor = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
    Write-Host "`n==> Publishing WolfSpeak $Version ($Runtime, $flavor)" -ForegroundColor Cyan
    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
    Invoke-Checked dotnet publish $project -c Release -r $Runtime --self-contained $SelfContained.IsPresent.ToString().ToLower() `
        "-p:Version=$Version" "-p:DebugType=none" -o $appDir

    # Grab the previous release (if any) so Velopack can build a small delta update.
    if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
    Write-Host "`n==> Fetching previous release for delta updates" -ForegroundColor Cyan
    $downloadArgs = @("vpk", "download", "github", "--repoUrl", $RepoUrl, "-o", $releaseDir)
    if ($Token) { $downloadArgs += @("--token", $Token) }
    & dotnet @downloadArgs
    if ($LASTEXITCODE -ne 0) { Write-Host "    (none found - first release or repo not reachable; continuing)" -ForegroundColor Yellow }

    Write-Host "`n==> Packing installer + update package" -ForegroundColor Cyan
    $packArgs = @("vpk", "pack", "--packId", "WolfSpeak", "--packVersion", $Version, "--packDir", $appDir,
        "--mainExe", "WolfSpeak.exe", "--packTitle", "WolfSpeak", "--icon", $icon, "-r", $Runtime, "-o", $releaseDir,
        "--releaseNotes", $notesFile)
    if (-not $SelfContained) { $packArgs += @("--framework", "net10.0-$arch-desktop") } # Setup installs the runtime if missing
    Invoke-Checked dotnet @packArgs

    if ($Upload) {
        if (-not $Token) { throw "Set `$env:GITHUB_TOKEN (a token with 'contents: write' on $RepoUrl) to upload." }
        Write-Host "`n==> Uploading GitHub Release v$Version" -ForegroundColor Cyan
        Invoke-Checked dotnet vpk upload github --repoUrl $RepoUrl --token $Token -o $releaseDir `
            --publish --releaseName "WolfSpeak $Version" --tag "v$Version"
    }

    Write-Host "`nDone. Installer: $(Join-Path $releaseDir 'WolfSpeak-win-Setup.exe')" -ForegroundColor Green
}
finally { Pop-Location }

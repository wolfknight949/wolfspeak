# Publishes WolfSpeak as a single exe.
#
#   .\scripts\publish.ps1                       # small exe, needs .NET 10 Desktop Runtime
#   .\scripts\publish.ps1 -Standalone           # bigger exe, runs anywhere (no runtime needed)
#   .\scripts\publish.ps1 -All                  # both flavors
#   .\scripts\publish.ps1 -All -Zip             # both, plus a .zip of each for sharing
#   .\scripts\publish.ps1 -Runtime win-arm64    # other architectures
#   .\scripts\publish.ps1 -Version 1.2.0        # override the version from the .csproj
#
# Output: publish\<runtime>\       (framework-dependent)
#         publish\<runtime>-standalone\
param(
    [string]$Runtime = "win-x64",
    [switch]$Standalone,
    [switch]$All,
    [switch]$Zip,
    [string]$Version
)
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\WolfSpeak.csproj"
$publishRoot = Join-Path $root "publish"

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$flavors = if ($All) { @($false, $true) } else { @([bool]$Standalone) }

foreach ($selfContained in $flavors) {
    $name = if ($selfContained) { "$Runtime-standalone" } else { $Runtime }
    $out = Join-Path $publishRoot $name

    Write-Host "`n==> Publishing WolfSpeak $Version ($name)" -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    $publishArgs = @(
        "publish", $project,
        "-c", "Release",
        "-r", $Runtime,
        "--self-contained", $selfContained.ToString().ToLower(),
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=none",
        "-p:Version=$Version",
        "-o", $out
    )
    if ($selfContained) { $publishArgs += "-p:EnableCompressionInSingleFile=true" }

    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $exe = Get-Item (Join-Path $out "WolfSpeak.exe")
    Write-Host ("    {0}  ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green

    if ($Zip) {
        $zipPath = Join-Path $publishRoot "WolfSpeak-$Version-$name.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zipPath
        Write-Host "    $zipPath" -ForegroundColor Green
    }
}

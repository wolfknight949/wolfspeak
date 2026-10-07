# Builds WolfSpeak.
#   .\scripts\build.ps1                 # Release build
#   .\scripts\build.ps1 -Configuration Debug
#   .\scripts\build.ps1 -Run            # build and launch
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Run
)
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\src\WolfSpeak.csproj"

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Run) {
    dotnet run --project $project -c $Configuration --no-build
}

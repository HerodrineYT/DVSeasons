[CmdletBinding()]
param(
    [string]$DVInstallDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Find-DotNet {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
    }

    throw 'A 64-bit .NET SDK was not found. Install .NET SDK 8.0.421 or newer.'
}

function Find-DerailValley {
    param([string]$ExplicitPath)

    $candidates = @($ExplicitPath, $env:DERAIL_VALLEY_DIR)
    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        $candidates += Join-Path $drive.Root 'Steam\steamapps\common\Derail Valley'
        $candidates += Join-Path $drive.Root 'SteamLibrary\steamapps\common\Derail Valley'
    }

    foreach ($candidate in $candidates | Where-Object { $_ } | Select-Object -Unique) {
        $managed = Join-Path $candidate 'DerailValley_Data\Managed\Assembly-CSharp.dll'
        if (Test-Path -LiteralPath $managed -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Derail Valley was not found. Pass -DVInstallDir or set DERAIL_VALLEY_DIR.'
}

$dotnet = Find-DotNet
$game = Find-DerailValley -ExplicitPath $DVInstallDir
$modProject = Join-Path $projectRoot 'DVSeasons.Game\DVSeasons.Game.csproj'
$testProject = Join-Path $projectRoot 'DVSeasons.Tests\DVSeasons.Tests.csproj'

Write-Host "Building DVSeasons ($Configuration) against $game"
& $dotnet build $modProject -c $Configuration "-p:DVInstallDir=$game"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

if (-not $SkipTests) {
    & $dotnet test $testProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}

& (Join-Path $PSScriptRoot 'verify_project.ps1') -RequireBuildOutput
if ($LASTEXITCODE -ne 0) { throw "Project verification failed with exit code $LASTEXITCODE." }

Write-Host "Build output: $(Join-Path $projectRoot 'artifacts\build\DVSeasons')"

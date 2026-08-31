[CmdletBinding()]
param([switch]$RequireBuildOutput)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$required = @(
    'DVSeasons.sln',
    'DVSeasons.Common\DVSeasons.Common.csproj',
    'DVSeasons.Game\DVSeasons.Game.csproj',
    'DVSeasons.MP\DVSeasons.MP.csproj',
    'DVSeasons.Tests\DVSeasons.Tests.csproj',
    'Resources\Runtime\AssetBundles\dvseasons_dv99',
    'Resources\Runtime\Textures\snowflake_realistic.png',
    'Resources\Runtime\Textures\snowflake_variations.png',
    'Resources\Runtime\Textures\winter_ballast_balanced.png',
    'Resources\Runtime\Textures\Seasonal\autumn\SleeperNew_d.png',
    'Resources\Runtime\Textures\Seasonal\autumn\SleeperOld_d.png',
    'Resources\Runtime\Textures\Seasonal\winter\SleeperNew_d.png',
    'Resources\Runtime\Textures\Seasonal\winter\SleeperOld_d.png',
    'DVSeasons.Unity\Assets\Editor\DVSeasonsAssetBundleBuilder.cs',
    'DVSeasons.Unity\ProjectSettings\ProjectVersion.txt',
    'Docs\BUILDING.md',
    'Docs\PROJECT_STRUCTURE.md'
)

foreach ($relativePath in $required) {
    $path = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required project file is missing: $relativePath"
    }
}

$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\info.source.json') -Raw | ConvertFrom-Json
$projectFiles = @(
    'DVSeasons.Common\DVSeasons.Common.csproj',
    'DVSeasons.Game\DVSeasons.Game.csproj',
    'DVSeasons.MP\DVSeasons.MP.csproj'
)
foreach ($relativePath in $projectFiles) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot $relativePath) -Raw
    $version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
    if ($version -ne $metadata.Version) {
        throw "Version mismatch: $relativePath has '$version', info.json has '$($metadata.Version)'."
    }
}

$unityTextures = Join-Path $projectRoot 'DVSeasons.Unity\Assets\DVSeasons\DV99'
$expectedSeasonTextures = @{ spring = 41; autumn = 43; winter = 43 }
foreach ($season in @('spring', 'autumn', 'winter')) {
    $count = @(Get-ChildItem -LiteralPath (Join-Path $unityTextures $season) -File -Filter '*.png').Count
    $expected = $expectedSeasonTextures[$season]
    if ($count -ne $expected) { throw "Expected $expected $season source textures, found $count." }
}

$runtimeBundle = Join-Path $projectRoot 'Resources\Runtime\AssetBundles\dvseasons_dv99'
if ((Get-Item -LiteralPath $runtimeBundle).Length -lt 1MB) { throw 'Runtime AssetBundle is unexpectedly small.' }

if ($RequireBuildOutput) {
    $output = Join-Path $projectRoot 'artifacts\build\DVSeasons'
    foreach ($relativePath in @('DVSeasons.dll', 'DVSeasons.Core.dll', 'DVSeasons.Multiplayer.dll', 'info.json', 'AssetBundles\dvseasons_dv99', 'Textures\snowflake_realistic.png', 'Textures\snowflake_variations.png', 'Textures\winter_ballast_balanced.png', 'Textures\Seasonal\autumn\SleeperNew_d.png', 'Textures\Seasonal\autumn\SleeperOld_d.png', 'Textures\Seasonal\winter\SleeperNew_d.png', 'Textures\Seasonal\winter\SleeperOld_d.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $relativePath) -PathType Leaf)) {
            throw "Required build output is missing: $relativePath"
        }
    }

    $sourceHash = (Get-FileHash -LiteralPath $runtimeBundle -Algorithm SHA256).Hash
    $outputHash = (Get-FileHash -LiteralPath (Join-Path $output 'AssetBundles\dvseasons_dv99') -Algorithm SHA256).Hash
    if ($sourceHash -ne $outputHash) { throw 'Build output AssetBundle differs from the runtime source.' }
}

Write-Host "DVSeasons project layout verified (version $($metadata.Version))."

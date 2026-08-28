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
foreach ($season in @('spring', 'autumn', 'winter')) {
    $count = @(Get-ChildItem -LiteralPath (Join-Path $unityTextures $season) -File -Filter '*.png').Count
    if ($count -ne 41) { throw "Expected 41 $season source textures, found $count." }
}

$runtimeBundle = Join-Path $projectRoot 'Resources\Runtime\AssetBundles\dvseasons_dv99'
if ((Get-Item -LiteralPath $runtimeBundle).Length -lt 1MB) { throw 'Runtime AssetBundle is unexpectedly small.' }

if ($RequireBuildOutput) {
    $output = Join-Path $projectRoot 'artifacts\build\DVSeasons'
    foreach ($relativePath in @('DVSeasons.dll', 'DVSeasons.Core.dll', 'DVSeasons.Multiplayer.dll', 'info.json', 'AssetBundles\dvseasons_dv99')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $relativePath) -PathType Leaf)) {
            throw "Required build output is missing: $relativePath"
        }
    }

    $sourceHash = (Get-FileHash -LiteralPath $runtimeBundle -Algorithm SHA256).Hash
    $outputHash = (Get-FileHash -LiteralPath (Join-Path $output 'AssetBundles\dvseasons_dv99') -Algorithm SHA256).Hash
    if ($sourceHash -ne $outputHash) { throw 'Build output AssetBundle differs from the runtime source.' }
}

Write-Host "DVSeasons project layout verified (version $($metadata.Version))."

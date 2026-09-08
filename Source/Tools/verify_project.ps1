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
    'Resources\Runtime\Audio\snow_step_1.wav',
    'Resources\Runtime\Audio\snow_step_2.wav',
    'Resources\Runtime\Textures\Seasonal\autumn\SleeperNew_d.png',
    'Resources\Runtime\Textures\Seasonal\autumn\SleeperOld_d.png',
    'Resources\Runtime\Textures\Seasonal\winter\SleeperNew_d.png',
    'Resources\Runtime\Textures\Seasonal\winter\SleeperOld_d.png',
    'Resources\Runtime\Textures\Seasonal\winter\WaterIceNormal.png',
    'Resources\Runtime\Textures\Seasonal\winter\WaterIceAlbedo.png',
    'Resources\Runtime\Textures\Seasonal\winter\SnowSurfaceDense.png',
    'Resources\Runtime\Textures\Seasonal\winter\AsphaltRoad_01d.png',
    'Resources\Runtime\Textures\Seasonal\winter\AsphaltTiling_01d.png',
    'Resources\Runtime\Textures\Seasonal\winter\RoadDetail.png',
    'Resources\Runtime\Textures\Seasonal\winter\Roads_LOD_01d.png',
    'Resources\Runtime\Textures\Seasonal\winter\Sidewalk_01d.png',
    'Resources\Runtime\Textures\Seasonal\winter\SidewalkTiles_01d.png',
    'Resources\Runtime\Textures\Seasonal\winter\MB_concrete_01d.png',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\winter\WaterIceNormal.png',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\winter\WaterIceAlbedo.png',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\winter\SnowSurfaceDense.png',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\winter\MB_concrete_01d.png',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\Shaders\PuddleIceGBuffer.shader',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\Shaders\WaterIceOverlay.shader',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\Shaders\ProceduralSnow.shader',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\Shaders\SnowExposure.shader',
    'DVSeasons.Game\ProceduralSnowController.cs',
    'DVSeasons.Game\SnowVehicleRegistry.cs',
    'DVSeasons.Game\RailSnowTracks.cs',
    'DVSeasons.Game\RailSnowGameSource.cs',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\Shaders\SnowVehicle.shader',
    'DVSeasons.Unity\Assets\Editor\DVSeasonsAssetBundleBuilder.cs',
    'DVSeasons.Unity\ProjectSettings\ProjectVersion.txt',
    'Docs\BUILDING.md',
    'Docs\PROJECT_STRUCTURE.md'
)

$newWinterSurfaces = @(
    'AsphaltTiling_01d_White',
    'MB_concrete_01d_blue',
    'MB_concrete_rough_01d',
    'MB_cobblestone_pavement_01d',
    'MB_rooftile_red_01d',
    'MB_rooftile_brown_01d',
    'MB_roofsheets_rusty_01d',
    'MB_roofsheets_01d_gray',
    'MB_roofsheets_01d_blue',
    'MB_rooftop_cinder_01d'
)
foreach ($name in $newWinterSurfaces) {
    $required += "Resources\Runtime\Textures\Seasonal\winter\$name.png"
    $required += "DVSeasons.Unity\Assets\DVSeasons\DV99\winter\$name.png"
}

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
$expectedSeasonTextures = @{ spring = 41; autumn = 43; winter = 63 }
foreach ($season in @('spring', 'autumn', 'winter')) {
    $count = @(Get-ChildItem -LiteralPath (Join-Path $unityTextures $season) -File -Filter '*.png').Count
    $expected = $expectedSeasonTextures[$season]
    if ($count -ne $expected) { throw "Expected $expected $season source textures, found $count." }
}

$winterTrackTextureNames = @(
    'RailMed_d.png',
    'RailOld_d.png',
    'BallastNew_d.png',
    'BallastMed_d.png',
    'BallastLODMed.png',
    'BallastOld_d.png',
    'SleeperNew_d.png',
    'SleeperOld_d.png'
)
foreach ($stage in @('early', 'middle', 'late')) {
    $runtimeStage = Join-Path $projectRoot "Resources\Runtime\Textures\Seasonal\winter_track\$stage"
    $unityStage = Join-Path $unityTextures "winter_track\$stage"
    foreach ($textureName in $winterTrackTextureNames) {
        foreach ($path in @((Join-Path $runtimeStage $textureName),
                (Join-Path $unityStage $textureName))) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Required staged track texture is missing: $path"
            }
        }
    }
}

$unityTextureCount = @(Get-ChildItem -LiteralPath $unityTextures -File -Recurse -Filter '*.png').Count
if ($unityTextureCount -ne 171) {
    throw "Expected 171 total Unity source textures, found $unityTextureCount."
}

$runtimeBundle = Join-Path $projectRoot 'Resources\Runtime\AssetBundles\dvseasons_dv99'
if ((Get-Item -LiteralPath $runtimeBundle).Length -lt 1MB) { throw 'Runtime AssetBundle is unexpectedly small.' }

if ($RequireBuildOutput) {
    $output = Join-Path $projectRoot 'artifacts\build\DVSeasons'
    foreach ($relativePath in @('DVSeasons.dll', 'DVSeasons.Core.dll', 'DVSeasons.Multiplayer.dll', 'info.json', 'AssetBundles\dvseasons_dv99', 'Audio\snow_step_1.wav', 'Audio\snow_step_2.wav', 'Textures\snowflake_realistic.png', 'Textures\snowflake_variations.png', 'Textures\winter_ballast_balanced.png', 'Textures\Seasonal\autumn\SleeperNew_d.png', 'Textures\Seasonal\autumn\SleeperOld_d.png', 'Textures\Seasonal\winter\SleeperNew_d.png', 'Textures\Seasonal\winter\SleeperOld_d.png', 'Textures\Seasonal\winter\WaterIceNormal.png', 'Textures\Seasonal\winter\WaterIceAlbedo.png', 'Textures\Seasonal\winter\AsphaltRoad_01d.png', 'Textures\Seasonal\winter\AsphaltTiling_01d.png', 'Textures\Seasonal\winter\RoadDetail.png', 'Textures\Seasonal\winter\Roads_LOD_01d.png', 'Textures\Seasonal\winter\Sidewalk_01d.png', 'Textures\Seasonal\winter\SidewalkTiles_01d.png', 'Textures\Seasonal\winter\MB_concrete_01d.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $relativePath) -PathType Leaf)) {
            throw "Required build output is missing: $relativePath"
        }
    }

    foreach ($stage in @('early', 'middle', 'late')) {
        foreach ($textureName in $winterTrackTextureNames) {
            $relativePath = "Textures\Seasonal\winter_track\$stage\$textureName"
            if (-not (Test-Path -LiteralPath (Join-Path $output $relativePath) -PathType Leaf)) {
                throw "Required build output is missing: $relativePath"
            }
        }
    }

    $sourceHash = (Get-FileHash -LiteralPath $runtimeBundle -Algorithm SHA256).Hash
    $runtimeRoot = Join-Path $projectRoot 'Resources\Runtime'
    foreach ($texture in Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'Textures') -File -Recurse -Filter '*.png') {
        $relative = $texture.FullName.Substring($runtimeRoot.Length + 1)
        $built = Join-Path $output $relative
        if (-not (Test-Path -LiteralPath $built) -or
            (Get-FileHash -LiteralPath $texture.FullName).Hash -ne (Get-FileHash -LiteralPath $built).Hash) {
            throw "Stale or missing output texture (check PreserveNewest after archive restore): $relative"
        }
    }
    $outputHash = (Get-FileHash -LiteralPath (Join-Path $output 'AssetBundles\dvseasons_dv99') -Algorithm SHA256).Hash
    if ($sourceHash -ne $outputHash) { throw 'Build output AssetBundle differs from the runtime source.' }
}

Write-Host "DVSeasons project layout verified (version $($metadata.Version))."

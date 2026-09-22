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
    'Resources\Runtime\AssetBundles\dvseasons_winter',
    'Resources\Runtime\AssetBundles\dvseasons_tracks',
    'Resources\Runtime\Overrides\README.txt',
    'Resources\Runtime\Textures\snowflake_realistic.png',
    'Resources\Runtime\Textures\snowflake_variations.png',
    'Resources\Runtime\Textures\winter_ballast_balanced.png',
    'Resources\Runtime\Audio\snow_step_1.wav',
    'Resources\Runtime\Audio\snow_step_2.wav',
    'Resources\Runtime\Audio\spring_bees.wav',
    'Resources\Runtime\Audio\Blizzard\early_ru.ogg',
    'Resources\Runtime\Audio\Blizzard\early_en.ogg',
    'Resources\Runtime\Audio\Blizzard\hour_ru.ogg',
    'Resources\Runtime\Audio\Blizzard\hour_en.ogg',
    'Resources\Runtime\Audio\Blizzard\ending_ru.ogg',
    'Resources\Runtime\Audio\Blizzard\ending_en.ogg',
    'Resources\Runtime\Audio\Blizzard\wind.mp3',
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
    'DVSeasons.Common\AutumnEffectsProfile.cs',
    'DVSeasons.Common\SeasonalThermalProfile.cs',
    'DVSeasons.Game\AutumnLeafParticleTexture.cs',
    'DVSeasons.Game\AutumnLeafGroundController.cs',
    'DVSeasons.Game\AutumnTreeSourceProvider.cs',
    'DVSeasons.Game\SeasonalThermalController.cs',
    'DVSeasons.Tests\SeasonalThermalProfileTests.cs',
    'DVSeasons.Game\SnowFootstepAudioController.cs',
    'DVSeasons.Game\StreamingTextureReadiness.cs',
    'DVSeasons.Unity\Assets\DVSeasons\DV99\Shaders\SnowVehicle.shader',
    'DVSeasons.Unity\Assets\Editor\DVSeasonsAssetBundleBuilder.cs',
    'DVSeasons.Unity\Assets\Editor\StreamingTextureReadinessVerification.cs',
    'DVSeasons.Unity\ProjectSettings\ProjectVersion.txt',
    'Docs\BUILDING.md',
    'Docs\PROJECT_STRUCTURE.md',
    'Docs\SEASONAL_THERMAL_PHYSICS.md'
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

$visualControllerSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\SeasonVisualController.cs') -Raw
if ($visualControllerSource -match 'startupNotBefore|Seasonal visuals deferred for') {
    throw 'SeasonVisualController still contains a fixed startup delay for streamed textures.'
}
$leafCoverSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\AutumnLeafGroundController.cs') -Raw
if ($leafCoverSource -notmatch 'GetTreeInstance' -or $leafCoverSource -match '\.treeInstances') {
    throw 'Autumn leaf cover must use bounded point probes without copying the full Terrain tree array.'
}
$treeSourceProvider = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\AutumnTreeSourceProvider.cs') -Raw
foreach ($safeVspMarker in @('OnRenderCompleteDelegate', 'Prepared', 'LoadedDistanceBand == 99',
    'LoadStateList.IsCreated', 'matrixList.IsCreated', 'FloatingOriginOffset')) {
    if ($treeSourceProvider -notmatch [Regex]::Escape($safeVspMarker)) {
        throw "Vegetation Studio tree source is missing safe-read marker '$safeVspMarker'."
    }
}
if ($treeSourceProvider -match 'CompleteCellLoading|GetVegetationItemInstances|OriginShift\.currentMove') {
    throw 'Vegetation Studio tree source must not force job completion or apply DV origin shift twice.'
}
$gameProjectSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\DVSeasons.Game.csproj') -Raw
foreach ($vspReference in @('AwesomeTechnologies.VegetationStudioPro.Runtime', 'Unity.Collections')) {
    if ($gameProjectSource -notmatch [Regex]::Escape($vspReference)) {
        throw "Game project is missing Vegetation Studio dependency '$vspReference'."
    }
}
$readinessSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\StreamingTextureReadiness.cs') -Raw
foreach ($requiredApi in @('requestedMipmapLevel', 'IsRequestedMipmapLevelLoaded', 'loadedMipmapLevel')) {
    if ($readinessSource -notmatch [Regex]::Escape($requiredApi)) {
        throw "Streaming texture readiness guard does not use Unity API '$requiredApi'."
    }
}
$autumnProfileSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Common\AutumnEffectsProfile.cs') -Raw
foreach ($requiredCurve in @('WetnessEquivalent = 0.005f', 'GetLeafEmissionRate',
    'GetHiddenLeafIngressRate', 'IsHiddenLeafIngressSource', 'GetTrainWakeStrength')) {
    if ($autumnProfileSource -notmatch [Regex]::Escape($requiredCurve)) {
        throw "Autumn effects profile is missing '$requiredCurve'."
    }
}
$forbiddenLeafFallback = 'if (!exactCanopy) canopyPosition'
if ($visualControllerSource -match [Regex]::Escape($forbiddenLeafFallback) -or
    $visualControllerSource -match 'TrainLeafReactionController') {
    throw 'The obsolete sky fallback or train-tail leaf emitter is still connected.'
}
foreach ($requiredBehavior in @('InitialCapacity = 2200', 'EnsureCapacity', 'int.MaxValue',
    'MaximumHiddenWindSpawnsPerFrame = 2', 'MaximumSources = 96',
    'InitialCoverCreditPerSource = 10f', 'MaximumCoverGrowthPerSecond = 72f',
    'surfaceQueriesRemaining = 10', 'PopulationRaycastsPerFrame = 5',
    'GetTreeInstance', 'Physics.Raycast', 'Physics.SphereCast',
    'OriginShift.currentMove', 'GetTrainWakeStrength', 'SetParticles', 'ParticleSystemRenderMode.Mesh',
    'EnsureTreeSourceProvider', 'treeSourceProvider = null',
    'GroundLitterSpreadRadius = 11f', 'SurfaceLocalPosition', 'InverseTransformPoint',
    'UpdateAnchoredLeaves(worldOffset, deltaTime)', 'ClearSurfaceAnchor', 'SurfaceVelocity',
    'TryGetRollingStockSurfaceProbe', 'EmitHiddenWindIngress', 'WorldToViewportPoint',
    'renderer.maxParticleSize = 0.5f', 'mesh.UploadMeshData(false)')) {
    if ($leafCoverSource -notmatch [Regex]::Escape($requiredBehavior)) {
        throw "Physical autumn leaf cover is missing '$requiredBehavior'."
    }
}
if ($leafCoverSource -match [Regex]::Escape('mesh.UploadMeshData(true)')) {
    throw 'The autumn leaf mesh must remain CPU-readable for ParticleSystemRenderer.'
}
$anchorUpdateIndex = $leafCoverSource.IndexOf('UpdateAnchoredLeaves(worldOffset, deltaTime)',
    [StringComparison]::Ordinal)
$leafPruneIndex = $leafCoverSource.IndexOf('PruneDistantLeaves(cameraPosition)',
    [StringComparison]::Ordinal)
if ($anchorUpdateIndex -lt 0 -or $leafPruneIndex -lt 0 -or $anchorUpdateIndex -gt $leafPruneIndex) {
    throw 'Moving leaf surfaces must be updated before distance pruning.'
}
$ordinaryLeafIndex = $leafCoverSource.IndexOf('EmitFallingLeaves(state, cameraPosition',
    [StringComparison]::Ordinal)
$hiddenLeafIndex = $leafCoverSource.IndexOf('EmitHiddenWindIngress(state, camera',
    [StringComparison]::Ordinal)
$simulateLeafIndex = $leafCoverSource.IndexOf('SimulateLeaves(weight, cameraPosition',
    [StringComparison]::Ordinal)
if ($ordinaryLeafIndex -lt 0 -or $hiddenLeafIndex -lt 0 -or $simulateLeafIndex -lt 0 -or
    $ordinaryLeafIndex -gt $hiddenLeafIndex -or $simulateLeafIndex -gt $ordinaryLeafIndex) {
    throw 'Surface checks for airborne leaves must precede emission; hidden wind ingress uses the budget remaining after ordinary emission.'
}
if ($leafCoverSource -match 'if\s*\(car\s*!=\s*null\)\s*continue') {
    throw 'Train surfaces are still excluded from autumn leaf placement.'
}
$leafMeshDepths = [Regex]::Matches($leafCoverSource,
    'new Vector3\([^\r\n]*,\s*(0\.\d+)f\)') | ForEach-Object {
        [double]$_.Groups[1].Value
    }
if (@($leafMeshDepths | Where-Object { $_ -ge 0.075 }).Count -lt 3) {
    throw 'Autumn leaf mesh is missing its curved centre ridge.'
}
$thermalSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\SeasonalThermalController.cs') -Raw
foreach ($thermalMarker in @('PassiveCooler', 'ActiveCooler', 'AutomaticCooler',
    'DirectionalMovementCooler', 'HeatReservoir', 'SimulateSteamConsumption',
    'ComputeTemperature', 'UpdateTemperature', 'UnpatchAll(HarmonyId)')) {
    if ($thermalSource -notmatch [Regex]::Escape($thermalMarker)) {
        throw "Seasonal locomotive thermal physics is missing '$thermalMarker'."
    }
}
$runtimeSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\SeasonRuntime.cs') -Raw
foreach ($thermalLifecycle in @('thermal.Apply(currentState.TemperatureCelsius, HasLocalAuthority())', 'thermal.Reset()',
    'thermal.Dispose()')) {
    if ($runtimeSource -notmatch [Regex]::Escape($thermalLifecycle)) {
        throw "Season runtime is missing thermal lifecycle call '$thermalLifecycle'."
    }
}
$footstepSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\SnowFootstepAudioController.cs') -Raw
foreach ($requiredCabCheck in @('TrainCar.Resolve', 'cabTeleportDestination', 'ClosestPoint', 'HasLowShelter')) {
    if ($footstepSource -notmatch [Regex]::Escape($requiredCabCheck)) {
        throw "Snow footstep cab exclusion is missing '$requiredCabCheck'."
    }
}
if ($footstepSource -match 'player\.IsChildOf\(candidate\)|candidate\.root\s*==\s*playerRoot') {
    throw 'Snow footstep shelter still discards cab geometry after player reparenting.'
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
$expectedSeasonTextures = @{ spring = 41; autumn = 43; winter = 65 }
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
if ($unityTextureCount -ne 173) {
    throw "Expected 173 total Unity source textures, found $unityTextureCount."
}

$runtimeBundle = Join-Path $projectRoot 'Resources\Runtime\AssetBundles\dvseasons_dv99'
if ((Get-Item -LiteralPath $runtimeBundle).Length -lt 1MB) { throw 'Runtime AssetBundle is unexpectedly small.' }

if ($RequireBuildOutput) {
    $output = Join-Path $projectRoot 'artifacts\build\DVSeasons'
    foreach ($relativePath in @('DVSeasons.dll', 'DVSeasons.Core.dll', 'DVSeasons.Multiplayer.dll', 'info.json', 'AssetBundles\dvseasons_dv99', 'AssetBundles\dvseasons_winter', 'AssetBundles\dvseasons_tracks', 'Overrides\README.txt', 'Audio\snow_step_1.wav', 'Audio\snow_step_2.wav', 'Textures\snowflake_realistic.png', 'Textures\snowflake_variations.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $relativePath) -PathType Leaf)) {
            throw "Required build output is missing: $relativePath"
        }
    }

    $sourceHash = (Get-FileHash -LiteralPath $runtimeBundle -Algorithm SHA256).Hash
    $runtimeRoot = Join-Path $projectRoot 'Resources\Runtime'
    foreach ($texture in Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'Textures') -File -Filter 'snowflake*.png') {
        $relative = $texture.FullName.Substring($runtimeRoot.Length + 1)
        $built = Join-Path $output $relative
        if (-not (Test-Path -LiteralPath $built) -or
            (Get-FileHash -LiteralPath $texture.FullName).Hash -ne (Get-FileHash -LiteralPath $built).Hash) {
            throw "Stale or missing output texture (check PreserveNewest after archive restore): $relative"
        }
    }
    foreach ($bundleName in @('dvseasons_dv99', 'dvseasons_winter', 'dvseasons_tracks')) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $runtimeRoot "AssetBundles/$bundleName") -Algorithm SHA256).Hash
        $outputHash = (Get-FileHash -LiteralPath (Join-Path $output "AssetBundles/$bundleName") -Algorithm SHA256).Hash
        if ($sourceHash -ne $outputHash) { throw "Build output AssetBundle differs from the runtime source: $bundleName" }
    }
    if (Test-Path -LiteralPath (Join-Path $output 'Textures/Seasonal')) {
        throw 'Build output must use bundled seasonal textures rather than distribution PNGs.'
    }
}

Write-Host "DVSeasons project layout verified (version $($metadata.Version))."

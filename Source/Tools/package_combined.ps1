[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must use numeric SemVer format.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$compressionLevelNames = [Enum]::GetNames([IO.Compression.CompressionLevel])
$releaseCompression = if ($compressionLevelNames -contains 'SmallestSize') {
    [IO.Compression.CompressionLevel]::SmallestSize
}
else {
    # Windows PowerShell 5.1/.NET Framework does not expose SmallestSize.
    [IO.Compression.CompressionLevel]::Optimal
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$modRoot = Join-Path $projectRoot 'artifacts\build\DVSeasons'
$packages = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\releases\packages' }
$nexusArchivePath = Join-Path $packages ("DVSeasons-{0}-Nexus.zip" -f $Version)
$githubArchivePath = Join-Path $packages ("DVSeasons-{0}-GitHub.zip" -f $Version)
$githubUploadLimitBytes = 100000000
$metadata = Get-Content -LiteralPath (Join-Path $modRoot 'info.json') -Raw | ConvertFrom-Json
if ($metadata.Version -ne $Version) {
    throw "Requested version $Version does not match build version $($metadata.Version)."
}

$runtimeAliasPaths = @(
    'Textures/Seasonal/autumn/SleeperOld_d.png',
    'Textures/Seasonal/winter_track/early/SleeperOld_d.png',
    'Textures/Seasonal/winter_track/middle/SleeperOld_d.png',
    'Textures/Seasonal/winter_track/late/SleeperNew_d.png',
    'Textures/Seasonal/winter_track/late/SleeperOld_d.png',
    'Textures/Seasonal/winter/SleeperOld_d.png',
    'Textures/Seasonal/winter/AsphaltTiling_01d_White.png',
    'Textures/Seasonal/winter/MB_concrete_01d_blue.png'
)
$runtimeFiles = @(Get-ChildItem -LiteralPath $modRoot -Recurse -File | Where-Object {
    if ($_.Extension -eq '.pdb') {
        return $false
    }
    $relative = $_.FullName.Substring($modRoot.Length + 1).Replace('\', '/')
    return $runtimeAliasPaths -notcontains $relative
})
$sourceInputs = @(
    '.gitignore',
    'Directory.Build.props',
    'DVSeasons.sln',
    'global.json',
    'README.md',
    'SOURCE_PACKAGE.md',
    'UNIVERSAL_ARCHIVE.md',
    'Docs',
    'DVSeasons.Common',
    'DVSeasons.Game',
    'DVSeasons.MP',
    'DVSeasons.Tests',
    'DVSeasons.Unity',
    'Resources\Reference',
    'Tools'
)
$blockedSourceExtensions = @(
    '.dll', '.exe', '.pdb', '.zip', '.7z', '.rar'
)
$sourceFiles = @(
    foreach ($input in $sourceInputs) {
        $path = Join-Path $projectRoot $input
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Get-Item -LiteralPath $path
        }
        elseif (Test-Path -LiteralPath $path -PathType Container) {
            Get-ChildItem -LiteralPath $path -Recurse -File
        }
        else {
            throw "Source input is missing: $input"
        }
    }
) | Where-Object {
    $relative = $_.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
    $extension = [IO.Path]::GetExtension($_.Name).ToLowerInvariant()
    $relative -notmatch '(^|/)(bin|obj|Library|Logs|Temp|UserSettings|Build)/' -and
    $relative -notmatch '^DVSeasons\.Unity/Assets/DVSeasons/DV99/Generated/' -and
    $blockedSourceExtensions -notcontains $extension
} | Sort-Object FullName -Unique

$requiredRuntimeEntries = @(
    'DVSeasons/info.json',
    'DVSeasons/DVSeasons.dll',
    'DVSeasons/DVSeasons.Core.dll',
    'DVSeasons/DVSeasons.Multiplayer.dll',
    'DVSeasons/AssetBundles/dvseasons_dv99',
    'DVSeasons/Audio/snow_step_1.wav',
    'DVSeasons/Audio/snow_step_2.wav',
    'DVSeasons/Textures/Seasonal/winter_track/early/RailMed_d.png',
    'DVSeasons/Textures/Seasonal/winter_track/middle/BallastMed_d.png',
    'DVSeasons/Textures/Seasonal/winter_track/late/BallastOld_d.png',
    'DVSeasons/Textures/Seasonal/winter/WaterIceNormal.png',
    'DVSeasons/Textures/Seasonal/winter/WaterIceAlbedo.png',
    'DVSeasons/Textures/Seasonal/winter/SnowSurfaceDense.png',
    'DVSeasons/Textures/Seasonal/winter/AsphaltRoad_01d.png',
    'DVSeasons/Textures/Seasonal/winter/AsphaltTiling_01d.png',
    'DVSeasons/Textures/Seasonal/winter/RoadDetail.png',
    'DVSeasons/Textures/Seasonal/winter/Roads_LOD_01d.png',
    'DVSeasons/Textures/Seasonal/winter/Sidewalk_01d.png',
    'DVSeasons/Textures/Seasonal/winter/SidewalkTiles_01d.png',
    'DVSeasons/Textures/Seasonal/winter/MB_concrete_01d.png',
    'DVSeasons/Textures/Seasonal/winter/MB_concrete_rough_01d.png',
    'DVSeasons/Textures/Seasonal/winter/MB_cobblestone_pavement_01d.png',
    'DVSeasons/Textures/Seasonal/winter/MB_rooftile_red_01d.png',
    'DVSeasons/Textures/Seasonal/winter/MB_rooftile_brown_01d.png',
    'DVSeasons/Textures/Seasonal/winter/MB_roofsheets_rusty_01d.png',
    'DVSeasons/Textures/Seasonal/winter/MB_roofsheets_01d_gray.png',
    'DVSeasons/Textures/Seasonal/winter/MB_roofsheets_01d_blue.png',
    'DVSeasons/Textures/Seasonal/winter/MB_rooftop_cinder_01d.png'
)
$requiredSourceEntries = @(
    'DVSeasons/Source/DVSeasons.sln',
    'DVSeasons/Source/DVSeasons.Common/WinterTrackTextureProfile.cs',
    'DVSeasons/Source/DVSeasons.Game/SeasonalTextureController.cs',
    'DVSeasons/Source/DVSeasons.Game/WaterIceController.cs',
    'DVSeasons/Source/DVSeasons.Game/ProceduralSnowController.cs',
    'DVSeasons/Source/DVSeasons.Game/SnowVehicleRegistry.cs',
    'DVSeasons/Source/DVSeasons.Game/RailSnowTracks.cs',
    'DVSeasons/Source/DVSeasons.Game/RailSnowGameSource.cs',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader',
    'DVSeasons/Source/DVSeasons.Game/WinterPuddleController.cs',
    'DVSeasons/Source/DVSeasons.Game/MicroSplatSeasonalTerrainController.cs',
    'DVSeasons/Source/DVSeasons.Unity/Assets/Editor/DVSeasonsAssetBundleBuilder.cs',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/winter/WaterIceNormal.png',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/winter/WaterIceAlbedo.png',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/winter/SnowSurfaceDense.png',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/Shaders/PuddleIceGBuffer.shader',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/Shaders/WaterIceOverlay.shader',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/Shaders/ProceduralSnow.shader',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/Shaders/SnowExposure.shader',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/winter/AsphaltRoad_01d.png',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/winter/SidewalkTiles_01d.png',
    'DVSeasons/Source/DVSeasons.Unity/Assets/DVSeasons/DV99/winter/MB_concrete_01d.png',
    'DVSeasons/Source/Tools/build.ps1',
    'DVSeasons/Source/Tools/generate_ice_normal.py',
    'DVSeasons/Source/Tools/prepare_winter_surface_textures.py',
    'DVSeasons/Source/Tools/repack_unity_bundle.py',
    'DVSeasons/Source/Tools/prepare_staged_track_textures.py',
    'DVSeasons/Source/SOURCE_PACKAGE.md'
)

function Assert-ReleaseEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$EntryNames,
        [Parameter(Mandatory = $true)]
        [bool]$IncludeSource,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedFileEntries
    )

    $missingFiles = @($ExpectedFileEntries | Where-Object { $EntryNames -notcontains $_ })
    if ($missingFiles.Count -ne 0) {
        throw "Release archive omitted expected files: $($missingFiles -join ', ')."
    }

    $requiredEntries = @($requiredRuntimeEntries)
    if ($IncludeSource) {
        $requiredEntries += $requiredSourceEntries
    }
    foreach ($entryName in $requiredEntries) {
        if ($EntryNames -notcontains $entryName) {
            throw "Release archive is missing $entryName."
        }
    }

    $descriptors = @($EntryNames | Where-Object { ($_ -split '/')[-1] -ieq 'info.json' })
    if ($descriptors.Count -ne 1) {
        throw "Release archive must contain exactly one UMM info.json; found $($descriptors.Count)."
    }
    $nestedArchives = @($EntryNames | Where-Object { $_ -match '\.(zip|7z|rar)$' })
    if ($nestedArchives.Count -ne 0) {
        throw "Nested archives are not allowed: $($nestedArchives -join ', ')."
    }
    $blockedFiles = @($EntryNames | Where-Object {
        $_ -match '(^|/)(bin|obj|Library|Logs|Temp|UserSettings|Build)/' -or
        $_ -match '\.(pdb|exe)$' -or
        $_ -match '\.dll\..*\.cache(?:\.pdb)?$'
    })
    if ($blockedFiles.Count -ne 0) {
        throw "Release archive contains blocked build or script files: $($blockedFiles -join ', ')."
    }

    $bundles = @($EntryNames | Where-Object { ($_ -split '/')[-1] -ieq 'dvseasons_dv99' })
    if ($bundles.Count -ne 1) {
        throw "Release archive must contain exactly one AssetBundle; found $($bundles.Count)."
    }
    $stagedTrackTextures = @($EntryNames | Where-Object {
        $_ -match '^DVSeasons/Textures/Seasonal/winter_track/(early|middle|late)/[^/]+\.png$'
    })
    if ($stagedTrackTextures.Count -ne 20) {
        throw "Release archive must contain 20 physical staged track textures plus four loader aliases; found $($stagedTrackTextures.Count)."
    }
    $packagedAliases = @($runtimeAliasPaths | ForEach-Object { "DVSeasons/$_" } | Where-Object {
        $EntryNames -contains $_
    })
    if ($packagedAliases.Count -ne 0) {
        throw "Release archive contains redundant runtime aliases: $($packagedAliases -join ', ')."
    }
    $runtimeDlls = @($EntryNames | Where-Object { $_ -match '^DVSeasons/[^/]+\.dll$' })
    $sourceDlls = @($EntryNames | Where-Object { $_ -match '^DVSeasons/Source/.*\.dll$' })
    if ($runtimeDlls.Count -ne 3 -or $sourceDlls.Count -ne 0) {
        throw 'Release archive must contain three runtime DLLs and no compiled DLLs in Source.'
    }

    $sourceEntries = @($EntryNames | Where-Object { $_ -match '^DVSeasons/Source/' })
    if ($IncludeSource -and $sourceEntries.Count -eq 0) {
        throw 'GitHub archive does not contain source files.'
    }
    if (-not $IncludeSource -and $sourceEntries.Count -ne 0) {
        throw 'Nexus archive unexpectedly contains source files.'
    }
}

function Assert-InputFileSizes {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)]
        [string]$InputSet
    )

    $oversized = @($Files | Where-Object { $_.Length -ge $githubUploadLimitBytes })
    if ($oversized.Count -ne 0) {
        $details = @($oversized | ForEach-Object { "$($_.FullName) ($($_.Length) bytes)" })
        throw "$InputSet contains files that reach or exceed GitHub's 100,000,000-byte file limit: $($details -join ', ')."
    }
}

function New-ReleaseArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [bool]$IncludeSource
    )

    if (Test-Path -LiteralPath $ArchivePath) {
        [IO.File]::Delete($ArchivePath)
    }

    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive(
        $stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in $runtimeFiles) {
            $relative = $file.FullName.Substring($modRoot.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, "DVSeasons/$relative",
                $releaseCompression) | Out-Null
        }
        if ($IncludeSource) {
            foreach ($file in $sourceFiles) {
                $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $file.FullName, "DVSeasons/Source/$relative",
                    $releaseCompression) | Out-Null
            }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }

    $check = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $oversizedEntries = @($check.Entries | Where-Object { $_.Length -ge $githubUploadLimitBytes })
        if ($oversizedEntries.Count -ne 0) {
            $details = @($oversizedEntries | ForEach-Object { "$($_.FullName) ($($_.Length) bytes)" })
            throw "Release ZIP contains entries that reach or exceed GitHub's 100,000,000-byte file limit: $($details -join ', ')."
        }

        $expectedFiles = @(
            $runtimeFiles | ForEach-Object {
                $relative = $_.FullName.Substring($modRoot.Length + 1).Replace('\', '/')
                "DVSeasons/$relative"
            }
            if ($IncludeSource) {
                $sourceFiles | ForEach-Object {
                    $relative = $_.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
                    "DVSeasons/Source/$relative"
                }
            }
        )
        Assert-ReleaseEntries `
            -EntryNames @($check.Entries | ForEach-Object FullName) `
            -IncludeSource $IncludeSource `
            -ExpectedFileEntries $expectedFiles
    }
    finally {
        $check.Dispose()
    }
}

function Remove-ObsoleteGitHubPackageFiles {
    $packageRoot = [IO.Path]::GetFullPath($packages).TrimEnd('\', '/')
    $namePrefix = "DVSeasons-$Version-GitHub"
    $obsoleteFiles = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object {
        $_.Name -in @("$namePrefix.sha256", "$namePrefix-README.txt") -or
        $_.Name -match ("^{0}\.wim(?:\.\d{{3}})?$" -f [Regex]::Escape($namePrefix))
    })

    foreach ($file in $obsoleteFiles) {
        $resolvedPath = [IO.Path]::GetFullPath($file.FullName)
        $resolvedDirectory = [IO.Path]::GetDirectoryName($resolvedPath).TrimEnd('\', '/')
        if (-not [string]::Equals(
            $resolvedDirectory, $packageRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove obsolete package outside the output directory: $resolvedPath"
        }
        Remove-Item -LiteralPath $resolvedPath -Force
    }
}

Assert-InputFileSizes -Files $runtimeFiles -InputSet 'Runtime input'
Assert-InputFileSizes -Files $sourceFiles -InputSet 'Source input'

New-Item -ItemType Directory -Path $packages -Force | Out-Null
New-ReleaseArchive -ArchivePath $nexusArchivePath -IncludeSource $false
New-ReleaseArchive -ArchivePath $githubArchivePath -IncludeSource $true
Remove-ObsoleteGitHubPackageFiles

Get-FileHash -LiteralPath $nexusArchivePath, $githubArchivePath -Algorithm SHA256

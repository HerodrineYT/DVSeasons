[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must use numeric SemVer format.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = Split-Path -Parent $PSScriptRoot
$modRoot = Join-Path $projectRoot 'artifacts\build\DVSeasons'
$packages = Join-Path $projectRoot 'artifacts\releases\packages'
$nexusArchivePath = Join-Path $packages ("DVSeasons-{0}-Nexus.zip" -f $Version)
$githubArchivePath = Join-Path $packages ("DVSeasons-{0}-GitHub.zip" -f $Version)
$metadata = Get-Content -LiteralPath (Join-Path $modRoot 'info.json') -Raw | ConvertFrom-Json
if ($metadata.Version -ne $Version) {
    throw "Requested version $Version does not match build version $($metadata.Version)."
}

$runtimeFiles = @(Get-ChildItem -LiteralPath $modRoot -Recurse -File |
    Where-Object Extension -ne '.pdb')
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
    'DVSeasons/AssetBundles/dvseasons_dv99'
)
$requiredSourceEntries = @(
    'DVSeasons/Source/DVSeasons.sln',
    'DVSeasons/Source/DVSeasons.Game/SeasonalTextureController.cs',
    'DVSeasons/Source/DVSeasons.Unity/Assets/Editor/DVSeasonsAssetBundleBuilder.cs',
    'DVSeasons/Source/Tools/build.ps1',
    'DVSeasons/Source/Tools/repack_unity_bundle.py',
    'DVSeasons/Source/SOURCE_PACKAGE.md'
)

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
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        if ($IncludeSource) {
            foreach ($file in $sourceFiles) {
                $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $file.FullName, "DVSeasons/Source/$relative",
                    [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }

    $check = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entryNames = @($check.Entries | ForEach-Object FullName)
        $requiredEntries = @($requiredRuntimeEntries)
        if ($IncludeSource) {
            $requiredEntries += $requiredSourceEntries
        }
        foreach ($entryName in $requiredEntries) {
            if ($entryNames -notcontains $entryName) {
                throw "Release archive is missing $entryName."
            }
        }

        $descriptors = @($entryNames | Where-Object { ($_ -split '/')[-1] -ieq 'info.json' })
        if ($descriptors.Count -ne 1) {
            throw "Release archive must contain exactly one UMM info.json; found $($descriptors.Count)."
        }
        $nestedArchives = @($entryNames | Where-Object { $_ -match '\.(zip|7z|rar)$' })
        if ($nestedArchives.Count -ne 0) {
            throw "Nested archives are not allowed: $($nestedArchives -join ', ')."
        }
        $blockedFiles = @($entryNames | Where-Object {
            $_ -match '(^|/)(bin|obj|Library|Logs|Temp|UserSettings|Build)/' -or
            $_ -match '\.(pdb|exe)$' -or
            $_ -match '\.dll\..*\.cache(?:\.pdb)?$'
        })
        if ($blockedFiles.Count -ne 0) {
            throw "Release archive contains blocked build or script files: $($blockedFiles -join ', ')."
        }

        $bundles = @($entryNames | Where-Object { ($_ -split '/')[-1] -ieq 'dvseasons_dv99' })
        if ($bundles.Count -ne 1) {
            throw "Release archive must contain exactly one AssetBundle; found $($bundles.Count)."
        }
        $runtimeDlls = @($entryNames | Where-Object { $_ -match '^DVSeasons/[^/]+\.dll$' })
        $sourceDlls = @($entryNames | Where-Object { $_ -match '^DVSeasons/Source/.*\.dll$' })
        if ($runtimeDlls.Count -ne 3 -or $sourceDlls.Count -ne 0) {
            throw 'Release archive must contain three runtime DLLs and no compiled DLLs in Source.'
        }

        $sourceEntries = @($entryNames | Where-Object { $_ -match '^DVSeasons/Source/' })
        if ($IncludeSource -and $sourceEntries.Count -eq 0) {
            throw 'GitHub archive does not contain source files.'
        }
        if (-not $IncludeSource -and $sourceEntries.Count -ne 0) {
            throw 'Nexus archive unexpectedly contains source files.'
        }
    }
    finally {
        $check.Dispose()
    }
}

New-Item -ItemType Directory -Path $packages -Force | Out-Null
New-ReleaseArchive -ArchivePath $nexusArchivePath -IncludeSource $false
New-ReleaseArchive -ArchivePath $githubArchivePath -IncludeSource $true

Get-FileHash -LiteralPath $nexusArchivePath, $githubArchivePath -Algorithm SHA256

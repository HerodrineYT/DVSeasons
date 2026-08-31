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
$archivePath = Join-Path $packages ("DVSeasons-{0}.zip" -f $Version)
$metadata = Get-Content -LiteralPath (Join-Path $modRoot 'info.json') -Raw | ConvertFrom-Json
if ($metadata.Version -ne $Version) {
    throw "Requested version $Version does not match build version $($metadata.Version)."
}

$sourceFiles = @(Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
    $relative -notmatch '^(artifacts|\.tools|\.vs)/' -and
    $relative -notmatch '(^|/)(bin|obj|Library|Logs|Temp|UserSettings|Build)/' -and
    $relative -notmatch '^DVSeasons\.Unity/Assets/DVSeasons/DV99/Generated/'
})

New-Item -ItemType Directory -Path $packages -Force | Out-Null
if (Test-Path -LiteralPath $archivePath) {
    [IO.File]::Delete($archivePath)
}

$stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew)
$archive = New-Object IO.Compression.ZipArchive(
    $stream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($file in Get-ChildItem -LiteralPath $modRoot -Recurse -File |
        Where-Object Extension -ne '.pdb') {
        $relative = $file.FullName.Substring($modRoot.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, "DVSeasons/$relative",
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    foreach ($file in $sourceFiles) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, "DVSeasons/Source/$relative",
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}

$requiredEntries = @(
    'DVSeasons/info.json',
    'DVSeasons/DVSeasons.dll',
    'DVSeasons/DVSeasons.Core.dll',
    'DVSeasons/DVSeasons.Multiplayer.dll',
    'DVSeasons/AssetBundles/dvseasons_dv99',
    'DVSeasons/Source/DVSeasons.sln',
    'DVSeasons/Source/DVSeasons.Game/SeasonalTextureController.cs'
)
$check = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entryNames = @($check.Entries | ForEach-Object FullName)
    foreach ($entryName in $requiredEntries) {
        if ($entryNames -notcontains $entryName) {
            throw "Combined archive is missing $entryName."
        }
    }
    $descriptors = @($entryNames | Where-Object { ($_ -split '/')[-1] -ieq 'info.json' })
    if ($descriptors.Count -ne 1) {
        throw "Combined archive must contain exactly one UMM info.json; found $($descriptors.Count)."
    }
    $nestedArchives = @($entryNames | Where-Object { $_ -match '\.(zip|7z|rar)$' })
    if ($nestedArchives.Count -ne 0) {
        throw "Nested archives are not allowed: $($nestedArchives -join ', ')."
    }
}
finally {
    $check.Dispose()
}

Get-FileHash -LiteralPath $archivePath -Algorithm SHA256

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use numeric SemVer format.' }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = Split-Path -Parent $PSScriptRoot
$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'DVSeasons.Game\info.source.json') -Raw | ConvertFrom-Json
if ($metadata.Version -ne $Version) { throw "Requested version $Version does not match info.json version $($metadata.Version)." }

$packages = Join-Path $projectRoot 'artifacts\releases\packages'
$modRoot = Join-Path $projectRoot 'artifacts\build\DVSeasons'
$sourceArchive = Join-Path $packages ("DVSeasons-{0}-source.zip" -f $Version)
$combinedArchive = Join-Path $packages ("DVSeasons-{0}-UMM-with-source.zip" -f $Version)
$sourceRootName = "DVSeasons-{0}-source" -f $Version
New-Item -ItemType Directory -Path $packages -Force | Out-Null

$sourceFiles = @(Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
    $relative -notmatch '^(artifacts|\.vs)/' -and
    $relative -notmatch '(^|/)(bin|obj|Library|Logs|Temp|UserSettings|Build)/' -and
    $relative -notmatch '^DVSeasons\.Unity/Assets/DVSeasons/DV99/Generated/'
})

function New-SourceArchive {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) { [IO.File]::Delete($Path) }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in $sourceFiles) {
            $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, "$sourceRootName/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
        $stream.Dispose()
    }
}

function New-CombinedArchive {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath (Join-Path $modRoot 'info.json') -PathType Leaf)) {
        throw 'Build output is missing. Run Tools/build.ps1 first.'
    }
    if (Test-Path -LiteralPath $Path) { [IO.File]::Delete($Path) }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $modRoot -Recurse -File) {
            $relative = $file.FullName.Substring($modRoot.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, "DVSeasons/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        foreach ($file in $sourceFiles) {
            $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
            $entryPath = "DVSeasons/Source/$relative"
            if ($relative -eq 'DVSeasons.Game/info.json') {
                $entryPath = 'DVSeasons/Source/DVSeasons.Game/info.source.json'
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip, $file.FullName, $entryPath, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
                continue
            }

            if ($relative -in @(
                'DVSeasons.Game/DVSeasons.Game.csproj',
                'Tools/verify_project.ps1',
                'Tools/package_source.ps1'
            )) {
                $content = Get-Content -LiteralPath $file.FullName -Raw
                $content = $content.Replace('DVSeasons.Game\info.source.json', 'DVSeasons.Game\info.source.json')
                if ($relative -eq 'DVSeasons.Game/DVSeasons.Game.csproj') {
                    $content = $content.Replace(
                        '<None Include="info.json" CopyToOutputDirectory="PreserveNewest" />',
                        '<None Include="info.source.json" Link="info.json" CopyToOutputDirectory="PreserveNewest" />')
                }
                $entry = $zip.CreateEntry($entryPath, [IO.Compression.CompressionLevel]::Optimal)
                $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
                try { $writer.Write($content) } finally { $writer.Dispose() }
                continue
            }

            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $entryPath, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
        $stream.Dispose()
    }
}

New-SourceArchive -Path $sourceArchive
New-CombinedArchive -Path $combinedArchive

$requiredSourceEntries = @(
    'DVSeasons.Common/DVSeasons.Common.csproj',
    'DVSeasons.Game/DVSeasons.Game.csproj',
    'DVSeasons.MP/DVSeasons.MP.csproj',
    'DVSeasons.Unity/ProjectSettings/ProjectVersion.txt',
    'DVSeasons.Tests/DVSeasons.Tests.csproj',
    'DVSeasons.sln',
    'README.md'
)
foreach ($archivePath in @($sourceArchive, $combinedArchive)) {
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($relative in $requiredSourceEntries) {
            $expected = if ($archivePath -eq $sourceArchive) { "$sourceRootName/$relative" } else { "DVSeasons/Source/$relative" }
            if ($names -notcontains $expected) { throw "Source archive entry is missing: $expected" }
        }
        if ($archivePath -eq $combinedArchive -and $names -notcontains 'DVSeasons/info.json') {
            throw 'Combined archive is missing DVSeasons/info.json.'
        }
        if ($archivePath -eq $combinedArchive) {
            $descriptors = @($names | Where-Object { ($_ -split '/')[-1] -ieq 'info.json' })
            if ($descriptors.Count -ne 1) {
                throw "Combined archive must contain exactly one UMM info.json, found $($descriptors.Count)."
            }
            if ($names -notcontains 'DVSeasons/Source/DVSeasons.Game/info.source.json') {
                throw 'Combined archive is missing the source metadata file.'
            }
        }
        if ($names | Where-Object { $_ -match '(^|/)(bin|obj|Library|Logs|Temp|UserSettings)/' }) {
            throw "Generated files leaked into $archivePath."
        }
    }
    finally { $archive.Dispose() }
}

Get-FileHash -LiteralPath $sourceArchive, $combinedArchive -Algorithm SHA256

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use numeric SemVer format, for example 0.6.1."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $projectRoot 'artifacts\build\DVSeasons'
$releaseRoot = Join-Path $projectRoot ("artifacts\releases\expanded\DVSeasons-{0}" -f $Version)
$releaseMod = Join-Path $releaseRoot 'DVSeasons'
$artifact = Join-Path $projectRoot ("artifacts\releases\packages\DVSeasons-{0}.zip" -f $Version)
$requiredFiles = @(
    'DVSeasons.dll',
    'DVSeasons.Core.dll',
    'DVSeasons.Multiplayer.dll',
    'info.json',
    'AssetBundles\dvseasons_dv99'
)
$optionalFiles = @(
    'DVSeasons.pdb',
    'DVSeasons.Core.pdb',
    'DVSeasons.Multiplayer.pdb'
)

foreach ($relativePath in $requiredFiles) {
    $sourcePath = Join-Path $dist $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required release file is missing: $sourcePath"
    }
}

$metadata = Get-Content -LiteralPath (Join-Path $dist 'info.json') -Raw | ConvertFrom-Json
if ($metadata.Version -ne $Version) {
    throw "Requested version $Version does not match artifacts/build/DVSeasons/info.json version $($metadata.Version)."
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $releaseMod -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $artifact) -Force | Out-Null
foreach ($relativePath in @($requiredFiles + $optionalFiles)) {
    $sourcePath = Join-Path $dist $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        if ($requiredFiles -contains $relativePath) {
            throw "Required release file is missing: $sourcePath"
        }

        continue
    }

    $destinationPath = Join-Path $releaseMod $relativePath
    $destinationDirectory = Split-Path -Parent $destinationPath
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}

if (Test-Path -LiteralPath $artifact) {
    Remove-Item -LiteralPath $artifact -Force
}

Compress-Archive -LiteralPath $releaseMod -DestinationPath $artifact -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($artifact)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('/', '\') })
    foreach ($relativePath in $requiredFiles) {
        $entryName = "DVSeasons\$relativePath"
        if ($entryNames -notcontains $entryName) {
            throw "Required archive entry is missing: $entryName"
        }
    }
}
finally {
    $archive.Dispose()
}

Get-FileHash -LiteralPath $artifact -Algorithm SHA256

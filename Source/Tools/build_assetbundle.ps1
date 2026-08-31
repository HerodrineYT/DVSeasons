[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityEditor,
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$unityProject = Join-Path $projectRoot 'DVSeasons.Unity'
$intermediate = Join-Path $projectRoot 'artifacts\build\assetbundle-lz4'
$runtimeDirectory = Join-Path $projectRoot 'Resources\Runtime\AssetBundles'
$runtimeBundle = Join-Path $runtimeDirectory 'dvseasons_dv99'
$temporaryBundle = Join-Path $runtimeDirectory 'dvseasons_dv99.tmp'
$log = Join-Path $projectRoot 'artifacts\build\assetbundle-unity.log'

if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "Unity editor not found: $UnityEditor" }
New-Item -ItemType Directory -Path $intermediate -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

& $UnityEditor -batchmode -quit -projectPath $unityProject `
    -executeMethod DVSeasons.AssetBundleBuild.DVSeasonsAssetBundleBuilder.Build `
    -bundleOutput $intermediate -logFile $log
if ($LASTEXITCODE -ne 0) { throw "Unity AssetBundle build failed with exit code $LASTEXITCODE. See $log" }

$lz4Bundle = Join-Path $intermediate 'dvseasons_dv99'
& $Python (Join-Path $PSScriptRoot 'repack_unity_bundle.py') $lz4Bundle $temporaryBundle lzma
if ($LASTEXITCODE -ne 0) { throw "AssetBundle LZMA repack failed with exit code $LASTEXITCODE." }

Move-Item -LiteralPath $temporaryBundle -Destination $runtimeBundle -Force
Write-Host "Runtime AssetBundle updated: $runtimeBundle"

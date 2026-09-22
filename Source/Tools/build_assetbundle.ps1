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

$unityArguments = @(
    '-batchmode',
    '-quit',
    '-projectPath', ('"{0}"' -f $unityProject),
    '-executeMethod', 'DVSeasons.AssetBundleBuild.DVSeasonsAssetBundleBuilder.Build',
    '-bundleOutput', ('"{0}"' -f $intermediate),
    '-logFile', ('"{0}"' -f $log)
)
$projectSettingPaths = @('ProjectSettings\ProjectSettings.asset', 'ProjectSettings\GraphicsSettings.asset') |
    ForEach-Object { Join-Path $unityProject $_ }
$originalProjectSettings = @{}
foreach ($settingPath in $projectSettingPaths) {
    $originalProjectSettings[$settingPath] = [IO.File]::ReadAllBytes($settingPath)
}
try {
    $unityProcess = Start-Process -FilePath $UnityEditor -ArgumentList $unityArguments `
        -PassThru -WindowStyle Hidden
    $unityProcess.WaitForExit()
}
finally {
    # The Editor can reserialize native defaults again during shutdown, after
    # the C# builder's finally block. Restore exact bytes after it has exited.
    foreach ($settingPath in $projectSettingPaths) {
        [IO.File]::WriteAllBytes($settingPath, $originalProjectSettings[$settingPath])
    }
}
if ($unityProcess.ExitCode -ne 0) {
    throw "Unity AssetBundle build failed with exit code $($unityProcess.ExitCode). See $log"
}

# Unity can silently strip built-in stereo variants even when the source uses
# multi_compile. Inspect actual packed D3D11 programs before replacing runtime
# resources; enabling a missing material keyword is not a sufficient test.
& $Python (Join-Path $PSScriptRoot 'verify_stereo_bundle.py') `
    (Join-Path $intermediate 'dvseasons_dv99') `
    (Join-Path $intermediate 'stereo-variants.json')
if ($LASTEXITCODE -ne 0) { throw 'Required mono/stereo shader programs are absent from the built bundle.' }

foreach ($bundleName in @('dvseasons_dv99', 'dvseasons_winter', 'dvseasons_tracks')) {
    $lz4Bundle = Join-Path $intermediate $bundleName
    $runtimeBundle = Join-Path $runtimeDirectory $bundleName
    $temporaryBundle = "$runtimeBundle.tmp"
    & $Python (Join-Path $PSScriptRoot 'repack_unity_bundle.py') $lz4Bundle $temporaryBundle lzma
    if ($LASTEXITCODE -ne 0) { throw "AssetBundle LZMA repack failed with exit code $LASTEXITCODE." }
    Move-Item -LiteralPath $temporaryBundle -Destination $runtimeBundle -Force
    Write-Host "Runtime AssetBundle updated: $runtimeBundle"
}

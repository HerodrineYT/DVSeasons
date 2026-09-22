[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DVInstallDir,
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [switch]$Bake
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $DVInstallDir 'DerailValley_Data/Managed'
$verification = Join-Path $projectRoot 'artifacts/verification'
$compiler = Join-Path (Split-Path -Parent $UnityEditor) 'Data/Tools/Roslyn/csc.exe'
New-Item -ItemType Directory -Path $verification -Force | Out-Null
$verifier = Join-Path $verification 'VerifyAutumnLeafAtlas.dll'
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'netstandard.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.ImageConversionModule.dll') |
    ForEach-Object { '/reference:' + (Join-Path $managed $_) }
& $compiler /nologo /nostdlib /target:library /langversion:7.3 "/out:$verifier" @references `
    (Join-Path $PSScriptRoot 'VerifyAutumnLeafAtlas.cs') `
    (Join-Path $projectRoot 'DVSeasons.Game/AutumnLeafParticleTexture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Leaf atlas regression fixture compilation failed.' }

$previousBake = $env:DVSEASONS_BAKE_LEAF_ATLAS
try {
    $env:DVSEASONS_BAKE_LEAF_ATLAS = if ($Bake) { '1' } else { '0' }
    $log = Join-Path $verification 'leaf-atlas-cache-unity.log'
    $unityProject = Join-Path $projectRoot 'DVSeasons.Unity'
    $arguments = @('-batchmode', '-projectPath', ('"{0}"' -f $unityProject),
        '-executeMethod', 'DVSeasons.AssetBundleBuild.AutumnLeafAtlasVerification.Run',
        '-logFile', ('"{0}"' -f $log))
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) {
        $process.Kill()
        throw "Leaf atlas regression fixture timed out. Log: $log"
    }
    $process.WaitForExit()
    Select-String -LiteralPath $log -Pattern 'LEAF_ATLAS_.*|Exception:.*|error CS.*' | ForEach-Object { $_.Line }
    if ($process.ExitCode -ne 0) { throw "Leaf atlas regression fixture failed. Log: $log" }
    Write-Host "Leaf atlas regression fixture passed. Log: $log"
}
finally { $env:DVSEASONS_BAKE_LEAF_ATLAS = $previousBake }

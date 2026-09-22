[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DVInstallDir,
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [string]$UnityProject
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$managed=Join-Path $DVInstallDir 'DerailValley_Data/Managed'
$verification=Join-Path $root 'artifacts/verification'
New-Item -ItemType Directory -Path $verification -Force | Out-Null
if (-not $UnityProject) { $UnityProject=Join-Path $root 'DVSeasons.Unity' }
$modDirectory=Join-Path $root 'artifacts/build/DVSeasons'
$refs=@('mscorlib.dll','System.dll','System.Core.dll','netstandard.dll','Assembly-CSharp.dll',
    'DV.Simulation.dll','DV.CabControls.Spec.dll','DV.Interaction.dll','DV.ThingTypes.dll','DV.Utils.dll',
    'DV.UIFramework.dll','Unity.TextMeshPro.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll',
    'UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.AnimationModule.dll',
    'UnityModManager/0Harmony.dll') | ForEach-Object {'/reference:'+(Join-Path $managed $_)}
$refs+='/reference:'+(Join-Path $modDirectory 'DVSeasons.Core.dll')
$compiler=Join-Path (Split-Path -Parent $UnityEditor) 'Data/Tools/Roslyn/csc.exe'
& $compiler /nologo /nostdlib /target:library /langversion:7.3 "/out:$verification/VerifyColdPowertrain.dll" @refs `
    (Join-Path $PSScriptRoot 'VerifyColdPowertrain.cs') (Join-Path $PSScriptRoot 'VerifyColdStartHints.cs')
if($LASTEXITCODE -ne 0){throw 'Cold start fixture compilation failed.'}
$previousGame=$env:DVSEASONS_VERIFY_GAME
$previousRoot=$env:DVSEASONS_VERIFY_ROOT
try {
    $env:DVSEASONS_VERIFY_GAME=(Resolve-Path -LiteralPath $DVInstallDir).Path
    $env:DVSEASONS_VERIFY_ROOT=$root
    $log=Join-Path $verification 'cold-start-hints-runtime.log'
    $arguments=@('-batchmode','-projectPath',('"{0}"' -f $UnityProject),
        '-executeMethod','DVSeasons.AssetBundleBuild.ColdFeaturesVerification.Run','-logFile',('"{0}"' -f $log))
    $process=Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddMinutes(3)
    while(-not $process.WaitForExit(1000)){
        if([DateTime]::UtcNow -gt $deadline){$process.Kill();throw 'Cold start fixture timed out.'}
    }
    $process.WaitForExit()
    Select-String -LiteralPath $log -Pattern 'DVSeasons.*verified|Exception:.*|error CS.*' | ForEach-Object {$_.Line}
    if($process.ExitCode -ne 0){throw "Cold start fixture failed. Log: $log"}
    if(-not (Select-String -LiteralPath $log -SimpleMatch 'DVSeasons cold start UI verified:')){throw 'Native UI verification did not complete.'}
} finally {$env:DVSEASONS_VERIFY_GAME=$previousGame;$env:DVSEASONS_VERIFY_ROOT=$previousRoot}

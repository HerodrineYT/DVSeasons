[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DVInstallDir,
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [string]$ModDirectory,
    [string]$LogName='heater-material-runtime.log'
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$managed=Join-Path $DVInstallDir 'DerailValley_Data/Managed'
$verification=Join-Path $root 'artifacts/verification'
if(-not $ModDirectory){$ModDirectory=Join-Path $root 'artifacts/build/DVSeasons'}
$ModDirectory=(Resolve-Path -LiteralPath $ModDirectory).Path
if([IO.Path]::GetFileName($LogName) -ne $LogName){throw 'LogName must be a filename.'}
$refs=@('mscorlib.dll','System.dll','System.Core.dll','netstandard.dll','Assembly-CSharp.dll',
    'DV.CabControls.Spec.dll','DV.Interaction.dll','DV.ThingTypes.dll','DV.Utils.dll',
    'UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityModManager/0Harmony.dll') |
    ForEach-Object {'/reference:'+(Join-Path $managed $_)}
$compiler=Join-Path (Split-Path -Parent $UnityEditor) 'Data/Tools/Roslyn/csc.exe'
& $compiler /nologo /nostdlib /target:library /langversion:7.3 "/out:$verification/VerifyHeaterMaterial.dll" @refs (Join-Path $PSScriptRoot 'VerifyHeaterMaterial.cs')
if($LASTEXITCODE -ne 0){throw 'Heater fixture compilation failed.'}
$previousGame=$env:DVSEASONS_VERIFY_GAME; $previousMod=$env:DVSEASONS_VERIFY_MOD
try {
    $env:DVSEASONS_VERIFY_GAME=(Resolve-Path -LiteralPath $DVInstallDir).Path
    $env:DVSEASONS_VERIFY_MOD=$ModDirectory
    $log=Join-Path $verification $LogName
    $args=@('-batchmode','-projectPath',('"{0}"' -f (Join-Path $root 'DVSeasons.Unity')),
        '-executeMethod','DVSeasons.AssetBundleBuild.HeaterMaterialVerification.Run','-logFile',('"{0}"' -f $log))
    $process=Start-Process -FilePath $UnityEditor -ArgumentList $args -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(180000)){$process.Kill();throw 'Heater fixture timed out.'}
    $process.WaitForExit()
    Select-String -LiteralPath $log -Pattern 'HEATER_.*|Exception:.*|error CS.*' | ForEach-Object {$_.Line}
    if($process.ExitCode -ne 0){throw "Heater fixture failed. Log: $log"}
} finally {$env:DVSEASONS_VERIFY_GAME=$previousGame; $env:DVSEASONS_VERIFY_MOD=$previousMod}

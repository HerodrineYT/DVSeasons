[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DVInstallDir,
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [string]$ModDirectory,
    [string]$LogName = 'weather-network-unity.log'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $DVInstallDir 'DerailValley_Data/Managed'
$verification = Join-Path $projectRoot 'artifacts/verification'
$compiler = Join-Path (Split-Path -Parent $UnityEditor) 'Data/Tools/Roslyn/csc.exe'
if (-not $ModDirectory) { $ModDirectory = Join-Path $projectRoot 'artifacts/build/DVSeasons' }
$ModDirectory = (Resolve-Path -LiteralPath $ModDirectory).Path
if ([IO.Path]::GetFileName($LogName) -ne $LogName) { throw 'LogName must be a filename.' }
New-Item -ItemType Directory -Path $verification -Force | Out-Null
$verifier = Join-Path $verification 'VerifyWeatherNetwork.dll'
$references = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'netstandard.dll',
    'Assembly-CSharp.dll', 'DV.Common.dll', 'DV.Utils.dll', 'DV.WeatherSystem.dll',
    'DV.UI.dll', 'DV.UIFramework.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.UI.dll', 'Newtonsoft.Json.dll', 'UnityModManager/0Harmony.dll'
) | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$references += '/reference:' + (Join-Path $ModDirectory 'DVSeasons.Core.dll')
& $compiler /nologo /nostdlib /target:library /langversion:7.3 "/out:$verifier" @references (Join-Path $PSScriptRoot 'VerifyWeatherNetwork.cs')
if ($LASTEXITCODE -ne 0) { throw 'Weather regression fixture compilation failed.' }

$previousGame = $env:DVSEASONS_VERIFY_GAME
$previousMod = $env:DVSEASONS_VERIFY_MOD
try {
    $env:DVSEASONS_VERIFY_GAME = (Resolve-Path -LiteralPath $DVInstallDir).Path
    $env:DVSEASONS_VERIFY_MOD = $ModDirectory
    $log = Join-Path $verification $LogName
    $unityProject = Join-Path $projectRoot 'DVSeasons.Unity'
    $arguments = @('-batchmode', '-nographics', '-projectPath', ('"{0}"' -f $unityProject),
        '-executeMethod', 'DVSeasons.AssetBundleBuild.WeatherNetworkVerification.Run',
        '-logFile', ('"{0}"' -f $log))
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) {
        $process.Kill()
        throw "Weather regression fixture timed out. Log: $log"
    }
    $process.WaitForExit()
    Select-String -LiteralPath $log -Pattern 'WEATHER_.*|Exception:.*|error CS.*' | ForEach-Object { $_.Line }
    if ($process.ExitCode -ne 0) { throw "Weather regression fixture failed. Log: $log" }
    Write-Host "Weather regression fixture passed. Log: $log"
}
finally {
    $env:DVSEASONS_VERIFY_GAME = $previousGame
    $env:DVSEASONS_VERIFY_MOD = $previousMod
}

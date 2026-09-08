[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DVInstallDir,
    [switch]$FailOnOutdated
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$modsRoot = Join-Path $DVInstallDir 'Mods'
$metadataPath = Join-Path $projectRoot 'DVSeasons.Game\info.source.json'

if (-not (Test-Path -LiteralPath $modsRoot -PathType Container)) {
    throw "Derail Valley Mods directory was not found: $modsRoot"
}
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
    throw "DVSeasons metadata was not found: $metadataPath"
}

function ConvertTo-ComparableVersion {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $match = [regex]::Match($Value.Trim(), '^\d+(?:\.\d+){0,3}')
    if (-not $match.Success) { return $null }
    $parts = @($match.Value.Split('.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 4) { $parts += 0 }
    return [Version]::new($parts[0], $parts[1], $parts[2], $parts[3])
}

function Get-LatestRelease {
    param(
        [Parameter(Mandatory = $true)]
        [object]$RepositoryData,
        [Parameter(Mandatory = $true)]
        [string]$ModId
    )

    $releases = @($RepositoryData.Releases | Where-Object {
        [string]::Equals([string]$_.Id, $ModId,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($releases.Count -eq 0) { return $null }
    return $releases | Sort-Object {
        $parsed = ConvertTo-ComparableVersion ([string]$_.Version)
        if ($null -eq $parsed) { [Version]::new(0, 0, 0, 0) } else { $parsed }
    } -Descending | Select-Object -First 1
}

$installedMods = @{}
Get-ChildItem -LiteralPath $modsRoot -Directory | ForEach-Object {
    $infoPath = Join-Path $_.FullName 'info.json'
    if (-not (Test-Path -LiteralPath $infoPath -PathType Leaf)) { return }
    try {
        $info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace([string]$info.Id)) {
            $installedMods[[string]$info.Id] = [pscustomobject]@{
                Info = $info
                Path = $infoPath
            }
        }
    }
    catch {
        Write-Warning "Could not read mod metadata '$infoPath': $($_.Exception.Message)"
    }
}

$dvSeasonsMetadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$usedModIds = @($dvSeasonsMetadata.LoadAfter | Sort-Object -Unique)
$outdated = @()

Write-Host 'Checking versions of mods used by DVSeasons...'
foreach ($modId in $usedModIds) {
    if (-not $installedMods.ContainsKey([string]$modId)) {
        Write-Host "  ${modId}: not installed (optional)."
        continue
    }

    $installed = $installedMods[[string]$modId].Info
    $installedVersion = [string]$installed.Version
    $repository = [string]$installed.Repository
    if ([string]::IsNullOrWhiteSpace($repository)) {
        Write-Warning "$modId $installedVersion has no official Repository feed in info.json; update status is unknown."
        continue
    }

    try {
        $repositoryData = Invoke-RestMethod -Uri $repository -Headers @{
            'User-Agent' = 'DVSeasons-dependency-version-check'
        } -TimeoutSec 20
        $latest = Get-LatestRelease -RepositoryData $repositoryData -ModId ([string]$modId)
        if ($null -eq $latest) {
            Write-Warning "$modId ${installedVersion}: the official feed contains no matching release."
            continue
        }

        $latestVersion = [string]$latest.Version
        $installedComparable = ConvertTo-ComparableVersion $installedVersion
        $latestComparable = ConvertTo-ComparableVersion $latestVersion
        if ($null -eq $installedComparable -or $null -eq $latestComparable) {
            Write-Warning "$modId version could not be compared (installed '$installedVersion', latest '$latestVersion')."
            continue
        }

        if ($installedComparable -lt $latestComparable) {
            $message = "$modId is outdated: installed $installedVersion, latest $latestVersion. $repository"
            Write-Warning $message
            $outdated += $message
        }
        else {
            Write-Host "  ${modId}: $installedVersion (current; official $latestVersion)."
        }
    }
    catch {
        Write-Warning "$modId $installedVersion update check failed: $($_.Exception.Message)"
    }
}

if ($FailOnOutdated -and $outdated.Count -gt 0) {
    throw "Outdated DVSeasons integration mod(s) found: $($outdated.Count)."
}

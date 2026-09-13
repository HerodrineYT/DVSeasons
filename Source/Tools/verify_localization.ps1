[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$metadata = Get-Content -LiteralPath (Join-Path $root 'DVSeasons.Game/info.source.json') -Raw | ConvertFrom-Json
if (@($metadata.Requirements) -notcontains 'DVLangHelper-1.2.1') { throw 'Language Helper minimum dependency is missing.' }
$csvPath = Join-Path $root 'Resources/Runtime/Localization/DVSeasons.csv'
if (-not (Test-Path -LiteralPath $csvPath)) { $csvPath = Join-Path (Split-Path -Parent $root) 'Localization/DVSeasons.csv' }
$rows = @(Import-Csv -LiteralPath $csvPath -Encoding UTF8)
$keys = @($rows.Key)
if (@($keys | Select-Object -Unique).Count -ne $rows.Count) { throw 'Duplicate localization keys.' }
$main = Get-Content -LiteralPath (Get-ChildItem -LiteralPath (Join-Path $root 'DVSeasons.Game') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName) -Raw
$used = @([regex]::Matches($main, '"((?:UI|Status|Settings|Action|Season|Heater)\.[A-Za-z]+)"') | ForEach-Object { 'DVSeasons/' + $_.Groups[1].Value } | Sort-Object -Unique)
foreach ($key in $used) { if ($keys -notcontains $key) { throw "Missing translation: $key" } }
foreach ($row in $rows) {
    if ($used -notcontains $row.Key) { throw "Unused translation: $($row.Key)" }
    $expected = $null
    foreach ($language in @('English','Russian')) {
        $value = $row.$language
        if ([string]::IsNullOrWhiteSpace($value)) { throw "Empty $language translation: $($row.Key)" }
        $placeholders = @([regex]::Matches($value,'\{\d+(?::[^}]+)?\}') | ForEach-Object Value | Sort-Object) -join '|'
        if ($null -ne $expected -and $placeholders -ne $expected) { throw "Mismatched placeholders: $($row.Key)" }
        $expected = $placeholders
        [string]::Format([Globalization.CultureInfo]::InvariantCulture,$value,[object[]]@(1.5,2.5,3.5)) | Out-Null
    }
}
$built = Join-Path $root 'artifacts/build/DVSeasons/Localization/DVSeasons.csv'
if ((Test-Path -LiteralPath $built) -and (Get-FileHash -LiteralPath $built).Hash -ne (Get-FileHash -LiteralPath $csvPath).Hash) {
    throw 'Built translations differ from source.'
}
Write-Host "Verified $($rows.Count) English/Russian translations, GUI coverage and format placeholders."

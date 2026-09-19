param(
    [Parameter(Mandatory)][string]$CsvPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][datetimeoffset]$RetrievedAt,
    [int]$FirstYear = 2025,
    [int]$LastYear = 2027
)
$ErrorActionPreference = 'Stop'
# Development-time normalization of saved official bytes. No runtime download or
# generated substitute/equinox dates; every source row in the covered years stays.
$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CsvPath))
$csv = [Text.Encoding]::GetEncoding(932).GetString($bytes)
$rows = @($csv | ConvertFrom-Csv)
if ($rows.Count -lt 1 -or $rows[0].PSObject.Properties.Name -notcontains '国民の祝日・休日月日') { throw 'Unexpected official CSV header' }
$dates = @($rows | ForEach-Object {
    $date = [datetime]::ParseExact($_.'国民の祝日・休日月日', 'yyyy/M/d', [cultureinfo]::InvariantCulture)
    if ($date.Year -ge $FirstYear -and $date.Year -le $LastYear) {
        if ([string]::IsNullOrWhiteSpace($_.'国民の祝日・休日名称')) { throw 'Missing holiday name' }
        [ordered]@{ Date = $date.ToString('yyyy-MM-dd'); Name = $_.'国民の祝日・休日名称' }
    }
})
if (($dates.Date | Select-Object -Unique).Count -ne $dates.Count) { throw 'Duplicate holiday date' }
foreach ($year in $FirstYear..$LastYear) {
    if (@($dates | Where-Object { $_.Date.StartsWith("$year-") }).Count -lt 16) { throw "Incomplete source year: $year" }
}
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
$preset = [ordered]@{
    Version = "cao-$FirstYear-$LastYear-$($hash.Substring(0,12))"
    Source = 'https://www8.cao.go.jp/chosei/shukujitsu/syukujitsu.csv'
    SourceSha256 = $hash; RetrievedAt = $RetrievedAt.ToString('o')
    FirstYear = $FirstYear; LastYear = $LastYear; Dates = $dates
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$preset | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8NoBOM -LiteralPath $OutputPath
Write-Output "Normalized $($dates.Count) official dates ($FirstYear-$LastYear); SHA256 $hash"

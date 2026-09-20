[CmdletBinding()]
param(
    [ValidateSet('Fresh','Weekly','Load')][string]$Scenario = 'Weekly',
    [string]$DataRoot,
    [switch]$Resume,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$app = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
$seed = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $seed)) { throw 'Build GhProjectsBoards.sln in Release first.' }
if (-not $DataRoot) { $DataRoot = Join-Path $repo ('TestResults/planning-evaluation-' + [guid]::NewGuid().ToString('N')) }
if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw 'DataRoot must be absolute.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$marker = Join-Path $DataRoot 'diagnostics/planning-evaluation.json'
if ($Resume) {
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Resume requires an existing isolated planning evaluation root.' }
    $Scenario = (Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json).scenario
} else {
    if (Test-Path -LiteralPath $DataRoot) { throw 'Existing data is never overwritten. Use Resume for a prepared evaluation root.' }
    & $seed --seed-planning-check $DataRoot $Scenario
    if ($LASTEXITCODE -ne 0) { throw 'Fixture preparation failed.' }
}
$manifest = Join-Path $DataRoot ('diagnostics/launch-' + [guid]::NewGuid().ToString('N') + '.json')
@{
    source = (git -C $repo rev-parse HEAD); changes = @(git -C $repo status --porcelain); scenario = $Scenario
    dataRoot = $DataRoot; executable = $app; appSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'GhProjectsBoards.App.dll')).Hash
    coreSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'GhProjectsBoards.Core.dll')).Hash
    seedSha256 = (Get-FileHash -LiteralPath $seed).Hash; os = [Environment]::OSVersion.VersionString
    preparedOnly = $PrepareOnly.IsPresent; humanAcceptance = 'not_run'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest
Write-Host "Synthetic $Scenario data: $DataRoot"
Write-Host 'Select the saved github.com / ID 42 profile and P1. No connection check is needed. P2 checks Project switching.'
Write-Host 'Fresh: configure field mappings and U1 weight once, then enter Estimate. Weekly: Actual 5 to 7, Remaining 4 to 3, and minute dates. Load: 1,000 tasks / 20 people with retained legacy plans.'
if (-not $PrepareOnly) {
    $start = [Diagnostics.ProcessStartInfo]::new($app)
    $start.UseShellExecute = $false; $start.WorkingDirectory = Split-Path $app -Parent
    $start.Environment['GHPB_DATA_ROOT'] = $DataRoot
    $start.Environment['GH_CONFIG_DIR'] = Join-Path $DataRoot 'diagnostics/empty-gh-config'
    foreach ($key in @('GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN')) { $start.Environment.Remove($key) | Out-Null }
    $process = [Diagnostics.Process]::Start($start)
    Write-Host "Ordinary application PID: $($process.Id)"
}

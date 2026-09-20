[CmdletBinding()]
param([string]$DataRoot, [switch]$Resume, [switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$app = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
$seed = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $seed)) { throw 'Build GhProjectsBoards.sln in Release first.' }
if (-not $DataRoot) { $DataRoot = Join-Path $repo ('TestResults/gantt-evaluation-' + [guid]::NewGuid().ToString('N')) }
if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw 'DataRoot must be absolute.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if ($Resume) {
    if (-not (Test-Path -LiteralPath (Join-Path $DataRoot 'gantt-fixture.json'))) { throw 'Not a prepared Gantt check.' }
} else {
    if (Test-Path -LiteralPath $DataRoot) { throw 'Existing data is never overwritten. Use Resume.' }
    & $seed --seed-gantt $DataRoot
    if ($LASTEXITCODE -ne 0) { throw 'Gantt fixture preparation failed.' }
}
Write-Host "Synthetic data: $DataRoot"
Write-Host 'Select the saved github.com / ID 42 profile, open P1, and choose Gantt. P2 tests switching. No connection check is needed.'
if (-not $PrepareOnly) {
    $start = [Diagnostics.ProcessStartInfo]::new($app)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $app -Parent
    $start.Environment['GHPB_DATA_ROOT'] = $DataRoot
    $process = [Diagnostics.Process]::Start($start)
    Write-Host "Ordinary application PID: $($process.Id)"
}

#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet("Debug", "Release")][string]$Configuration = "Release", [ValidateSet("2027.04", "2027.10")][string]$Version = "2027.04",
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string]$StatusDate, [string]$DataRoot, [switch]$Resume, [switch]$NoBuild, [switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $DataRoot) {
    if ($Resume) { throw '-Resume requires -DataRoot.' }
    & (Join-Path $PSScriptRoot 'Clear-TestResults.ps1')
    $DataRoot = Join-Path $repo ('TestResults/evaluation/' + [guid]::NewGuid().ToString('N'))
}
if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw 'DataRoot must be absolute.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$app = Join-Path $repo "src/GhProjectsBoards.App/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe"
$fixture = Join-Path $repo "tests/GhProjectsBoards.Tests/bin/$Configuration/net10.0-windows/GhProjectsBoards.Tests.exe"
if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'GhProjectsBoards.sln') -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Restore dependencies separately before retrying.' }
}
foreach ($file in @($app, $fixture)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing executable: $file" } }
function IsolatedStart([string]$Executable) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = Split-Path $Executable -Parent
    foreach ($name in @('GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN','GH_DEBUG','GH_HOST','GH_REPO','GHPB_PLAN_METRICS','GHPB_PUBLISH_METRICS')) { [void]$start.Environment.Remove($name) }
    return $start
}
$bin = Join-Path $DataRoot 'fake-gh/bin'
$fake = Join-Path $bin 'gh.exe'
if ($Resume) {
    if (-not (Test-Path -LiteralPath (Join-Path $DataRoot 'evaluation.json')) -or -not (Test-Path -LiteralPath $fake)) { throw 'Not a prepared evaluation root. Use a new empty root without -Resume.' }
} else {
    $prepare = IsolatedStart $fixture
    $prepare.ArgumentList.Add('--prepare-evaluation'); $prepare.ArgumentList.Add($DataRoot); $prepare.ArgumentList.Add($Version)
    if ($StatusDate) { $prepare.ArgumentList.Add($StatusDate) }
    $process = [Diagnostics.Process]::Start($prepare)
    try { $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw 'Evaluation initialization failed; existing files were not replaced.' } }
    finally { $process.Dispose() }
    New-Item -ItemType Directory -Path $bin | Out-Null
    Get-ChildItem -LiteralPath (Split-Path $fixture -Parent) -File | Copy-Item -Destination $bin
    Copy-Item -LiteralPath (Join-Path $bin 'GhProjectsBoards.Tests.exe') -Destination $fake
}
Write-Host "Data root: $DataRoot"
Write-Host "Offline fake gh: $fake"
$summary = Get-Content -LiteralPath (Join-Path $DataRoot 'evaluation.json') -Raw | ConvertFrom-Json
Write-Host "接続 → $($summary.title)。40要求事項・1,000タスク・20名。初期状態は 未発行 0 タスク。状況日は $($summary.statusDate)。"
Write-Host "Resume: & '$PSCommandPath' -NoBuild -Configuration $Configuration -Resume -DataRoot '$DataRoot'"
if ($PrepareOnly) { return }
$start = IsolatedStart $app
$start.Environment['GHPB_DATA_ROOT'] = $DataRoot
$start.Environment['GH_CONFIG_DIR'] = Join-Path $DataRoot 'fake-gh'
$start.Environment['PATH'] = $bin + [IO.Path]::PathSeparator + $start.Environment['PATH']
$process = [Diagnostics.Process]::Start($start)
Write-Host "Application PID: $($process.Id)"
$process.Dispose()

[CmdletBinding()]
param([string]$DataRoot, [switch]$Resume, [switch]$PrepareOnly, [string]$Executable, [string]$SeedExecutable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Executable) { $Executable = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe' }
if (-not $SeedExecutable) { $SeedExecutable = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe' }
foreach ($path in @($Executable, $SeedExecutable)) { if (-not [IO.Path]::IsPathFullyQualified($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Build Release first, or supply absolute existing Executable and SeedExecutable paths.' } }
if (-not $DataRoot) { $DataRoot = Join-Path $repo ('TestResults/summary-evaluation-' + [guid]::NewGuid().ToString('N')) }
if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw 'DataRoot must be absolute.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$marker = Join-Path $DataRoot 'summary-fixture.json'
if ($Resume) {
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Not a prepared Summary check.' }
    $manifest = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($manifest.kind -ne 'synthetic-summary-v1' -or -not $manifest.validatedReadback) { throw 'Invalid Summary check marker.' }
} else {
    if (Test-Path -LiteralPath $DataRoot) { throw 'Existing data is never overwritten. Use Resume.' }
    & $SeedExecutable --seed-summary $DataRoot
    if ($LASTEXITCODE -ne 0) { throw 'Summary fixture preparation failed.' }
}
Write-Host "Synthetic data: $DataRoot"
Write-Host 'Select saved github.com / ID 42, open P1, choose Summary. P2 has a separate allowance. Keep this evaluation offline; no connection or Apply is needed.'
if (-not $PrepareOnly) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $Executable -Parent
    $start.Environment['GHPB_DATA_ROOT'] = $DataRoot
    $process = [Diagnostics.Process]::Start($start)
    Write-Host "Ordinary application PID: $($process.Id)"
}

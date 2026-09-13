[CmdletBinding()]
param([string]$DataRoot, [switch]$Resume, [switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$app = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
$seed = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $seed)) { throw 'Build GhProjectsBoards.sln in Release first.' }
if (-not $DataRoot) { $DataRoot = Join-Path $repo ('TestResults/manual-editing-' + [guid]::NewGuid().ToString('N')) }
if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw 'DataRoot must be absolute.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$marker = Join-Path $DataRoot 'synthetic-editing-check.txt'
if ($Resume) {
    if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'Synthetic registered Projects; no live authentication.') { throw 'This is not a prepared synthetic editing check.' }
} else {
    if (Test-Path -LiteralPath $DataRoot) { throw 'Refusing to overwrite an existing data directory. Use Resume for this prepared check.' }
    & $seed --seed-editing $DataRoot
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic registration preparation failed.' }
    Set-Content -LiteralPath $marker 'Synthetic registered Projects; no live authentication.'
}
Write-Host "Synthetic data: $DataRoot"
Write-Host 'Open 登録済みProject, select github.com / ID 42, then P1 or P2. No connection check is needed.'
if (-not $PrepareOnly) {
    $start = [Diagnostics.ProcessStartInfo]::new($app)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $app -Parent
    $start.Environment['GHPB_DATA_ROOT'] = $DataRoot
    $process = [Diagnostics.Process]::Start($start)
    Write-Host "Ordinary application PID: $($process.Id)"
}

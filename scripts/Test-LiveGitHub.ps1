[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$GhPath = 'C:\Program Files\GitHub CLI\gh.exe',
    [switch]$DiagnosticsOnly
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not (Test-Path -LiteralPath $GhPath -PathType Leaf)) {
    throw 'Live validation requires Windows, an unlocked interactive desktop, and the real GitHub CLI.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$results = Join-Path $repoRoot "TestResults/live/$runId"
$previous = @{}
foreach ($name in @('GHPB_RUN_LIVE_GITHUB', 'GHPB_LIVE_GH_PATH', 'GHPB_LIVE_ARTIFACTS', 'GHPB_E2E_APP_PATH')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location $repoRoot
try {
    Write-Host 'Authorized targets: fukuda-yuki/codex-sandbox and user Project fukuda-yuki/3 only.'
    if ($DiagnosticsOnly) { Write-Host 'Read-only ordinary-executable diagnostics. Keep the desktop unlocked.' }
    else { Write-Host 'This test creates, updates and deletes disposable sandbox data. Keep the desktop unlocked.' }
    dotnet build GhProjectsBoards.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE. Live validation was not run." }
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    $env:GHPB_RUN_LIVE_GITHUB = '1'
    $env:GHPB_LIVE_GH_PATH = (Resolve-Path -LiteralPath $GhPath).Path
    $env:GHPB_LIVE_ARTIFACTS = $results
    $env:GHPB_E2E_APP_PATH = Join-Path $repoRoot "src/GhProjectsBoards.App/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe"
    if (-not (Test-Path -LiteralPath $env:GHPB_E2E_APP_PATH -PathType Leaf)) { throw 'The ordinary WinUI executable was not built.' }

    $suites = if ($DiagnosticsOnly) { @('GhProjectsBoards.E2E.Tests') } else { @('GhProjectsBoards.Tests', 'GhProjectsBoards.E2E.Tests') }
    foreach ($suite in $suites) {
        dotnet test "tests/$suite/$suite.csproj" --configuration $Configuration --no-build `
            --filter 'TestCategory=LiveGitHub' --logger "trx;LogFileName=$suite.trx" `
            --results-directory $results -- NUnit.NumberOfTestWorkers=0
        if ($LASTEXITCODE -ne 0) { throw "Live validation failed in $suite. Inspect cleanup evidence before another run: $results" }
        $trxPath = Join-Path $results "$suite.trx"
        if (-not (Test-Path -LiteralPath $trxPath)) { throw "No live execution report: $trxPath" }
        [xml]$report = Get-Content -LiteralPath $trxPath -Raw
        $counters = $report.TestRun.ResultSummary.Counters
        if ($null -eq $counters -or [int]$counters.executed -lt 1 -or
            [int]$counters.total -ne [int]$counters.executed -or
            [int]$counters.passed -ne [int]$counters.executed -or [int]$counters.notExecuted -gt 0) {
            throw "Live validation did not pass a complete, nonempty suite: $trxPath"
        }
    }
    Write-Host "Live results (DiagnosticsOnly=$DiagnosticsOnly): $results"
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}

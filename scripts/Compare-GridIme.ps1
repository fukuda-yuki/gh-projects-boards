[CmdletBinding()]
param([switch]$TraceInput)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Requires Microsoft Japanese IME on an unlocked Windows desktop.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$results = Join-Path $repoRoot "TestResults/ime-comparison/$runId"
$previous = @{}
foreach ($name in @('GHPB_RUN_E2E', 'GHPB_RUN_REAL_IME', 'GHPB_E2E_APP_PATH', 'GHPB_E2E_FAKE_GH_PATH',
    'GHPB_E2E_ARTIFACTS', 'GHPB_GRID_INPUT_TRACE_DIRECTORY')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $repoRoot
try {
    Write-Host 'Select Microsoft Japanese IME in alphanumeric mode. Keep the desktop unlocked and idle.'
    dotnet build GhProjectsBoards.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; comparisons were not run.' }
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    $env:GHPB_RUN_E2E = '1'
    $env:GHPB_RUN_REAL_IME = '1'
    $env:GHPB_E2E_APP_PATH = Join-Path $repoRoot 'src/GhProjectsBoards.App/bin/Release/net10.0-windows/GhProjectsBoards.App.exe'
    $env:GHPB_E2E_FAKE_GH_PATH = Join-Path $repoRoot 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
    $env:GHPB_E2E_ARTIFACTS = $results
    $env:GHPB_GRID_INPUT_TRACE_DIRECTORY = if ($TraceInput) { Join-Path $results 'trace' } else { $null }
    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    @{
        recordedAt = (Get-Date).ToUniversalTime().ToString('o')
        os = $operatingSystem.Caption; osVersion = $operatingSystem.Version
        architecture = $operatingSystem.OSArchitecture
        sdk = (dotnet --version); runtimes = @(dotnet --list-runtimes)
        ime = 'Microsoft Japanese IME'
        imeBinaryVersion = (Get-Item "$env:WINDIR/System32/IME/IMEJP/IMJPTIP.DLL").VersionInfo.FileVersion
        operator = 'Automated FlaUI physical virtual-key input; not human acceptance'
        traceEnabled = $TraceInput.IsPresent
        commit = (git rev-parse HEAD); worktree = @(git status --short)
        appVersion = (Get-Item $env:GHPB_E2E_APP_PATH).VersionInfo.FileVersion
        appDllSha256 = (Get-FileHash 'src/GhProjectsBoards.App/bin/Release/net10.0-windows/GhProjectsBoards.App.dll' -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'environment.json') -Encoding utf8
    git diff --no-ext-diff HEAD | Set-Content -LiteralPath (Join-Path $results 'source.patch') -Encoding utf8
    dotnet test tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj --configuration Release --no-build `
        --filter 'TestCategory=ImeComparison' --logger 'trx;LogFileName=comparison.trx' `
        --results-directory $results -- NUnit.NumberOfTestWorkers=0 RunConfiguration.TestSessionTimeout=180000
    $testExitCode = $LASTEXITCODE
    $trxPath = Join-Path $results 'comparison.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) { throw "No comparison report was produced: $results" }
    [xml]$report = Get-Content -LiteralPath $trxPath -Raw
    $counts = $report.TestRun.ResultSummary.Counters
    if ([int]$counts.executed -lt 1 -or [int]$counts.notExecuted -ne 0) {
        throw "Comparison execution was incomplete: $results"
    }
    Write-Host "Comparison evidence: $results"
    if ($testExitCode -ne 0) {
        throw "IME requirements failed in one or more compared controls. Preserve the failed results: $results"
    }
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}

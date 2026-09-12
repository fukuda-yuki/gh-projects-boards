[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Desktop E2E requires Windows and an unlocked interactive desktop.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK before running this script.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = 'tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$results = Join-Path $repoRoot "TestResults/e2e/$runId"
$previous = @{}
foreach ($name in @('GHPB_RUN_E2E', 'GHPB_E2E_APP_PATH', 'GHPB_E2E_ARTIFACTS', 'GHPB_E2E_FAKE_GH_PATH')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location $repoRoot
try {
    Write-Host 'Keep the desktop unlocked and do not interact with it while E2E runs.'
    dotnet build GhProjectsBoards.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE. E2E was not run." }
    $env:GHPB_RUN_E2E = '1'
    $env:GHPB_E2E_APP_PATH = Join-Path $repoRoot "src/GhProjectsBoards.App/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe"
    $env:GHPB_E2E_FAKE_GH_PATH = Join-Path $repoRoot "tests/GhProjectsBoards.Tests/bin/$Configuration/net10.0-windows/GhProjectsBoards.Tests.exe"
    $env:GHPB_E2E_ARTIFACTS = $results
    foreach ($path in @($env:GHPB_E2E_APP_PATH, $env:GHPB_E2E_FAKE_GH_PATH)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected executable was not built: $path" }
    }
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    $sourceCommit = $null
    $sourceChanges = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $value = git rev-parse HEAD
        if ($LASTEXITCODE -eq 0) { $sourceCommit = "$value".Trim() }
        $value = git status --porcelain
        if ($LASTEXITCODE -eq 0) { $sourceChanges = @($value) }
    }
    @{
        sourceCommit = $sourceCommit; sourceChanges = $sourceChanges
        configuration = $Configuration; os = [Environment]::OSVersion.VersionString
        dotnetSdk = (dotnet --version | Out-String).Trim()
        app = $env:GHPB_E2E_APP_PATH
        appSha256 = (Get-FileHash -LiteralPath $env:GHPB_E2E_APP_PATH -Algorithm SHA256).Hash
        fakeGhSha256 = (Get-FileHash -LiteralPath $env:GHPB_E2E_FAKE_GH_PATH -Algorithm SHA256).Hash
        testAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot "tests/GhProjectsBoards.E2E.Tests/bin/$Configuration/net10.0-windows10.0.26100.0/GhProjectsBoards.E2E.Tests.dll") -Algorithm SHA256).Hash
        driver = 'FlaUI.UIA3 5.0.0'; suite = 'TestCategory=E2E'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'metadata.json') -Encoding utf8

    dotnet test $testProject --configuration $Configuration --no-build `
        --filter 'TestCategory=E2E' --logger 'trx;LogFileName=e2e.trx' `
        --results-directory $results -- `
        NUnit.NumberOfTestWorkers=0 RunConfiguration.TestSessionTimeout=120000
    if ($LASTEXITCODE -ne 0) { throw "E2E failed with exit code $LASTEXITCODE. Results: $results" }
    $trxPath = Join-Path $results 'e2e.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) { throw "No TRX report was produced. E2E is unverified: $results" }
    [xml]$report = Get-Content -LiteralPath $trxPath -Raw
    $counters = $report.TestRun.ResultSummary.Counters
    if ($null -eq $counters -or [int]$counters.executed -lt 4 -or
        [int]$counters.total -ne [int]$counters.executed -or
        [int]$counters.passed -ne [int]$counters.executed -or [int]$counters.notExecuted -gt 0) {
        throw "E2E did not pass the complete connection suite without skips. Inspect: $trxPath"
    }
    Write-Host "E2E results: $results"
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}

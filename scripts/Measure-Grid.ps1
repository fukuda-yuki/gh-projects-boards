[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Requires an unlocked Windows desktop and .NET 10.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$results = Join-Path $repoRoot "TestResults/grid-measurements/$runId"
$previous = @{}
foreach ($name in @('GHPB_RUN_E2E', 'GHPB_E2E_APP_PATH', 'GHPB_E2E_FAKE_GH_PATH', 'GHPB_E2E_ARTIFACTS', 'GHPB_GRID_MEASUREMENTS')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $repoRoot
try {
    Write-Host 'Keep the desktop unlocked and do not interact with it during measurement.'
    dotnet build GhProjectsBoards.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; measurements were not run.' }
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    $env:GHPB_RUN_E2E = '1'
    $env:GHPB_E2E_APP_PATH = Join-Path $repoRoot 'src/GhProjectsBoards.App/bin/Release/net10.0-windows/GhProjectsBoards.App.exe'
    $env:GHPB_E2E_FAKE_GH_PATH = Join-Path $repoRoot 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
    $env:GHPB_E2E_ARTIFACTS = $results
    $env:GHPB_GRID_MEASUREMENTS = $results
    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    $processor = Get-CimInstance Win32_Processor | Select-Object -First 1
    $imeBinary = Get-Item "$env:WINDIR/System32/IME/IMEJP/IMJPTIP.DLL"
    @{
        recordedAt = (Get-Date).ToUniversalTime().ToString('o')
        os = $operatingSystem.Caption; osVersion = $operatingSystem.Version
        architecture = $operatingSystem.OSArchitecture; cpu = $processor.Name
        logicalProcessors = $processor.NumberOfLogicalProcessors
        memoryGiB = [math]::Round($operatingSystem.TotalVisibleMemorySize / 1MB, 2)
        sdk = (dotnet --version); runtimes = @(dotnet --list-runtimes)
        ime = 'Microsoft Japanese IME'; imeBinaryVersion = $imeBinary.VersionInfo.FileVersion
        operator = 'Codex using FlaUI physical input/UI Automation; not a human acceptance review'
        commit = (git rev-parse HEAD); worktree = @(git status --short)
        appVersion = (Get-Item $env:GHPB_E2E_APP_PATH).VersionInfo.FileVersion
        appDllSha256 = (Get-FileHash 'src/GhProjectsBoards.App/bin/Release/net10.0-windows/GhProjectsBoards.App.dll' -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'environment.json') -Encoding utf8
    foreach ($project in @('GhProjectsBoards.Tests', 'GhProjectsBoards.E2E.Tests')) {
        dotnet test "tests/$project/$project.csproj" --configuration Release --no-build `
            --filter 'TestCategory=GridMeasurement' --logger "trx;LogFileName=$project.trx" `
            --results-directory $results -- NUnit.NumberOfTestWorkers=0 RunConfiguration.TestSessionTimeout=300000
        if ($LASTEXITCODE -ne 0) { throw "Measurement failed. Preserve and inspect: $results" }
        [xml]$report = Get-Content -LiteralPath (Join-Path $results "$project.trx") -Raw
        $counts = $report.TestRun.ResultSummary.Counters
        if ([int]$counts.executed -lt 1 -or [int]$counts.notExecuted -ne 0) { throw "Measurement did not execute: $project" }
    }
    foreach ($file in @('application-processing.json', 'ui-elapsed.json')) {
        $data = Get-Content -LiteralPath (Join-Path $results $file) -Raw | ConvertFrom-Json
        $data.summary | Format-Table operation, median, maximum
    }
    Write-Host "Measurement evidence: $results"
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}

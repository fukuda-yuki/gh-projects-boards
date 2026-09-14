[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Filter = 'TestCategory=E2E&TestCategory!=GridIme'
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Desktop E2E requires Windows and an unlocked interactive desktop.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK before running this script.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = 'tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$results = Join-Path $repoRoot "TestResults/e2e/$runId"
New-Item -ItemType Directory -Path $results | Out-Null
$previous = @{}
foreach ($name in @('GHPB_DATA_ROOT', 'GHPB_RUN_E2E', 'GHPB_E2E_APP_PATH', 'GHPB_E2E_ARTIFACTS', 'GHPB_E2E_FAKE_GH_PATH')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location $repoRoot
try {
    Write-Host 'Keep the desktop unlocked and do not interact with it while E2E runs.'
    @{
        sourceCommit = (git rev-parse HEAD | Out-String).Trim()
        sourceChanges = @(git status --porcelain); worktree = $repoRoot
        configuration = $Configuration; startedAt = (Get-Date).ToUniversalTime().ToString('o')
        os = [Environment]::OSVersion.VersionString; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        dotnetSdk = (dotnet --version | Out-String).Trim(); powershell = $PSVersionTable.PSVersion.ToString()
        buildCommand = "dotnet build GhProjectsBoards.sln --configuration $Configuration"
        testArguments = @('test', $testProject, '--configuration', $Configuration, '--no-build', '--filter', $Filter,
            '--logger', 'trx;LogFileName=e2e.trx', '--results-directory', $results, '--', 'NUnit.NumberOfTestWorkers=0', 'RunConfiguration.TestSessionTimeout=600000')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'source-environment.json') -Encoding utf8
    dotnet --info | Set-Content -LiteralPath (Join-Path $results 'dotnet-info.txt') -Encoding utf8
    git diff --binary HEAD --output="$results/source.patch"
    dotnet build GhProjectsBoards.sln --configuration $Configuration 2>&1 | Tee-Object -FilePath (Join-Path $results 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE. E2E was not run." }
    $env:GHPB_RUN_E2E = '1'
    $env:GHPB_DATA_ROOT = Join-Path $results 'isolated-default-data'
    $env:GHPB_E2E_APP_PATH = Join-Path $repoRoot "src/GhProjectsBoards.App/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe"
    $env:GHPB_E2E_FAKE_GH_PATH = Join-Path $repoRoot "tests/GhProjectsBoards.Tests/bin/$Configuration/net10.0-windows/GhProjectsBoards.Tests.exe"
    $env:GHPB_E2E_ARTIFACTS = $results
    foreach ($path in @($env:GHPB_E2E_APP_PATH, $env:GHPB_E2E_FAKE_GH_PATH)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected executable was not built: $path" }
    }
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
        driver = 'FlaUI.UIA3 5.0.0'; suite = $Filter
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'metadata.json') -Encoding utf8

    $appDirectory = Split-Path -Parent $env:GHPB_E2E_APP_PATH
    $fakeDirectory = Split-Path -Parent $env:GHPB_E2E_FAKE_GH_PATH
    $testDirectory = Join-Path $repoRoot "tests/GhProjectsBoards.E2E.Tests/bin/$Configuration/net10.0-windows10.0.26100.0"
    $binaries = @(
        $env:GHPB_E2E_APP_PATH, "$appDirectory/GhProjectsBoards.App.dll", "$appDirectory/GhProjectsBoards.Core.dll",
        "$appDirectory/Microsoft.UI.Xaml.dll", "$appDirectory/Microsoft.UI.Xaml.Controls.dll",
        "$appDirectory/DWriteCore.dll", "$appDirectory/Microsoft.Windows.Widgets.dll",
        "$appDirectory/Microsoft.Windows.Widgets.Projection.dll", "$appDirectory/Microsoft.Windows.Widgets.winmd",
        "$appDirectory/coreclr.dll", "$appDirectory/GhProjectsBoards.App.runtimeconfig.json",
        $env:GHPB_E2E_FAKE_GH_PATH, "$fakeDirectory/GhProjectsBoards.Tests.dll", "$fakeDirectory/GhProjectsBoards.Core.dll",
        "$testDirectory/GhProjectsBoards.E2E.Tests.dll", "$testDirectory/FlaUI.Core.dll", "$testDirectory/FlaUI.UIA3.dll"
    )
    @($binaries | ForEach-Object {
        $file = Get-Item -LiteralPath $_
        @{ path = $file.FullName; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $_).Hash; version = $file.VersionInfo.FileVersion }
    }) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'binaries.json') -Encoding utf8
    foreach ($project in @('src/GhProjectsBoards.App', 'src/GhProjectsBoards.Core', 'tests/GhProjectsBoards.Tests', 'tests/GhProjectsBoards.E2E.Tests')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "$project/obj/project.assets.json") -Destination (Join-Path $results ((Split-Path $project -Leaf) + '.assets.json'))
    }

    dotnet test $testProject --configuration $Configuration --no-build `
        --filter $Filter --logger 'trx;LogFileName=e2e.trx' `
        --results-directory $results -- `
        NUnit.NumberOfTestWorkers=0 RunConfiguration.TestSessionTimeout=600000 2>&1 | Tee-Object -FilePath (Join-Path $results 'test.log')
    if ($LASTEXITCODE -ne 0) { throw "E2E failed with exit code $LASTEXITCODE. Results: $results" }
    $trxPath = Join-Path $results 'e2e.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) { throw "No TRX report was produced. E2E is unverified: $results" }
    [xml]$report = Get-Content -LiteralPath $trxPath -Raw
    $counters = $report.TestRun.ResultSummary.Counters
    if ($null -eq $counters -or [int]$counters.executed -lt 1 -or
        [int]$counters.total -ne [int]$counters.executed -or
        [int]$counters.passed -ne [int]$counters.executed -or [int]$counters.notExecuted -gt 0) {
        throw "E2E did not pass the complete connection suite without skips. Inspect: $trxPath"
    }
    $required = @{
        OrdinaryExecutable_OpensAndCloses = 1
        DiagnosesTargetsAndRequiresExplicitAccountRebinding = 1
        CancelAndWindowCloseStopTheOwnedGhProcess = 1
        MissingGhAndMissingLoginAreActionableInTheOrdinaryScreen = 1
        CloseWithTextBoxFocusedExitsNormally = 4
        ChromeCloseDuringGhStopsOwnedProcess = 2
        NativePickerSelectsExecutableAndCancelPreservesIt = 1
        RegisterTwoProjectsRestartRestoreAndUnregisterLocally = 1
        CancelFirstRetrievalDoesNotRegister = 1
        NormalCloseDuringProjectRetrievalStopsOwnedWork = 1
        ChangingConnectionInputsClearsPrivateDiscoveryAndDisablesReads = 1
        GridEditsScrolledRowsSharedTitlesRestartBuffersAndUndo = 1
        GridRectangleCopyPasteValidationClearAndOperationUndo = 1
        FailedDraftSaveCancelsNavigationAndCloseUntilRetry = 1
        RefreshPreservesPendingInputStartedDuringRetrieval = 1
        RefreshConflictComparisonResolutionAndRestart = 3
        RefreshIndependentFieldsAndPartialFailureKeepCompleteCheckpoint = 1
        RefreshCancellationAndCloseRetainExistingConflict = 1
        DeliberateRefreshInterruptionRecoversCoherentCheckpoint = 1
        UnregistrationRequiresDecisionAndPreservesSurvivingSharedDraft = 1
        DeliberateProcessInterruptionRecoversAcknowledgedTransactionAndUndo = 1
    }
    if ($Filter -ne 'TestCategory=E2E&TestCategory!=GridIme') { $required = @{} }
    if ($Filter -eq 'TestCategory=GridIme') { $required = @{ RegisteredGridPhysicalJapaneseIme = 6 } }
    foreach ($name in $required.Keys) {
        $cases = @($report.TestRun.Results.UnitTestResult | Where-Object { $_.testName -eq $name -or $_.testName.StartsWith($name + '(') })
        if ($cases.Count -ne $required[$name] -or @($cases | Where-Object outcome -ne 'Passed').Count -gt 0) {
            throw "Required journey missing or incomplete: $name. Inspect: $trxPath"
        }
    }
    @{ total = [int]$counters.total; executed = [int]$counters.executed; passed = [int]$counters.passed;
       failed = [int]$counters.failed; skipped = [int]$counters.notExecuted; requiredJourneys = $required
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'result.json') -Encoding utf8
    Write-Host "E2E results: $results"
}
catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $results 'runner-failure.txt') -Encoding utf8
    throw
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}

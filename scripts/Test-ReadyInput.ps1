[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('', 'selection', 'draft', 'reference', 'f2', 'mouse', 'keyboard', 'mouse-preenabled',
        'keyboard-preenabled', 'cancel-direct', 'cancel-f2', 'reconvert-direct', 'reconvert-f2')]
    [string]$Scenario = ''
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'IME validation requires an unlocked Windows desktop with Microsoft Japanese IME.' }
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root ('TestResults/ready-input/run-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$names = @('GHPB_RUN_READY_INPUT', 'GHPB_READY_APP_PATH', 'GHPB_READY_ARTIFACTS')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $root
try {
    Write-Host 'Keep the desktop unlocked and do not interact while physical-key IME validation runs.'
    $filter = 'TestCategory=ReadyInput'
    if ($Scenario) {
        $filter += "&Name~$Scenario"
        if ($Scenario -in 'mouse', 'keyboard') { $filter += '&Name!~preenabled' }
        if ($Scenario -eq 'f2') { $filter += '&Name!~cancel&Name!~reconvert' }
    }
    $testArgs = @('test', 'tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj',
        '-c', $Configuration, '--no-build', '--filter', $filter, '--logger', 'trx;LogFileName=ready.trx',
        '--results-directory', $run, '--', 'NUnit.NumberOfTestWorkers=0', 'RunConfiguration.TestSessionTimeout=180000')
    $sourceFiles = @(Get-ChildItem "$root/src/GhProjectsBoards.App" -File | Where-Object Extension -in '.cs', '.xaml', '.csproj';
        Get-Item "$root/tests/GhProjectsBoards.E2E.Tests/ReadyInputTests.cs", "$root/tests/GhProjectsBoards.E2E.Tests/WinUiProcess.cs", $PSCommandPath)
    @{
        commit = (git rev-parse HEAD); status = @(git status --porcelain); sourceHashes = @($sourceFiles | Get-FileHash)
        sdk = (dotnet --version); os = [Environment]::OSVersion.VersionString; powershell = $PSVersionTable.PSVersion.ToString()
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        buildCommand = "dotnet build GhProjectsBoards.sln -c $Configuration"; testArguments = $testArgs
        appArguments = @('--input-check'); at = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$run/metadata.json" -Encoding utf8
    dotnet --info | Set-Content -LiteralPath "$run/dotnet-info.txt" -Encoding utf8
    git diff --binary HEAD --output="$run/source.patch"
    dotnet build GhProjectsBoards.sln -c $Configuration *> "$run/build.log"
    if ($LASTEXITCODE -ne 0) { throw "Product build failed; no tests executed. See $run/build.log" }
    $appDirectory = "$root/src/GhProjectsBoards.App/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64"
    $testDirectory = "$root/tests/GhProjectsBoards.E2E.Tests/bin/$Configuration/net10.0-windows10.0.26100.0"
    $env:GHPB_RUN_READY_INPUT = '1'
    $env:GHPB_READY_APP_PATH = "$appDirectory/GhProjectsBoards.App.exe"
    $env:GHPB_READY_ARTIFACTS = $run
    @($env:GHPB_READY_APP_PATH, "$appDirectory/GhProjectsBoards.App.dll", "$appDirectory/GhProjectsBoards.Core.dll",
        "$appDirectory/Microsoft.UI.Xaml.dll", "$appDirectory/Microsoft.UI.Xaml.Controls.dll", "$appDirectory/coreclr.dll",
        "$appDirectory/GhProjectsBoards.App.runtimeconfig.json", "$testDirectory/GhProjectsBoards.E2E.Tests.dll",
        "$testDirectory/FlaUI.Core.dll", "$testDirectory/FlaUI.UIA3.dll") | Get-FileHash |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$run/binary-hashes.json" -Encoding utf8
    foreach ($project in @('src/GhProjectsBoards.App', 'src/GhProjectsBoards.Core', 'tests/GhProjectsBoards.E2E.Tests')) {
        Copy-Item -LiteralPath "$root/$project/obj/project.assets.json" -Destination "$run/$((Split-Path $project -Leaf)).assets.json"
    }
    dotnet @testArgs *> "$run/test.log"
    $testExit = $LASTEXITCODE
    Get-Content -LiteralPath "$run/test.log"
    if (!(Test-Path -LiteralPath "$run/ready.trx")) { throw 'Missing TRX; no execution evidence.' }
    [xml]$trx = Get-Content -LiteralPath "$run/ready.trx" -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    $expected = if ($Scenario) { @($Scenario) } else {
        @('selection', 'draft', 'reference', 'f2', 'mouse', 'keyboard', 'mouse-preenabled',
            'keyboard-preenabled', 'cancel-direct', 'cancel-f2', 'reconvert-direct', 'reconvert-f2')
    }
    @{
        executed = [int]$counts.executed; passed = [int]$counts.passed; failed = [int]$counts.failed
        skipped = [int]$counts.notExecuted; expectedScenarios = $expected
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$run/result.json" -Encoding utf8
    if ($testExit -ne 0 -or [int]$counts.executed -ne $expected.Count -or [int]$counts.total -ne $expected.Count -or
        [int]$counts.passed -ne $expected.Count -or [int]$counts.notExecuted -ne 0) { throw "Input gate failed. Evidence: $run" }
    foreach ($case in $expected) {
        $caseName = 'NativeEditorKeepsCellAndImeBoundaries("' + $case + '")'
        $observed = @($trx.TestRun.Results.UnitTestResult | Where-Object testName -eq $caseName)
        if ($observed.Count -ne 1 -or $observed[0].outcome -ne 'Passed') { throw "Required scenario missing: $case" }
    }
}
catch {
    $_ | Out-String | Set-Content -LiteralPath "$run/runner-failure.txt" -Encoding utf8
    throw
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
    Write-Host "Evidence: $run"
}

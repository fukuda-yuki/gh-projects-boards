# Opt-in local diagnosis. A completed driver is evidence collection, not product acceptance.
[CmdletBinding()]
param(
    [ValidateRange(100, 1000)][int]$ItemCount = 101,
    [ValidateRange(1, 12)][int]$SelectFieldCount = 1,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$RunId = ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')),
    [switch]$NoBuild,
    [string]$Executable,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceRevision,
    [switch]$Trace,
    [switch]$Ime,
    [switch]$Frames,
    [switch]$BulkPerformance
)

$ErrorActionPreference = 'Stop'
if ($BulkPerformance -and $SelectFieldCount -ne 12) { throw 'Bulk performance requires twelve single-select fields.' }
if (!$BulkPerformance -and $ItemCount -lt 101) { throw 'The existing local diagnostic requires at least 101 rows.' }
if ($env:OS -ne 'Windows_NT') { throw 'This diagnostic requires Windows and an unlocked interactive desktop.' }
foreach ($command in @('dotnet', 'git')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required command is unavailable: $command" }
}
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$run = Join-Path $repo "TestResults/sheet-diagnostic/$RunId"
if (Test-Path -LiteralPath $run) { throw "Refusing to overwrite diagnostic evidence: $run" }
if ($Executable -and -not [IO.Path]::IsPathFullyQualified($Executable)) { throw 'Executable must be an absolute path.' }
$defaultApp = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
$app = if ($Executable) { [IO.Path]::GetFullPath($Executable) } else { $defaultApp }
if ($Executable -and -not (Test-Path -LiteralPath $app -PathType Leaf)) { throw "Immutable executable is missing: $app" }
$data = Join-Path $run 'data'
$observations = Join-Path $run 'observations'
$testProject = 'tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj'
$testName = if ($BulkPerformance) { 'CachedSheetPixelMeasurements' } else { 'CachedSheetFocusedNativeScrollAndLocalActions' }
$testClass = if ($BulkPerformance) { 'BulkPerformanceTests' } else { 'LocalSheetDiagnosticTests' }
$filter = "FullyQualifiedName=GhProjectsBoards.E2E.Tests.$testClass.$testName"
$buildArguments = @('build', 'GhProjectsBoards.sln', '--configuration', 'Release')
$testArguments = @('test', $testProject, '--configuration', 'Release', '--no-build', '--filter', $filter,
    '--logger', 'trx;LogFileName=sheet-diagnostic.trx', '--results-directory', $run, '--',
    'NUnit.NumberOfTestWorkers=0', 'RunConfiguration.TestSessionTimeout=600000')
$environmentNames = @('APP', 'DATA_ROOT', 'OUTPUT', 'TRACE', 'ROWS', 'FIELDS', 'IME', 'FRAMES') | ForEach-Object { "GHPB_DIAGNOSTIC_$_" }
$previous = @{}
foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
New-Item -ItemType Directory -Path $run | Out-Null
$state = [ordered]@{
    schema = 1; runId = $RunId; phase = 'prepared'; startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    runnerPid = $PID; worktree = $repo; rows = $ItemCount; selectFields = $SelectFieldCount; totalColumns = $SelectFieldCount + 2
    app = $app; immutableOverride = [bool]$Executable; declaredAppSourceRevision = $SourceRevision
    sourceClaim = 'SourceRevision is caller-declared. Binary hashes identify the executed artifacts; NoBuild does not prove they match current source.'
    noBuild = [bool]$NoBuild; bulkPerformance = [bool]$BulkPerformance; traceRequested = [bool]$Trace; physicalImeRequested = [bool]$Ime; timedFramesRequested = [bool]$Frames; buildExitCode = $null; testExitCode = $null
    commands = @(
        @{ executable = 'dotnet'; arguments = $buildArguments; selected = !$NoBuild },
        @{ executable = (Join-Path $PSScriptRoot 'Start-EditingCheck.ps1'); arguments = @('-DataRoot', $data, '-ItemCount', "$ItemCount", '-SelectFieldCount', "$SelectFieldCount", '-PrepareOnly') },
        @{ executable = 'dotnet'; arguments = $testArguments }
    )
    boundary = 'Ordinary cached local-sheet diagnostic only. Driver waits and UIA calls are not isolated app latency. Pixels, app traces and durable state need separate review; no speedup or human acceptance claim.'
}
function Save-State {
    $state | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
}
function File-Evidence([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    @{ path = $file.FullName; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
function Source-Files {
    $paths = @(git ls-files --cached --others --exclude-standard -- src tests scripts '*.sln' '*.props' '*.targets' 'global.json' 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate current source files.' }
    @($paths | Sort-Object -Unique | ForEach-Object {
        $path = Join-Path $repo $_
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            @{ path = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        } else { @{ path = $_; missing = $true } }
    })
}
Save-State
Push-Location $repo
try {
    Write-Host "Diagnostic only: $ItemCount rows / $SelectFieldCount select fields. Keep the desktop unlocked and leave input to the driver."
    $state.phase = 'capturing-source'; Save-State
    $head = (git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve source HEAD.' }
    $branch = (git branch --show-current | Out-String).Trim()
    $status = @(git status --porcelain --untracked-files=all)
    $untracked = @(git ls-files --others --exclude-standard -- src tests scripts)
    $sourceFiles = @(Source-Files)
    @{
        head = $head; branch = $branch; status = $status; files = $sourceFiles; untrackedSourceFiles = $untracked
        capturedUtc = [DateTimeOffset]::UtcNow.ToString('o'); worktree = $repo
        declaredAppSourceRevision = $SourceRevision; immutableOverride = [bool]$Executable
        os = [Environment]::OSVersion.VersionString; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        powershell = $PSVersionTable.PSVersion.ToString(); dotnetSdk = (dotnet --version | Out-String).Trim()
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'source-environment.json') -Encoding utf8
    $patchPath = Join-Path $run 'source.patch'
    git diff --binary HEAD "--output=$patchPath"
    if ($LASTEXITCODE -ne 0) { throw 'Could not preserve the tracked source patch.' }
    foreach ($relative in $untracked) {
        $target = Join-Path (Join-Path $run 'untracked-source') $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
    }
    dotnet --info | Set-Content -LiteralPath (Join-Path $run 'dotnet-info.txt') -Encoding utf8
    $overrideFiles = if ($Executable) {
        @($app, (Join-Path (Split-Path -Parent $app) 'GhProjectsBoards.App.dll'), (Join-Path (Split-Path -Parent $app) 'GhProjectsBoards.Core.dll')) |
            ForEach-Object { File-Evidence $_ }
    } else { @() }
    if (-not $NoBuild) {
        $state.phase = 'building'; Save-State
        & dotnet @buildArguments 2>&1 | Tee-Object -FilePath (Join-Path $run 'build.log')
        $state.buildExitCode = $LASTEXITCODE; Save-State
        if ($state.buildExitCode -ne 0) { throw "Build failed ($($state.buildExitCode)); no diagnostic was run." }
    }
    foreach ($file in $overrideFiles) {
        if ((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw 'An immutable app binary changed during build. Use an isolated copied output directory.'
        }
    }
    $seedExe = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
    $testDirectory = Join-Path $repo 'tests/GhProjectsBoards.E2E.Tests/bin/Release/net10.0-windows10.0.26100.0'
    $appDirectory = Split-Path -Parent $app
    $seedDirectory = Split-Path -Parent $seedExe
    $requiredBinaries = @($app, (Join-Path $appDirectory 'GhProjectsBoards.App.dll'), (Join-Path $appDirectory 'GhProjectsBoards.Core.dll'),
        $seedExe, (Join-Path $seedDirectory 'GhProjectsBoards.Tests.dll'), (Join-Path $seedDirectory 'GhProjectsBoards.Core.dll'),
        (Join-Path $testDirectory 'GhProjectsBoards.E2E.Tests.dll'), (Join-Path $testDirectory 'FlaUI.Core.dll'), (Join-Path $testDirectory 'FlaUI.UIA3.dll'))
    foreach ($path in $requiredBinaries) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required compiled diagnostic artifact is missing: $path" }
    }
    $binaries = @($requiredBinaries) + @(@('Microsoft.UI.Xaml.dll', 'coreclr.dll', 'GhProjectsBoards.App.runtimeconfig.json') |
        ForEach-Object { Join-Path $appDirectory $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    @($binaries | Sort-Object -Unique | ForEach-Object { File-Evidence $_ }) | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $run 'binaries.json') -Encoding utf8
    $state.phase = 'seeding'; Save-State
    & (Join-Path $PSScriptRoot 'Start-EditingCheck.ps1') -DataRoot $data -ItemCount $ItemCount -SelectFieldCount $SelectFieldCount -PrepareOnly -BulkScenario:$BulkPerformance *>&1 |
        Tee-Object -FilePath (Join-Path $run 'seed.log')
    $seedManifestPath = Join-Path $data 'diagnostics/editing-seed.json'
    $seedManifest = Get-Content -LiteralPath $seedManifestPath -Raw | ConvertFrom-Json
    if (!$seedManifest.validatedReadback -or $seedManifest.count -ne $ItemCount -or $seedManifest.selectFieldCount -ne $SelectFieldCount) {
        throw 'Validated seed readback does not match the requested workload.'
    }
    Copy-Item -LiteralPath $seedManifestPath -Destination (Join-Path $run 'seed-manifest.json')
    $state.seed = File-Evidence $seedManifestPath
    $env:GHPB_DIAGNOSTIC_APP = $app
    $env:GHPB_DIAGNOSTIC_DATA_ROOT = $data
    $env:GHPB_DIAGNOSTIC_OUTPUT = $observations
    $env:GHPB_DIAGNOSTIC_ROWS = "$ItemCount"
    $env:GHPB_DIAGNOSTIC_FIELDS = "$SelectFieldCount"
    [Environment]::SetEnvironmentVariable('GHPB_DIAGNOSTIC_IME', $(if ($Ime) { '1' } else { $null }), 'Process')
    [Environment]::SetEnvironmentVariable('GHPB_DIAGNOSTIC_FRAMES', $(if ($Frames) { '1' } else { $null }), 'Process')
    $tracePath = Join-Path $run 'app-trace.jsonl'
    [Environment]::SetEnvironmentVariable('GHPB_DIAGNOSTIC_TRACE', $(if ($Trace) { $tracePath } else { $null }), 'Process')
    $state.phase = 'executing-diagnostic'; $state.testStartedUtc = [DateTimeOffset]::UtcNow.ToString('o'); Save-State
    & dotnet @testArguments 2>&1 | Tee-Object -FilePath (Join-Path $run 'test.log')
    $state.testExitCode = $LASTEXITCODE
    $state.testFinishedUtc = [DateTimeOffset]::UtcNow.ToString('o'); Save-State
    $trx = Join-Path $run 'sheet-diagnostic.trx'
    if (-not (Test-Path -LiteralPath $trx -PathType Leaf)) { throw 'Diagnostic produced no TRX report; execution is unverified.' }
    [xml]$report = Get-Content -LiteralPath $trx -Raw
    $counters = $report.SelectSingleNode("//*[local-name()='Counters']")
    $cases = @($report.SelectNodes("//*[local-name()='UnitTestResult']"))
    $state.result = @{
        total = [int]$counters.total; executed = [int]$counters.executed; passed = [int]$counters.passed
        failed = [int]$counters.failed; skipped = [int]$counters.notExecuted; trx = File-Evidence $trx
        cases = @($cases | ForEach-Object { @{ name = $_.testName; outcome = $_.outcome; duration = $_.duration } })
    }
    Save-State
    if ($state.testExitCode -ne 0 -or $null -eq $counters -or [int]$counters.total -ne 1 -or [int]$counters.executed -ne 1 -or
        [int]$counters.passed -ne 1 -or [int]$counters.notExecuted -ne 0 -or $cases.Count -ne 1 -or $cases[0].testName -ne $testName) {
        throw 'The exact diagnostic case did not complete once without failure/skips. Retain this attempt and inspect TRX/logs.'
    }
    $state.phase = 'diagnostic-completed'
    Write-Host "Diagnostic artifacts: $run"
    Write-Host 'Completion is not product acceptance. Review pixels, state, omissions and app/driver timing boundaries separately.'
}
catch {
    $state.phase = 'failed'; $state.failure = $_.Exception.Message
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $run 'runner-failure.txt') -Encoding utf8
    throw
}
finally {
    $state.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    try {
        $lifetime = Join-Path $observations 'lifetime.json'
        $state.appLifetime = if (Test-Path -LiteralPath $lifetime -PathType Leaf) { Get-Content -LiteralPath $lifetime -Raw | ConvertFrom-Json } else { $null }
        $state.trace = @{ requested = [bool]$Trace; available = Test-Path -LiteralPath (Join-Path $run 'app-trace.jsonl') -PathType Leaf }
        if ($state.trace.available) { $state.trace.file = File-Evidence (Join-Path $run 'app-trace.jsonl') }
        $driverLog = Join-Path $observations 'driver.jsonl'
        if (Test-Path -LiteralPath $driverLog -PathType Leaf) {
            $state.omissions = @(Get-Content -LiteralPath $driverLog | ForEach-Object { $_ | ConvertFrom-Json } |
                Where-Object { $_.kind -like '*not-executed*' -or $_.kind -like '*error*' -or $_.kind -eq 'wheel-endpoint-not-reached' })
        }
        @(Source-Files) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'source-files-after.json') -Encoding utf8
    } catch { $state.finalEvidenceError = $_.Exception.Message }
    Save-State
    Pop-Location
}

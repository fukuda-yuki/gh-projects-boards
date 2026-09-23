[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceRevision,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$RunId,
    [ValidateSet('cold','warm')][string]$Condition = 'cold',
    [ValidateSet('standard','ime','scroll','readiness')][string]$Mode = 'standard',
    [string]$SeedExecutable,
    [ValidateSet('full','light','off')][string]$TraceDetail = 'full',
    [ValidateSet('none','immediate','settled')][string]$EarlyScroll = 'none',
    [ValidateSet('stress','near1','near3','continuous')][string]$ScrollProfile = 'stress',
    [ValidateRange(0, 100)][int]$ReadinessKeyDelayMs = 20,
    [ValidateSet('sheet','filter-control')][string]$ReadinessSurface = 'sheet'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathFullyQualified($Executable) -or -not (Test-Path -LiteralPath $Executable)) { throw 'Use an existing absolute executable path.' }
$run = Join-Path $repo "TestResults/issue61-65/$RunId"
if (Test-Path -LiteralPath $run) { throw 'Existing run evidence is retained. Choose a new RunId.' }
New-Item -ItemType Directory -Path $run | Out-Null
$data = Join-Path $run 'data'
$output = Join-Path $run 'observations'
$seed = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
if ($SeedExecutable) { $seed = (Resolve-Path -LiteralPath $SeedExecutable).Path }
$test = Join-Path $repo 'tests/GhProjectsBoards.E2E.Tests/bin/Release/net10.0-windows10.0.26100.0/GhProjectsBoards.E2E.Tests.dll'
$command = @('test', $test, '--filter', 'FullyQualifiedName=GhProjectsBoards.E2E.Tests.SustainedInputDiagnosticTests.SustainedPlanningSheetInputAndScroll', '--logger', 'trx;LogFileName=diagnostic.trx', '--results-directory', $run, '--', 'NUnit.NumberOfTestWorkers=0', 'RunConfiguration.TestSessionTimeout=240000')
$sourceFiles = @(git -C $repo ls-files --cached --others --exclude-standard | Sort-Object -Unique | ForEach-Object {
    $file = Join-Path $repo $_
    if (Test-Path -LiteralPath $file -PathType Leaf) { @{path=$_; sha256=(Get-FileHash -LiteralPath $file).Hash} }
})
@{
    sourceRevision=$SourceRevision; driverHead=(git -C $repo rev-parse HEAD); driverChanges=@(git -C $repo status --porcelain)
    sourceFiles=$sourceFiles; executable=$Executable; condition=$Condition; mode=$Mode; traceDetail=$TraceDetail; earlyScroll=$EarlyScroll; scrollProfile=$ScrollProfile; dataRoot=$data; dataKind='isolated synthetic Gantt fixture'
    readinessKeyDelayMs=$ReadinessKeyDelayMs
    readinessSurface=$ReadinessSurface
    flags=@{recycledPresentation=$env:GHPB_RECYCLED_PRESENTATION; desktopObserver=$env:GHPB_SUSTAINED_DESKTOP_OBSERVER; threadTiming=$env:GHPB_SHEET_THREAD_TIMING;
        renderingCallbacks=$env:GHPB_SHEET_RENDER_CALLBACKS}
    os=[Environment]::OSVersion.VersionString; architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    sdk=(dotnet --version); powershell=$PSVersionTable.PSVersion.ToString(); command=@('dotnet')+$command
    seedCommand=@($seed,'--seed-gantt',$data); seedSha256=(Get-FileHash -LiteralPath $seed).Hash
    seedCoreSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $seed) 'GhProjectsBoards.Core.dll')).Hash
    humanAcceptance='not_run'; liveGitHub='not_run'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'environment.json')
& $seed --seed-gantt $data *> (Join-Path $run 'seed.log')
if ($LASTEXITCODE -ne 0) { throw 'Fixture preparation failed.' }
# RegistrationStore owns root-level JSON. Fixture metadata belongs outside that namespace.
$diagnostics = Join-Path $data 'diagnostics'
New-Item -ItemType Directory -Path $diagnostics | Out-Null
Move-Item -LiteralPath (Join-Path $data 'gantt-fixture.json') -Destination (Join-Path $diagnostics 'gantt-fixture.json')
Copy-Item -LiteralPath (Get-ChildItem -LiteralPath (Join-Path $data 'Drafts') -Filter '*.json').FullName -Destination (Join-Path $run 'synthetic-initial-checkpoint.json')
$values=@{ GHPB_SUSTAINED_APP=$Executable; GHPB_SUSTAINED_DATA=$data; GHPB_SUSTAINED_OUTPUT=$output; GHPB_SUSTAINED_SOURCE=$SourceRevision; GHPB_SUSTAINED_CONDITION=$Condition; GHPB_SUSTAINED_MODE=$Mode; GHPB_SUSTAINED_TRACE_DETAIL=$TraceDetail; GHPB_SUSTAINED_EARLY_SCROLL=$EarlyScroll; GHPB_SUSTAINED_SCROLL_PROFILE=$ScrollProfile }
$previous=@{}
$values.GHPB_SUSTAINED_READINESS_KEY_DELAY_MS = $ReadinessKeyDelayMs.ToString()
$values.GHPB_SUSTAINED_READINESS_SURFACE = $ReadinessSurface
try {
    foreach($name in $values.Keys) { $previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,$values[$name],'Process') }
    & dotnet @command *> (Join-Path $run 'test.log')
    $result=$LASTEXITCODE
} finally { foreach($name in $values.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') } }
Get-Content -LiteralPath (Join-Path $run 'test.log') -Tail 15
if ($result -ne 0) { throw "Diagnostic failed ($result). Evidence retained at $run" }
[xml]$trx=Get-Content -Raw -LiteralPath (Join-Path $run 'diagnostic.trx')
$counts=$trx.TestRun.ResultSummary.Counters
if ($counts.executed -ne '1' -or $counts.passed -ne '1' -or $counts.failed -ne '0') { throw 'Expected one executed diagnostic; discovery/skip is not evidence.' }
Write-Host "Diagnostic observations: $output"

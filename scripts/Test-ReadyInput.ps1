param(
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release',
    [string]$Scenario = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root ('TestResults/ready-input/run-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run | Out-Null
$names = @('GHPB_RUN_READY_INPUT','GHPB_WINUI_PROBE_PATH','GHPB_READY_ARTIFACTS')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $sources = @(Get-ChildItem "$root/prototypes/WinUI.Feasibility" -File | Where-Object Extension -in '.cs','.xaml','.csproj','.json'; Get-Item "$root/tests/GhProjectsBoards.E2E.Tests/ReadyInputTests.cs",$PSCommandPath) | Get-FileHash
    @{ commit = (git -C $root rev-parse HEAD); status = (git -C $root status --porcelain); sourceHashes = $sources;
       sdk = (dotnet --version); os = [Environment]::OSVersion.VersionString;
       command = "Test-ReadyInput.ps1 -Configuration $Configuration -Scenario $Scenario";
       at = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 6 | Set-Content "$run/metadata.json"
    dotnet build "$root/prototypes/WinUI.Feasibility/WinUI.Feasibility.csproj" -c $Configuration -p:RestoreLockedMode=true *> "$run/build.log"
    if ($LASTEXITCODE -ne 0) { throw "Prototype build failed; no tests executed. See $run/build.log" }
    $env:GHPB_RUN_READY_INPUT = '1'
    $env:GHPB_WINUI_PROBE_PATH = "$root/prototypes/WinUI.Feasibility/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/WinUI.Feasibility.exe"
    $env:GHPB_READY_ARTIFACTS = $run
    $filter = 'TestCategory=ReadyInput'
    if ($Scenario) {
        $filter += "&Name~$Scenario"
        if ($Scenario -in 'mouse','keyboard') { $filter += '&Name!~preenabled' }
        if ($Scenario -eq 'f2') { $filter += '&Name!~cancel&Name!~reconvert' }
    }
    dotnet test "$root/tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj" -c $Configuration --filter $filter --logger 'trx;LogFileName=ready.trx' --results-directory $run *> "$run/test.log"
    $testExit = $LASTEXITCODE
    @(Get-Item $env:GHPB_WINUI_PROBE_PATH,
        "$root/prototypes/WinUI.Feasibility/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/WinUI.Feasibility.dll",
        "$root/prototypes/WinUI.Feasibility/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/Microsoft.UI.Xaml.dll",
        "$root/tests/GhProjectsBoards.E2E.Tests/bin/$Configuration/net10.0-windows/GhProjectsBoards.E2E.Tests.dll") |
        Get-FileHash | ConvertTo-Json -Depth 4 | Set-Content "$run/binary-hashes.json"
    Get-Content "$run/test.log"
    if (!(Test-Path "$run/ready.trx")) { throw 'Missing TRX; no execution evidence.' }
    [xml]$trx = Get-Content "$run/ready.trx"
    $counts = $trx.TestRun.ResultSummary.Counters
    $expected = if ($Scenario) { 1 } else { 12 }
    if ($testExit -ne 0 -or [int]$counts.executed -ne $expected -or [int]$counts.passed -ne $expected -or [int]$counts.notExecuted -ne 0) {
        throw "Ready editor gate failed. Evidence: $run"
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Write-Host "Evidence: $run"
}

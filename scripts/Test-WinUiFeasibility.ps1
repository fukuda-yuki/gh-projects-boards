param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root ('TestResults/issue22/probe-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run | Out-Null
$names = @('GHPB_RUN_WINUI_PROBE', 'GHPB_WINUI_PROBE_PATH', 'GHPB_WINUI_PROBE_ARTIFACTS')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    dotnet build "$root/prototypes/WinUI.Feasibility/WinUI.Feasibility.csproj" -c $Configuration -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'WinUI prototype build failed.' }
    $env:GHPB_RUN_WINUI_PROBE = '1'
    $env:GHPB_WINUI_PROBE_PATH = "$root/prototypes/WinUI.Feasibility/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/WinUI.Feasibility.exe"
    $env:GHPB_WINUI_PROBE_ARTIFACTS = $run
    dotnet test "$root/tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj" -c $Configuration --filter 'TestCategory=WinUiFeasibility' --logger 'trx;LogFileName=probe.trx' --results-directory $run
    $testExit = $LASTEXITCODE
    if (!(Test-Path "$run/probe.trx")) { throw 'Missing TRX; no execution evidence.' }
    [xml]$trx = Get-Content "$run/probe.trx"
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($testExit -ne 0 -or [int]$counts.executed -ne 4 -or [int]$counts.passed -ne 4 -or [int]$counts.notExecuted -ne 0) {
        throw "WinUI feasibility gate did not pass. Preserve evidence: $run"
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Write-Host "Evidence: $run"
}

param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root ('TestResults/ime-followup/run-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run | Out-Null
$names = @('GHPB_RUN_DIRECT_IME','GHPB_WINUI_PROBE_PATH','GHPB_DIRECT_IME_ARTIFACTS')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $sources = @(Get-ChildItem "$root/prototypes/WinUI.Feasibility" -File | Where-Object Extension -in '.cs','.xaml','.csproj','.json'; Get-Item "$root/tests/GhProjectsBoards.E2E.Tests/DirectImeDesignTests.cs",$PSCommandPath) | Get-FileHash
    @{ commit = (git -C $root rev-parse HEAD); status = (git -C $root status --porcelain); sourceHashes = $sources;
       sdk = (dotnet --version); os = [Environment]::OSVersion.VersionString; command = "Test-DirectImeDesign.ps1 -Configuration $Configuration";
       at = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 6 | Set-Content "$run/metadata.json"
    dotnet build "$root/prototypes/WinUI.Feasibility/WinUI.Feasibility.csproj" -c $Configuration -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Prototype build failed; no test execution claimed.' }
    $env:GHPB_RUN_DIRECT_IME = '1'
    $env:GHPB_WINUI_PROBE_PATH = "$root/prototypes/WinUI.Feasibility/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/WinUI.Feasibility.exe"
    $env:GHPB_DIRECT_IME_ARTIFACTS = $run
    dotnet test "$root/tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj" -c $Configuration --filter TestCategory=DirectImeDesign --logger 'trx;LogFileName=design.trx' --results-directory $run
    $testExit = $LASTEXITCODE
    if (!(Test-Path "$run/design.trx")) { throw 'Missing TRX; no execution evidence.' }
    [xml]$trx = Get-Content "$run/design.trx"
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($testExit -ne 0 -or [int]$counts.executed -ne 6 -or [int]$counts.passed -ne 6 -or [int]$counts.notExecuted -ne 0) {
        throw "Direct IME design gate failed. Evidence: $run"
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Write-Host "Evidence: $run"
}

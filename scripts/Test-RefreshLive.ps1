[CmdletBinding()]
param([string]$GhPath = 'C:\Program Files\GitHub CLI\gh.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $repo ('TestResults/refresh-live/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
& $GhPath issue view 1 --repo fukuda-yuki/codex-sandbox --json body | Set-Content (Join-Path $directory 'scope.json')
if ($LASTEXITCODE -ne 0) { throw 'Local sandbox scope read failed. No live test started.' }
$names = @('GHPB_RUN_REFRESH_LIVE','GHPB_REFRESH_LIVE_ARTIFACTS','GHPB_REFRESH_LIVE_GH')
$old = @{}; foreach ($name in $names) { $old[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    [Environment]::SetEnvironmentVariable($names[0], '1', 'Process')
    [Environment]::SetEnvironmentVariable($names[1], $directory, 'Process')
    [Environment]::SetEnvironmentVariable($names[2], $GhPath, 'Process')
    git -C $repo rev-parse HEAD | Set-Content (Join-Path $directory 'source.txt')
    git -C $repo status --porcelain | Set-Content (Join-Path $directory 'source-status.txt')
    dotnet --info | Set-Content (Join-Path $directory 'environment.txt')
    dotnet test (Join-Path $repo 'tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj') -c Release --filter 'FullyQualifiedName~RefreshLiveTests' --logger 'trx;LogFileName=live-refresh.trx' --results-directory $directory 2>&1 | Tee-Object (Join-Path $directory 'test.log')
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) { throw "Live refresh failed. Inspect $directory/fixture-evidence.json before any retry; uncertain fixture creation must not be retried blindly." }
    [xml]$trx = Get-Content (Join-Path $directory 'live-refresh.trx')
    $c = $trx.TestRun.ResultSummary.Counters
    if ([int]$c.executed -ne 1 -or [int]$c.passed -ne 1 -or [int]$c.failed -ne 0 -or [int]$c.notExecuted -ne 0) { throw 'Live test did not execute exactly one passing case.' }
    $evidence = Get-Content (Join-Path $directory 'fixture-evidence.json') -Raw | ConvertFrom-Json
    if (-not $evidence.scenarioPassed -or -not $evidence.cleanupPassed) { throw 'Scenario or owned cleanup is unverified.' }
    Write-Host "Product refresh/resolution and separate fixture cleanup verified: $directory"
} finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $old[$name], 'Process') } }

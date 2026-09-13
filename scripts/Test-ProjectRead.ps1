[CmdletBinding()]
param([string]$GhPath = 'C:\Program Files\GitHub CLI\gh.exe')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $GhPath -PathType Leaf)) { throw 'The selected GitHub CLI does not exist.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$results = Join-Path $repoRoot ('TestResults/project-read/' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results | Out-Null
$names = @('GHPB_RUN_PROJECT_READ','GHPB_PROJECT_READ_ARTIFACTS','GHPB_PROJECT_READ_GH','GHPB_PROJECT_READ_SOURCE')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $repoRoot
try {
    $env:GHPB_RUN_PROJECT_READ = '1'
    $env:GHPB_PROJECT_READ_ARTIFACTS = $results
    $env:GHPB_PROJECT_READ_GH = $GhPath
    $env:GHPB_PROJECT_READ_SOURCE = (git rev-parse HEAD | Out-String).Trim()
    $testArguments = @('test', 'tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj', '-c', 'Release',
        '--filter', 'FullyQualifiedName~ProjectReadLiveTests', '--logger', 'trx;LogFileName=read.trx',
        '--results-directory', $results, '--', 'NUnit.NumberOfTestWorkers=0')
    @{
        sourceCommit = $env:GHPB_PROJECT_READ_SOURCE; changes = @(git status --porcelain)
        os = [Environment]::OSVersion.VersionString; sdk = (dotnet --version); powershell = $PSVersionTable.PSVersion.ToString()
        command = 'dotnet'; arguments = $testArguments; at = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'source-environment.json') -Encoding utf8
    dotnet @testArguments *> (Join-Path $results 'test.log')
    $testExitCode = $LASTEXITCODE
    [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'read.trx') -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($testExitCode -ne 0 -or [int]$counts.total -ne 1 -or [int]$counts.executed -ne 1 -or [int]$counts.passed -ne 1 -or [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0) {
        throw "Read-only smoke did not pass completely. See $results"
    }
    Write-Host "Read-only production Project retrieval: 1 executed / 1 passed / 0 failed / 0 skipped. Evidence: $results"
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}

#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Run','Cleanup')][string]$Mode = 'Run',
    [Parameter(Mandatory)][string]$ArtifactsRoot,
    [string]$GhPath = 'C:\Program Files\GitHub CLI\gh.exe',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathFullyQualified($ArtifactsRoot)) { throw 'ArtifactsRoot must be absolute.' }
if (-not (Test-Path -LiteralPath $GhPath -PathType Leaf)) { throw 'The installed gh executable is required.' }
if ($Mode -eq 'Run' -and (Test-Path -LiteralPath $ArtifactsRoot)) { throw 'Run requires a fresh artifacts directory.' }
if (-not $NoBuild) {
    & dotnet build (Join-Path $repoRoot 'tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Ask the PMO to restore missing assets; no restore was attempted.' }
}
$executable = Join-Path $repoRoot 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
$sourceHead = & git -C $repoRoot rev-parse HEAD
$sourceBranch = & git -C $repoRoot branch --show-current
$oldOptIn = $env:GHPB_RUN_PLAN_PUBLISH_PROOF
$oldGh = $env:GHPB_LIVE_GH_PATH
try {
    $env:GHPB_RUN_PLAN_PUBLISH_PROOF = '1'
    $env:GHPB_LIVE_GH_PATH = $GhPath
    & $executable --plan-publish-live $Mode $ArtifactsRoot
    if ($LASTEXITCODE -ne 0) { throw "Live proof failed; preserve all artifacts at $ArtifactsRoot. Cleanup can be run independently with the same directory." }
    Get-Content -LiteralPath (Join-Path $ArtifactsRoot 'plan-publish-live.json')
}
finally {
    $env:GHPB_RUN_PLAN_PUBLISH_PROOF = $oldOptIn
    $env:GHPB_LIVE_GH_PATH = $oldGh
    if (Test-Path -LiteralPath $ArtifactsRoot -PathType Container) {
        @{ head = $sourceHead; branch = $sourceBranch; mode = $Mode; command = $MyInvocation.Line } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactsRoot ("source-" + $Mode.ToLowerInvariant() + ".json"))
    }
}

[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$parent = Join-Path $repo 'TestResults/creation-live'
if (Test-Path -LiteralPath $parent) {
    foreach ($run in Get-ChildItem -LiteralPath $parent -Directory) {
        $cleanup = Join-Path $run.FullName 'cleanup.json'
        if (-not (Test-Path -LiteralPath $cleanup) -or -not (Get-Content -LiteralPath $cleanup -Raw | ConvertFrom-Json).cleanupVerified) {
            throw "Previous creation fixture requires reconciliation before another run: $($run.FullName)"
        }
    }
}
$root = Join-Path $parent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
[IO.File]::WriteAllText((Join-Path $root 'marker.txt'), ('creation11-' + [guid]::NewGuid().ToString('N')))
$prior = $env:GHPB_CREATION_LIVE_ROOT
try {
    $env:GHPB_CREATION_LIVE_ROOT = $root
    & (Join-Path $PSScriptRoot 'Test-E2E.ps1') -Filter 'FullyQualifiedName~LiveCreationTests'
}
finally { $env:GHPB_CREATION_LIVE_ROOT = $prior }

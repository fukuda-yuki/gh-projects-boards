[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo ('TestResults/apply-live/' + [guid]::NewGuid().ToString('N'))
$manifest = Join-Path $root 'fixture.json'
$prior = $env:GHPB_APPLY_FIXTURE
try {
    & (Join-Path $PSScriptRoot 'Manage-RefreshFixture.ps1') -Action Start -Manifest $manifest
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $root 'created-fixture.json')
    $env:GHPB_APPLY_FIXTURE = $manifest
    & (Join-Path $PSScriptRoot 'Test-E2E.ps1') -Filter 'FullyQualifiedName~LiveApplyTests'
}
finally {
    $env:GHPB_APPLY_FIXTURE = $prior
    if (Test-Path -LiteralPath $manifest) {
        $fixture = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($fixture.issue -and $fixture.item) { & (Join-Path $PSScriptRoot 'Manage-RefreshFixture.ps1') -Action Cleanup -Manifest $manifest }
        else { Write-Warning "Incomplete fixture identity retained at $manifest; reconcile before any retry." }
    }
}

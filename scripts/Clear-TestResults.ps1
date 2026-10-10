# Deletes test output that has not been written for a while, so TestResults/ stays bounded without
# anyone deciding what to keep. The test scripts and the Claude Code session hook run it automatically.
[CmdletBinding()]
param(
    [string]$Root = (Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults'),
    [int]$Days = 14
)
$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('Days') -and $env:GHPB_TESTRESULTS_DAYS) {
    if (-not [int]::TryParse($env:GHPB_TESTRESULTS_DAYS, [ref]$Days)) { throw 'GHPB_TESTRESULTS_DAYS must be a whole number of days.' }
}
if ($Days -lt 1) { throw 'The TestResults retention period must be at least one day.' }
if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }

$cutoff = [DateTime]::UtcNow.AddDays(-$Days)
# These scripts add one folder per run, so the folder itself is always recent; judge each run instead.
$runFolders = @('coverage', 'e2e', 'evaluation', 'internal-distribution', 'live', 'project-read', 'ui-integration')

# A folder's own timestamp changes only when its direct children change, so look for any recent write
# inside it; a workspace in use writes deep below its root. Links are not followed.
function Test-RecentlyWritten([IO.FileSystemInfo]$Entry) {
    if ($Entry.LastWriteTimeUtc -ge $cutoff) { return $true }
    if (-not $Entry.PSIsContainer -or $Entry.LinkType) { return $false }
    $recent = Get-ChildItem -LiteralPath $Entry.FullName -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $cutoff } | Select-Object -First 1
    return $null -ne $recent
}

$entries = foreach ($entry in Get-ChildItem -LiteralPath $Root -Force) {
    if ($entry.PSIsContainer -and -not $entry.LinkType -and $runFolders -contains $entry.Name) { Get-ChildItem -LiteralPath $entry.FullName -Force }
    else { $entry }
}
$removed = 0
foreach ($entry in $entries) {
    if (Test-RecentlyWritten $entry) { continue }
    try {
        # Delete a link itself, never what it points to.
        if ($entry.LinkType) { $entry.Delete() }
        else { Remove-Item -LiteralPath $entry.FullName -Recurse -Force }
        $removed++
    }
    catch { Write-Warning "Could not delete $($entry.FullName): $($_.Exception.Message)" }
}
if ($removed -gt 0) { Write-Host "Deleted $removed TestResults entries unchanged for $Days days." }

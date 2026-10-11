[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($env:OS -ne 'Windows_NT') { throw 'Plan performance requires an unlocked Windows desktop.' }
$run = Join-Path $repo ('TestResults/plan-performance/run-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $run | Out-Null
$previous = $env:GHPB_PLAN_EVIDENCE
$attempts = [Collections.Generic.List[object]]::new()
function Median($Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    return ($ordered[[int][Math]::Floor(($ordered.Count - 1) / 2)] + $ordered[[int][Math]::Floor($ordered.Count / 2)]) / 2
}
try {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $repo 'GhProjectsBoards.sln') -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Build failed; no measurement was started.' }
    }
    for ($number = 1; $number -le 5; $number++) {
        $env:GHPB_PLAN_EVIDENCE = Join-Path $run "scroll-$number"
        New-Item -ItemType Directory -Path $env:GHPB_PLAN_EVIDENCE | Out-Null
        $failure = $null
        try {
            & (Join-Path $PSScriptRoot 'Test-UiIntegration.ps1') -NoBuild -Where 'method == VersionRowsScrollRoundTripRecordsFrameIntervalsAndViewportPopulation' -TimeoutSeconds 300
        } catch { $failure = $_.ToString() }
        $path = Join-Path $env:GHPB_PLAN_EVIDENCE 'plan-scroll.json'
        $measurement = if (Test-Path $path) { Get-Content -Raw $path | ConvertFrom-Json } else { $null }
        $phases = @(if ($measurement) {
            foreach ($group in ($measurement.frames | Group-Object Phase)) {
                [ordered]@{ phase = $group.Name; frames = $group.Count
                    medianMs = Median $group.Group.IntervalMs; maxMs = ($group.Group.IntervalMs | Measure-Object -Maximum).Maximum
                    longFrames = @($group.Group | Where-Object IntervalMs -gt 50).Count
                    unpopulatedFrames = @($group.Group | Where-Object { $_.UnpopulatedRows.Count -gt 0 }).Count }
            }
        })
        $depths = @(if ($measurement) {
            foreach ($phase in 'vertical-down', 'vertical-up') {
                for ($quarter = 0; $quarter -lt 4; $quarter++) {
                    $samples = @($measurement.frames | Where-Object {
                        $_.Phase -eq $phase -and $_.VerticalExtent -gt 0 -and
                        [Math]::Min(3, [Math]::Floor(4 * $_.VerticalOffset / $_.VerticalExtent)) -eq $quarter
                    })
                    [ordered]@{ phase = $phase; depthQuarter = $quarter + 1; frames = $samples.Count
                        medianMs = Median $samples.IntervalMs; unpopulatedFrames = @($samples | Where-Object { $_.UnpopulatedRows.Count -gt 0 }).Count }
                }
            }
        })
        $attempts.Add([ordered]@{ attempt = $number; outcome = $measurement.outcome; failure = $failure
            measurementFailure = $measurement.failure; complete = ($null -eq $failure -and $measurement.outcome -eq 'completed')
            elapsedMs = ($measurement.frames.ElapsedMs | Measure-Object -Maximum).Maximum
            maxRealizedRows = $measurement.maxRealizedRows; phases = $phases; depthQuarters = $depths })
        $attempts | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'scroll-attempts.json') -Encoding utf8
        # A failed/partial attempt remains evidence and does not suppress the remaining attempts.
    }
    $env:GHPB_PLAN_EVIDENCE = Join-Path $run 'hosted-edits'
    try {
        & (Join-Path $PSScriptRoot 'Test-UiIntegration.ps1') -NoBuild -Where 'method == VersionRowsCommitToRenderedDatesAndBarsRecordsTwentySamples or method == VersionRowsFilterApplicationRecordsPauseAndFrameTimings' -TimeoutSeconds 300
    } catch { $_ | Out-String | Set-Content -LiteralPath (Join-Path $run 'hosted-edit-failure.txt'); $hostedFailure = $true }
    try {
        & (Join-Path $PSScriptRoot 'Test-E2E.ps1') -NoBuild -Filter 'TestCategory=PlanScreenPerformance'
    } catch { $_ | Out-String | Set-Content -LiteralPath (Join-Path $run 'screen-failure.txt'); $screenFailure = $true }
    if (@($attempts | Where-Object { -not $_.complete }).Count -gt 0 -or $hostedFailure -or $screenFailure) {
        throw "One or more measurements failed; all available evidence is preserved: $run"
    }
} finally {
    $env:GHPB_PLAN_EVIDENCE = $previous
    Write-Host "Plan performance evidence: $run"
}

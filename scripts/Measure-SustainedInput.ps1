[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Observations,
    [ValidateSet('functional','normal_scroll','stress_scroll','editor_readiness','continuous_progress','observation_quality')]
    [string[]]$RequiredSections
)
$ErrorActionPreference='Stop'
$plan=Get-Content -Raw -LiteralPath (Join-Path $Observations 'plan.json') | ConvertFrom-Json
$driver=@(Get-Content -LiteralPath (Join-Path $Observations 'driver.jsonl') | ConvertFrom-Json)
$trace=@(if(Test-Path -LiteralPath (Join-Path $Observations 'app-trace.jsonl')) { Get-Content -LiteralPath (Join-Path $Observations 'app-trace.jsonl') | ConvertFrom-Json })
$frequency=[double]$plan.frequency
function Distribution($values) {
    $sorted=@($values | Sort-Object)
    if ($sorted.Count -eq 0) { return @{count=0} }
    return @{count=$sorted.Count; p50Ms=$sorted[[Math]::Ceiling($sorted.Count*0.50)-1]; p95Ms=$sorted[[Math]::Ceiling($sorted.Count*0.95)-1]; maxMs=$sorted[-1]; over100Ms=@($sorted | Where-Object { $_ -ge 100 }).Count}
}
$input=@($driver | Where-Object kind -eq 'typed-value' | ForEach-Object detail)
$spans=@($trace | Where-Object kind -eq 'ui-span' | ForEach-Object data)
$core=@($trace | Where-Object kind -eq 'core-checkpoint-trace' | ForEach-Object { $_.data.samples })
$start=($driver | Where-Object kind -eq 'workload-start').detail.start
$end=($driver | Where-Object kind -eq 'workload-end').ticks
$typed=@($input | Group-Object phase | ForEach-Object { @{phase=$_.Name; measured=$_.Group[0].measured; latency=(Distribution @($_.Group.milliseconds))} })
$ui=@($spans | Where-Object { $_.start -ge $start -and $_.end -le $end } | Group-Object kind | ForEach-Object {
    @{kind=$_.Name; latency=(Distribution @($_.Group | ForEach-Object { $_.elapsedTicks*1000/$frequency })); allocatedBytes=($_.Group.managedAllocatedBytesOnThread | Measure-Object -Sum).Sum}
})
$slow=@($input | Where-Object { $_.milliseconds -ge 100 } | ForEach-Object {
    $sample=$_
    @{phase=$sample.phase; index=$sample.index; milliseconds=$sample.milliseconds; begin=$sample.begin; end=$sample.end;
      overlappingUiSpans=@($spans | Where-Object { $_.start -le $sample.end -and $_.end -ge $sample.begin } | ForEach-Object { @{kind=$_.kind; reason=$_.reason; milliseconds=$_.elapsedTicks*1000/$frequency; start=$_.start; end=$_.end} })}
})
$gaps=@($trace | Where-Object { $_.kind -eq 'rendering-callback' -and $_.ticks -ge $start -and $_.ticks -le $end -and $_.data.previousGapTicks -and ($_.ticks - $_.data.previousGapTicks) -ge $start } | ForEach-Object { @{ticks=$_.ticks; milliseconds=$_.data.previousGapTicks*1000/$frequency} })
$captures=@(if (Test-Path -LiteralPath (Join-Path $Observations 'scroll-captures.json')) { Get-Content -LiteralPath (Join-Path $Observations 'scroll-captures.json') | ConvertFrom-Json })
$captureGaps=@(for ($i=1; $i -lt $captures.Count; $i++) { @{index=$captures[$i].index; milliseconds=($captures[$i].begin-$captures[$i-1].begin)*1000/$frequency} })
$captureFiles=@($captures | ForEach-Object { $file=Join-Path $Observations $_.path; @{index=$_.index; path=$_.path; exists=(Test-Path -LiteralPath $file); sha256=if(Test-Path -LiteralPath $file){(Get-FileHash -LiteralPath $file).Hash}else{$null}} })
$terminal=@($trace | Where-Object kind -eq 'trace-end')
$sequences=@($trace | Where-Object sequence | ForEach-Object sequence | Sort-Object)
$missingSequences=@(for($i=1;$i -lt $sequences.Count;$i++){ if($sequences[$i] -ne $sequences[$i-1]+1){@{before=$sequences[$i-1];after=$sequences[$i]}} })
$complete=$terminal.Count -eq 1 -and $terminal[0].data.queued -eq 0 -and $terminal[0].data.dropped -eq 0 -and $terminal[0].data.failed -eq 0 -and $missingSequences.Count -eq 0 -and @($trace | Where-Object kind -eq 'records-dropped').Count -eq 0
$summary=@{
    source=$plan.source; condition=$plan.condition; typed=$typed; ui=$ui
    traceComplete=$complete; terminal=$terminal; sequenceGaps=$missingSequences
    flushRequests=@($trace | Where-Object { $_.kind -eq 'flush-request' -and $_.ticks -ge $start -and $_.ticks -le $end }).Count
    processLifetimeFlushRequests=@($trace | Where-Object kind -eq 'flush-request').Count
    durableCommits=($core | Where-Object { $_.Kind -eq 'checkpoint-commits' -and $_.Start -ge $start -and $_.Start -le $end } | Measure-Object Count -Sum).Sum
    processLifetimeDurableCommits=($core | Where-Object Kind -eq 'checkpoint-commits' | Measure-Object Count -Sum).Sum
    captureDurations=(Distribution @($captures | ForEach-Object { ($_.end-$_.begin)*1000/$frequency })); captureGaps=(Distribution @($captureGaps.milliseconds))
    captureStalls=@($captureGaps | Where-Object milliseconds -ge 100); captureFiles=$captureFiles
    coreSpans=@($core | Where-Object { $_.Start -ge $start -and $_.End -le $end -and $_.End -gt $_.Start } | Group-Object Kind | ForEach-Object { @{kind=$_.Name; latency=(Distribution @($_.Group | ForEach-Object { ($_.End-$_.Start)*1000/$frequency }))} })
    renderingGaps=(Distribution @($gaps.milliseconds)); renderingStalls=@($gaps | Where-Object milliseconds -ge 100)
    slowInput=$slow
    caveats='UI spans are inclusive, not additive; Core async spans are wall time, not CPU time. Rendering callbacks precede composition. Native-value latency includes UIA observer overhead. No human acceptance or physical scanout claim.'
}
$summary | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $Observations 'measurements.json')
$summary | Select-Object source,condition,traceComplete,typed,flushRequests,durableCommits,renderingGaps,captureGaps | ConvertTo-Json -Depth 6

# State success is not a performance verdict. Keep missing observations explicit
# and make the caller select the sections whose acceptance it is evaluating.
$profile = if($plan.scrollProfile) { $plan.scrollProfile } else { 'stress' }
$scrollSection = switch($profile) { 'stress' { 'stress_scroll' }; 'continuous' { 'continuous_progress' }; default { 'normal_scroll' } }
if (-not $RequiredSections) { $RequiredSections = @('functional', $(if($plan.mode -eq 'readiness'){'editor_readiness'}else{$scrollSection}), 'observation_quality') }
function Result($status, $scope) { return @{status=$status; scope=$scope} }
$evaluation = @{
    source=$plan.source; profile=$profile; condition=$plan.condition; required=$RequiredSections
    acceptanceRole='100-ms improvement metric and observation completeness only. Daily-workflow engineering acceptance is a separate Issue #65 review; exceeding this target alone is not a reason to continue the implementation loop.'
    targetMilliseconds=100
    functional=(Result 'INCONCLUSIVE' 'This run only: exact pending Title/NUMBER, normal close, independent checkpoint, preserved history and planning attribution. Broader G1/FLOW coverage is separate.')
    normal_scroll=(Result 'NOT_RUN' 'N1 +/-1 and +/-3 remain separate profiles; each demanded readable response <=100 ms.')
    stress_scroll=(Result 'NOT_RUN' 'S80 unchanged large-jump waveform; each readable response <=100 ms.')
    editor_readiness=(Result 'INCONCLUSIVE' 'Native-value typing recorded; selection-to-visible first-character readiness is not established by focus/UIA lookup.')
    continuous_progress=(Result 'NOT_RUN' 'N2 fixed 20-second/100-ms demand; complete readable progress required, excluding clamped input.')
    observation_quality=(Result 'INCONCLUSIVE' 'Trace drain, aligned nonzero DXGI timestamps, retained pixels, complete updates and independently reviewed content.')
    unverified_scope=(Result 'NOT_RUN' 'Full G1, FLOW-01..06, E1 selection-to-visible, physical IME, accessibility, final cold3/warm3 and human acceptance are not supplied by this individual diagnostic.')
}
$durableFile=Join-Path $Observations 'durable-readback.json'
$lifeFile=Join-Path $Observations 'lifetime.json'
if (@($driver | Where-Object kind -eq 'failure').Count -gt 0) { $evaluation.functional.status='FAIL' }
elseif ((Test-Path -LiteralPath $durableFile) -and (Test-Path -LiteralPath $lifeFile)) {
    $durable=Get-Content -Raw -LiteralPath $durableFile | ConvertFrom-Json
    $life=Get-Content -Raw -LiteralPath $lifeFile | ConvertFrom-Json
    $evaluation.functional.status=if($durable.passed -and $life.normal -and -not $life.forced){'PASS'}else{'FAIL'}
}
if (Test-Path -LiteralPath (Join-Path $Observations 'desktop/frames.json')) {
    & python (Join-Path $PSScriptRoot 'Measure-SustainedDesktop.py') $Observations
    if ($LASTEXITCODE -eq 0) {
        $desktop=Get-Content -Raw -LiteralPath (Join-Path $Observations 'desktop-measurements.json') | ConvertFrom-Json
        $eligible=@($desktop.commands | Where-Object { -not $_.noOp })
        $evaluation[$scrollSection].status=if(@($eligible | Where-Object status -eq 'FAIL').Count -gt 0){'FAIL'}
            elseif($eligible.Count -gt 0 -and @($eligible | Where-Object status -ne 'PASS').Count -eq 0){'PASS'}else{'INCONCLUSIVE'}
        $evaluation[$scrollSection].samples=$eligible.Count
        $evaluation[$scrollSection].counts=$desktop.counts
        $evaluation.observation_quality.status=if($complete -and $desktop.observationQuality -eq 'PASS' -and
            $eligible.Count -gt 0 -and @($eligible | Where-Object { $_.status -eq 'INCONCLUSIVE' }).Count -eq 0){'PASS'}else{'INCONCLUSIVE'}
    }
}
if ($plan.mode -eq 'readiness' -and (Test-Path -LiteralPath (Join-Path $Observations 'readiness-desktop/frames.json'))) {
    & python (Join-Path $PSScriptRoot 'Measure-SustainedDesktop.py') $Observations
    if ($LASTEXITCODE -eq 0) {
        $readiness=Get-Content -Raw -LiteralPath (Join-Path $Observations 'editor-readiness-measurements.json') | ConvertFrom-Json
        $evaluation.editor_readiness=@{status=$readiness.status; scope=$readiness.boundary; selectionVisibleP95Ms=$readiness.selectionVisibleP95Ms;
            continuingNativeP95Ms=$readiness.continuingNativeP95Ms; selectionSamples=$readiness.selectionSamples; continuingSamples=$readiness.continuingSamples; keyDelayMs=$readiness.keyDelayMs}
        $evaluation.observation_quality.status=if($complete -and $readiness.observationQuality -eq 'PASS' -and
            @($readiness.trials | Where-Object { -not $_.contentReviewed }).Count -eq 0){'PASS'}else{'INCONCLUSIVE'}
    }
}
$evaluation.overall=if(@($RequiredSections | Where-Object { $evaluation[$_].status -eq 'FAIL' }).Count -gt 0){'FAIL'}
    elseif(@($RequiredSections | Where-Object { $evaluation[$_].status -ne 'PASS' }).Count -gt 0){'INCONCLUSIVE'}else{'PASS'}
$evaluation.evaluatorSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash
$evaluation.desktopEvaluatorSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Measure-SustainedDesktop.py')).Hash
$evaluation | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Observations 'evaluation.json')
$evaluation | ConvertTo-Json -Depth 6
if($evaluation.overall -ne 'PASS') { throw "Required performance evaluation is $($evaluation.overall); see evaluation.json. State-test success does not override this outcome." }

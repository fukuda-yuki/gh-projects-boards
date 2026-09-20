[CmdletBinding()]
param([Parameter(Mandatory)][string]$Observations)
$ErrorActionPreference='Stop'
$plan=Get-Content -Raw -LiteralPath (Join-Path $Observations 'plan.json') | ConvertFrom-Json
$driver=@(Get-Content -LiteralPath (Join-Path $Observations 'driver.jsonl') | ConvertFrom-Json)
$trace=@(Get-Content -LiteralPath (Join-Path $Observations 'app-trace.jsonl') | ConvertFrom-Json)
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

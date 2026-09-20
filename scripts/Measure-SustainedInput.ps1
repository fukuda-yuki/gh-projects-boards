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
$gaps=@($trace | Where-Object { $_.kind -eq 'rendering-callback' -and $_.ticks -ge $start -and $_.ticks -le $end -and $_.data.previousGapTicks } | ForEach-Object { @{ticks=$_.ticks; milliseconds=$_.data.previousGapTicks*1000/$frequency} })
$summary=@{
    source=$plan.source; condition=$plan.condition; typed=$typed; ui=$ui
    flushRequests=@($trace | Where-Object kind -eq 'flush-request').Count
    durableCommits=($core | Where-Object Kind -eq 'checkpoint-commits' | Measure-Object Count -Sum).Sum
    coreSpans=@($core | Where-Object { $_.Start -ge $start -and $_.End -le $end -and $_.End -gt $_.Start } | Group-Object Kind | ForEach-Object { @{kind=$_.Name; latency=(Distribution @($_.Group | ForEach-Object { ($_.End-$_.Start)*1000/$frequency }))} })
    renderingGaps=(Distribution @($gaps.milliseconds)); renderingStalls=@($gaps | Where-Object milliseconds -ge 100)
    slowInput=$slow
    caveats='UI spans are inclusive, not additive; Core async spans are wall time, not CPU time. Rendering callbacks precede composition. Native-value latency includes UIA observer overhead. No human acceptance or physical scanout claim.'
}
$summary | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $Observations 'measurements.json')
$summary | Select-Object source,condition,typed,flushRequests,durableCommits,renderingGaps | ConvertTo-Json -Depth 6

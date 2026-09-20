[CmdletBinding()]
param([Parameter(Mandatory)][string]$Trace, [Parameter(Mandatory)][string]$TraceEventAssembly, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
Add-Type -Path ([IO.Path]::GetFullPath($TraceEventAssembly))
$source=[Microsoft.Diagnostics.Tracing.EventPipeEventSource]::new([IO.Path]::GetFullPath($Trace))
$events=[Collections.Generic.List[object]]::new()
$allocations=@{}
function EventRow($kind, $event, $detail) {
    $events.Add(@{kind=$kind; milliseconds=$event.TimeStampRelativeMSec; utc=$event.TimeStamp.ToUniversalTime().ToString('o'); thread=$event.ThreadID; detail=$detail})
}
$source.Clr.add_GCStart({ param($event) EventRow 'gc-start' $event @{count=$event.Count; generation=$event.Depth; reason=$event.Reason.ToString(); type=$event.Type.ToString()} })
$source.Clr.add_GCStop({ param($event) EventRow 'gc-stop' $event @{count=$event.Count; generation=$event.Depth} })
$source.Clr.add_GCSuspendEEStart({ param($event) EventRow 'suspend-start' $event @{reason=$event.Reason.ToString()} })
$source.Clr.add_GCRestartEEStop({ param($event) EventRow 'restart-stop' $event @{} })
$source.Clr.add_GCAllocationTick({ param($event)
    $name=$event.TypeName
    if (!$name) { $name='unknown' }
    if (!$allocations.ContainsKey($name)) { $allocations[$name]=0L }
    $allocations[$name]+=$event.AllocationAmount64
})
try {
    # TraceEvent's dynamic binder exposes Process as Boolean; invoke the declared
    # method to avoid PowerShell's Object-return call-site mismatch.
    [void]$source.GetType().GetMethod('Process', [Type[]]@()).Invoke($source, @())
    @{
        sourceFile=[IO.Path]::GetFileName($Trace); sessionStartUtc=$source.SessionStartTime.ToUniversalTime().ToString('o'); lostEvents=$source.EventsLost
        events=$events; sampledAllocations=@($allocations.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { @{type=$_.Key; sampledBytes=$_.Value} })
        boundary='EventPipe GC reason, generation, runtime suspension/restart and sampled managed allocations. Sampled stack report is separate; neither report is physical presentation.'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output
    $events | Where-Object kind -eq 'gc-start' | Group-Object {$_.detail.reason + '/' + $_.detail.generation} | Select-Object Name,Count
    $allocations.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 Name,Value
} finally { $source.Dispose() }

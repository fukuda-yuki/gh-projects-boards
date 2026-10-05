# Offline tests of the real measurement orchestration; only the gh boundary is replaced.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/Measure-PlanningSandbox.ps1')
$script:checks=0
function Check($Condition,[string]$Reason){$script:checks++;if(-not $Condition){throw $Reason}}
function Reset-Remote {
    $script:RemoteIssues=@(@{id='I1';number=1;title='Scope';body='Keep';state='CLOSED';updatedAt='2026-10-05';repository=@{id='R';nameWithOwner='fukuda-yuki/codex-sandbox'}})
    $script:RemoteItems=@(@{id='P1';type='ISSUE';isArchived=$false;content=$script:RemoteIssues[0];fieldValues=@{nodes=@();totalCount=0;pageInfo=@{hasNextPage=$false}}})
    $script:RemoteFields=@(@{id='N';name='Remaining';dataType='NUMBER'},@{id='D';name='Start date';dataType='DATE'})
    $script:GoneDelete=$null;$script:BadDelete=$false;$script:MembershipReads=0;$script:HiddenReads=0;$script:AddMode='normal';$script:Waits=@();$script:Overflow=$false;$script:Documents=@();$script:FailCreate=$false;$script:CorruptValue=$false;$script:NextIssue=2;$script:RemoteValues=@{}
}
function Wait-MeasurementPoll([int]$Milliseconds){ $script:Waits+=$Milliseconds }
function Connection($nodes){@{nodes=@($nodes);totalCount=@($nodes).Count;pageInfo=@{hasNextPage=$false;endCursor=$null}}}
function Invoke-MeasurementTransport([string]$Query,[hashtable]$Variables=@{}){
    $script:Documents+=$Query
    if($Query -match '^query MeasureMembership'){
        $script:MembershipReads++
        if($script:AddMode -eq 'observed' -and $script:MembershipReads -eq 2){
            foreach($issue in $script:RemoteIssues){
                if($issue.id -notin @($script:RemoteItems.content.id)){$script:RemoteItems+=@{id='P'+$issue.number;content=$issue;fieldValues=(Connection @())}}
            }
        }
        if($script:HiddenReads -gt 0){$script:HiddenReads--;return @{node=@{items=(Connection @($script:RemoteItems|Where-Object id -eq 'P1'))}}}
        return @{node=@{items=(Connection $script:RemoteItems)}}
    }
    if($Query -match '^query MeasureValues'){return @{node=@{fieldValues=(Connection @())}}}
    if($Query -match '^query MeasureRelations'){
        $name=if($Query.Contains('assignees(')){'assignees'}else{'blockedBy'}
        return @{node=@{$name=(Connection @())}}
    }
    if($Query -match '^query MeasureScope') { $scope=$script:RemoteIssues[0].Clone();foreach($k in @('repository','assignees','blockedBy','__typename')){$scope.Remove($k)}; return @{repository=@{id='R';nameWithOwner='fukuda-yuki/codex-sandbox';issue=$scope};user=@{login='fukuda-yuki';projectV2=@{id='P';number=3;url='https://github.com/users/fukuda-yuki/projects/3'}}} }
    if($Query -match '^query MeasureFields'){return @{node=@{fields=(Connection $script:RemoteFields)}}}
    if($Query -match '^query MeasureOwnedIssues'){return @{node=@{issues=(Connection $script:RemoteIssues)}}}
    if($Query -match '^query MeasureItems'){
        foreach($item in $script:RemoteItems){
            if ($item.content -and $item.type -ne 'DRAFT_ISSUE') { $item.content.assignees=Connection @();$item.content.blockedBy=Connection @();$item.content.__typename='Issue' }
            $values=@(foreach($field in @('N','D')){if($script:RemoteValues.ContainsKey($item.id+'-'+$field)){
                $v=$script:RemoteValues[$item.id+'-'+$field].Clone();$v.field=@{id=$field};if($script:CorruptValue -and $field -eq 'N'){$v.number=-999};$v
            }})
            $item.fieldValues=Connection $values
            if ($script:Overflow -and $item.content.__typename -eq 'Issue') {
                $item.fieldValues.pageInfo.hasNextPage=$true
                $item.content.assignees.pageInfo.hasNextPage=$true
                $item.content.blockedBy.pageInfo.hasNextPage=$true
            }
        }
        return @{node=@{items=(Connection $script:RemoteItems)}}
    }
    $results=@{};$errors=@()
    if($Query -match '^mutation MeasureCreate'){$script:MembershipReads=0}
    if($Query -match '^mutation MeasureAdd' -and $script:AddMode -in @('lag','invisible')){
        $script:HiddenReads=if($script:AddMode -eq 'lag'){2}else{99}
    }
    foreach($key in @($Variables.Keys | Sort-Object { [int]$_.Substring(1) })){
        $input=$Variables[$key];$alias='m'+$key.Substring(1)
        if($Query -match '^mutation MeasureCreate'){
            $number=$script:NextIssue++;$issue=@{id="I$number";number=$number;title=$input.title;body=$input.body;repository=@{id='R';nameWithOwner='fukuda-yuki/codex-sandbox'}}
            $script:RemoteIssues+= $issue;$results[$alias]=@{issue=@{id=$issue.id;number=$number}}
        }elseif($Query -match '^mutation MeasureAdd'){
            $issue=$script:RemoteIssues | Where-Object id -eq $input.contentId
            $existing=@($script:RemoteItems|Where-Object { $_.content.id -eq $issue.id })
            $item=if($existing.Count){$existing[0]}else{@{id='P'+$issue.number;content=$issue;fieldValues=(Connection @())}}
            if(-not $existing.Count){$script:RemoteItems+=$item}
            if($existing.Count -or $script:AddMode -eq 'all' -or ($script:AddMode -in @('partial','other','lag','invisible') -and $alias -eq 'm8')){
                $message=if($script:AddMode -eq 'other'){'Field is invalid'}else{'Content already exists in this project'}
                $errors+=@{message=$message;path=@($alias);type='UNPROCESSABLE'};$results[$alias]=$null
            }else{$results[$alias]=@{item=$item}}
        }elseif($Query -match '^mutation MeasureUpdate'){
            $script:RemoteValues[$input.itemId+'-'+$input.fieldId]=$input.value.Clone();$results[$alias]=@{projectV2Item=@{id=$input.itemId}}
        }elseif($Query -match '^mutation MeasureRemoveItem'){
            $script:RemoteItems=@($script:RemoteItems|Where-Object id -ne $input.itemId);$results[$alias]=@{deletedItemId=$input.itemId}
        }elseif($Query -match '^mutation MeasureDeleteIssue'){
            Check ($input.issueId -ne 'I1') 'Scope Issue deletion attempted'
            $script:RemoteIssues=@($script:RemoteIssues|Where-Object id -ne $input.issueId);$results[$alias]=@{clientMutationId=$(if($script:BadDelete -and $alias -eq 'm0'){'wrong'}else{$input.clientMutationId})}
            if($script:GoneDelete -eq 'NOT_FOUND'){$results[$alias]=$null;$errors+=@{type='NOT_FOUND';message='Could not resolve Issue';path=@($alias)}}
        }else{throw 'Unexpected fixture query'}
    }
    if($Query -match '^mutation MeasureCreate' -and $script:FailCreate){$script:FailCreate=$false;throw 'Simulated response lost after server creation'}
    if($Query -match '^mutation MeasureDeleteIssue' -and $script:GoneDelete -eq '410'){
        return Resolve-MeasurementResponse $null 'MeasureDeleteIssue' $Variables 1 $true $null 410
    }
    $operation=if($Query -match '^mutation (\w+)'){$Matches[1]}else{'unknown'}
    return Resolve-MeasurementResponse @{data=$results;errors=$errors} $operation $Variables $(if($errors.Count){1}else{0}) $true $null
}
$cases=@(
    @{name='Issues gone between discovery and deletion count as removed for NOT_FOUND and HTTP 410';run={
        foreach($gone in @('NOT_FOUND','410')){
            Reset-Remote;$script:GoneDelete=$gone
            $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
            Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
            Check ($script:State.cleanup.verified -and $script:RemoteIssues.Count -eq 1) 'Already-gone Issue blocked cleanup'
            Check (@($script:State.deletions|Where-Object alreadyGone).Count -eq 50) 'Already-gone outcomes not recorded'
        }
    }},
    @{name='Resource limit is a distinct persisted stop reason';run={
        Reset-Remote
        $script:RunDirectory=Join-Path $PSScriptRoot ('../TestResults/live/g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory $script:RunDirectory|Out-Null
        $script:State=@{deleteResponses=@()}
        $failed=$false
        try{Resolve-MeasurementResponse @{data=@{m0=$null};errors=@(@{type='RESOURCE_LIMITS_EXCEEDED';message='Resource limits for this query exceeded.';path=@('m0','clientMutationId')})} 'MeasureDeleteIssue' @{v0=@{issueId='I2'}} 1 $true $null}
        catch{$failed=$_.Exception.Message -like '*RESOURCE_LIMITS_EXCEEDED*'}
        Check ($failed -and $script:State.stopReason -eq 'RESOURCE_LIMITS_EXCEEDED') 'Resource limit misclassified'
    }},
    @{name='Duplicate classification cannot bypass transport stops or unknown alias paths';run={
        Reset-Remote
        $script:RunDirectory=Join-Path $PSScriptRoot ('../TestResults/live/g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory $script:RunDirectory|Out-Null
        $script:State=@{addResponses=@()}
        foreach($scenario in @(
            @{alias='m0';completed=$true;exit=1;rate='primary'},
            @{alias='m0';completed=$false;exit=1;rate=$null},
            @{alias='m0';completed=$true;exit=2;rate=$null},
            @{alias='m99';completed=$true;exit=1;rate=$null}
        )){
            $body=@{data=@{m0=$null};errors=@(@{type='UNPROCESSABLE';message='Content already exists in this project';path=@($scenario.alias)})}
            $failed=$false;try{Resolve-MeasurementResponse $body 'MeasureAdd' @{v0=@{contentId='I2'}} $scenario.exit $scenario.completed $scenario.rate}catch{$failed=$true}
            Check $failed 'Unsafe duplicate response accepted'
        }
    }},
    @{name='Auto-added items first appear at the second observation and stop waiting';run={
        Reset-Remote;$script:AddMode='observed'
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        $observation=$script:State.autoAddObservations[0]
        Check ($observation.probes.Count -eq 2 -and $observation.requestedWaitMs -eq 1000 -and $observation.found.Count -eq 10) 'Auto-add appearance not observed'
        Check ($observation.probes[0].visibleContentIds.Count -eq 0 -and $observation.probes[1].visibleContentIds.Count -eq 10) 'Visibility history lost'
        Check ($observation.found[0].firstObservedMs -ge $observation.found[0].probeStartMs) 'Invalid time bounds'
        Check ($script:State.addsAlreadyPresent -eq 50 -and $script:State.cleanup.verified) 'Observed memberships duplicated or lost'
    }},
    @{name='Duplicate reconciliation waits for delayed visibility without recreating';run={
        Reset-Remote;$script:AddMode='lag'
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.addReconciliations[0].probes.Count -eq 3 -and $script:State.addReconciliations[0].found.Count -eq 1) 'Delayed membership not reconciled'
        Check ($script:NextIssue -eq 52 -and $script:State.cleanup.verified) 'Creation replayed'
    }},
    @{name='Invisible duplicate membership exhausts bounded reads and stops';run={
        Reset-Remote;$script:AddMode='invisible'
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        $failed=$false;try{Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3}catch{$failed=$_.Exception.Message -like 'Already-present item not visible*'}
        Check ($failed -and $script:NextIssue -eq 12 -and $script:State.addSuccesses.Count -eq 9) 'Unresolved add continued or lost successes'
        Check ($script:State.addReconciliations[0].probes.Count -eq 3 -and $script:State.cleanup.verified) 'Unbounded reconciliation or cleanup failure'
    }},
    @{name='Wrong delete alias receipt stops cleanup and later cleanup rediscovers survivors';run={
        Reset-Remote;$script:BadDelete=$true
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        $failed=$false;try{Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3}catch{$failed=$true}
        Check ($failed -and -not $script:State.cleanup.verified -and $script:RemoteIssues.Count -eq 50) 'Bad delete receipt was accepted'
        Check ($script:State.deleteResponses[0].data.m0.clientMutationId -eq 'wrong') 'Other alias receipt lost'
        $script:BadDelete=$false
        Start-PlanningMeasurement 'Cleanup' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.cleanup.verified -and $script:RemoteIssues.Count -eq 1) 'Survivor cleanup failed'
    }},
    @{name='One duplicate alias preserves nine successful adds and resolves the existing item';run={
        Reset-Remote;$script:AddMode='partial'
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.addsAlreadyPresent -eq 5 -and $script:State.items.Count -eq 50) 'Partial batch results lost'
        Check ($script:State.cleanup.verified -and $script:NextIssue -eq 52) 'Recreated Issues or cleanup failed'
        Check ($script:State.autoAddObservations.Count -eq 5) 'Missing consistency observations'
        Check ($script:State.autoAddObservations[0].probes.Count -eq 3 -and $script:State.autoAddObservations[0].requestedWaitMs -eq 3000) 'Observation window missing'
    }},
    @{name='Every add alias already exists and all item identities are reconciled';run={
        Reset-Remote;$script:AddMode='all'
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.addsAlreadyPresent -eq 50 -and @($script:State.items|Select-Object -Unique).Count -eq 50) 'All-duplicate batch not reconciled'
        Check ($script:State.cleanup.verified -and $script:NextIssue -eq 52) 'Creation repeated'
    }},
    @{name='Other UNPROCESSABLE add errors stop while preserving partial evidence';run={
        Reset-Remote;$script:AddMode='other'
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        $failed=$false;try{Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3}catch{$failed=$true}
        Check ($failed -and -not $script:State.measurementCompleted -and $script:NextIssue -eq 12) 'Unexpected error continued or recreated Issues'
        Check ($script:State.addResponses[0].data.m0.item.id -eq 'P2' -and $script:State.addResponses[0].errors[0].message -eq 'Field is invalid') 'Partial evidence lost'
        Check $script:State.cleanup.verified 'Partial failure left resources'
    }},
    @{name='Cleanup deletes one Issue per request with receipts and baseline readback';run={
        Reset-Remote
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.deleteResponses.Count -eq 50 -and @($script:State.deleteResponses|Where-Object {$_.inputs.Count -ne 1}).Count -eq 0) 'Deletion payload must contain exactly one Issue'
        Check ($script:State.deletions.Count -eq 50 -and @($script:State.deletions|Where-Object {-not $_.verified}).Count -eq 0) 'Deletion receipts incomplete'
        Check ($script:State.cleanup.verified -and $script:RemoteIssues.Count -eq 1) 'Deletion readback failed'
    }},
    @{name='Overflow documents for values and both relations pass the transport guard';run={
        Reset-Remote; $script:Overflow=$true; $script:State=@{projectId='P'}
        $snapshot=Read-MeasurementProject
        Check ($snapshot.items.Count -eq 1 -and $script:Documents.Count -eq 5) 'Overflow paths did not complete'
        Check (@($script:Documents | Where-Object {$_ -match '^query MeasureRelations'}).Count -eq 2) 'Missing relation documents'
    }},
    @{name='Existing evidence survives a new marker and cannot be replayed';run={
        Reset-Remote
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        $folder=Join-Path $PSScriptRoot ('../TestResults/live/'+$marker)
        New-Item -ItemType Directory $folder | Out-Null
        $evidence=Join-Path $folder 'results.json'
        Set-Content -LiteralPath $evidence -Value '{"failed":true,"created":0}'
        $before=(Get-FileHash $evidence).Hash
        $failed=$false;try{Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3}catch{$failed=$_.Exception.Message -like 'Existing run*'}
        Check $failed 'Existing evidence was replayed'
        $fresh='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $fresh 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.cleanup.verified -and (Get-FileHash $evidence).Hash -eq $before) 'New marker altered old evidence'
    }},
    @{name='Malformed documents are rejected before transport';run={
        foreach($query in @('query X { node(id:"x"){id}', 'query X { node(id:"x" {id}}', 'query X { node {,} }', 'query X { node([)]{id}}', 'query X { node { # empty
 } }', 'query X {node(id:"unterminated){id}}')){
            $rejected=$false;try{Invoke-MeasurementGraphQL $query}catch{$rejected=$_.Exception.Message -like 'Invalid GraphQL document*'}
            Check $rejected ('Malformed query accepted: '+$query)
        }
        Assert-MeasurementDocument 'query X {node(id:"{}()"){id} # ignored }
 }'
        Assert-MeasurementDocument 'query X {node(text:"""{}()"""){id}}'
        Assert-MeasurementDocument 'query X {node(text:"escaped \" }"){id}}'
    }},
    @{name='Items document has balanced selections';run={
        Check (($script:ItemsQuery.ToCharArray() | Where-Object {$_ -eq '{'}).Count -eq ($script:ItemsQuery.ToCharArray() | Where-Object {$_ -eq '}'}).Count) 'Unbalanced Items query'
    }},
    @{name='GraphQL diagnostics retain message path and nullable type';run={
        $errors=@(ConvertTo-MeasurementErrors @(@{message='Expected NAME';path=@('node','items',0);type=$null}))
        Check ($errors.Count -eq 1 -and $errors[0].message -eq 'Expected NAME' -and $errors[0].path[2] -eq 0 -and $null -eq $errors[0].type) 'Lost GraphQL diagnostics'
        Check (@(ConvertTo-MeasurementErrors $null).Count -eq 0) 'Absent errors must be empty'
    }},
    @{name='Null and non-Issue content survive baseline and cleanup';run={
        Reset-Remote
        $script:RemoteItems+=@{id='P-unreadable';content=$null;fieldValues=(Connection @())}
        $script:RemoteItems+=@{id='P-draft';type='DRAFT_ISSUE';content=@{__typename='DraftIssue'};fieldValues=(Connection @())}
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.cleanup.verified -and ($script:RemoteItems.id -join ',') -eq 'P1,P-unreadable,P-draft') 'Baseline item identity/order lost'
    }},
    @{name='Plan does not call transport';run={
        $plan=Start-PlanningMeasurement 'Plan' '' 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3 | ConvertFrom-Json
        Check ($plan.mutations -eq 0 -and $plan.network -eq $false) 'Plan must be offline'
    }},
    @{name='Other scopes are refused';run={
        $rejected=$false;try{Start-PlanningMeasurement 'Plan' '' 'fukuda-yuki/gh-projects-boards' 'fukuda-yuki' 3}catch{$rejected=$true}
        Check $rejected 'Unsafe repository accepted'
        $rejected=$false;try{Start-PlanningMeasurement 'Plan' '' 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 4}catch{$rejected=$true}
        Check $rejected 'Unsafe Project accepted'
    }},
    @{name='Complete run verifies updates and restores preexisting resources';run={
        Reset-Remote
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check ($script:State.cleanup.verified -and $script:RemoteIssues.Count -eq 1 -and $script:RemoteItems.Count -eq 1) 'Cleanup failed'
        Check ($script:State.timings.Count -eq 8) 'Timing series incomplete'
        Check ($script:State.updatesVerified -eq $true) 'Updates were not independently verified'
    }},
    @{name='Server success with lost creation response is reconciled without creating again';run={
        Reset-Remote;$script:FailCreate=$true
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        $failed=$false;try{Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3}catch{$failed=$true}
        Check $failed 'Lost response must be reported'
        Check ($script:State.cleanup.verified -and $script:RemoteIssues.Count -eq 1 -and $script:NextIssue -eq 12) 'Uncertain creation replayed or orphaned'
        Start-PlanningMeasurement 'Cleanup' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3
        Check $script:State.cleanup.verified 'Standalone cleanup is not repeatable'
    }},
    @{name='A preexisting marker is never adopted or deleted';run={
        Reset-Remote
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        $script:RemoteIssues+=@{id='I2';number=2;title=('['+$marker+'] task 01');body=('Disposable planning measurement '+$marker);repository=@{id='R';nameWithOwner='fukuda-yuki/codex-sandbox'}}
        $failed=$false;try{Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3}catch{$failed=$true}
        Check ($failed -and $script:RemoteIssues.Count -eq 2) 'Preexisting marked Issue was deleted'
    }},
    @{name='Wrong readback cannot be called successful measurement';run={
        Reset-Remote;$script:CorruptValue=$true
        $marker='g76-20000101T000000-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        $failed=$false;try{Start-PlanningMeasurement 'Run' $marker 'fukuda-yuki/codex-sandbox' 'fukuda-yuki' 3}catch{$failed=$true}
        Check $failed 'Incorrect server values were accepted'
        Check $script:State.cleanup.verified 'Readback failure must still clean up'
    }}
)
$results=@()
foreach($case in $cases){try{& $case.run;$results+=@{name=$case.name;passed=$true}}catch{$results+=@{name=$case.name;passed=$false;error=$_.Exception.Message}}}
$report=@{executed=$results.Count;passed=@($results|Where-Object passed).Count;failed=@($results|Where-Object {-not $_.passed}).Count;skipped=0;checks=$script:checks;cases=$results;environment='Offline in-memory transport and substituted wait; real document guard, response policy, orchestration and files'}
$report.source=@{commit=(git rev-parse HEAD);scriptHash=(Get-FileHash (Join-Path $PSScriptRoot '../scripts/Measure-PlanningSandbox.ps1')).Hash;testHash=(Get-FileHash $PSCommandPath).Hash}
$report.runtime=@{powershell=$PSVersionTable.PSVersion.ToString();os=[Environment]::OSVersion.VersionString}
$report.command=$MyInvocation.Line
$report | ConvertTo-Json -Depth 6
if($report.failed){exit 1}
exit 0


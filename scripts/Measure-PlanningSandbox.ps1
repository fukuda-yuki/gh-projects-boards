#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Plan','Run','Cleanup')][string]$Mode = 'Plan',
    [string]$RunMarker,
    [string]$Repository = 'fukuda-yuki/codex-sandbox',
    [string]$Owner = 'fukuda-yuki',
    [int]$ProjectNumber = 3
)
$ErrorActionPreference = 'Stop'

function Assert-MeasurementScope($Repository, $Owner, $ProjectNumber) {
    if ($Repository -cne 'fukuda-yuki/codex-sandbox' -or $Owner -cne 'fukuda-yuki' -or $ProjectNumber -ne 3) {
        throw 'Only fukuda-yuki/codex-sandbox and fukuda-yuki Project 3 are allowed.'
    }
}
function Save-Measurement {
    $json = $script:State | ConvertTo-Json -Depth 40
    $temp = Join-Path $script:RunDirectory 'results.json.tmp'
    [IO.File]::WriteAllText($temp, $json)
    [IO.File]::Move($temp, (Join-Path $script:RunDirectory 'results.json'), $true)
}
function ConvertTo-MeasurementCanonical($Value) {
    if ($Value -is [Collections.IDictionary]) {
        $ordered=[ordered]@{}
        foreach($key in @($Value.Keys | Sort-Object -CaseSensitive)){ $ordered[$key]=ConvertTo-MeasurementCanonical $Value[$key] }
        return $ordered
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        return ,@($Value | ForEach-Object { ConvertTo-MeasurementCanonical $_ })
    }
    return $Value
}
function Get-MeasurementHash($Value) {
    $Value=ConvertTo-MeasurementCanonical $Value
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 40 -Compress))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}
function Assert-MeasurementDocument([string]$Query) {
    # Structural protection only; names, types and field placement still need the service schema.
    $stack = [Collections.Generic.Stack[char]]::new()
    $previous = ''; $selections = 0
    for ($i = 0; $i -lt $Query.Length; $i++) {
        $c = $Query[$i]
        if ([char]::IsWhiteSpace($c) -or $c -eq ',') { continue }
        if ($c -eq '#') { while ($i -lt $Query.Length -and $Query[$i] -ne "`n") { $i++ }; continue }
        if ($c -eq '"') {
            $block = $Query.Substring($i).StartsWith('"""')
            $i += $(if ($block) { 3 } else { 1 })
            $closed = $false
            while ($i -lt $Query.Length) {
                if ($Query[$i] -eq '\') { $i += 2; continue }
                if ($block -and $Query.Substring($i).StartsWith('"""')) { $i += 2; $closed = $true; break }
                if (-not $block -and $Query[$i] -eq '"') { $closed = $true; break }
                $i++
            }
            if (-not $closed) { throw 'Invalid GraphQL document: unterminated string' }
            $previous = 'value'; continue
        }
        if ($c -in @('{','(','[')) {
            $stack.Push($c)
            if ($c -eq '{') { $selections++ }
        } elseif ($c -in @('}',')',']')) {
            $opening = switch ($c) { '}' { '{' }; ')' { '(' }; ']' { '[' } }
            if ($stack.Count -eq 0 -or $stack.Pop() -ne $opening) { throw 'Invalid GraphQL document: mismatched delimiters' }
            if ($c -eq '}' -and $previous -eq '{') { throw 'Invalid GraphQL document: empty selection set' }
        }
        $previous = [string]$c
    }
    if ($stack.Count -ne 0 -or $selections -eq 0) { throw 'Invalid GraphQL document: missing or unclosed selection' }
}
function ConvertTo-MeasurementErrors($Errors) {
    foreach ($errorRecord in $Errors) {
        if ($null -ne $errorRecord) { [ordered]@{ message=$errorRecord.message; path=$errorRecord.path; type=$errorRecord.type } }
    }
}
function Invoke-MeasurementGraphQL([string]$Query, [hashtable]$Variables = @{}) {
    Assert-MeasurementDocument $Query
    Invoke-MeasurementTransport $Query $Variables
}
function Resolve-MeasurementResponse($Body, [string]$Operation, [hashtable]$Variables, [int]$ExitCode, [bool]$Completed, $RateClass, [int]$HttpStatus = 0) {
    $duplicates=@()
    if ($Operation -eq 'MeasureAdd') {
        # Persist successes even when another alias or the entire request must stop.
        $script:State.addResponses+=@{data=($Body.data | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable);errors=@(ConvertTo-MeasurementErrors $Body.errors)}
        Save-Measurement
    }
    if ($Operation -eq 'MeasureDeleteIssue') {
        $script:State.deleteResponses+=@{data=$Body.data;errors=@(ConvertTo-MeasurementErrors $Body.errors);inputs=$Variables;httpStatus=$HttpStatus}
        Save-Measurement
    }
    if (@($Body.errors | Where-Object type -CEQ 'RESOURCE_LIMITS_EXCEEDED').Count) {
        $script:State.stopReason='RESOURCE_LIMITS_EXCEEDED'
        $script:State.stopRemote=$true
        Save-Measurement
        throw "GitHub request stopped: $Operation; RESOURCE_LIMITS_EXCEEDED. See structured request record."
    }
    # Only a single, already-discovered deletion can interpret absence as success.
    $notFound=@($Body.errors | Where-Object {
        $_.type -ceq 'NOT_FOUND' -and @($_.path).Count -eq 1 -and $_.path[0] -ceq 'm0'
    })
    if ($Operation -eq 'MeasureDeleteIssue' -and $Variables.Count -eq 1 -and
        $Variables.v0.issueId -and $Completed -and -not $RateClass -and $ExitCode -in @(0,1) -and
        (($HttpStatus -eq 410 -and -not $Body.errors) -or
         ($notFound.Count -gt 0 -and $notFound.Count -eq @($Body.errors).Count))) {
        return @{m0=@{alreadyGone=$true}}
    }
    foreach ($errorRecord in $Body.errors) {
        $path=@($errorRecord.path)
        if ($Operation -ne 'MeasureAdd' -or $errorRecord.type -cne 'UNPROCESSABLE' -or
            $errorRecord.message -cne 'Content already exists in this project' -or
            $path.Count -ne 1 -or $path[0] -cnotmatch '^m(0|[1-9][0-9]*)$' -or
            -not $Variables.ContainsKey('v'+$path[0].Substring(1))) {
            throw "GitHub request stopped: $Operation; unrecognized GraphQL error. See structured request record."
        }
        $duplicates+=$path[0]
    }
    if (-not $Completed -or $null -eq $Body.data -or $RateClass -or
        ($ExitCode -ne 0 -and -not ($ExitCode -eq 1 -and $duplicates.Count -gt 0))) {
        throw "GitHub request stopped: $Operation; exit=$ExitCode; timedOut=$(-not $Completed); rate=$RateClass. No automatic retry."
    }
    if ($duplicates.Count) { $Body.data['__alreadyPresentAliases']=@($duplicates | Select-Object -Unique) }
    return $Body.data
}
function Invoke-MeasurementTransport([string]$Query, [hashtable]$Variables = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new('C:\Program Files\GitHub CLI\gh.exe')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($key in @('GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN')) { [void]$start.Environment.Remove($key) }
    $start.Environment['GH_PROMPT_DISABLED'] = '1'
    $start.Environment['GH_NO_UPDATE_NOTIFIER'] = '1'
    $start.Environment['GH_PAGER'] = ''
    foreach ($argument in @('api','graphql','--hostname','github.com','--include','--input','-')) { $start.ArgumentList.Add($argument) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine((@{ query = $Query; variables = $Variables } | ConvertTo-Json -Depth 30 -Compress))
    $process.StandardInput.Close()
    $completed = $process.WaitForExit(120000)
    if (-not $completed) { $process.Kill($true); $process.WaitForExit() }
    $output = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
    $parts = $output -split '\r?\n\r?\n',2
    $headers = @{}
    if ($parts.Count -eq 2) {
        foreach ($line in ($parts[0] -split '\r?\n')) {
            if ($line -match '^(x-ratelimit-[\w-]+|retry-after|x-github-request-id):\s*(.*)$') { $headers[$Matches[1].ToLowerInvariant()] = $Matches[2].Trim() }
        }
    }
    $httpStatus=0
    if ($parts.Count -eq 2 -and $parts[0] -match '^HTTP/[^ ]+ (\d{3})') { $httpStatus=[int]$Matches[1] }
    $body = $null
    try { $body = ($parts[-1] | ConvertFrom-Json -AsHashtable -ErrorAction Stop) } catch { }
    $rateClass = if (($output + $errorText) -match '(?i)secondary rate|abuse detection') { 'secondary' }
        elseif (($output + $errorText) -match '(?i)rate.limit|RATE_LIMITED') { 'primary-or-unspecified' } else { $null }
    $operation = if ($Query -match '^\s*(?:query|mutation)\s+(\w+)') { $Matches[1] } else { 'unknown' }
    $entry = [ordered]@{ operation=$operation; utc=[DateTimeOffset]::UtcNow.ToString('o'); ms=$watch.Elapsed.TotalMilliseconds; exitCode=$process.ExitCode; timedOut=(-not $completed); headers=$headers; rateLimit=$body.data.rateLimit; rateLimitResponse=$rateClass; graphqlErrors=@(ConvertTo-MeasurementErrors $body.errors);httpStatus=$httpStatus;stopReason=$(if(@($body.errors|Where-Object type -CEQ 'RESOURCE_LIMITS_EXCEEDED').Count){'RESOURCE_LIMITS_EXCEEDED'}else{$null}) }
    if ($headers.ContainsKey('x-ratelimit-used')) {
        $used = [long]$headers['x-ratelimit-used']
        if ($null -ne $script:PreviousUsed -and $used -ge $script:PreviousUsed) { $entry.observedUsedDelta=$used-$script:PreviousUsed }
        $script:PreviousUsed=$used
    }
    if ($headers.ContainsKey("retry-after") -or $headers["x-ratelimit-remaining"] -eq "0") { $rateClass = "header-cooldown"; $entry.rateLimitResponse=$rateClass }
    if ($rateClass) { $script:State.stopRemote = $true }
    $script:State.requests += $entry
    Save-Measurement
    return Resolve-MeasurementResponse $body $operation $Variables $process.ExitCode $completed $rateClass $httpStatus
}
function Read-MeasurementConnection([string]$Query, [string]$Id, [string]$Name) {
    $items = [Collections.Generic.List[object]]::new(); $after = $null; $seen = [Collections.Generic.HashSet[string]]::new()
    do {
        $data = Invoke-MeasurementGraphQL $Query @{ id=$Id; after=$after }
        $connection = $data.node[$Name]
        if ($null -eq $connection -or $null -eq $connection.nodes -or $null -eq $connection.totalCount -or $null -eq $connection.pageInfo) { throw "Incomplete $Name connection" }
        foreach ($node in $connection.nodes) { if ($null -eq $node) { throw "Null $Name node" }; $items.Add($node) }
        if (-not $connection.pageInfo.hasNextPage) { break }
        $after = $connection.pageInfo.endCursor
        if (-not $after -or -not $seen.Add($after)) { throw "Invalid $Name cursor" }
    } while ($true)
    if ($items.Count -ne [int]$connection.totalCount) { throw "Incomplete or changing $Name count" }
    return ,$items.ToArray()
}
$script:RateSelection = 'rateLimit { cost remaining used limit resetAt }'
$script:FieldsQuery = 'query MeasureFields($id:ID!,$after:String){ node(id:$id){... on ProjectV2{fields(first:100,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{... on ProjectV2FieldCommon{id name dataType} ... on ProjectV2SingleSelectField{options{id name}}}}}} rateLimit{cost remaining used limit resetAt}}'
$script:ValueSelection = 'totalCount pageInfo{hasNextPage endCursor} nodes{__typename ... on ProjectV2ItemFieldNumberValue{number field{... on ProjectV2FieldCommon{id name}}} ... on ProjectV2ItemFieldDateValue{date field{... on ProjectV2FieldCommon{id name}}} ... on ProjectV2ItemFieldSingleSelectValue{optionId field{... on ProjectV2FieldCommon{id name}}}}'
$script:ItemsQuery = 'query MeasureItems($id:ID!,$after:String){node(id:$id){... on ProjectV2{items(first:100,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{id type isArchived content{__typename ... on Issue{id number title state repository{id nameWithOwner} assignees(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{id login}} blockedBy(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{id}} parent{id}}} fieldValues(first:100){'+$script:ValueSelection+'}}}}} rateLimit{cost remaining used limit resetAt}}'
function Read-MeasurementProject {
    $fields = Read-MeasurementConnection $script:FieldsQuery $script:State.projectId 'fields'
    $items = Read-MeasurementConnection $script:ItemsQuery $script:State.projectId 'items'
    foreach ($item in $items) {
        if ($item.fieldValues.pageInfo.hasNextPage) {
            $query = 'query MeasureValues($id:ID!,$after:String){node(id:$id){... on ProjectV2Item{fieldValues(first:100,after:$after){'+$script:ValueSelection+'}}} rateLimit{cost remaining used limit resetAt}}'
            $item.fieldValues.nodes = Read-MeasurementConnection $query $item.id 'fieldValues'
        } elseif (@($item.fieldValues.nodes).Count -ne $item.fieldValues.totalCount) { throw 'Incomplete field values' }
        if ($null -eq $item.content -or $item.content.__typename -ne 'Issue') { continue }
        foreach ($name in @('assignees','blockedBy')) {
            $connection = $item.content[$name]
            if ($connection.pageInfo.hasNextPage) {
                $selection = if ($name -eq 'assignees') { 'id login' } else { 'id' }
                $query = 'query MeasureRelations($id:ID!,$after:String){node(id:$id){... on Issue{'+$name+'(first:100,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{'+$selection+'}}}} rateLimit{cost remaining used limit resetAt}}'
                $item.content[$name].nodes = Read-MeasurementConnection $query $item.content.id $name
            } elseif (@($connection.nodes).Count -ne $connection.totalCount) { throw "Incomplete $name" }
        }
    }
    if (@($items.id | Select-Object -Unique).Count -ne $items.Count) { throw 'Duplicate Project items' }
    return @{ fields=$fields; items=$items }
}
function Resolve-MeasurementScope {
    $data = Invoke-MeasurementGraphQL 'query MeasureScope { repository(owner:"fukuda-yuki",name:"codex-sandbox"){id nameWithOwner issue(number:1){id number title body state updatedAt}} user(login:"fukuda-yuki"){login projectV2(number:3){id number url}} rateLimit{cost remaining used limit resetAt}}'
    if ($data.repository.nameWithOwner -cne 'fukuda-yuki/codex-sandbox' -or $data.user.login -cne 'fukuda-yuki' -or $data.user.projectV2.number -ne 3 -or $data.user.projectV2.url -ne 'https://github.com/users/fukuda-yuki/projects/3' -or $data.repository.issue.number -ne 1) { throw 'Remote identity verification failed' }
    if (-not $data.repository.id -or -not $data.user.projectV2.id -or -not $data.repository.issue.id) { throw 'Missing target node IDs' }
    if ($script:State.repositoryId -and ($script:State.repositoryId -ne $data.repository.id -or $script:State.projectId -ne $data.user.projectV2.id)) { throw 'Remote IDs differ from manifest' }
    $script:State.repositoryId=$data.repository.id; $script:State.projectId=$data.user.projectV2.id
    Write-Host "Repository: fukuda-yuki/codex-sandbox $($data.repository.id)"
    Write-Host "Project: fukuda-yuki/3 $($data.user.projectV2.id)"
    return $data.repository.issue
}
function Invoke-MeasurementBatch([string]$Operation, [object[]]$Inputs, [string]$InputType, [string]$Mutation, [string]$Selection) {
    $variables=@{}; $declarations=@(); $aliases=@()
    for ($i=0; $i -lt $Inputs.Count; $i++) {
        $variables["v$i"]=$Inputs[$i]
        $declarations += ('$v'+$i+':'+$InputType+'!')
        $aliases += ('m'+$i+':'+$Mutation+'(input:$v'+$i+'){'+$Selection+'}')
    }
    $query='mutation '+$Operation+'('+($declarations -join ',')+'){'+($aliases -join ' ')+'}'
    $data=Invoke-MeasurementGraphQL $query $variables
    if ($Operation -eq 'MeasureAdd') {
        $duplicateAliases=@($data.__alreadyPresentAliases | Where-Object { $null -ne $_ })
        $script:State.addsAlreadyPresent += $duplicateAliases.Count
        $successful=@()
        for ($i=0; $i -lt $Inputs.Count; $i++) {
            $alias="m$i"
            if ($alias -in $duplicateAliases) { continue }
            $item=$data[$alias].item
            if (-not $item.id -or $item.content.id -cne $Inputs[$i].contentId) { throw "Invalid add result $alias" }
            $successful+=@{alias=$alias;contentId=$item.content.id;itemId=$item.id}
        }
        $script:State.addSuccesses+=$successful
        Save-Measurement
        if ($duplicateAliases.Count) {
            $ids=@($duplicateAliases | ForEach-Object { $Inputs[[int]$_.Substring(1)].contentId })
            $observation=Watch-MeasurementMembership $ids 'duplicate-reconciliation'
            foreach ($alias in $duplicateAliases) {
                $id=$Inputs[[int]$alias.Substring(1)].contentId
                $item=@($observation.found | Where-Object contentId -CEQ $id)
                if ($item.Count -ne 1) { throw "Already-present item not visible for $alias; no mutation retry" }
                $data[$alias]=@{item=@{id=$item[0].itemId;content=@{id=$id}}}
            }
        }
    }
    $result=@()
    for ($i=0; $i -lt $Inputs.Count; $i++) { if ($null -eq $data["m$i"]) { throw "Missing mutation result m$i" }; $result += $data["m$i"] }
    return ,$result
}
function Wait-MeasurementPoll([int]$Milliseconds) { Start-Sleep -Milliseconds $Milliseconds }
function Watch-MeasurementMembership([string[]]$ContentIds, [string]$Purpose, $Watch = $null) {
    $query='query MeasureMembership($id:ID!,$after:String){node(id:$id){... on ProjectV2{items(first:100,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{id content{... on Issue{id}}}}}} rateLimit{cost remaining used limit resetAt}}'
    if ($null -eq $Watch) { $Watch=[Diagnostics.Stopwatch]::StartNew() }
    $observation=@{purpose=$Purpose;startedUtc=[DateTimeOffset]::UtcNow.ToString('o');contentIds=$ContentIds;requestedWaitMs=0;probes=@();found=@()}
    if ($Purpose -eq 'post-create') { $script:State.autoAddObservations+=$observation }
    else { $script:State.addReconciliations+=$observation }
    Save-Measurement
    foreach ($delay in @(0,1000,2000)) {
        if ($delay) { Wait-MeasurementPoll $delay; $observation.requestedWaitMs+=$delay }
        $startMs=$watch.Elapsed.TotalMilliseconds
        $items=Read-MeasurementConnection $query $script:State.projectId 'items'
        $endMs=$watch.Elapsed.TotalMilliseconds
        $visible=@($items | Where-Object { $_.content.id -in $ContentIds })
        $observation.probes+=@{startMs=$startMs;endMs=$endMs;visibleContentIds=@($visible | ForEach-Object { $_.content.id })}
        foreach ($id in $ContentIds) {
            $matching=@($visible | Where-Object { $_.content.id -ceq $id })
            if ($matching.Count -gt 1) { throw 'Multiple Project items for one content identity' }
            if ($matching.Count -eq 1 -and $id -notin @($observation.found.contentId)) {
                if (-not $matching[0].id) { throw 'Missing membership item ID' }
                $observation.found+=@{contentId=$id;itemId=$matching[0].id;firstObservedMs=$endMs;probeStartMs=$startMs}
            }
        }
        $observation.elapsedMs=$watch.Elapsed.TotalMilliseconds
        Save-Measurement
        if ($observation.found.Count -eq $ContentIds.Count) { break }
    }
    return $observation
}
function Find-MeasurementIssues {
    $query='query MeasureOwnedIssues($id:ID!,$after:String){node(id:$id){... on Repository{issues(first:100,after:$after,orderBy:{field:CREATED_AT,direction:ASC}){totalCount pageInfo{hasNextPage endCursor} nodes{id number title body repository{id nameWithOwner}}}}} rateLimit{cost remaining used limit resetAt}}'
    $all=Read-MeasurementConnection $query $script:State.repositoryId 'issues'
    $prefix='['+$script:State.marker+'] task '
    $body='Disposable planning measurement '+$script:State.marker
    return ,@($all | Where-Object { $_.title.StartsWith($prefix,[StringComparison]::Ordinal) -and $_.body -ceq $body } | ForEach-Object {
        if ($_.number -eq 1 -or $_.repository.id -ne $script:State.repositoryId -or $_.repository.nameWithOwner -cne 'fukuda-yuki/codex-sandbox' -or $_.id -eq $script:State.baseline.scopeIssueId) { throw 'Unsafe cleanup Issue' }
        $suffix=$_.title.Substring($prefix.Length)
        if ($suffix -notmatch '^\d{2}$' -or [int]$suffix -lt 1 -or [int]$suffix -gt 50) { throw 'Unexpected marked Issue title; manual reconciliation required' }
        $_
    })
}
function Clear-MeasurementRun {
    if (-not $script:State.baseline -or -not $script:State.creationAuthorized) { throw 'Cleanup requires the saved pre-mutation baseline' }
    $scope=Resolve-MeasurementScope
    if ((Get-MeasurementHash $scope) -ne $script:State.baseline.scopeHash) { throw 'Scope Issue #1 changed; cleanup stopped' }
    $owned=Find-MeasurementIssues
    $project=Read-MeasurementProject
    $ownedIds=@($owned.id)
    $deleteItems=@($project.items | Where-Object { $_.content.id -in $ownedIds })
    foreach ($item in $deleteItems) {
        if ($item.id -in $script:State.baseline.itemIds) { throw 'Refusing to remove pre-existing Project item' }
        $null=Invoke-MeasurementBatch 'MeasureRemoveItem' @(@{projectId=$script:State.projectId; itemId=$item.id}) 'DeleteProjectV2ItemInput' 'deleteProjectV2Item' 'deletedItemId'
    }
    for ($offset=0; $offset -lt $owned.Count; $offset++) {
        $batch=@($owned | Select-Object -Skip $offset -First 1)
        $inputs=@($batch | ForEach-Object { @{issueId=$_.id;clientMutationId=($script:State.marker+'-'+$_.id)} })
        $deleted=Invoke-MeasurementBatch 'MeasureDeleteIssue' $inputs 'DeleteIssueInput' 'deleteIssue' 'clientMutationId'
        $badReceipt=$false
        for ($i=0; $i -lt $batch.Count; $i++) {
            $alreadyGone=$deleted[$i].alreadyGone -eq $true
            $verified=$alreadyGone -or $deleted[$i].clientMutationId -ceq $inputs[$i].clientMutationId
            $script:State.deletions+=@{issueId=$batch[$i].id;alias="m$i";verified=$verified;alreadyGone=$alreadyGone}
            Save-Measurement
            if (-not $verified) { $badReceipt=$true }
        }
        if ($badReceipt) { throw "Delete receipt mismatch; see per-alias evidence" }
    }
    $remaining=Find-MeasurementIssues
    $after=Read-MeasurementProject
    $scopeAfter=Resolve-MeasurementScope
    $sameItems=(Get-MeasurementHash @($after.items.id)) -eq (Get-MeasurementHash @($script:State.baseline.itemIds))
    $sameFields=(Get-MeasurementHash $after.fields) -eq $script:State.baseline.fieldsHash
    $sameScope=(Get-MeasurementHash $scopeAfter) -eq $script:State.baseline.scopeHash
    $script:State.cleanup=@{ verified=($remaining.Count -eq 0 -and $sameItems -and $sameFields -and $sameScope); removedIssues=$owned.Count; removedItems=$deleteItems.Count; remainingMarkedIssues=$remaining.Count; sameItemOrder=$sameItems; sameFields=$sameFields; scopeIssueUnchanged=$sameScope; utc=[DateTimeOffset]::UtcNow.ToString('o') }
    Save-Measurement
    if (-not $script:State.cleanup.verified) { throw 'Cleanup readback differs from baseline; do not delete unrelated data. Review results.json.' }
}
function Start-PlanningMeasurement([string]$Mode,[string]$RunMarker,[string]$Repository,[string]$Owner,[int]$ProjectNumber) {
    Assert-MeasurementScope $Repository $Owner $ProjectNumber
    if (-not $RunMarker) { $RunMarker='g76-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8) }
    if ($RunMarker -cnotmatch '^g76-\d{8}T\d{6}-[0-9a-f]{8}$') { throw 'Invalid run marker' }
    if ($Mode -eq 'Plan') {
        [ordered]@{mode='Plan'; network=$false; mutations=0; repository=$Repository; owner=$Owner; project=3; marker=$RunMarker; creation=@{issues=50;batchSize=10;requests=10};refreshRepeats=3;updatesPerSize=300;batchSizes=@(1,10,25,50);serial=$true;autoAddObservation=@{maxReads=3;waitMilliseconds=@(0,1000,2000);explicitAddAfterObservation=$true};deleteBatchSize=1;cleanup='Only exactly marked Issues; baseline order, fields and #1 verified';command="& C:\w\g76\scripts\Measure-PlanningSandbox.ps1 -Mode Run -RunMarker $RunMarker"} | ConvertTo-Json -Depth 6
        return
    }
    $script:RunDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('TestResults/live/'+$RunMarker)
    $script:PreviousUsed=$null
    if ($Mode -eq 'Cleanup') {
        $script:State=Get-Content -LiteralPath (Join-Path $script:RunDirectory 'results.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($script:State.marker -cne $RunMarker -or $script:State.repository -cne $Repository -or $script:State.owner -cne $Owner -or $script:State.projectNumber -ne 3) { throw 'Manifest scope mismatch' }
        Clear-MeasurementRun; Write-Host "Cleanup verified: $script:RunDirectory"; return
    }
    if (Test-Path -LiteralPath $script:RunDirectory) { throw 'Existing run cannot be replayed. Use Cleanup with its marker.' }
    New-Item -ItemType Directory -Path $script:RunDirectory | Out-Null
    $script:State=[ordered]@{ schema=1; marker=$RunMarker; repository=$Repository; owner=$Owner; projectNumber=3; started=[DateTimeOffset]::UtcNow.ToString('o'); requests=@(); timings=@(); issues=@(); items=@(); creationDispatches=@(); addsAlreadyPresent=0; addResponses=@(); addSuccesses=@(); autoAddObservations=@(); addReconciliations=@(); deletions=@(); deleteResponses=@(); cleanup=@{verified=$false} }
    Save-Measurement
    try {
        $scope=Resolve-MeasurementScope
        $before=Read-MeasurementProject
        $number=@($before.fields | Where-Object { $_.dataType -eq 'NUMBER' } | Sort-Object @{Expression={if($_.name -eq 'Remaining'){0}else{1}}},name)[0]
        $date=@($before.fields | Where-Object { $_.dataType -eq 'DATE' } | Sort-Object @{Expression={if($_.name -eq 'Start date'){0}else{1}}},name)[0]
        if (-not $number.id -or -not $date.id) { throw 'An existing NUMBER and DATE field are required; no fields will be created.' }
        $script:State.measurementFields=@{numberId=$number.id;numberName=$number.name;dateId=$date.id;dateName=$date.name}
        $script:State.baseline=@{itemIds=@($before.items.id);fieldsHash=(Get-MeasurementHash $before.fields);scopeIssueId=$scope.id;scopeHash=(Get-MeasurementHash $scope)}
        if ((Find-MeasurementIssues).Count -ne 0) { throw 'Run marker already exists remotely' }
        $script:State.creationAuthorized=$true
        Save-Measurement
        $watch=[Diagnostics.Stopwatch]::StartNew()
        for ($offset=0; $offset -lt 50; $offset+=10) {
            $inputs=@(for($i=$offset+1;$i -le $offset+10;$i++){ @{repositoryId=$script:State.repositoryId;title=('['+$RunMarker+'] task '+$i.ToString('00'));body=('Disposable planning measurement '+$RunMarker)} })
            $script:State.creationDispatches+=@{first=$offset+1;count=10;state='uncertain'}; Save-Measurement
            $created=Invoke-MeasurementBatch 'MeasureCreate' $inputs 'CreateIssueInput' 'createIssue' 'issue{id number}'
            $creationWatch=[Diagnostics.Stopwatch]::StartNew()
            $script:State.creationDispatches[-1].responseUtc=[DateTimeOffset]::UtcNow.ToString('o')
            foreach($entry in $created){if(-not $entry.issue.id -or $entry.issue.number -eq 1){throw 'Invalid created Issue identity'}; $script:State.issues+= $entry.issue}
            $script:State.creationDispatches[-1].state='received'; Save-Measurement
            $null=Watch-MeasurementMembership @($created.issue.id) 'post-create' $creationWatch
            $added=Invoke-MeasurementBatch 'MeasureAdd' @($created | ForEach-Object { @{projectId=$script:State.projectId;contentId=$_.issue.id} }) 'AddProjectV2ItemByIdInput' 'addProjectV2ItemById' 'item{id content{... on Issue{id}}}'
            foreach($entry in $added){if(-not $entry.item.id -or $entry.item.content.id -notin @($created.issue.id)){throw 'Invalid added item identity'};$script:State.items+=$entry.item.id};Save-Measurement
        }
        $script:State.timings+=@{kind='create-and-add';count=50;batchSize=10;ms=$watch.Elapsed.TotalMilliseconds}
        for($repeat=1;$repeat -le 3;$repeat++) {
            $watch.Restart();$snapshot=Read-MeasurementProject;$elapsed=$watch.Elapsed.TotalMilliseconds
            $script:State.timings+=@{kind='full-refresh';repeat=$repeat;count=$snapshot.items.Count;ms=$elapsed;msPerItem=($elapsed/[Math]::Max(1,$snapshot.items.Count));linearEstimate1000Ms=($elapsed/[Math]::Max(1,$snapshot.items.Count)*1000);estimateCaveat='Linear extrapolation includes fixed process costs and is not a measured 1000-item result'}
            Save-Measurement
        }
        $ordinal=0
        foreach($batchSize in @(1,10,25,50)) {
            $ordinal++;$expected=@{};$watch.Restart()
            for($offset=0;$offset -lt 300;$offset+=$batchSize) {
                $inputs=@(for($i=$offset;$i -lt [Math]::Min(300,$offset+$batchSize);$i++){
                    $value=if($i%2 -eq 0){@{number=[double]($ordinal*1000+$i)}}else{@{date=([DateTime]::new(2026,10,5).AddDays($ordinal+$i%20).ToString('yyyy-MM-dd'))}}
                    $field=if($i%2 -eq 0){$number.id}else{$date.id}
                                        $itemId=$script:State.items[[int][Math]::Floor($i/2)%50]
                    $expected[$itemId+'|'+$field]=@{itemId=$itemId;fieldId=$field;value=$value}
                    @{projectId=$script:State.projectId;itemId=$itemId;fieldId=$field;value=$value}
                })
                $null=Invoke-MeasurementBatch 'MeasureUpdate' $inputs 'UpdateProjectV2ItemFieldValueInput' 'updateProjectV2ItemFieldValue' 'projectV2Item{id}'
            }
            $script:State.timings+=@{kind='field-updates';count=300;uniqueCells=100;writesPerCell=3;batchSize=$batchSize;ms=$watch.Elapsed.TotalMilliseconds}
            $readback=Read-MeasurementProject
            foreach($expectation in $expected.Values) {
                $item=@($readback.items | Where-Object id -eq $expectation.itemId)
                if($item.Count -ne 1){throw 'Update readback missing item'}
                $fieldValue=@($item[0].fieldValues.nodes | Where-Object {$_.field.id -eq $expectation.fieldId})
                if($fieldValue.Count -ne 1){throw 'Update readback missing field'}
                foreach($key in $expectation.value.Keys){if($fieldValue[0][$key] -ne $expectation.value[$key]){throw 'Update readback value mismatch'}}
            }
            $script:State.timings[-1].verified=$true
            Save-Measurement
        }
        $script:State.updatesVerified=$true
        $script:State.measurementCompleted=$true
    } catch { $script:State.failure=$_.Exception.Message; Save-Measurement; throw }
    finally {
        if ($script:State.baseline -and $script:State.creationAuthorized -and -not $script:State.stopRemote) {
            try { Clear-MeasurementRun } catch { $script:State.cleanup.failure=$_.Exception.Message;Save-Measurement;Write-Warning "Cleanup unverified. Run -Mode Cleanup -RunMarker $RunMarker" }
        }
        Save-Measurement;Write-Host "Results: $script:RunDirectory"
    }
    if(-not $script:State.cleanup.verified){throw 'Measurement finished but cleanup was not verified'}
}
if ($MyInvocation.InvocationName -ne '.') { Start-PlanningMeasurement $Mode $RunMarker $Repository $Owner $ProjectNumber }








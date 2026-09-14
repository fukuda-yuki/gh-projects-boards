param([Parameter(Mandatory)][string]$Baseline, [Parameter(Mandatory)][string]$Candidate, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { throw 'Comparison output exists' }
New-Item -ItemType Directory -Path $Output | Out-Null
$rows = @()
foreach ($case in Get-ChildItem -LiteralPath $Baseline -Directory | Where-Object Name -match '^case-\d+$' | Sort-Object Name) {
    $candidateCase = Join-Path $Candidate $case.Name
    $plan = Get-Content (Join-Path $case.FullName 'plan.json') -Raw | ConvertFrom-Json
    $otherPlan = Get-Content (Join-Path $candidateCase 'plan.json') -Raw | ConvertFrom-Json
    foreach ($property in 'count','changes','mixed','samples','field','instrument','frequency') {
        if ($plan.$property -ne $otherPlan.$property) { throw "Incompatible plans: $($case.Name) $property" }
    }
    foreach ($sample in Get-ChildItem -LiteralPath $case.FullName -File | Where-Object Name -match '^sample-\d+\.json$') {
        $before = Get-Content $sample.FullName -Raw | ConvertFrom-Json
        $after = Get-Content (Join-Path $candidateCase $sample.Name) -Raw | ConvertFrom-Json
        if (!$before.success -or !$after.success -or $before.initialSha256 -ne $after.initialSha256) { throw 'Failed outcome or different initial state' }
        foreach ($entry in @(@('baseline',$before), @('candidate',$after))) {
            $data = $entry[1]
            function Count-Kind($kind) { ($data.spans | Where-Object Kind -eq $kind | Measure-Object Count -Sum).Sum }
            function Time-Kind($kind) { (($data.spans | Where-Object Kind -eq $kind | ForEach-Object { $_.End - $_.Start } | Measure-Object -Sum).Sum) * 1000.0 / $plan.frequency }
            $rows += [pscustomobject]@{ case = $case.Name; source = $entry[0]; sample = $data.sample; items = $plan.count; changes = $plan.changes; field = $plan.field; mixed = $plan.mixed; instrument = $plan.instrument;
                prepareMs = $data.prepareMs; executeMs = $data.executeMs; endToEndMs = $data.endToEndMs;
                observationMsInclusive = (Time-Kind 'operation-observation'); checkpointMsInclusive = (Time-Kind 'checkpoint-save'); mandatoryWaitMs = (Time-Kind 'mandatory-wait');
                mutations = $data.mutations; fullTraversals = (Count-Kind 'full-project-traversal'); returnedItems = (Count-Kind 'returned-items'); returnedValues = (Count-Kind 'returned-values'); returnedUtf8Bytes = (Count-Kind 'returned-utf8-bytes');
                versionCalls = (Count-Kind 'process-version'); authCalls = (Count-Kind 'process-auth'); identityCalls = (Count-Kind 'process-identity'); dataQueries = (Count-Kind 'process-query'); mutationCalls = (Count-Kind 'process-mutation'); checkpointCommits = (Count-Kind 'checkpoint-commits'); checkpointBytes = (Count-Kind 'checkpoint-bytes') }
        }
    }
}
$rows | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Output 'samples.json')
$rows | Export-Csv -NoTypeInformation (Join-Path $Output 'samples.csv')
$summary = foreach ($group in $rows | Group-Object case,source) {
    $first = $group.Group[0]
    $result = [ordered]@{ case=$first.case; source=$first.source; samples=$group.Count; items=$first.items; changes=$first.changes; field=$first.field; mixed=$first.mixed; instrument=$first.instrument }
    foreach ($metric in @('prepareMs','executeMs','endToEndMs','observationMsInclusive','checkpointMsInclusive','mandatoryWaitMs','mutations','fullTraversals','returnedItems','returnedValues','returnedUtf8Bytes','versionCalls','authCalls','identityCalls','dataQueries','mutationCalls','checkpointCommits','checkpointBytes')) {
        if (!$first.instrument -and $metric -notin 'prepareMs','executeMs','endToEndMs','mutations') { $result[$metric] = $null; continue }
        $values = @($group.Group.$metric | Sort-Object)
        $result[$metric] = @{ median=$values[[int][Math]::Floor($values.Count/2)]; min=$values[0]; max=$values[-1] }
    }
    [pscustomobject]$result
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Output 'summary.json')
Write-Output $Output

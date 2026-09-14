param([Parameter(Mandatory)][string]$Baseline, [Parameter(Mandatory)][string]$Candidate, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { throw 'Comparison output exists' }
New-Item -ItemType Directory -Path $Output | Out-Null
$baselineManifest = Get-Content (Join-Path $Baseline 'manifest.json') -Raw | ConvertFrom-Json
$candidateManifest = Get-Content (Join-Path $Candidate 'manifest.json') -Raw | ConvertFrom-Json
if (($baselineManifest.matrix | ConvertTo-Json -Depth 8 -Compress) -ne ($candidateManifest.matrix | ConvertTo-Json -Depth 8 -Compress)) { throw 'Workload matrices differ' }
$expectedCases = @(0..($baselineManifest.matrix.Count - 1) | ForEach-Object { "case-$_" } | Sort-Object)
foreach ($root in @($Baseline,$Candidate)) {
    $actualCases = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object Name -match '^case-\d+$' | Select-Object -ExpandProperty Name | Sort-Object)
    if (Compare-Object $expectedCases $actualCases) { throw 'Incomplete case set' }
}
$provenance = @{ baseline = $baselineManifest; candidate = $candidateManifest; baselineHashes = (Get-Content (Join-Path $Baseline 'hashes.json') -Raw | ConvertFrom-Json); candidateHashes = (Get-Content (Join-Path $Candidate 'hashes.json') -Raw | ConvertFrom-Json) }
$provenance | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $Output 'provenance.json')
$rows = @()
foreach ($case in Get-ChildItem -LiteralPath $Baseline -Directory | Where-Object Name -match '^case-\d+$' | Sort-Object Name) {
    $candidateCase = Join-Path $Candidate $case.Name
    $plan = Get-Content (Join-Path $case.FullName 'plan.json') -Raw | ConvertFrom-Json
    $otherPlan = Get-Content (Join-Path $candidateCase 'plan.json') -Raw | ConvertFrom-Json
    foreach ($property in 'count','changes','mixed','samples','field','instrument','frequency','boundary','runtime','os','processorCount','warmup') {
        if ($plan.$property -ne $otherPlan.$property) { throw "Incompatible plans: $($case.Name) $property" }
    }
    if ($plan.samples -lt 3 -or $plan.warmup -ne 1) { throw 'Insufficient sampling plan' }
    $expectedSamples = @(0..($plan.samples - 1) | ForEach-Object { "sample-$_.json" } | Sort-Object)
    foreach ($root in @($case.FullName,$candidateCase)) {
        $actualSamples = @(Get-ChildItem -LiteralPath $root -File | Where-Object Name -match '^sample-\d+\.json$' | Select-Object -ExpandProperty Name | Sort-Object)
        if (Compare-Object $expectedSamples $actualSamples) { throw 'Incomplete measured sample set' }
        if (!(Get-Content (Join-Path $root 'warmup.json') -Raw | ConvertFrom-Json).success) { throw 'Warmup failed' }
    }
    foreach ($sample in Get-ChildItem -LiteralPath $case.FullName -File | Where-Object Name -match '^sample-\d+\.json$') {
        $before = Get-Content $sample.FullName -Raw | ConvertFrom-Json
        $after = Get-Content (Join-Path $candidateCase $sample.Name) -Raw | ConvertFrom-Json
        if (!$before.success -or !$after.success -or $before.initialSha256 -ne $after.initialSha256) { throw 'Failed outcome or different initial state' }
        if ($before.sample -ne $after.sample -or $sample.BaseName -ne "sample-$($before.sample)") { throw 'Sample identity mismatch' }
        foreach ($root in @($case.FullName,$candidateCase)) {
            if ((Get-FileHash (Join-Path $root ($sample.BaseName + '-seed.json'))).Hash -ne $before.initialSha256) { throw 'Initial checkpoint hash mismatch' }
        }
        if ((Get-FileHash (Join-Path $case.FullName ($sample.BaseName + '-remote.json'))).Hash -ne (Get-FileHash (Join-Path $candidateCase ($sample.BaseName + '-remote.json'))).Hash) { throw 'Remote fixture mismatch' }
        foreach ($entry in @(@('baseline',$before), @('candidate',$after))) {
            $data = $entry[1]
            function Count-Kind($kind) { if (!$plan.instrument) { return $null }; ($data.spans | Where-Object Kind -eq $kind | Measure-Object Count -Sum).Sum }
            function Time-Kind($kind) { if (!$plan.instrument) { return $null }; (($data.spans | Where-Object Kind -eq $kind | ForEach-Object { $_.End - $_.Start } | Measure-Object -Sum).Sum) * 1000.0 / $plan.frequency }
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
        $middle = [int][Math]::Floor($values.Count/2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle-1] + $values[$middle]) / 2 }
        $result[$metric] = @{ median=$median; min=$values[0]; max=$values[-1] }
    }
    [pscustomobject]$result
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Output 'summary.json')
Write-Output $Output

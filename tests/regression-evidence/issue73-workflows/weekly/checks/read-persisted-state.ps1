$ErrorActionPreference = 'Stop'
$repo = 'C:/Users/mwam0/.copilot/repos/gh-projects-boards'
$root = Join-Path $repo 'TestResults/issue73/weekly-v1'
$checkpoint = Join-Path $root 'Drafts/31E1AE58D13395DE138AD21F335E381D56A242DFEAE6AEAB6BAD290DD66ABE0A.json'
$markerPath = Join-Path $root 'diagnostics/planning-evaluation.json'
$initialHash = '485762E1283A8943217F1531F2499FFB23B7D83CF8EB13489B49B5DA03571390'
$initialReadAt = '2026-09-27T02:59:38.4583103+00:00'
$hashBefore = (Get-FileHash -LiteralPath $checkpoint -Algorithm SHA256).Hash
$readAt = [DateTimeOffset]::UtcNow.ToString('o')
$marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json -DateKind String
if ($marker.synthetic -ne $true -or $marker.validatedReadback -ne $true -or $marker.scenario -ne 'Weekly' -or
    [IO.Path]::GetFullPath($marker.evaluationRoot) -ne [IO.Path]::GetFullPath($root)) { throw 'The expected isolated synthetic marker was not verified.' }
$data = Get-Content -Raw -LiteralPath $checkpoint | ConvertFrom-Json -DateKind String
$plan = @($data.Planning | Where-Object ProjectId -eq 'P1')[0]
$t1 = @($plan.Tasks | Where-Object Id -eq 'I1')[0]
$t2 = @($plan.Tasks | Where-Object Id -eq 'I2')[0]
function Field([string]$item, [string]$id) {
    $matches = @($data.Fields | Where-Object { $_.Key.ProjectId -eq 'P1' -and $_.Key.NodeId -eq $item -and $_.Key.FieldId -eq $id })
    if ($matches.Count -ne 1) { throw "Expected exactly one persisted field: $item / $id" }
    return $matches[0]
}
function Value($field) { if ($null -ne $field.Change) { $field.Change.Value } else { $field.Baseline } }
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$name, $actual, $expected) {
    $actualJson = ConvertTo-Json -InputObject $actual -Depth 8 -Compress
    $expectedJson = ConvertTo-Json -InputObject $expected -Depth 8 -Compress
    $checks.Add([ordered]@{ name=$name; actual=$actual; expected=$expected; pass=$actualJson -ceq $expectedJson })
}
Check 'Checkpoint schema' $data.Version 12
Check 'Project planning schema' $plan.Version 3
Check 'Task I1 progress enum: InProgress' $t1.Progress 1
Check 'Task I1 actual start in Tokyo wall-clock minutes' $t1.ActualStart '2026-10-05T09:00:00'
Check 'Task I1 actual finish remains empty' $t1.ActualFinish $null
Check 'Confirmed Actual values for P1T1 and P1T2' @((Value (Field 'P1T1' 'F-Actual')), (Value (Field 'P1T2' 'F-Actual'))) @('7','2')
Check 'Confirmed Remaining values for P1T1 and P1T2' @((Value (Field 'P1T1' 'F-Remaining')), (Value (Field 'P1T2' 'F-Remaining'))) @('3','1')
Check 'Estimate values for P1T1 and P1T2' @((Value (Field 'P1T1' 'F-Estimate')), (Value (Field 'P1T2' 'F-Estimate'))) @('4','4')
$buffers = @($data.Fields | Where-Object { $null -ne $_.Buffer })
Check 'One pending buffer remains' $buffers.Count 1
Check 'Pending buffer identity and text' @($buffers[0].Key.ProjectId,$buffers[0].Key.NodeId,$buffers[0].Key.FieldId,$buffers[0].Buffer) @('P1','P1T1','F-Remaining','3x')
Check 'Actual contribution totals' @($t1.Actuals[0].Hours,$t2.Actuals[0].Hours) @(7,2)
Check 'Actual report dates remain distinct from cutoff time' @($t1.Actuals[0].ReportedThrough,$t2.Actuals[0].ReportedThrough,$plan.Cutoff) @('2026-10-09','2026-10-09','2026-10-09T18:00:00')
Check 'Task I2 progress enum remains Unstarted' $t2.Progress 0
Check 'Task I2 actual date candidates absent' @($t2.ActualStart,$t2.ActualFinish) @($null,$null)
Check 'Task I1 persisted date-only projections' @((Value (Field 'P1T1' 'F-Start')), (Value (Field 'P1T1' 'F-Finish'))) @('2026-10-13','2026-10-13')
Check 'Apply journal is empty' @($data.Journal).Count 0
$registration = @($data.Registrations | Where-Object { $_.Snapshot.Id.NodeId -eq 'P1' })[0]
$item1 = @($registration.Snapshot.Items | Where-Object { $_.Id.NodeId -eq 'P1T1' })[0]
$item2 = @($registration.Snapshot.Items | Where-Object { $_.Id.NodeId -eq 'P1T2' })[0]
Check 'Registration task-to-item mapping' @($item1.ContentId.NodeId,$item2.ContentId.NodeId) @('I1','I2')
$hashAfter = (Get-FileHash -LiteralPath $checkpoint -Algorithm SHA256).Hash
Check 'Checkpoint unchanged throughout independent read' $hashAfter $hashBefore
Check 'Checkpoint unchanged since prior read during final candidate-only journey' $hashAfter $initialHash
$hashPaths = @($checkpoint,$markerPath,$PSCommandPath,
    (Join-Path $repo 'src/GhProjectsBoards.Core/Projects/EditingWorkspace.cs'),
    (Join-Path $repo 'src/GhProjectsBoards.Core/Projects/DraftStore.cs'),
    (Join-Path $repo 'src/GhProjectsBoards.Core/Projects/PlanningContract.cs'),
    (Join-Path $root 'diagnostics/launch-cdc3eb58402940f3b5d6473a92408abb.json'),
    (Join-Path $root 'diagnostics/launch-bba6eea8aa6645828bc1337dfc947bf4.json'),
    (Join-Path $root 'observations/09-saved-adopted.txt'),
    (Join-Path $root 'observations/10-result-reason.txt'))
$files = @($hashPaths | ForEach-Object { $f=Get-Item -LiteralPath $_; [ordered]@{
    path=[IO.Path]::GetRelativePath($repo,$f.FullName).Replace('\','/');
    sha256=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash;
    length=$f.Length; lastWriteUtc=$f.LastWriteTimeUtc.ToString('o') } })
$priorAx = Get-Content -Raw -LiteralPath (Join-Path $root 'observations/10-result-reason.txt')
$priorMinuteMatch = $priorAx.Contains('owner/repo #1 週次計画の確認 自動計算: 2026-10-13 09:00 → 2026-10-13 12:00')
$evidence = [ordered]@{
    kind='Independent read-only persisted synthetic-state inspection';
    readAtUtc=$readAt; sourceAtRead=(git -C $repo rev-parse HEAD).Trim();
    dataRoot=[IO.Path]::GetRelativePath($repo,$root).Replace('\','/');
    marker=[ordered]@{synthetic=$marker.synthetic; scenario=$marker.scenario; isolationId=$marker.isolationId; validatedReadback=$marker.validatedReadback};
    checkpointRevision=$data.Revision; checkpointScope=$data.Scope;
    checkpointSourceBoundary='Checkpoint was produced by ordinary executables; initial source 140928b, later resumed by candidates 66856a0 and dd34eee. This read does not relabel the initial save as a new dd34eee save.';
    priorRead=[ordered]@{readAtUtc=$initialReadAt; sha256=$initialHash};
    checks=$checks.ToArray(); allChecksPassed=(@($checks | Where-Object { -not $_.pass }).Count -eq 0);
    adoptedMinuteEndpoints=[ordered]@{persistedDirectly=$false; persistedProjectionPrecision='date only'; priorRuntimeObservationVerified=$priorMinuteMatch; start='2026-10-13T09:00:00'; finish='2026-10-13T12:00:00'; source='TestResults/issue73/weekly-v1/observations/10-result-reason.txt'; qualification='Separate retained AX observation, not a fresh core recomputation or persisted minute field.'};
    method='PowerShell 7.6.6 JSON read, selecting exact Project/item/task/field identities. Effective field values follow EditingWorkspace.Value: Change.Value when present, otherwise Baseline. Buffers inspected separately. PlanningProgress numeric mapping read from PlanningContract.';
    boundaries=@('No state mutation, app launch, build or native UI action performed by this inspection.', 'No live GitHub behavior or human acceptance established.', 'Empty persisted Journal establishes no retained Apply batch; it is not a network-call count.', 'Normal-close interaction and process exit are supplied by the parent native journey, not independently executed here.');
    files=$files
}
$output=Join-Path $PSScriptRoot 'independent-persisted-readback-20260927.json'
$evidence | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $output -Encoding utf8
[pscustomobject]@{path=$output; allChecksPassed=$evidence.allChecksPassed; checks=$checks.Count; failed=@($checks | Where-Object { -not $_.pass }).Count; checkpointRevision=$data.Revision; sha256=$hashAfter; priorRuntimeMinuteMatch=$priorMinuteMatch} | ConvertTo-Json

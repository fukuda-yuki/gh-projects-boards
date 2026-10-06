# Performance measurement

## Current plan sheet (#78)

Run the prepared Release build serially, without another build/test workload. Use a new evidence directory per run.

~~~powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
$env:GHPB_PLAN_EVIDENCE = 'C:\w\g76\TestResults\phase6\measurement-05'
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanSheetPerformance' -TimeoutSeconds 300
Remove-Item Env:\GHPB_PLAN_EVIDENCE
~~~

The fixture has 1,000 tasks, 20 people and ten-task finish-to-start chains. Twenty edits alternate row 1 Remaining between 8 and 16 hours. Each sample starts at the cell commit and ends at the next CompositionTarget.Rendered callback after local-operation acceptance/scheduling and refreshed visible dates/bars. Autosave runs concurrently and is still awaited before the next command or normal close. The test verifies the displayed date/bar for every sample. plan-frames.jsonl retains individual outcomes; plan-measurement.json reports median/max and whether all samples meet 200 ms. Superseded, rejected, unloaded and missing frames are not successful samples. This is hosted-control frame timing, not physical display latency or scrolling FPS.

The run also creates synthetic-1000, containing fake-gh remote data and a current PlanStore document. For ordinary-app screenshot and physical interaction review, replace the evidence directory below with the actual run directory:

~~~powershell
$env:GH_CONFIG_DIR = 'C:\w\g76\TestResults\phase6\measurement-05\synthetic-1000'
$env:GHPB_DATA_ROOT = "$env:GH_CONFIG_DIR\data"
$env:GHPB_PLAN_METRICS = 'C:\w\g76\TestResults\phase6\ordinary-plan-frames.jsonl'
& C:\w\g76\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe
~~~

In the ordinary connection page, set gh.exe to C:\w\g76\tests\GhProjectsBoards.Tests\bin\Release\net10.0-windows\GhProjectsBoards.Tests.exe, connect, and open 開発計画. Use the sheet and each day/week/month zoom, scroll to rows 500/1,000 and back, and inspect focus, cell values, arrows, row alignment and column access. Change row 1 Remaining to 16; dates and bars update through the real plan scheduler. Restore the shell environment variables after closing. These endpoints are synthetic; use a separate data root and real gh for authorized Project 3 review.

## Retained Core/adapter measurements

Issue #12 owns actual results, failures and acceptance; #65 owns the sheet workload and current next-roadmap evidence is recorded in #73. These runners do not establish human UX, enterprise or large-Project support. Synthetic fixtures are controlled scalability probes.

These Core runners exercise the retained Apply pipeline; they do not measure the new planning workspace. Planning-editor throughput and rendering contracts are in [decisions](decisions.md) and [test policy](../tests/README.md). Ordinary-shell measurement routes must use the current UI.

## Reproduce

Run Release on Windows with the repository build prerequisites, serially with no other build/test workload. Retain every run directory, including failures. Never overwrite a run or compare an incomplete run as successful evidence.

```powershell
./scripts/Test-Performance.ps1 -RunId baseline
# Check out the candidate source, using the same runner and instrumentation.
./scripts/Test-Performance.ps1 -RunId candidate -ReferenceRoot <absolute-baseline-directory>
./scripts/Compare-Performance.ps1 -Baseline <absolute-baseline-directory> -Candidate <absolute-candidate-directory> -Output <new-comparison-directory>
```

Synthetic mode uses real Core orchestration, transport parsing, filesystem checkpoints and production pacing, substituting in-process gh responses. Logical process-call counts are real invocations of that boundary; their elapsed times are **not real gh process/network durations**. Separate `--version` subprocess calibration records five samples after warmup and is an estimate, never subtracted from product spans.

All results are under `TestResults/performance/<run-id>/`. The manifest precedes sampling and records source, workload, environment and policy. Build/binary hashes identify execution sources. `-NoBuild -Executable <path> -SourceRevision <40-character-SHA>` can use an immutable copied synthetic executable. The source revision is caller-declared: retain its original build record, including any common harness/instrumentation overlay, and verify the hashes. The current checkout's HEAD identifies the invoking script, not necessarily that binary.

The fixed matrix covers no-change, ten titles, all 100 titles; ten titles and ten alternating select-set/clear operations at 100, 101 and 1,000 items; and ten titles with a local row, pending buffer, completed history and a superseded approval retaining an Unknown attempt. Active unresolved existing approvals still block new Apply; they are never discarded to enable benchmarking. Fields/options and payload shapes are fixed. Each expensive case has one warmup and three measured samples; no-change has five. Instrumentation-off repeats the representative ten-title case. Exact initial checkpoint and remote fixture state are preserved per sample; candidate `-ReferenceRoot` restores those bytes, including IDs, timestamps and history, before timing. Comparison rejects different initial hashes or failed outcomes.

## Timing and counters

The unpublished-work status uses a read-only projection of the same candidate rules as Apply review. Candidate field association must scale with the visible and retained field set, preserving Project-scoped scalar keys, shared Issue titles, missing-field recovery and candidate order. The status path does not initialize fields; the Apply-review path retains its existing initialization. Neither may scan the complete workspace field collection for every row. This is a calculation-cost boundary; ordinary input and scrolling measurements remain necessary before claiming a user-visible improvement.

Fixture creation, remote seed changes, initial connection/registration, history preparation and user review are outside timing. `prepare` measures flush, complete retrieval/reconciliation and review generation. `execute` measures confirmation through durable acknowledgement/settlement. Their sum is product execution end-to-end, excluding review. Ordinary-app local editing is recorded separately from both. Checkpoint setup and calibration are outside the measured phases.

All trace spans use monotonic Stopwatch ticks and record frequency. Span kinds and counters contain no request, title, token, raw response or content-bearing payload. The trace is an explicit in-memory diagnostic scope, not a file observer or recovery authority; it neither opens checkpoint files nor delays replacement. Disabled tracing bypasses byte counting. Checkpoint byte counts are the bytes actually serialized to temporary files; commits count only completed atomic replacement. No saves or attempt records are removed.

Spans are **inclusive and nested**: process requests occur within observations, which occur within prepare/execute. Report each as a breakdown, never add inclusive observation and process spans to derive end-to-end. Mandatory wait spans are separate from observation and checkpoint spans. The summary labels inclusive spans explicitly. Counts distinguish version/auth/identity preflight, data queries, mutation calls, full Project traversals, returned item/value nodes, UTF-8 response bytes, checkpoint bytes/commits and waits. Returned response bytes include protocol metadata; checkpoint bytes describe serialized work rather than total physical disk traffic.

Publish each sample plus median/min/max. Do not infer tail percentiles from these sample sizes. Retain an optimization only for a repeatable reduction in work counts or elapsed time beyond sample variation and passing correctness gates. Pacing remains enabled. Where mandatory waits dominate, report the work reduction without inventing an end-to-end speedup. CI runs deterministic safety and structural count assertions, not machine-specific timing thresholds. Benchmark repetition does not change the ordinary correctness timeout policy.

## Local candidate projection

The local unpublished-work status is a read-only projection. It must not materialize cell data for every unchanged fetched row when only a few rows contain work. Resolve candidate membership from field identity before constructing those rows, while retaining all local new rows, actual shared-Issue memberships, Project-scoped scalar and dependency fields, removed-field work, orphan recovery, conflicts and projection decisions. Candidate order and complete related-field information remain unchanged. The initializing Apply review path retains its existing behavior; a status read cannot initialize fields, consume pending input or mutate the checkpoint.

The opt-in 1,000-row, twelve-select-field, two-Project Core measurement exercises sparse committed and pending work with exact candidate/state assertions. Its managed-allocation budget is 8 MiB per projection. This is a synchronous Core work budget, not an input-to-display deadline or a supported-capacity claim. Keep ordinary-app viewport, native input, bulk Undo, durable values and presented-pixel observations separate from this measurement.

## Scoped observation safety mapping

The operation reader retrieves the initial Project definition and exact target item pages together, then completes each connection's remaining pages. This deliberately reuses `ReadSession` identity, ownership, option, pagination and error parsing. Field traversal depends on fields/options, not unrelated items. It returns only `FieldObservation`; it cannot satisfy registration, reconciliation or creation-promotion complete-snapshot requirements. Initial review and creation promotion retain `ReadAsync` full traversal.

| Existing predicate | Fresh evidence used for every dispatch validation and independent readback |
| --- | --- |
| Bound account, host, executable, keyring and required scope | First scoped read consolidates version/auth-store/scope preflight with dispatch under the connection gate. Every contributing response carries `viewer.databaseId` and must match the bound account. Continuations still use fresh guarded sends. Missing/malformed viewer data rejects the observation; a different viewer invalidates the context. No batch credential cache; mutation dispatch retains its full independent preflight. |
| Correct operation ownership | Exact Title Issue key or Select item/Project/field key shape |
| Target membership and identity | Direct node typename/id, item type, Project ID, content Issue typename/id and non-archival |
| Title value and capability | Typed Issue title/state/identity parsing and Issue `viewerCanUpdate` |
| Select definition and capability | Exact definition ID, Project ownership, supported single-select type and Project `viewerCanUpdate` |
| Current/intended options | Unfiltered definition options, unique IDs and exact observed/intended option membership; names never resolve identity |
| Absent means known empty | Complete definitions plus target value traversal with stable totalCount, terminal pageInfo, valid advancing cursors, unique value/field identities and no errors/unknown field identity |
| Failure or cancellation | No observation on incomplete/partial/error data; existing executor Unknown/Waiting and durable recovery handling remains in force |

Completeness is deliberately narrower than a Project snapshot: exactly one requested item, its value connection, and the definition connection. Unrelated items are not observed or reconciled. Missing initial values can be retrieved through the existing dedicated value query, but a missing/error continuation never proves empty. Unsupported unrelated values retain the reader's conservative classification. Pagination and read/write races still exist; this is not a transactional snapshot, compare-and-swap or exactly-once guarantee. Every continuation is guarded and verifies item/Project identity; the existing reader does not atomically lock item content, archival or definitions across pages.

Schema references: [global node lookup](https://docs.github.com/en/graphql/guides/using-global-node-ids), [Project types and connections](https://docs.github.com/en/graphql/reference/projects), [Issue fields](https://docs.github.com/en/graphql/reference/issues). `fieldValueByName` is not used. The [official API best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api) recommend sequential requests and pauses between mutations; existing one-second mutation pacing, rate-limit waiting and retry decisions remain unchanged.

## Staged existing-item adapter comparison

`-Mode AdapterStage -NoBuild -Executable <frozen-exe>` runs exactly one lifecycle stage. `Initialize -PlanPath <json>` writes an offline plan with explicit fixture root, source/binary hashes, shared ledger roots, sample order and setup evidence. `Prepare`, `Verify`, `Measure`, `Restore` and `Cleanup` require that fixture root and a new run ID. Measurement labels must follow the fixed plan; restoration uses the pinned main binary. Do not start bulk preparation without a supported correction or validated containment for any unresolved setup failure.

Each stage rechecks resource identity, capability, complete remote state, quota, persisted service cooldown and the rolling mutation ledger. Admission reserves restoration/cleanup; deferred runs return a checkpoint and continuation arguments without dispatching setup again. A fixture lease protects the central manifest. Run stages serially. An uncertain creation/add/update/removal/deletion must be independently reconciled; a timeout never authorizes replay. Restoration resumes its retained durable Apply journal. Deleted fixtures remain provenance and cannot seed a later live sample.

Preparation/addition and restoration/cleanup are outside timed Apply. Use one verified set of run-owned items and byte-identical edited checkpoints across samples. `-Workload CandidateCheck` selects focused current-source corroboration (50 Title changes, no-change/one-change controls, paginated Select and mixed fields), not the full historical variant matrix. Fixed workload, budgets, interpretation and exact commands belong in the owning Issue's evidence plan before dispatch.

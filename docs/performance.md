# Measured performance

Issue #12 owns actual results, failures and acceptance. These runners do not establish human UX, enterprise or large-Project support. The 1,000-item fixture is a controlled scalability probe.

## Reproduce

Run Release on Windows with the repository build prerequisites, serially with no other build/test workload. Retain every run directory, including failures. Never overwrite a run or compare an incomplete run as successful evidence.

```powershell
./scripts/Test-Performance.ps1 -RunId baseline
# Check out the candidate source, using the same runner and instrumentation.
./scripts/Test-Performance.ps1 -RunId candidate -ReferenceRoot <absolute-baseline-directory>
./scripts/Compare-Performance.ps1 -Baseline <absolute-baseline-directory> -Candidate <absolute-candidate-directory> -Output <new-comparison-directory>
./scripts/Test-Performance.ps1 -Mode Desktop -RunId desktop
# Explicit bounded live adapter validation, never a synthetic fallback:
./scripts/Test-Performance.ps1 -Mode Live -RunId live
```

Synthetic mode uses real Core orchestration, transport parsing, filesystem checkpoints and production pacing, substituting in-process gh responses. Logical process-call counts are real invocations of that boundary; their elapsed times are **not real gh process/network durations**. Separate `--version` subprocess calibration records five samples after warmup and is an estimate, never subtracted from product spans. Desktop mode uses the ordinary WinUI executable and actual isolated fake-gh subprocesses. Its invocation-to-visible-completion timings include UIA polling/interaction overhead. Live delegates to the existing Apply and creation runners with their complete-baseline, exact-resource and interrupted-cleanup guards. A small live journey verifies schema/adapter behavior; it is not a live 100-item throughput benchmark.

All results are under `TestResults/performance/<run-id>/`; desktop/live runner logs link their additional retained evidence directories. The manifest precedes sampling and records source, workload, environment and policy. Build/binary hashes identify execution sources. `-NoBuild -Executable <path>` can use an immutable copied synthetic executable; its source revision must match the recorded manifest. It is intended to protect a baseline from later compilation, not to substitute another implementation unnoticed.

The fixed matrix covers no-change, ten titles, all 100 titles; ten titles and ten alternating select-set/clear operations at 100, 101 and 1,000 items; and ten titles with a local row, pending buffer, completed history and a superseded approval retaining an Unknown attempt. Active unresolved existing approvals still block new Apply; they are never discarded to enable benchmarking. Fields/options and payload shapes are fixed. Each expensive case has one warmup and three measured samples; no-change has five. Instrumentation-off repeats the representative ten-title case. Exact initial checkpoint and remote fixture state are preserved per sample; candidate `-ReferenceRoot` restores those bytes, including IDs, timestamps and history, before timing. Comparison rejects different initial hashes or failed outcomes.

## Timing and counters

Fixture creation, remote seed changes, initial connection/registration, history preparation and user review are outside timing. `prepare` measures flush, complete retrieval/reconciliation and review generation. `execute` measures confirmation through durable acknowledgement/settlement. Their sum is product execution end-to-end, excluding review. Ordinary-app local editing is recorded separately from both. Desktop measurements include UI Automation polling and the edit helper's fixed 100 ms key-settling delay; they are not pure rendering or input latency. Checkpoint setup and calibration are outside the measured phases.

All trace spans use monotonic Stopwatch ticks and record frequency. Span kinds and counters contain no request, title, token, raw response or content-bearing payload. The trace is an explicit in-memory diagnostic scope, not a file observer or recovery authority; it neither opens checkpoint files nor delays replacement. Disabled tracing bypasses byte counting. Checkpoint byte counts are the bytes actually serialized to temporary files; commits count only completed atomic replacement. No saves or attempt records are removed.

Spans are **inclusive and nested**: process requests occur within observations, which occur within prepare/execute. Report each as a breakdown, never add inclusive observation and process spans to derive end-to-end. Mandatory wait spans are separate from observation and checkpoint spans. The summary labels inclusive spans explicitly. Counts distinguish version/auth/identity preflight, data queries, mutation calls, full Project traversals, returned item/value nodes, UTF-8 response bytes, checkpoint bytes/commits and waits. Returned response bytes include protocol metadata; checkpoint bytes describe serialized work rather than total physical disk traffic.

Publish each sample plus median/min/max. Do not infer tail percentiles from these sample sizes. Retain an optimization only for a repeatable reduction in work counts or elapsed time beyond sample variation and passing correctness gates. Pacing remains enabled. Where mandatory waits dominate, report the work reduction without inventing an end-to-end speedup. CI runs deterministic safety and structural count assertions, not machine-specific timing thresholds. Benchmark repetition does not change the ordinary correctness timeout policy.

## Cached local-sheet diagnosis

`Test-SheetDiagnostic.ps1` selects one opt-in `LocalSheetDiagnostic` case, outside the default E2E suite. It exercises an ordinary application's cached local session through public input/UI, real workspace orchestration and isolated checkpoint storage: this is end-to-end collaboration to a local-store endpoint. The seed is synthetic; connection, refresh, Apply and live GitHub are outside this diagnostic.

```powershell
# Each invocation creates a new isolated seed and evidence directory.
./scripts/Test-SheetDiagnostic.ps1 -RunId sheet-101 -Trace
./scripts/Test-SheetDiagnostic.ps1 -RunId sheet-1000 -ItemCount 1000 -Trace
./scripts/Test-SheetDiagnostic.ps1 -RunId sheet-columns -SelectFieldCount 12 -Trace
# Optional physical Japanese composition probe, restricted to 101 rows:
./scripts/Test-SheetDiagnostic.ps1 -RunId sheet-ime -Ime -Trace
# Use a previously copied complete app output; retain its source evidence separately.
./scripts/Test-SheetDiagnostic.ps1 -RunId sheet-baseline -NoBuild `
    -Executable 'C:\evidence\baseline\GhProjectsBoards.App.exe' -SourceRevision '<40-character-source-SHA>'
```

Rows accept 101–1,000 and single-select fields 1–12, with two additional ordinary columns. Start at 101 rows, then vary row count or column count independently; run 1,000×12 only for a relevant scaling question. These examples are selectable workloads, not a required Cartesian suite. Run serially on an unlocked desktop. The optional IME phase requires the existing Microsoft Japanese IME; it sends physical keys, keeps composition active across native wheel input, and checks IME confirmation separately from cell commit. It does not replace the broader IME suite or human typing acceptance.

The runner builds Release unless `-NoBuild` is supplied, then uses `Start-EditingCheck.ps1 -PrepareOnly` for a fresh, reread-validated seed. `-NoBuild` requires existing app, seed and driver outputs. An absolute `-Executable` selects an immutable copied app, independently of the current seed/driver binaries. `-SourceRevision` is optional caller-declared provenance, not verification that a binary matches current HEAD. Preserve the copied output's original build record. The runner records current HEAD, dirty patch, untracked source copies/hashes, binary hashes and before/after source hashes; builds do not silently establish provenance for older outputs.

Evidence lives in `TestResults/sheet-diagnostic/<RunId>/`: source/seed/binary manifests, commands and process state, TRX/logs, screenshots with UIA observations, checkpoints and app lifetime. `-Trace` requests a new `app-trace.jsonl` through `GHPB_SHEET_DIAGNOSTICS`; an older immutable app may not implement that probe. Missing, dropped or incomplete trace records are unavailable evidence, never zero work. The runner restores its process environment and working directory and refuses an existing run directory.

Only visible-title selection and the up/down arrow pair have one warmup plus five measured samples. Cached Project readiness, commit, Undo and Project roundtrip are single observations; wheel/drag phases have their own raw observations. Driver timings include input, UIA calls, readiness polling and recorded waits. They are not product input or rendering milliseconds. Different driver pacing, workload, binaries or tracing configurations must not be compared as equivalent samples.

App `ui-span` records measure synchronous method work with nested inclusive spans and per-thread managed allocation deltas. These include managed probe overhead and exclude native XAML allocations. Core checkpoint records distinguish synchronous work from asynchronous wall spans; they are not isolated disk time or per-span thread attribution. Rendering callbacks occur before presentation: callback gaps and post-span callbacks do not prove displayed pixels or the duration of a blank screen. Correlate raw timestamps with screenshots, stable row/field identities, actual typed text and durable Buffer/Change state. Do not add nested spans together.

A successful driver result means the selected diagnostic completed. Inspect `run.json` omissions and raw observations, including unavailable scrollbar thumbs and `wheel-endpoint-not-reached`; a screenshot then shows the attained viewport, not the requested endpoint. Review the typed target IDs, durable conditions and normal process exit before drawing conclusions. Geometry, UIA focus and nonempty PNGs alone do not establish readable, stable content. Keep failures and omitted phases visible; do not infer a speedup, supported maximum size or human acceptance from completion.

## Scoped observation safety mapping

The operation reader traverses complete Project field definitions, then directly fetches the exact target item and all its value pages. This deliberately reuses `ReadSession` identity, ownership, option, pagination and error parsing. Field traversal depends on fields/options, not unrelated items. It returns only `FieldObservation`; it cannot satisfy registration, reconciliation or creation-promotion complete-snapshot requirements. Initial review and creation promotion retain `ReadAsync` full traversal.

| Existing predicate | Fresh evidence used for every dispatch validation and independent readback |
| --- | --- |
| Bound account, host, executable, keyring and required scope | Existing `RecheckAsync` plus every guarded `SendAsync`; no cached permission authorizes a request |
| Correct operation ownership | Exact Title Issue key or Select item/Project/field key shape |
| Target membership and identity | Direct node typename/id, item type, Project ID, content Issue typename/id and non-archival |
| Title value and capability | Typed Issue title/state/identity parsing and Issue `viewerCanUpdate` |
| Select definition and capability | Exact definition ID, Project ownership, supported single-select type and Project `viewerCanUpdate` |
| Current/intended options | Unfiltered definition options, unique IDs and exact observed/intended option membership; names never resolve identity |
| Absent means known empty | Complete definitions plus target value traversal with stable totalCount, terminal pageInfo, valid advancing cursors, unique value/field identities and no errors/unknown field identity |
| Failure or cancellation | No observation on incomplete/partial/error data; existing executor Unknown/Waiting and durable recovery handling remains in force |

Completeness is deliberately narrower than a Project snapshot: exactly one requested item, its value connection, and the definition connection. Unrelated items are not observed or reconciled. Missing initial values can be retrieved through the existing dedicated value query, but a missing/error continuation never proves empty. Unsupported unrelated values retain the reader's conservative classification. Pagination and read/write races still exist; this is not a transactional snapshot, compare-and-swap or exactly-once guarantee. Every continuation is guarded and verifies item/Project identity; the existing reader does not atomically lock item content, archival or definitions across pages.

Schema references: [global node lookup](https://docs.github.com/en/graphql/guides/using-global-node-ids), [Project types and connections](https://docs.github.com/en/graphql/reference/projects), [Issue fields](https://docs.github.com/en/graphql/reference/issues). `fieldValueByName` is not used. The [official API best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api) recommend sequential requests and pauses between mutations; existing one-second mutation pacing, rate-limit waiting and retry decisions remain unchanged.

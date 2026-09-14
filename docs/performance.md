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

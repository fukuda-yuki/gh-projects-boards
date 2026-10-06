# Tests

Derive acceptance from the relevant Issue and [specification](../docs/spec.md). Use the least expensive test boundary that can detect the failure. Preserve contractual behavior when changing test mechanics.

## Test policy

**Primary testing order: logic-layer unit tests > UI-layer integration tests > E2E tests.** Logic tests carry the main behavioral coverage and development feedback; UI integration tests verify presentation wiring and states; E2E provides representative whole-application and native-boundary confidence. The goal is not merely to run E2E less often: do not leave rules or UI-state combinations covered only by E2E when a lower layer can verify them. This is a priority for where behavior is verified, not a test-count ratio, a per-class mocking requirement or a prohibition on E2E/live validation.

Start with a short test list covering normal behavior, boundaries, failures and prohibited side effects. Assign each behavior to the lowest reliable boundary and identify the remaining UI/native/external integration risks. Use short Red-Green-Refactor cycles there. Confirm a test's intended failure before implementing the behavior; unrelated build/environment failure is not a behavioral Red. Bug fixes start with a reproducer. A native or live reproducer is valid when necessary; add a lower-layer regression for the underlying defect wherever it can detect the failure.

Prefer real in-process collaborators and observable results. A unit of behavior may exercise several collaborating classes. Substitute external or nondeterministic boundaries when control is needed; do not mock the behavior under test or calculate expected values with the same production logic. Validate real adapters separately. Persistence tests must use the actual implementation and isolated storage. Adapter/process/storage integration remains necessary; the priority order does not replace it with mocks or mislabel it as unit testing.

UI-specific selectors, focus and lifetime mechanics may change when justified by the specified user contract. Keeping a suite available does not require running it on every iteration.

Select executions by changed behavior, affected boundaries and explicit acceptance needs, not by copying a previous delivery's command list. Prefer focused logic tests during implementation, then affected UI integration and adapter tests. Run selected E2E, physical IME or live checks when they establish a relevant risk not covered below; full regression remains appropriate for broad changes, shared-boundary risks or an explicit acceptance gate. There is no fixed execution quota or blanket all-suite requirement per edit, commit or UI task. Explain the higher-boundary risk, not just that a script exists. Do not debug ordinary UI waits or deterministic rules primarily through live GitHub when they can be reproduced with synthetic boundaries. Repository policy takes precedence over generic skill-generated batch-UI checklists.

Document-only changes need no product behavior execution; review their consistency and links. Behavior-preserving refactoring uses relevant existing regression tests at the affected boundaries. A targeted run can satisfy its declared scope without being described as full regression. Distinguish tests outside the selected scope, unavailable relevant coverage, and selected tests that failed or were skipped. Report unavailable execution honestly; never turn missing coverage into a pass.

### Test design

Assert behavior, not interactions. Exercise the real in-process collaboration and assert its observable result, state change, error or persisted output. Do not prove the same behavior through call counts, call order, internal method invocation or private state: interaction assertions multiply the observation surface and fail on behavior-preserving refactoring without establishing acceptance.

Do not restate a caller's behavior in its collaborator's test. Assert a collaborator directly only for its own contract that the caller's behavior test cannot isolate — invariants, boundary values, ordering, rounding, normalization and error mapping. Re-asserting the caller's viewpoint at the collaborator level is duplicate coverage: it adds no acceptance and turns an internal change in the collaborator into a false failure. When a behavior-preserving refactor fails a test, the test is the defect; fix or remove it rather than mocking the behavior under test to obtain a pass.

"Coverage" in this policy means the agreed behaviors that have a case, not a line or branch percentage. Do not pursue a coverage percentage, a case count or a suite-size target; add the cases the agreed behavior requires and none that repeats another. A large generated suite is not coverage.

Thin behavior — plain create/read/update/delete and pass-through mapping — is covered once at the integration boundary that exercises the real storage or adapter, without a parallel unit case per operation. Add logic-unit cases where branching, validation, ordering, identity, conflict or failure rules actually exist.

Name each case for the behavior it establishes — the condition and the expected outcome — not for the method under test. Structure a case as arrange/act/assert so its given, when and then are explicit, and use table-driven cases for input/output or branch matrices instead of near-identical copies.

Test design stays with the user. Propose a short behavior list for the change, state which behavior each case establishes and which boundary it belongs to, and flag cases you could not verify or place confidently. Do not widen the suite to satisfy a metric.

## Boundaries

The opt-in [performance measurements](../docs/performance.md) separate scheduler/storage timing, hosted frame callbacks, ordinary-app interaction and sandbox throughput. Machine timing targets are measurement evidence, not routine unit-test thresholds.

| Level | Scope | Execution |
| --- | --- | --- |
| Logic unit | UI-independent rules, validation, differences, Undo, planning and state transitions with real in-process collaborators | Primary development loop and deterministic CI; no UI or live GitHub |
| Adapter/storage integration | Real connection orchestration, gh process handling with a synthetic executable, and actual persistence with isolated storage | Deterministic CI; preserve alongside logic unit coverage |
| UI integration | Bounded collaboration of UI components, events/commands, presentation state and rendered results; real views/controls where their wiring is asserted | Secondary development boundary; existing runtime or a suitable test host, direct or external-driver interaction, isolated data and controlled dependencies |
| Desktop E2E | Representative user workflows through the application's principal layers to a declared result/endpoint | Supplementary, risk-selected; ordinary product execution, controlled desktop where needed, and explicit real or substituted external endpoints |
| Physical-key IME | Real Japanese IME, composition/focus/value and confirmation boundaries | Relevant native-input changes or acceptance; controlled Windows desktop and separate evidence |
| Human IME acceptance | Natural typing, candidates, cancellation/reconversion and selection/editing usability | Explicit human confirmation |
| Live GitHub | Production adapter and real CLI against exact sandbox resources | Relevant external-contract risk or acceptance; opt-in, authorized and independently read back |
| Performance | Defined workload, warmup, sample counts and environment | Separate raw measurements; #12 owns acceptance |

The first four rows describe test scope. Physical IME, live GitHub and performance describe additional execution/evidence requirements, not automatic E2E classifications; human acceptance is separate sign-off. Record scope, mechanism and environment separately.

Classify each case by its declared system boundary, actual production collaboration, fixture setup, replaced dependencies and assertions. UI automation, an external process, a test host, the number of screens, or the test project's name is not sufficient to classify it. Clicking one control does not make a test UI integration when the fixture and action exercise a whole-application workflow; launching the ordinary executable does not make a deliberately bounded UI collaboration E2E. An app-level E2E can stop at fake gh, but it must disclose that boundary and cannot establish real-GitHub behavior. Conversely, a focused real-CLI/API adapter check can be live integration without being an application E2E.

Evidence must match the claimed behavior. Core tests do not establish view/control interaction. A UI test must exercise the real control/event/binding path it claims, but it need not establish all OS or whole-application behavior. Native focus, IME, clipboard/picker and process-lifetime assertions require the real facilities and observations relevant to those assertions, regardless of scope label. Ordinary-product E2E must use the product executable and verify its WinUI module, not substitute a probe or placeholder.

## Workspace shell and settings (#81)

Build from the short checkout path without restoring packages:

```powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~PlanWorkspaceTests|FullyQualifiedName~PlanLivePreflightTests'
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanWorkspace'
C:\w\g76\scripts\Test-E2E.ps1 -Filter 'FullyQualifiedName~PlanningWorkspaceJourneyTests'
```

`PlanWorkspaceTests` exercises scoped storage, restore, matching and refreshed assignee names through real readers and isolated fake gh. `PlanWorkspaceHostedTests` mounts real views and drives native controls for discovery/open/switch, mappings, explicit field addition, settings recalculation/Undo, CSV and settings files, excluded counts, invalid input and close. Review regressions use real file locks for save retry and catalog failure, hold real fake-gh subprocesses until cancellation, and inspect bounded Project-list geometry. Calendar controls exercise explicit date addition/removal, duplicate rejection and Undo. Only the external gh executable and OS file picker are substituted. See the [host guide](GhProjectsBoards.UiIntegration.Tests/README.md).

`PlanningWorkspaceJourneyTests` uses the ordinary Release executable and public UI Automation with 1,000 synthetic tasks, from fresh data through connection, one-click Project open, mapped task presentation, a focused Unicode text edit, normal close and durable restart. At 1600 × 960 it records the initial date/predecessor visibility, Gantt width and physical row pitch. This input is not an IME composition sequence. Its endpoint is isolated fake gh, not GitHub. The runner records source/environment, hashes, logs and TRX under `TestResults/e2e` and rejects zero execution and skipped selections. Physical IME, live sandbox and human acceptance remain separate evidence.

## Plan sheet and Gantt (#78)

PlanSheetEditingTests verifies displayed predecessor IDs, filtered-row identities and rectangular paste rules. Phase 3 PlanDocumentTests remains the authority for atomic operations, scheduling rejection, Undo/Redo and storage. The real-control PlanSheetHostedTests mounts the product renderer and checks pending/committed input, rectangular copy/paste, fill, clear, Undo/Redo, insertion, hierarchy, hidden columns, status date/reasons, cycle errors, recycling and sheet/bar alignment at day/week/month scales. Only the OS clipboard is substituted in routine copy/paste cases. UI Automation selection/invoke patterns, native focus, TextBox input and control events remain real.

~~~powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --no-restore --filter '(FullyQualifiedName~PlanSheetEditingTests|FullyQualifiedName~PlanDocumentTests|FullyQualifiedName~PlanWorkspaceTests)&FullyQualifiedName!~ThousandTasks'
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where '(cat == PlanSheet or cat == PlanWorkspace) and cat != PlanSheetNative and cat != PlanSheetPerformance'
~~~

Physical keyboard/pointer UI integration remains a separate desktop selection. It exercises Enter/Tab, Ctrl+D, Delete, Undo/Redo including invalid-input refusal, divider pointer drag and arrow-key adjustment, and fill-handle capture/release through actual native input, plus real OS clipboard copy/paste with preservation of the original clipboard. Do not report those cases passed from toolbar/automation invocation.

~~~powershell
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanSheetNative'
C:\w\g76\scripts\Test-E2E.ps1 -Filter 'TestCategory=GridIme'
~~~

PlanSheetImeTests replaces the old registered Boards route. Six ordinary-app cases use physical Microsoft Japanese IME keys for direct/F2 entry, conversion confirmation versus the following commit Enter, cancellation and reconversion. They inspect native focus, unpublished count and durable title, require normal process exit after each scenario and assert zero fake-gh mutations. Failure diagnostics preserve the original exit assertion when the window is already gone. They require the PMO desktop; compilation/discovery is not execution evidence. ReadyInputTests and --input-check remain an independent native-input diagnostic, not an alternative product renderer.

For 1,000-task, 20-person, ten-task FS-chain commit-to-frame measurement, use the opt-in PlanSheetPerformance case with no concurrent build/test workload. It records every frame outcome, exactly 20 valid edits, median/max and the 200 ms target comparison, plus a synthetic ordinary-app fixture. See [performance](../docs/performance.md). The next Rendered callback does not establish physical display latency or scrolling FPS.

The planning-editor tests exercise the shipped model and controls. Removed Apply, registration-checkpoint and legacy planning behaviors have no retained regression suite. Project retrieval, scoped connection and process tests, holiday CSV tests and the planning-editor invariants remain independent contracts.

## Execution evidence

Record command, source, environment, executed/passed/failed/skipped counts and artifacts. Keep failed and zero-execution attempts. The UI runner records binaries, source diff and test-source hashes; E2E records the ordinary executable and endpoint substitutions. Native clipboard, physical IME, live sandbox and human acceptance are separate evidence. No selected skip-only or discovery-only run is a pass.

## Sandbox throughput measurement

Keep `scripts/Measure-PlanningSandbox.ps1` as the standalone baseline until #79 has ordinary-product performance evidence. Product targets and operation-specific batch limits are in [decisions](../docs/decisions.md#throughput-targets-for-79).

```powershell
Set-Location C:\w\g76
& C:\w\g76\tests\MeasurePlanningSandbox.Tests.ps1
$measurementPlan = & C:\w\g76\scripts\Measure-PlanningSandbox.ps1 -Mode Plan | ConvertFrom-Json
# PMO desktop with network and sandbox authorization:
& C:\w\g76\scripts\Measure-PlanningSandbox.ps1 -Mode Run -RunMarker $measurementPlan.marker
# After interruption, use the original marker and honor recorded cooldown:
& C:\w\g76\scripts\Measure-PlanningSandbox.ps1 -Mode Cleanup -RunMarker <original-marker>
```

Plan makes no requests or mutations. Run is allowlisted to github.com repository fukuda-yuki/codex-sandbox and fukuda-yuki Project 3; it resolves their IDs and preserves #1. It requires existing NUMBER/DATE fields, creates 50 marked Issues with 10-alias create/add requests, samples three fully paged reads including overflowing nested connections, and measures serial update batches 1/10/25/50. Each update series has 300 writes over 100 cells; final values are independently read back.

`TestResults/live/<marker>/results.json` contains identities, timings, rate headers/data, partial responses, creation dispatches, auto-add observations/reconciliation, deletion receipts and cleanup verification. It contains no credentials. Auto-add observation uses up to three reads and 1 s/2 s waits; first-seen times are sampled visibility bounds, not workflow timestamps. The create/add timer includes those waits. Duplicate add aliases are resolved by Project/content identity while successful siblings are retained. Creation/add mutations are never retried automatically. Keep every failed run; a fresh Run needs a new marker.

Cleanup rediscovers exactly marked Issues on every attempt, deletes one per request, accepts scoped NOT_FOUND/410 as already removed, and verifies original Project item order, fields and #1. Resource limits and rate throttling remain distinct stop reasons. Only an explicit later Cleanup resumes; unrelated resources are never deleted to restore a baseline. Success requires `measurementCompleted`, `updatesVerified` and `cleanup.verified`, eight timing entries and no unresolved stop. No-throttle request cost is not a guarantee for another Project; header deltas may include other clients.

The offline tests run real document guards, response policy, orchestration and files with transport/poll waits substituted. They cover scope refusal, malformed queries, null content, nested pagination, creation uncertainty, partial/all-duplicate adds, delayed/invisible membership, unrelated errors, deletion receipts and resumable cleanup. They do not establish service schema, auth, live consistency latency or product throughput. Retain source/environment/commands/counts/artifacts and distinguish estimated 1,000-item time from an actual measurement.

## Planning editor Core scheduler (#78)

`PlanEditorSchedulingTests` exercises the real new model, calendar, pure input transformations and scheduler with fixed dates. It covers work/rate/calendar boundaries, ordinary zero-effort milestone entry versus completed work, row isolation for invalid refreshed values versus rejected typed edits, caller-supplied today versus an explicit status date, retained versus calculated dates, nullable baseline differences, dependency/hierarchy rejection, inherited predecessor constraints, summary missingness and no leveling. Fractional-rate chains at 30/70/90 percent, rate changes and work immediately above/below a day boundary verify exact internal endpoints. Automatic start edits may pass old calculated ends; inverted kept pairs are rejected. The existing holiday CSV parser and bundle reader are real collaborators. No storage, gh, controls or network are involved; this is logic evidence, not UI or publish acceptance.

```powershell
Set-Location C:\w\g76;
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~PlanEditorSchedulingTests&FullyQualifiedName!~ThousandTasks' --logger 'trx;LogFileName=phase2-scheduler.trx' --results-directory C:\w\g76\TestResults\phase2
# Opt-in deterministic timing experiment; no CI threshold assertion:
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ThousandTasksReportTwentyRecalculationSamples' --logger 'console;verbosity=normal' --logger 'trx;LogFileName=phase2-performance.trx' --results-directory C:\w\g76\TestResults\phase2
```

The timing case creates 1,000 tasks, 20 people, 900 FS edges and mixed progress; five warmups precede 20 measured complete recalculations. It prints all samples, median, maximum and runtime/OS/processor count. Input/settings construction is excluded; model validation, dependency traversal, calculation and result construction are included. NUnit/coverage instrumentation may affect timing. This does not measure editing, rendering, compositor completion or the 0.2-second ordinary-app target.

## Planning editor local document (#78 / #79 / #81)

`PlanDocumentTests` exercises real commands, scheduling, operation patches and isolated filesystem storage. The boundary is Core plus storage integration: there are no UI controls, fake save services or network endpoints. It covers each operation and exact Undo/Redo, atomic rejection, retained invalid source values, restart and the 200-step bound, unpublished markers, scope isolation, portable settings, concurrent/external writers, retryable failures, corrupt/unreadable/interrupted files and untouched legacy data. Invalid imports preserve the complete document and history.

```powershell
Set-Location C:\w\g76;
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~PlanDocumentTests&FullyQualifiedName!~ThousandTasks' --logger 'trx;LogFileName=phase3-document.trx' --results-directory C:\w\g76\TestResults\phase3
# Explicit real-filesystem timing experiment; no CI threshold assertion:
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ThousandTasksAutosaveAndRestoreReportLatencyAndBytes' --logger 'console;verbosity=normal' --logger 'trx;LogFileName=phase3-storage-performance.trx' --results-directory C:\w\g76\TestResults\phase3
```

The timing case starts with 1,000 existing rows, applies one 1,000-cell Remaining paste and measures synchronous command return, command through durable save, restart and checkpoint bytes. It verifies exact restoration and one-step Undo after restart. This is one observed storage sample, including serialization, actual writes and durable flush; it is not an edit-to-screen or rendering measurement. The NUnit runner and coverage instrumentation may affect results. For a normal close, callers await `FlushAsync`; failed saves retain memory and require explicit retry or reconciliation.

## Planning editor publication (#79)

`PlanMergeTests` owns the B/L/R decision table. `PlanPublishTests` owns batch boundaries and the calculated-date/summary-effort review contract. `PlanPublisherTests` uses the real Core session, isolated checkpoint store, connection service, transport and **real fake-gh subprocesses**. It covers complete pagination, field batches, conflict resolution/Undo, native relationships/order, failed reads, explicit field setup, partial aliases, resource limits, interrupted creation/update responses, workflow auto-add reconciliation, restart, verification mismatches durable Retry-After/reset deadlines, rejected-title correction, inserted-row anchors, and Undo after partial or complete publish. Process counts are emitted per case in TRX output; synthetic remote state independently establishes outcomes. These are adapter/storage integration checks, not UI acceptance or real GitHub evidence.

```powershell
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-restore --filter '(FullyQualifiedName~PlanMergeTests|FullyQualifiedName~PlanPublishTests|FullyQualifiedName~PlanPublisherTests|FullyQualifiedName~PlanDocumentTests)&TestCategory!=HistorySequence' --logger 'trx;LogFileName=phase4.trx' --results-directory C:\w\g76\TestResults\phase4
```

The opt-in live proof invokes the product Core publisher on the allowlisted repository/Project. Before mutation, its schema check sends at most two input-type introspections per request, retains each response and reports bounded GitHub error types/messages. It creates 100 marked seed Issues and three batches of 50 new Issues (250 total), refusing a plan above 300. The harness resolves Estimate / Remaining / Actual (NUMBER), Start date / Target date (DATE) by exact name and type before seeding, without creating fields. The two optional Japanese scheduling fields stay unmapped. It locally fixes the original baseline rows to preserve their existing dates. For the workload: each run updates 100 owned Issues across three distinct fields (300 distinct cells) with new values. Derived date writes are included in total publish timing and reported separately from the 300 input cells. Three full product refreshes record verified item/page counts, seconds per item/page and an explicitly labelled linear 1,000-task extrapolation. Product creation pacing is included in measured write time; setup waits are recorded separately. Print the conservative budget (480 mutation requests including derived date writes, item ordering and cleanup; retries/reconciliation excluded) and estimated 20–30 minute duration before mutation (network/throttling can extend it). Independent Cleanup mode deletes only owned Issues, one per request with a two-second minimum interval, and verifies the original Project fingerprint; allow approximately 8–9 minutes for 250 deletes. Preserve baseline Issues, fields and values. This is Core throughput evidence, not UI/20-person/human acceptance.

```powershell
C:\w\g76\scripts\Test-PlanPublisherLive.ps1 -Mode Run -ArtifactsRoot C:\w\g76\TestResults\plan-publish-live-review-07
# Independently recover cleanup after interruption, using the same recorded directory:
C:\w\g76\scripts\Test-PlanPublisherLive.ps1 -Mode Cleanup -ArtifactsRoot C:\w\g76\TestResults\plan-publish-live-review-07 -NoBuild
```

The invariant matrix exercises real plan/session/storage collaborators with partial, failed and uncertain writes, title correction, Undo/Redo, later edits, republish and restart. Fake gh rejects transient hierarchy/dependency cycles and supplies changing pagination totals so product read retries and unverified recovery are exercised through the real adapter. Assertions concern durable identities, local/remote values, duplicate prevention, unlocked preflight failures and final acyclic relationships; they do not replace these behaviors with mocks.

Independent harness reads require two consecutive complete snapshots with the same membership/order, bounded to 120 seconds with three seconds between attempts. `settling.jsonl` records these observations and waits outside product timing samples. Failure diagnostics retain the read outcome and problem kinds/stages without payloads; incomplete snapshots are never accepted as a baseline or cleanup verification.

`plan-publish-live.json` must contain nine passing samples and `CleanupComplete: true`; `processes.jsonl` preserves request timings and `schema-01.json` through `schema-04.json` record the checked input contracts. The live transport rejects writes to baseline Issue/item identities before dispatch. `failure.txt` retains a failed run, including preflight failure. Failures return a normal non-zero exit; owned cleanup still runs after workload failures, and a cleanup failure is retained alongside the original failure. New GraphQL sub-issue ordering and field-creation contracts require the PMO's live schema proof; fake gh validates the intended request shape and local behavior only.

History invariant validation reopens every saved checkpoint and traverses both retained history directions. The routine fixed-seed matrix runs 20 sequences of 30 operations; the explicit `HistorySequence` matrix runs all 200 sequences using the same assertions through the real publisher, fake-gh subprocesses and isolated storage. It mixes local edits/relationships/order/insertion, random remote hierarchy/blocked-by changes, partial/failed/uncertain publish outcomes, Undo/Redo and restart. Each step reopens the checkpoint and traverses all retained history in both directions, checking durable outcomes, identities, remote graph acyclicity and absence of GitHub writes during history travel. Report the executed sequences and operations separately from NUnit case counts. Refresh page rates sum only the refresh sample's item-page request durations; total command time and its extrapolation remain separate. Position updates use serial single-alias requests; retain safe HTTP/outcome diagnostics for failed requests without payloads.

Seed membership waits for exactly the baseline item identities plus every created seed (not merely two unchanged partial memberships). After 120 seconds, add missing seeds explicitly and reconcile auto-add races by identity; allow a further bounded 30 seconds for verification. Unexpected members or missing baseline items fail. Setup observations and elapsed time stay in `settling.jsonl`, outside product samples. Cleanup retries transient HTTP 5xx or GraphQL UNCLASSIFIED server failures at most three times with 2/4/8-second backoff (respecting longer server delays); scoped NOT_FOUND/404/410 means already deleted. Other failures stop immediately. Retry diagnostics remain in `cleanup-retries.jsonl`. Every cleanup send, including a retry, starts at least two seconds after the previous send; this also applies when moving to the next Issue after recovery.

```powershell
# Full history sequence experiment, explicitly selected; excluded from routine runs:
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --filter 'TestCategory=HistorySequence' --logger 'trx;LogFileName=history-sequences.trx' --results-directory C:\w\g76\TestResults\history-sequences
```

For planning-editor UI phases, report the complete default hosted suite in addition to focused checks. Hosted asynchronous failures remain in the run outcome; each following case starts a fresh tracking context so an earlier failure does not become its failure. Teardown must remove the visual root even when idle/failure checks throw.

## People view (#80)

`PlanPeopleTests` covers the two Issue acceptance examples, period boundaries and weighted aggregation, daily scheduler allocation across FS endpoints, personal holidays, fixed/completed/summary tasks, current assignment, ambiguous assignment groups and missing inputs. It exercises the real scheduler and pure aggregation without UI or remote access.

The people cases in `PlanWorkspaceHostedTests` exercise actual controls, overload text/color, period changes, expansion, all four task edit fields, allowance persistence, invalid input/navigation, one-step Undo and bounds for 20 people plus both assignment groups at 1280×720. The complete default hosted suite remains the phase regression boundary. `PlanningPeopleJourneyTests` opens 1,000 synthetic tasks with 20 people through the ordinary executable and public UI Automation, checks logical client dimensions and visible person bounds, switches day/week, edits allowance and restarts. Only the GitHub endpoint is replaced by fake gh; screenshots require inspection and do not become visual evidence merely because the journey passes.

```powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --no-restore --filter '(FullyQualifiedName~PlanPeopleTests|FullyQualifiedName~PlanEditorSchedulingTests)&FullyQualifiedName!~ThousandTasks'
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild
C:\w\g76\scripts\Test-E2E.ps1 -Filter 'FullyQualifiedName~PlanningPeopleJourneyTests'
```

## Publish workspace (#79)

`PlanWorkspaceHostedTests` invokes real controls for before/after review, explicit confirm/close, conflict choices, refresh adoption and read failure, stage presentation, new Issue creation, row failures/unverified state, retry and local Undo after publish. It uses the real publisher, isolated checkpoint files and fake-gh subprocesses. `PlanSheetHostedTests.SaveFailureKeepsTheEditAndRetryPersistsItWithoutAnotherUndo` covers edit autosave failure at the sheet boundary. Run the entire default hosted suite, not only new cases.

`PlanningPublishJourneyTests` drives the ordinary executable through public UI Automation: edit, review, confirm, close/restart and confirm no resend, with a second case losing a creation response and failing subsequent reads before restart. The substituted endpoint is fake gh; these journeys establish neither live GitHub nor physical IME. Desktop captures must be inspected: an all-black capture is unavailable visual evidence even when UI Automation passes.

```powershell
Set-Location C:\w\g76;
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild
C:\w\g76\scripts\Test-E2E.ps1 -Filter 'FullyQualifiedName~PlanningPublishJourneyTests'
```

## Pending-input invariant: hosted checks

The shared contract is [pending-input invariant](../docs/spec.md#pending-input-invariant-all-editing-views). Tests use real controls, focus, events and isolated persistence; they do not substitute the editing collaboration.

| View | Hosted cases | Observable contract |
| --- | --- | --- |
| Sheet | `SheetPendingInputSurvivesLeavingTheAcceptedFilter`, existing `RejectedFilterKeepsInvalidCellVisibleAndCanBeAppliedAfterCorrection` | Invalid input remains reachable after a title edit leaves the filter; rejected filter/history commands focus the problem cell. |
| Sheet | `SheetRefusedColumnToggleRestoresAcceptedVisibility`, existing `RejectedZoomKeepsSelectionScaleAndLabelsTogether` | Refused visibility and zoom selectors show the accepted setting. |
| Sheet | `SheetEscapeDiscardsRetainedInput` (`PlanSheetNative`) | Real Escape discards the pinned cell, clears its error and permits leaving the filter without a new Undo entry. |
| People | `PeoplePendingInputSurvivesLeavingTheDrillDown` | Remaining zero removes work from the period but not an invalid Actual editor; refused navigation focuses that editor. |
| People | `PeopleRefusedControlsReflectTheDocument`, `PeopleFailedSaveControlsReflectTheAcceptedDocument` | Fixed, assignee and zoom controls return to current values, including accepted in-memory changes after save failure; the invalid cell remains focused and correctable. |
| People | `PeopleEscapeDiscardsRetainedInput`, `PeopleEscapeRestoresTheCurrentDocumentAfterFailedSave` (`PlanSheetNative`) | Real Escape releases a retained task, clears its error and restores the current committed display even after a save failure, without another edit. |

Native Escape cases are excluded from the default selection and require the PMO desktop. Do not replace their physical-key path with direct handler invocation or claim them from non-key control tests.

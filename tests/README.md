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

## Routine verification

```powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --no-restore --filter 'TestCategory!=LiveGitHub&TestCategory!=HistorySequence&FullyQualifiedName!~ThousandTasks' --logger 'trx;LogFileName=core.trx' --results-directory C:\w\g76\TestResults\core
dotnet build C:\w\g76\tests\GhProjectsBoards.UiIntegration.Tests\GhProjectsBoards.UiIntegration.Tests.csproj -c Release --no-restore
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild
C:\w\g76\scripts\Test-E2E.ps1 -Configuration Release -Filter 'FullyQualifiedName~PlanningWorkspaceJourneyTests|FullyQualifiedName~PlanningPublishJourneyTests|FullyQualifiedName~PlanningPeopleJourneyTests'
```

Restore once in a network-enabled development environment. Missing assets or NU1301 block dependent builds; do not call a zero-run outcome passed. Use the short path above for Release packaging when a long checkout causes MSB3030. The complete default hosted suite is the UI regression boundary; native-input, performance and infrastructure-failure categories are separately selected. See the [host guide](GhProjectsBoards.UiIntegration.Tests/README.md).

Claude Code cloud sessions run on Linux and can only exercise Core logic. The [session hook](../.claude/hooks/session-start.sh) installs the .NET 10 SDK and restores the Core test project for `net10.0`, because NUnit skips the whole `net10.0-windows` assembly on Linux. Build and test there with the same override and `--no-restore`; an implicit restore drops the override and reverts the assets to `net10.0-windows`:

```bash
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-restore -p:TargetFramework=net10.0 --filter 'TestCategory!=LiveGitHub&TestCategory!=HistorySequence&FullyQualifiedName!~ThousandTasks'
```

Adapter tests that start fake gh as a Windows executable or expect Windows paths and credential storage fail there; they are Windows evidence, not Linux defects. Windows CI and the owner's PC remain the evidence for those adapters and for every UI, desktop, IME and live boundary.

## Planning editor Core scheduler (#78)

PlanEditorSchedulingTests covers work/rate/calendar boundaries, status date and Project start, kept/calculated dates, zero-effort milestones, in-progress and complete work, hierarchy roll-ups, cycles, external predecessors, isolation of invalid refreshed values, exact fractional hours and no leveling. PlanLatenessTests covers status-date boundaries, both lateness levels, descendant roll-ups and leaf totals, predecessor delay, end reasons, and entered versus recalculated unpublished inputs. PlanSheetEditingTests covers input conversion and operation preparation. HolidayCsvImportTests and PlanningReaderTests retain the actual holiday/parser/read boundaries. Timing is opt-in; use [performance measurement](../docs/performance.md).

## Planning editor local document (#78 / #79 / #81)

PlanDocumentTests and PlanHistoryTests use real operations, scheduler and isolated storage for atomic rejection, 200-step Undo/Redo, autosave coalescing, restart, concurrent-file protection and portable settings. Storage latency and bytes are distinct from UI frame timing. Session close awaits FlushAsync; failed saves preserve memory.

## Workspace shell and settings (#81)

PlanWorkspaceTests exercises real connection/discovery, Project selection and catalog persistence through fake-gh subprocesses. PlanWorkspaceHostedTests mounts actual controls for connection/open/switch, the current-Project checkmark, shell chrome and view-specific legend, status counts, default divider geometry at wide and narrow client sizes, mapping, calendar/people settings, file picking, operation cancellation and failure recovery. Only external endpoints and native file selection are substituted. PlanningWorkspaceJourneyTests drives the ordinary app, opens the offline evaluation Project at zero unpublished tasks, edits and restarts.

## Plan sheet and Gantt (#78)

PlanSheetHostedTests exercises real cells, events, selection, input, rectangular operations, Undo, visible dates/bars, alignment and pending-input retention with a real PlanSession. It also covers two-level lateness markers and end-date color, entered versus recalculated unpublished cells, full-value title/assignee editing and copying, selection-line reasons/pills/totals, and Gantt lateness tint/labels and row geometry. PlanOverviewHostedTests adds requirement folding with stable identities, filter/input focus, full-period and rapid selected-date navigation, scale anchors and date-viewport preservation, full-period gutters and status-date inclusion, dependency-arrow direction, and searchable predecessor selection with visible popup bounds. Routine clipboard cases substitute the OS transport. Native clipboard/keys/pointer and frame performance are opt-in:

```powershell
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanSheetNative'
C:\w\g76\scripts\Test-E2E.ps1 -Configuration Release -Filter 'TestCategory=GridIme'
```

PlanSheetImeTests uses physical Microsoft Japanese IME keys in six ordinary-app cases for direct/F2 entry, confirmation versus cell commit, cancellation and reconversion. It verifies native focus, durable values, ordinary close and zero fake-gh writes. These require the PMO desktop; Unicode/TextBox assignment is not physical IME evidence. Pending text/error visibility and Escape are checked in real hosted controls, with physical Escape in the native category.

## Planning editor publication (#79)

PlanMergeTests covers baseline/local/remote decisions; PlanPublishTests covers batch limits and review semantics. PlanPublisherTests exercises real Core, storage, connection, transport and fake-gh subprocesses for partial failures, serial batching, parent/order/dependency changes, duplicate creation guard, verification, workflow auto-add, restart and Undo after publish. Sub-issue and Project moves use single-alias serial requests. Integration assertions verify resulting state, not merely dispatched aliases. PlanLivePreflightTests verifies allowlists and harness failure/cleanup behavior offline.

The routine history matrix runs 20 seeds with 30 operations each. The larger experiment is explicitly selected:

```powershell
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --no-restore --filter 'TestCategory=HistorySequence' --logger 'trx;LogFileName=history.trx' --results-directory C:\w\g76\TestResults\history
```

Live proof uses the product publisher, exact sandbox identities and independent readback. It creates up to 250 owned Issues, measures three refreshes, three 300-cell updates and three 50-Issue creations, then deletes only its owned resources and verifies the original baseline. Budget and stop rules are printed before mutation. Respect the current task's authorization: this command changes the sandbox and is not part of ordinary offline validation.

```powershell
C:\w\g76\scripts\Test-PlanPublisherLive.ps1 -Mode Run -ArtifactsRoot C:\w\g76\TestResults\publisher-live-new
# After interruption, use the same evidence root:
C:\w\g76\scripts\Test-PlanPublisherLive.ps1 -Mode Cleanup -ArtifactsRoot C:\w\g76\TestResults\publisher-live-new -NoBuild
```

Require nine passing samples and CleanupComplete in plan-publish-live.json. Keep failure.txt, schema responses, process timings, settling observations and cleanup retries. Seed membership must equal baseline plus all owned seeds. HTTP 5xx/UNCLASSIFIED cleanup retries are bounded and preserve two-second spacing. Core timings do not establish ordinary-screen latency; extrapolated 1,000-item refresh is not a measured 1,000-item result. Existing Test-LiveGitHub.ps1 and Test-ProjectRead.ps1 exercise the current connection/reader contracts independently.

## Publish workspace (#79)

Publish cases in PlanWorkspaceHostedTests cover changed markers/count, review, conflict outlines and selected GitHub values, failure InfoBars/retry, pending inputs and workspace-owned operation lifetime. Remote-operation cases cover the progress bar, running command labels and locked cells; local Undo leaves remote progress hidden, and successful saves clear save failures. PlanPublishReviewHostedTests covers grouped full-value review, 1,040-Issue virtualization and last-item access, and recycled conflict actions without sending mutations. PlanningPublishJourneyTests drives two ordinary-app workflows including interrupted publication/restart through fake gh. Neither establishes live service acceptance.

## People view (#80)

PlanPeopleTests covers both Issue examples, weighted day/week/month aggregation, partial-day scheduler allocation, current assignees, missing inputs and fixed/complete/summary work. It also retains known daily overload dates and causes when period averages hide them or other work is unallocated. PlanPeopleOverloadHostedTests exercises week/month warnings, accessible untrimmed text, date-specific contributing tasks, reassignment and Undo through the real People view. People cases in PlanWorkspaceHostedTests cover real edits, contributing tasks, overload text/color, retained pending input and 20 people plus assignment groups at 1280×720. PlanningPeopleJourneyTests covers the ordinary app's period change, allowance edit and restart at that client size.

## CSV new tasks (#82)

PlanCsvImportTests verifies encoding, whole-file validation, line errors, keys/references, duplicates and one-step Undo. Publisher adapter tests verify 100 imported tasks and their relationships. Hosted workspace cases drive actual import controls with only file selection and remote access substituted. Native picker and live publication remain separate checks.

## Main screen design conformance (#109)

UI integration covers headers/tooltips, date round trips, two lateness levels, unpublished and conflict markers, selection-line explanations, Gantt geometry and scale navigation, labelled commands, shell chrome and Undo/Redo, default divider widths, operation-state feedback and the light theme. Whether the ordinary app matches the agreed mockup at the PMO's display size is a separate visual check. Its [conformance test plan](plans/issue-109/README.md) holds the states, viewports, checklist and reference images. It records findings and bug candidates; it does not establish the owner's acceptance.

## Offline evaluation fixture

EvaluationFixtureTests runs the fixture initializer, stored document, workspace connection and refresh through real fake-gh subprocesses. It verifies 40 requirements with 25 tasks each (1,040 Issues), 20 people, zero unpublished tasks before/after refresh, complete load allocation, near-capacity weekly demand across the two-wave plan, a daily overload hidden by its weekly average, one total-allowance overrun, native sibling order, and refusal to replace an occupied root. Every displayed week is reported, including the low-demand tail. Start-Evaluation.ps1 uses this initializer and the ordinary executable. Its fixed status date makes restart reproducible; Resume retains local and synthetic remote changes. See [Build and run](../README.md#offline-evaluation).

## Execution evidence

Record source, environment, exact command, executed/passed/failed/skipped counts and artifacts. Retain failed attempts and distinguish exclusions from skips. The hosted runner records source diff, source/binary hashes and runtime outcomes; E2E records the ordinary executable and substituted endpoint. Inspect captured images before claiming visual review. Physical IME, live GitHub, performance and PMO evaluation are separate claims.

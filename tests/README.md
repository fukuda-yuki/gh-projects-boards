# Tests

Derive acceptance from the relevant Issue and [specification](../docs/spec.md). Use the least expensive test boundary that can detect the failure. Preserve contractual behavior when changing test mechanics.

## Test policy

**Primary testing order: logic-layer unit tests > UI-layer integration tests > E2E tests.** Logic tests carry the main behavioral coverage and development feedback; UI integration tests verify presentation wiring and states; E2E provides representative whole-application confidence. The goal is not merely to run E2E less often: do not leave rules or UI-state combinations covered only by E2E when a lower layer can verify them. This is a design and coverage priority, not a test-count ratio, a per-class mocking requirement or a prohibition on E2E/live validation.

Start with a short test list covering normal behavior, boundaries, failures and prohibited side effects. Assign each behavior to the lowest reliable boundary and identify the remaining UI/native/external integration risks. Use short Red-Green-Refactor cycles there. Confirm a test's intended failure before implementing the behavior; unrelated build/environment failure is not a behavioral Red. Bug fixes start with a reproducer. A native or live reproducer is valid when necessary; add a lower-layer regression for the underlying defect wherever it can detect the failure.

Prefer real in-process collaborators and observable results. A unit of behavior may exercise several collaborating classes. Substitute external or nondeterministic boundaries when control is needed; do not mock the behavior under test or calculate expected values with the same production logic. Validate real adapters separately. Persistence tests must use the actual implementation and isolated storage. Adapter/process/storage integration remains necessary; the priority order does not replace it with mocks or mislabel it as unit testing.

Retain logic/adapter assertions during UI work. UI-specific selectors, focus and lifetime mechanics may change when justified by the specified user contract. Never remove, weaken, skip or relabel failures merely to obtain a pass. Moving redundant E2E coverage down requires an explicit mapping of contractual behaviors and observed replacement coverage before retiring those higher-layer checks. Retain representative native/end-to-end assertions that the replacement cannot establish. Record the mapping and evidence in the owning Issue/PR. Keeping a suite available does not require running it on every iteration.

Select executions by changed behavior, affected boundaries and explicit acceptance needs, not by copying a previous delivery's command list. Prefer focused logic tests during implementation, then affected UI integration and adapter tests. Run selected E2E, physical IME or live checks when they establish a relevant risk not covered below; full regression remains appropriate for broad changes, shared-boundary risks or an explicit acceptance gate. There is no fixed execution quota or blanket all-suite requirement per edit, commit or UI task. Explain the higher-boundary risk, not just that a script exists. Do not debug ordinary UI waits or deterministic rules primarily through live GitHub when they can be reproduced with synthetic boundaries. Repository policy takes precedence over generic skill-generated batch-UI checklists.

Document-only changes need no product behavior execution; review their consistency and links. Behavior-preserving refactoring uses relevant existing regression tests at the affected boundaries. A targeted run can satisfy its declared scope without being described as full regression. Distinguish tests outside the selected scope, unavailable relevant coverage, and selected tests that failed or were skipped. Report unavailable execution honestly; never turn missing coverage into a pass.

## Boundaries

The opt-in [performance runner](../docs/performance.md) separates synthetic Core timing, ordinary-app interaction and bounded live schema validation. Use `scripts/Test-Performance.ps1`; routine CI checks structural work counts and safety, never machine timing thresholds. Results and remaining acceptance belong to #12.

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

## Logic and integration

The editing/recovery suite exercises real scoped workspaces and isolated filesystem stores: exact title differences, whole-batch rejection, explicit clear, prior-state Undo, shared title versus independent selects, invalid pending buffers, interrupted replacement, corrupted schema/scope/history, competing writers, late changes during saving and old registration compatibility. Cache replacement has a final synchronous draft/generation predicate directly before atomic file replacement.

`GhProjectsBoards.Tests` references Core, not the UI application. Its routine fake gh scenarios have no network fallback. The separately gated creation-live proxy forwards explicitly scoped product requests to real gh, verifies run-owned identities, and can suppress a create response for recovery testing. Test dependency versions are pinned in its project file.

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj --configuration Release --filter 'TestCategory!=LiveGitHub'
```

Preserve coverage of Unicode/quotes/stdin, invalid executables, child environment isolation, timeout/cancellation, safe errors, HTTP/GraphQL outcomes, authentication/storage guards, stable identity, host changes, scopes, resource permissions and orchestration. No new UI acceptance is inferred from these results.

## UI integration

UI integration verifies collaboration among the UI components within a bounded feature or screen: input/control events, commands, presentation state, bindings and displayed results. Keep that collaboration real. Include the actual relevant WinUI view/control when asserting its wiring or rendered behavior, and provide the runtime, UI thread and dispatcher/lifetime handling it needs. Control dependencies outside the chosen scope to exercise success, failure and pending states without traversing an unrelated whole-app workflow for every combination. Do not mock the UI collaboration being verified.

For example, combine the real button, event/command wiring and presentation state; control only the service completion/result; activate the control and assert its busy/disabled state, error display and recovery to an operable state. Directly assigning a ViewModel property and reading it back does not prove this collaboration. A ViewModel/service test establishes only the collaboration it actually exercises; without the view it cannot prove XAML/control wiring. Calling a handler directly does not prove that the control event invokes it.

A dedicated UI test host is one implementation option, not the definition of UI integration. Existing application/test setup and an external driver such as FlaUI can also support a genuinely bounded UI integration case. Select the least costly reliable mechanism for the asserted behavior; neither a host nor an external driver guarantees a particular scope.

Before declaring coverage missing or selecting new infrastructure, inspect the relevant existing cases and fixtures, including those in `GhProjectsBoards.E2E.Tests`. Record the case, actual collaboration, real/replaced dependencies, entry/result boundary and assertions in the owning Issue/PR. Reuse or narrow existing mechanisms where appropriate. Add a minimal host/seam only for an identified uncovered behavior that existing mechanisms cannot test adequately. Record any remaining gap without inventing coverage, a new-project requirement or a completed classification audit.

These are allocation examples, not a reclassification of existing cases:

| Scope | Example |
| --- | --- |
| Logic unit | Validation, differences, Undo or execution-state rules across normal/boundary/failure inputs without navigating a screen |
| UI integration | A bounded control/event/presentation/rendering collaboration with the service boundary controlled, including busy, failure and recovery states |
| App-level E2E | A representative retrieve/edit/review/Apply/reopen workflow through real app orchestration, persistence and adapters, with the external endpoint explicitly declared |

## Desktop E2E

This section documents the existing desktop suite and runner, whose names include `E2E`; those names do not settle each case's classification. Apply the scope rules above when auditing or adding cases. Renaming a suite or adding a category is not new coverage and does not reduce the scope/cost of an unchanged whole-app journey.

`GhProjectsBoards.E2E.Tests` uses NUnit and FlaUI UIA3 with build-only app/fake-gh references. Its current external-driver tests may not call product internals. Launch the ordinary exe, use stable AutomationIds on real controls, wait for observable conditions and verify both UI close and the original process's exit status. These are this runner's mechanics, not universal requirements for UI integration. Do not assume a top-level native window has the same AutomationId as its XAML content or that every table exposes GridPattern.

Additional ordinary-executable journeys cover scrolled editing, shared drafts across Projects, actual normal process restart with pending text, rectangular clipboard operations, save-failure cancellation of navigation/close, edit-during-refresh protection and explicit unregistration with surviving shared work. The complete deterministic desktop suite is available through `scripts/Test-E2E.ps1`; select its scope under the test policy rather than treating the inventory as an instruction to run everything. Use `-Filter 'TestCategory=GridIme'` for separate physical Japanese IME direct/F2, cancellation and reconversion cases in real registered-Project title cells. `scripts/Test-ReadyInput.ps1` remains the independent input-check regression suite. Filtered runs require nonzero executed and zero failed/skipped tests within the selected scope; the default desktop run additionally checks every original required journey by name. A filtered pass is scoped evidence, not a full-suite pass or human grid acceptance. Run full regression when the selected risk/acceptance scope requires it.

```powershell
# Complete deterministic desktop regression when selected:
.\scripts\Test-E2E.ps1
.\scripts\Test-E2E.ps1 -Configuration Debug

# Example of a focused ordinary-process smoke, not full regression:
.\scripts\Test-E2E.ps1 -Filter 'TestCategory=E2E&FullyQualifiedName~OrdinaryExecutable_OpensAndCloses'
```

Use Windows x64 with the build prerequisites from [README](../README.md). Keep the interactive desktop unlocked, run serially and match app/test elevation. The script does not alter security policy, machine environment, lock settings or install a driver server. A disconnected/locked session is not valid UI evidence.

The connection contract includes these four journeys:

| Journey | Required observable result |
| --- | --- |
| Startup and close | Product title and connection screen are visible; ordinary close exits the launched process successfully |
| Connection and identity | Issue/Project diagnostics, explicit rebind after identity change, copyable host-specific login command and no mutations |
| Cancellation and close during gh | User cancellation and window close stop the owned child; controls recover appropriately |
| Actionable failures | Missing executable and missing login are explained without displaying an invented account or permission |

Routine tests choose fake gh through the normal path field, set isolated `GH_CONFIG_DIR` and use synthetic data. There is no developer credential or business-Project dependency. OLE/native clipboard access is test infrastructure; save/restore available clipboard content, retry short-lived contention, and report restoration failure rather than silently clearing it. Do not publish clipboard data.

Additional cases cover native picker selection/cancellation and both title-bar Close and Alt+F4, while an editable/read-only TextBox is focused and while gh is active. Tests never move focus away from the TextBox to prepare for shutdown. Command-copy checks materialize the original OLE formats before replacement, verify Unicode/custom-format restoration with synthetic data, then restore the user's clipboard and compare text privately. If an offered format cannot be captured, abort before copying. Arbitrary application-specific delayed formats still require their owning application's validation.

Results go to unique `TestResults/e2e/<run-id>/` directories. Keep TRX and metadata for the source/build/environment. Failure capture is limited to the app rectangle; overlapping windows may still contain private content, so use a clean desktop and review before sharing. Capture failure must not hide a test failure. Cleanup may terminate only owned processes and must not turn a failed normal-close assertion into a pass.

The runner retains build/test logs, source state, resolved package assets, runtime versions, file hashes for the app executable/DLL, Core DLL, WinUI DLLs and both test assemblies. Its full-suite required-journey checks and execution counters remain intact; policy-based selection does not weaken a selected suite's success criteria. Replacing a connection journey with an unrelated passing case cannot satisfy that gate. Connection fixtures record owned process IDs, normal versus forced exit and remaining recorded children. Raw screen/log/clipboard evidence stays local until reviewed for publication.

Desktop tests are opt-in (`GHPB_RUN_E2E=1`); use the script as the supported entry point. It sets child-process paths and artifact variables, restores prior process environment, and rejects zero execution, incomplete/skipped outcomes and failed tests. A plain discovery or skipped run is not successful E2E.

## Registration verification

The deterministic registration cases exercise production discovery, workspace and reader collaborators with external process responses, plus the real JSON store in unique temporary directories. They cover paging/errors, duplicate routes, 101 items/two repositories/two Projects, scoped identity, late cancelled results, partial/failed/cancelled attempts, file replacement failure, writer contention, corrupt/versioned data, restart and local removal. The original reader and connection regression tests remain intact.

`Test-E2E.ps1` also runs the ordinary executable through the visible Project entry point using isolated fake gh and `GHPB_DATA_ROOT`. New journeys register via a linked repository and URL, detect duplicates, scroll to item 101, restart the process into actual saved content/settings, unregister locally, cancel and close during retrieval, and clear private discovery on a connection change. Existing eleven connection/native-window journeys and twelve separately run IME scenarios remain distinct. All deterministic desktop launches receive a dedicated data root; no invalid override falls back to the developer's cache.

Screenshots and JSON caches use synthetic data and are retained privately with TRX/process records. Review images before publication. New registration/discovery flows do not create or mutate live fixtures; the optional existing ProjectRead smoke is independent and cannot establish wider discovery/two-Project acceptance. Draft/Undo persistence, enterprise policies and distribution are unverified by this suite.

## Editable-grid contract

Local-row and creation tests exercise whole-profile version 1–4 migration to v5 (including successful/unresolved operations, attempts, conflicts, drafts, local Undo and pending text), independent identities/destinations, all-or-nothing append/duplication/removal, mixed-paste Undo after Apply, stale commit guards, failed acknowledgement and retained re-registration. Refresh tests cover complete/partial/failed/cancelled observations and disappearing options without local-row loss. Ordinary-app journeys include offline addition, existing/local continuous editing, removal/Undo, normal restart, save failure, forced interruption and explicitly selected mixed Apply. Physical `GridIme` cases cover direct/F2 input, refresh/restart and creation promotion while composition is active. Fixture calls for local preparation must contain zero product mutations; approved Apply journeys assert exact payloads and counts.

Creation regression uses the real planner, executor, store and guarded readers with only the external gh boundary substituted. It covers retained dispatch uncertainty across reload/supersede, partial response identity, explicit URL binding and duplicate-risk retry, known-Issue setup review, auto-membership, incomplete reads, server defaults, explicit clear, option changes, promotion of later drafts/buffers, execution ownership and mixed Undo. Public UI tests exercise both ambiguity choices and reopen without replay. See the [ordinary creation workflow](../docs/creation-workflow.md).

Derive table cases from #7 and the few-row input contract in #24. Address rows by stable identity rather than visible row index. Selection/range navigation must not change draft or committed values. Verify direct physical-key Japanese input and F2 independently, retaining initial input exactly once. Verify IME confirmation versus cell commit, candidates, cancellation, reconversion, keyboard navigation and public UI Automation.

Unicode insertion is not IME evidence. The application must not replay keys/text, insert F2 on behalf of direct input, or use private TSF/runtime hooks. A passing F2 case does not establish direct input. Human usability acceptance stays separate from injected physical-key automation. Do not mark unimplemented table tests or another executable's results as passing product coverage.

Independent shell and test-infrastructure work does not wait for a grid component to pass. Table acceptance does.

## Native input execution

Select physical-key scenarios for changes affecting native editing, composition, focus, editor lifetime or explicit input acceptance. The input suite remains available and its full contract is preserved; unrelated logic/presentation changes do not automatically require every IME scenario.

```powershell
.\scripts\Test-ReadyInput.ps1
.\scripts\Test-ReadyInput.ps1 -Scenario reconvert-direct
```

The `ReadyInput` suite runs the ordinary `GhProjectsBoards.App.exe --input-check` with Microsoft Japanese IME on an unlocked Windows desktop. It builds the current solution and retains all twelve scenarios: selection, draft, reference, F2, mouse/keyboard direct input, both preenabled variants, direct/F2 cancellation and direct/F2 reconversion. The runner validates case names as well as executed/passed/skipped counts; a focused run must execute exactly its selected case. It is separate from the eleven deterministic connection/close/picker cases in `Test-E2E.ps1`.

Each case owns a fresh product process, verifies its loaded WinUI module, reads real editor/committed values and focus, and requires ordinary close with exit code zero. Selection/range checks inspect all six values. Exact commit-event counts supplement the observable assertions. The test-only foreground modifier never starts editing; direct input must use ordinary mouse/key selection without internal-editor UIA focus or F2 injection.

Unique `TestResults/ready-input/run-*` directories retain source/environment, build/test commands and logs, product/Core/WinUI/test DLL hashes, resolved package assets, TRX, scenario results, optional private screenshots and process-lifetime records. `GHPB_IME_TRACE` is a process-local, opt-in diagnostic file path used by this runner; it contains input values and must remain private until reviewed. Failed attempts remain separate. Existing prototype results are historical evidence and cannot substitute for execution of the integrated app.

For human confirmation, launch with `--input-check`. Use the standard TextBox as a positive control, then try direct typing after cell selection with IME enabled before and after selection. Compare F2; test conversion candidates and both Enter boundaries, cancellation/reconversion, arrows and Shift ranges. Editor text may change during composition, but the committed line must remain unchanged until cell commit. Check normal title-bar close while text focus remains. Record human observations separately in #24; automation and source review are not human acceptance. Full-grid/Tab/last-row/accessibility/performance acceptance remains outside this bounded check.

## Live sandbox

Use live checks for relevant production CLI/API, authentication/permission or service-contract risks and explicit acceptance. Sandbox authorization permits these checks; it does not require them for every implementation or commit. Prefer deterministic adapter and UI tests for cases they can establish, then use a bounded live scenario for the remaining external risk. Keep live execution available and preserve its independent readback and cleanup requirements.

Read the [authorized scope and validation record](https://github.com/fukuda-yuki/codex-sandbox/issues/1) before running. Targets are exactly `fukuda-yuki/codex-sandbox` and user Project `fukuda-yuki/3`; verify API IDs before mutation. Use designated stored keyring authentication with the required scopes. Never extract tokens or alter authentication configuration in tests.

```powershell
.\scripts\Test-LiveGitHub.ps1
.\scripts\Test-LiveGitHub.ps1 -DiagnosticsOnly
.\scripts\Test-LiveGitHub.ps1 -GhPath 'C:\path\to\gh.exe'
```

The default run creates a disposable Issue with Unicode/newlines/quotes, independently reads it, updates/rereads it, adds it to Project 3 and verifies a Status update. Cleanup removes only that run's created item/Issue and independently verifies absence and preservation of existing items/fields. The retained scope Issue must survive. The fixture rejects a baseline over 100 items/fields rather than verifying a partial snapshot.

`-DiagnosticsOnly` runs the real-CLI UI diagnostics without mutation and is not adapter-write acceptance. The `LiveGitHub` category is separately gated. Unique `TestResults/live/` evidence includes structured results, resource IDs, timestamps, process timings and cleanup status, not payloads or credentials. An uncertain create is not retried. After failure/interruption, reconcile the run marker/IDs and clean up only that run before another scenario. Review live screenshots for account metadata before sharing.

## Read-only Project retrieval smoke

The separate opt-in `scripts/Test-CreationLive.ps1` runs the ordinary product executable: two approved run-owned rows, verified Project setup, a third response-loss case resolved through public URL binding, and reopen without replay. Project automation may provide membership before an explicit add is needed; evidence counts these separately. The proxy's private identity records constrain test mutations and are never supplied to the product. Independent readback checks all three completions, titles, empty bodies, membership and requested single-select values. Cleanup reconciles the unique marker against a complete baseline and verifies pre-existing data is unchanged. A previous run without verified cleanup blocks another run. Keep failed TRX and cleanup evidence under `TestResults/creation-live/` and `TestResults/e2e/`; this is automated live evidence, not human acceptance.

After reading [sandbox scope Issue #1](https://github.com/fukuda-yuki/codex-sandbox/issues/1), run:

```powershell
.\scripts\Test-ProjectRead.ps1
```

This separate opt-in test runs the production Core reader through the real guarded connection/CLI against existing user Project `fukuda-yuki/3`. It verifies the explicit repository/Project IDs, retrieves existing data and independently compares Issue title/state and single-select IDs/values. Its process wrapper rejects mutation documents and non-GET REST requests. It creates no data and does not invoke the mixed mutation suite. Missing representative data is a smoke limitation, not permission to create fixtures.

Unique `TestResults/project-read/` directories retain source/environment, command, TRX and counts/classifications without payloads or credentials. The script requires exactly one executed/passed case with no failures/skips. Deterministic `ProjectReaderTests` substitute only the external process response boundary and exercise real reader/connection/transport collaborators, including over-100 and nested pagination, partial/error/empty/type distinctions, duplicates/cursors, cancellation and identity isolation. Existing Core, connection/native-window and physical-key IME regression suites remain separate.

## Refresh reconciliation verification

`ReconciliationTests` exercises all known B/L/R branches, repeated conflict/resolution, explicit clear, pending text, scoped shared titles with older caches, structural/permission loss, stale choices and safe Undo. Real isolated stores exercise v1 migration/backups, partial candidate writes, stale revisions, competing writers, orphan/corrupt checkpoints and independent profile recovery. `RefreshWorkflowTests` uses the guarded reader/discovery/workspace with fake gh at the external process boundary for partial paging, edits during I/O, composition deferral and stale legacy writers.

The ordinary executable E2E suite includes three resolution choices with B/L/R readback and restart, independent title/select changes, partial results preserving the complete checkpoint, pending input during refresh, cancellation/close and interruption recovery. The prior refresh-prohibition journey is replaced by equivalent input-retention assertions under the authorized reconciliation contract. Preserve the connection, registration, editing, physical-key IME and input-check contracts under the coverage-migration policy above; their inventory is not a per-change execution checklist. Run selected desktop scenarios serially; never equate test discovery or Unicode text injection with physical IME evidence.

`scripts/Test-RefreshLive.ps1` opts into an isolated production registration/refresh/resolution scenario with a separate sandbox fixture service. It verifies exact sandbox identities before creation, records returned IDs before further writes, compares A/B/C, verifies GitHub remains C after local resolution, and removes only the disposable item/Issue with independent absence and existing-data checks. Product requests pass through a query-only process guard; fixture writes are explicitly separate. Unknown creation is not retried. The manual utility/workflow is in [refresh manual check](../docs/refresh-manual-check.md); do not create its fixture before the user starts the check.

## CI and reporting

The existing-field Apply tests exercise the actual planner, session, store, executor, guarded connection and reader with synthetic gh responses. They inspect mutation payloads and coherent recovered records, keeping query-only refresh/edit guards unchanged. The ordinary Apply desktop case reviews and applies a title, then reopens its history without dispatch. `scripts/Test-ApplyLive.ps1` runs the ordinary app with real stored authentication against only the designated sandbox, through title/set/clear and independent readback. Its fixture setup/cleanup counts are separate from product operations; interrupted manifests must be reconciled before another setup.

Public PR CI is credential-free. It builds the solution, executes deterministic logic/integration tests excluding live cases, and lists desktop tests without launching them. Do not execute untrusted public PR code on a privileged/credentialed interactive runner. Desktop execution requires a controlled local or dedicated Windows session. Current CI does not execute the views/controls in the desktop suite; discovery is not interaction evidence. Evaluate any UI integration CI path by the cases it actually executes, not by the presence or absence of a separately named host project.

Report changed behaviors and their test scopes, real/replaced dependencies and entry/result boundaries, separately from driver/process/environment details. State the reason for selected E2E/IME/live execution and relevant coverage that was outside scope or unavailable. For executed checks, report exact source/build, command, environment, executed/passed/failed/skipped counts and artifact locations. Preserve failed attempts. Build success, discovery, scoped UI integration, ordinary-product E2E, sandbox validation and human acceptance support different claims. GHEC + EMU, distribution, storage recovery and 100-item performance require their own evidence in #12/#13.

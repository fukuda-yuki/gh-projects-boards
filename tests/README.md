# Tests

Derive acceptance from the relevant Issue and [specification](../docs/spec.md). Use the least expensive test boundary that can detect the failure. Preserve contractual behavior when changing test mechanics.

## Test policy

Start with a short test list covering normal behavior, boundaries, failures and prohibited side effects. Use short Red-Green-Refactor cycles. Confirm a test's intended failure before implementing the behavior; unrelated build/environment failure is not a behavioral Red. Bug fixes start with a reproducer.

Prefer real in-process collaborators and observable results. A test may exercise several collaborating classes. Substitute external or nondeterministic boundaries when control is needed; do not mock the behavior under test or calculate expected values with the same production logic. Validate real adapters separately. Persistence tests must use the actual implementation and isolated storage.

Retain logic/adapter assertions during UI work. UI-specific selectors, focus and lifetime mechanics may change when justified by the specified user contract. Never remove, weaken, skip or relabel failures merely to obtain a pass. Document-only changes need no new behavior test; behavior-preserving refactoring uses the existing regression suite. Report unavailable execution honestly.

## Boundaries

| Level | Scope | Execution |
| --- | --- | --- |
| Logic/integration | Core rules, connection orchestration and real gh process handling with a synthetic executable | Deterministic CI; no UI or live GitHub |
| Desktop E2E | Ordinary WinUI 3 executable, real public UI Automation and isolated fake gh | Unlocked Windows desktop, serial execution |
| Physical-key IME | Real Japanese IME, composition/focus/value and confirmation boundaries | Controlled Windows desktop; separate evidence |
| Human IME acceptance | Natural typing, candidates, cancellation/reconversion and selection/editing usability | Explicit human confirmation |
| Live GitHub | Production adapter and real CLI against exact sandbox resources | Opt-in, authorized and independently read back |
| Performance | Defined workload, warmup, sample counts and environment | Separate raw measurements; #12 owns acceptance |

Core tests do not validate XAML, native focus, the visual tree or clipboard. E2E must run the product executable, not a probe or placeholder. It verifies the launched process loads `Microsoft.UI.Xaml.dll`; a successful substitute executable is not product evidence.

## Logic and integration

The editing/recovery suite exercises real scoped workspaces and isolated filesystem stores: exact title differences, whole-batch rejection, explicit clear, prior-state Undo, shared title versus independent selects, invalid pending buffers, interrupted replacement, corrupted schema/scope/history, competing writers, late changes during saving and old registration compatibility. Cache replacement has a final synchronous draft/generation predicate directly before atomic file replacement.

Additional ordinary-executable journeys cover scrolled editing, shared drafts across Projects, actual normal process restart with pending text, rectangular clipboard operations, save-failure cancellation of navigation/close, edit-during-refresh protection and explicit unregistration with surviving shared work. Run all deterministic desktop journeys with `scripts/Test-E2E.ps1`. Use `-Filter 'TestCategory=GridIme'` for the separate physical Japanese IME direct/F2, cancellation and reconversion cases in real registered-Project title cells. `scripts/Test-ReadyInput.ps1` remains the complete independent input-check regression suite. Filtered runs require nonzero executed and zero failed/skipped tests; the default desktop run additionally checks every original required journey by name. No filtered run replaces full regression or human grid acceptance.

`GhProjectsBoards.Tests` references Core, not the UI application. It supplies an isolated fake gh executable with synthetic scenarios and no network fallback. Test dependency versions are pinned in its project file.

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj --configuration Release --filter 'TestCategory!=LiveGitHub'
```

Preserve coverage of Unicode/quotes/stdin, invalid executables, child environment isolation, timeout/cancellation, safe errors, HTTP/GraphQL outcomes, authentication/storage guards, stable identity, host changes, scopes, resource permissions and orchestration. No new UI acceptance is inferred from these results.

## Desktop E2E

`GhProjectsBoards.E2E.Tests` uses NUnit and FlaUI UIA3 with build-only app/fake-gh references. Tests may not call product internals. Launch the ordinary exe, use stable AutomationIds on real controls, wait for observable conditions and verify both UI close and the original process's exit status. Do not assume a top-level native window has the same AutomationId as its XAML content or that every table exposes GridPattern.

```powershell
.\scripts\Test-E2E.ps1
.\scripts\Test-E2E.ps1 -Configuration Debug
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

The runner retains build/test logs, source state, resolved package assets, runtime versions, file hashes for the app executable/DLL, Core DLL, WinUI DLLs and both test assemblies. It checks the required journey names as well as counts, so replacing a connection journey with an unrelated passing case cannot satisfy the gate. Connection fixtures record owned process IDs, normal versus forced exit and remaining recorded children. Raw screen/log/clipboard evidence stays local until reviewed for publication.

Desktop tests are opt-in (`GHPB_RUN_E2E=1`); use the script as the supported entry point. It sets child-process paths and artifact variables, restores prior process environment, and rejects zero execution, incomplete/skipped outcomes and failed tests. A plain discovery or skipped run is not successful E2E.

## Registration verification

The deterministic registration cases exercise production discovery, workspace and reader collaborators with external process responses, plus the real JSON store in unique temporary directories. They cover paging/errors, duplicate routes, 101 items/two repositories/two Projects, scoped identity, late cancelled results, partial/failed/cancelled attempts, file replacement failure, writer contention, corrupt/versioned data, restart and local removal. The original reader and connection regression tests remain intact.

`Test-E2E.ps1` also runs the ordinary executable through the visible Project entry point using isolated fake gh and `GHPB_DATA_ROOT`. New journeys register via a linked repository and URL, detect duplicates, scroll to item 101, restart the process into actual saved content/settings, unregister locally, cancel and close during retrieval, and clear private discovery on a connection change. Existing eleven connection/native-window journeys and twelve separately run IME scenarios remain distinct. All deterministic desktop launches receive a dedicated data root; no invalid override falls back to the developer's cache.

Screenshots and JSON caches use synthetic data and are retained privately with TRX/process records. Review images before publication. New registration/discovery flows do not create or mutate live fixtures; the optional existing ProjectRead smoke is independent and cannot establish wider discovery/two-Project acceptance. Draft/Undo persistence, enterprise policies and distribution are unverified by this suite.

## Editable-grid contract

Derive table cases from #7 and the few-row input contract in #24. Address rows by stable identity rather than visible row index. Selection/range navigation must not change draft or committed values. Verify direct physical-key Japanese input and F2 independently, retaining initial input exactly once. Verify IME confirmation versus cell commit, candidates, cancellation, reconversion, keyboard navigation and public UI Automation.

Unicode insertion is not IME evidence. The application must not replay keys/text, insert F2 on behalf of direct input, or use private TSF/runtime hooks. A passing F2 case does not establish direct input. Human usability acceptance stays separate from injected physical-key automation. Do not mark unimplemented table tests or another executable's results as passing product coverage.

Independent shell and test-infrastructure work does not wait for a grid component to pass. Table acceptance does.

## Native input execution

```powershell
.\scripts\Test-ReadyInput.ps1
.\scripts\Test-ReadyInput.ps1 -Scenario reconvert-direct
```

The `ReadyInput` suite runs the ordinary `GhProjectsBoards.App.exe --input-check` with Microsoft Japanese IME on an unlocked Windows desktop. It builds the current solution and retains all twelve scenarios: selection, draft, reference, F2, mouse/keyboard direct input, both preenabled variants, direct/F2 cancellation and direct/F2 reconversion. The runner validates case names as well as executed/passed/skipped counts; a focused run must execute exactly its selected case. It is separate from the eleven deterministic connection/close/picker cases in `Test-E2E.ps1`.

Each case owns a fresh product process, verifies its loaded WinUI module, reads real editor/committed values and focus, and requires ordinary close with exit code zero. Selection/range checks inspect all six values. Exact commit-event counts supplement the observable assertions. The test-only foreground modifier never starts editing; direct input must use ordinary mouse/key selection without internal-editor UIA focus or F2 injection.

Unique `TestResults/ready-input/run-*` directories retain source/environment, build/test commands and logs, product/Core/WinUI/test DLL hashes, resolved package assets, TRX, scenario results, optional private screenshots and process-lifetime records. `GHPB_IME_TRACE` is a process-local, opt-in diagnostic file path used by this runner; it contains input values and must remain private until reviewed. Failed attempts remain separate. Existing prototype results are historical evidence and cannot substitute for execution of the integrated app.

For human confirmation, launch with `--input-check`. Use the standard TextBox as a positive control, then try direct typing after cell selection with IME enabled before and after selection. Compare F2; test conversion candidates and both Enter boundaries, cancellation/reconversion, arrows and Shift ranges. Editor text may change during composition, but the committed line must remain unchanged until cell commit. Check normal title-bar close while text focus remains. Record human observations separately in #24; automation and source review are not human acceptance. Full-grid/Tab/last-row/accessibility/performance acceptance remains outside this bounded check.

## Live sandbox

Read the [authorized scope and validation record](https://github.com/fukuda-yuki/codex-sandbox/issues/1) before running. Targets are exactly `fukuda-yuki/codex-sandbox` and user Project `fukuda-yuki/3`; verify API IDs before mutation. Use designated stored keyring authentication with the required scopes. Never extract tokens or alter authentication configuration in tests.

```powershell
.\scripts\Test-LiveGitHub.ps1
.\scripts\Test-LiveGitHub.ps1 -DiagnosticsOnly
.\scripts\Test-LiveGitHub.ps1 -GhPath 'C:\path\to\gh.exe'
```

The default run creates a disposable Issue with Unicode/newlines/quotes, independently reads it, updates/rereads it, adds it to Project 3 and verifies a Status update. Cleanup removes only that run's created item/Issue and independently verifies absence and preservation of existing items/fields. The retained scope Issue must survive. The fixture rejects a baseline over 100 items/fields rather than verifying a partial snapshot.

`-DiagnosticsOnly` runs the real-CLI UI diagnostics without mutation and is not adapter-write acceptance. The `LiveGitHub` category is separately gated. Unique `TestResults/live/` evidence includes structured results, resource IDs, timestamps, process timings and cleanup status, not payloads or credentials. An uncertain create is not retried. After failure/interruption, reconcile the run marker/IDs and clean up only that run before another scenario. Review live screenshots for account metadata before sharing.

## Read-only Project retrieval smoke

After reading [sandbox scope Issue #1](https://github.com/fukuda-yuki/codex-sandbox/issues/1), run:

```powershell
.\scripts\Test-ProjectRead.ps1
```

This separate opt-in test runs the production Core reader through the real guarded connection/CLI against existing user Project `fukuda-yuki/3`. It verifies the explicit repository/Project IDs, retrieves existing data and independently compares Issue title/state and single-select IDs/values. Its process wrapper rejects mutation documents and non-GET REST requests. It creates no data and does not invoke the mixed mutation suite. Missing representative data is a smoke limitation, not permission to create fixtures.

Unique `TestResults/project-read/` directories retain source/environment, command, TRX and counts/classifications without payloads or credentials. The script requires exactly one executed/passed case with no failures/skips. Deterministic `ProjectReaderTests` substitute only the external process response boundary and exercise real reader/connection/transport collaborators, including over-100 and nested pagination, partial/error/empty/type distinctions, duplicates/cursors, cancellation and identity isolation. Existing Core, connection/native-window and physical-key IME regression suites remain separate.

## Refresh reconciliation verification

`ReconciliationTests` exercises all known B/L/R branches, repeated conflict/resolution, explicit clear, pending text, scoped shared titles with older caches, structural/permission loss, stale choices and safe Undo. Real isolated stores exercise v1 migration/backups, partial candidate writes, stale revisions, competing writers, orphan/corrupt checkpoints and independent profile recovery. `RefreshWorkflowTests` uses the guarded reader/discovery/workspace with fake gh at the external process boundary for partial paging, edits during I/O, composition deferral and stale legacy writers.

The ordinary executable E2E suite includes three resolution choices with B/L/R readback and restart, independent title/select changes, partial results preserving the complete checkpoint, pending input during refresh, cancellation/close and interruption recovery. The prior refresh-prohibition journey is replaced by equivalent input-retention assertions under the authorized reconciliation contract. Keep all original connection, registration, editing, physical-key IME and input-check scenarios. Run desktop scenarios serially; never equate test discovery or Unicode text injection with physical IME evidence.

`scripts/Test-RefreshLive.ps1` opts into an isolated production registration/refresh/resolution scenario with a separate sandbox fixture service. It verifies exact sandbox identities before creation, records returned IDs before further writes, compares A/B/C, verifies GitHub remains C after local resolution, and removes only the disposable item/Issue with independent absence and existing-data checks. Product requests pass through a query-only process guard; fixture writes are explicitly separate. Unknown creation is not retried. The manual utility/workflow is in [refresh manual check](../docs/refresh-manual-check.md); do not create its fixture before the user starts the check.

## CI and reporting

Public PR CI is credential-free. It builds the solution, executes deterministic logic/integration tests excluding live cases, and lists desktop tests without launching them. Do not execute untrusted public PR code on a privileged/credentialed interactive runner. Desktop execution requires a controlled local or dedicated Windows session.

Report exact source/build, command, environment, executed/passed/failed/skipped counts and artifact locations. Preserve failed attempts. Build success, discovery, a narrow probe, sandbox success and human acceptance are distinct claims. GHEC + EMU, distribution, storage recovery and 100-item performance require their own evidence in #12/#13.

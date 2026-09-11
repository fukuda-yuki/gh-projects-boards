# Tests

Sources: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2), [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3), and [#12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12).

Derive cases from the relevant Issue's acceptance criteria. Keep local automated tests, ordinary Windows UI checks, and live GitHub tests distinct. Use only explicitly designated data for live mutation tests.

[Issue #12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12) owns cross-feature acceptance and performance measurements. An empty test suite or successful build does not establish feature acceptance.

## Test policy

### Workflow

Start with a short test list derived from the Issue's acceptance criteria.
Consider normal cases, boundaries, failures, and prohibited side effects.

Turn one item into a runnable test and confirm that it fails for the
intended reason. Unrelated build or environment failures are not evidence
that the test detects the missing behavior.

Make the smallest coherent production change, then refactor with the
relevant tests green. Extend the test list as new cases are discovered.
Run the affected suite before completion.

### Boundaries and assertions

A behavior test may exercise several collaborating classes.
Do not require one isolated test fixture per production class.

Use real application logic by default. Substitute GitHub access,
gh execution, time, or other difficult boundaries when the scenario
requires control or isolation.

When testing persistence, exercise the actual persistence implementation
against isolated test storage. A fake store does not prove durability
or recovery.

Assert observable results and contractual side effects.
Do not mock the behavior under test or compute expected values by
reusing the production logic being verified.

Validate real adapters separately. Passing against a test double does
not establish live GitHub or GHEC + EMU compatibility.

### Test levels

- Logic tests verify rules using real domain and application objects.
- Integration tests exercise connected components, including UI
  orchestration where relevant, without mocking every lower layer.
- UI E2E tests exercise the real Windows application through user actions.
  Controlled external boundaries are allowed; identify them in results.

Choose the lowest-cost level that can detect the relevant failure.
Do not duplicate every case at every level.
Live GitHub mutation tests remain separate and explicitly authorized.

### Exceptions

Documentation-only changes do not require new behavior tests.
Behavior-preserving refactoring normally uses existing tests.

Exploratory spikes and environments that cannot execute the required
tests must be reported explicitly. Record alternative checks and
unverified scope; do not claim an unobserved Red or Green result.

## Test boundaries

Use NUnit for all three test levels. Classify by the boundary exercised, not by the name of the class under test.

| Level | Scope | External dependencies |
| --- | --- | --- |
| Unit | Validation, field differences, three-way comparisons, paste interpretation, operation-state rules; isolated ViewModel behavior can also be unit-tested | No UI, disk, network, or real gh |
| Integration | ViewModel/commands plus real application logic; persistence and gh adapter integration belong here too | Use temporary real storage when relevant; fake the remote/process boundary, not every collaborating class |
| Desktop E2E | Launch the ordinary app executable and drive its actual WPF UI with FlaUI UIA3 | Deterministic synthetic data and isolated storage once those features exist; no live GitHub by default |

In WPF, the proposed controller-level tests normally target ViewModels and commands. Pure ViewModel tests do not validate XAML bindings, focus, keyboard input, or the visual tree; desktop E2E covers those interactions. WPF-specific in-process tests may need an STA thread and a Dispatcher, but most application logic should not depend on either.

Keep many cheap logic tests, fewer integration cases, and a small set of high-value E2E journeys. Add test projects with their first real behavior; do not create empty projects or placeholder passing tests. Verify argument/JSON handling, exit codes, timeouts, and cancellation at the gh boundary; add actual persistence and restart checks when storage is implemented.

## Unit and integration tests

`GhProjectsBoards.Tests` uses the same NUnit, adapter, and test SDK versions as the desktop project. It exercises real connection/diagnostic logic, substitutes remote responses through `IGhProcessRunner`, and launches its own test assembly as a fake gh executable for process-boundary cases. The fake accepts synthetic scenarios only and has no network fallback.

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj --configuration Release --filter 'TestCategory!=LiveGitHub'
```

Cases cover Unicode/quotes/stdin, missing and invalid executables, child environment isolation, timeout/cancellation, safe diagnostic output, HTTP and GraphQL failures, authentication states, stable identity, host changes, storage write guards, separate scopes/resource permissions, and ViewModel orchestration. Live cases are excluded from this command.

## Ordinary executable tests

`GhProjectsBoards.E2E.Tests` uses NUnit 4 and FlaUI UIA3. It launches the normal app, verifies connection journeys and window shutdown through UI Automation, and checks the original launch process's exit code. References are build-only; tests do not call application internals. Routine cases use a test-only fake gh selected through the ordinary path field, an isolated `GH_CONFIG_DIR`, and synthetic data. They cover diagnostics, rechecking, explicit account rebinding, command copying, missing CLI/login, cancellation, and closing while gh is active. No developer credentials or live API requests are needed.

The test thread uses per-monitor DPI awareness and restores its prior context afterward. This keeps [UI Automation physical coordinates](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-screenscaling) consistent with window screenshots at non-100% scaling. These tests do not establish Project registration, editing, persistence, synchronization, or GHEC + EMU acceptance.

### Run on Windows

Install the .NET 10 SDK, sign in to an interactive Windows desktop, and run from the repository root:

```powershell
.\scripts\Test-E2E.ps1
# Optional debug build:
.\scripts\Test-E2E.ps1 -Configuration Debug
```

The script builds the solution and runs the deterministic E2E category, excluding real IME cases. It needs no WinAppDriver/Appium server and does not change execution policy, screen-lock settings, or machine-wide environment variables. Keep the desktop unlocked; do not interact with it during the test. A disconnected or locked remote session can invalidate UI testing. Match the app/test elevation; administrator access is not required.

Reports are written to a unique `TestResults/e2e/<run-id>/` directory. Failures attempt to attach a PNG of the app window, not the whole desktop. A covered window can capture overlapping content: use a clean desktop and synthetic data, and review artifacts before sharing. No screenshot is guaranteed if the window never appears or has already exited. Cleanup targets only the process launched by the test; it does not make a failed close assertion pass.

A plain `dotnet test` skips desktop E2E unless `GHPB_RUN_E2E=1` is explicitly set. The script supplies this flag, `GHPB_E2E_APP_PATH`, `GHPB_E2E_FAKE_GH_PATH`, and `GHPB_E2E_ARTIFACTS` for its child processes and restores previous process environment values afterward. It requires a nonempty executed suite with no skipped cases. Skipped tests are not a successful E2E run. Visual Studio can discover the tests; running through the script is the supported entry point.

## Offline grid acceptance and measurements

[#19](https://github.com/fukuda-yuki/gh-projects-boards/issues/19) owns this prototype's results and rejection decision. Logic tests cover the five field rules, CRLF/LF TSV with trailing blanks, atomic rejection, explicit clearing, stable new-row identity, and operation Undo. Ordinary-executable grid journeys cover entry without gh, rectangle selection, keyboard paste/clear/Undo, errors and correction, choices, virtualized row 101, Esc without committed-row rollback, and text-editor Undo isolation.

Run real IME checks separately with **Microsoft Japanese IME selected in alphanumeric mode** on an unlocked desktop:

```powershell
.\scripts\Test-E2E.ps1 -RealIme
```

This sets `GHPB_RUN_REAL_IME` for the child run and restores the process environment afterward. Physical virtual-key input enables Japanese mode, types romanized syllables, converts and confirms, and cancels composition; Unicode insertion is not used as proof of these behaviors. The reconversion case seeds text, then selects the native IME's actual reconversion menu item and checks cell commit plus one Undo. The fixtures restore alphanumeric mode and clipboard content. The direct-start case currently **fails** because the first key is dropped; keep this reproducer failing until a demonstrated fix changes the observed result. An ordinary E2E or CI pass does not override that acceptance failure.

For an independent real-input review, open the normal exe and prototype, select a Title cell, enable Japanese mode, and type `n`, `i` slowly. Retain the observed `い` result and original value/Undo state. Then press Esc to cancel; use F2 to start editing and repeat `nihongo`, Space twice, candidate selection, first Enter (same editor, no history), and second Enter (one committed operation and downward move). Re-enter and cancel a different composition; select committed Japanese text, open its native reconversion menu with Shift+F10, choose a different candidate, commit, and Undo. Record operator, OS/IME/runtime/app version, scaling, screenshots, failures, and unverified checks in #19. An agent-operated check is not a human review.

```powershell
.\scripts\Measure-Grid.ps1
```

Measurements use the existing test projects and real application collaborators. Each series retains warmup sample 0, ten measured samples, median, and maximum in `TestResults/grid-measurements/<run-id>/`. `environment.json` records versions, CPU/memory, source state, and app hash; `ui-elapsed.json` also records window DPI. `application-processing.json` measures the ViewModel, validation, history, and notifications without WPF subscribers. It excludes rendering, clipboard, input, and UI Automation; model initialization is not display latency. `ui-elapsed.json` measures the ordinary executable through FlaUI and includes its input calls and condition waits. There is no invented performance pass threshold.

The UI series measures the prototype-button-to-ready transition for 100 rows, ten consecutive cell edits, a Ctrl+End/Ctrl+Home scroll round trip, and 10x5/100x5 paste and operation Undo. Clipboard setup and last-row content assertions occur outside paste timing; the payload changes all five cells per row. Each paste must create exactly one operation and Undo must restore the endpoint. These are local elapsed times, not click-to-photon measurements, human editing speed, large-dataset guarantees, or persistence/apply performance. Preserve failed runs separately; the script requires executed, non-skipped measurement cases. The in-process measurement fixture is explicit and excluded from routine correctness checks.

### Standard-control IME comparison

[#20](https://github.com/fukuda-yuki/gh-projects-boards/issues/20) owns the comparison and bounded repair experiments. From the ordinary prototype, **標準DataGrid比較** opens a default DataGridTextColumn with writable TwoWay Title bindings and a standalone TextBox. Neither comparison control uses the prototype's commit, validation, Undo, or input handlers.

```powershell
.\scripts\Compare-GridIme.ps1
.\scripts\Compare-GridIme.ps1 -TraceInput
```

Both commands require Microsoft Japanese IME selected in alphanumeric mode and an unlocked desktop. The explicit `ImeComparison` fixture uses fresh application processes for three samples of prototype direct/F2, standard direct/F2, and TextBox input. It asserts the actual physical-key result `にほんご`, preserving failures rather than treating a failed control as a pass. Two additional cases hold the initial key for 250 ms to distinguish hold duration from inter-key spacing. These diagnostics are separate from the existing `-RealIme` acceptance gate; the original failed acceptance test remains active.

Each unique `TestResults/ime-comparison/<run-id>/` retains TRX, per-key displayed/committed values, focus, DPI, screenshots, app hash, environment, and source state. `-TraceInput` sets `GHPB_GRID_INPUT_TRACE_DIRECTORY` only for that run. The app buffers routed input, focus, edit-boundary, committed-value, and history observations and writes JSON when the window closes; it does not handle input, force layout, or drain the Dispatcher to observe it. Diagnostics are off by default. The script restores process environment values, and the fixture restores the required alphanumeric mode and clipboard. Repeat without tracing to check for observation effects. A process crash may prevent buffered traces from being written; TRX and external screenshots remain separate evidence.

The current direct-start paths in both grids fail, including the held-key controls; F2 and TextBox succeed in the evaluated environment. The script therefore reports a nonzero result for this retained failure. No Unicode injection, F2 substitution for direct input, or weakening of expectations establishes acceptance. Record further findings and experiment patches in #20, not in product documents.

## Live sandbox validation

Read the [authorized scope and validation record](https://github.com/fukuda-yuki/codex-sandbox/issues/1) first. Live tests are confined to `fukuda-yuki/codex-sandbox` and user Project `fukuda-yuki/3`; identifiers are checked before mutations. Stored gh authentication must use the designated account and keyring, with `repo` and `project` scopes. The tests do not alter gh configuration or obtain a token.

```powershell
.\scripts\Test-LiveGitHub.ps1
# A different real gh executable:
.\scripts\Test-LiveGitHub.ps1 -GhPath 'C:\path\to\gh.exe'
# Recheck the ordinary UI without creating more test data:
.\scripts\Test-LiveGitHub.ps1 -DiagnosticsOnly
```

The default run executes a production-adapter scenario followed by a real-CLI ordinary UI check. It creates a disposable Issue with Japanese text, newlines, and quotes; independently reads the result; updates and rereads it; adds it to Project 3; updates an existing Status field; and independently checks Issue identity, Project identity, and the selected option. Cleanup deletes only the created item and Issue. A separate Issue GET must return 404 or 410, and a final Project snapshot must match the original item/field identities. The retained scope Issue and existing items/fields must survive. The bounded sandbox fixture rejects a baseline exceeding 100 items or fields rather than comparing a partial snapshot.

The separate `LiveGitHub` category is guarded by `GHPB_RUN_LIVE_GITHUB=1`. The script supplies the real CLI/app paths and a unique `TestResults/live/<run-id>/` directory, restores its process environment afterward, and requires executed TRX results with no skips. `-DiagnosticsOnly` does not establish adapter mutation acceptance.

`adapter-evidence.json` records stage outcomes, returned resource IDs, timestamps, subprocess count/duration, and cleanup status. It never records request bodies, credentials, or raw process streams. An uncertain create is not retried. If the run fails or is interrupted, inspect the run marker and returned IDs, reconcile remote state, and clean up only that run's data before starting another scenario. Preserve failed evidence; do not overwrite it with a later pass. UI screenshots use the ordinary window and real account metadata; review them before sharing.

## CI and later acceptance

PR CI builds the solution, executes unit/integration tests excluding `LiveGitHub`, and lists desktop tests without launching them. Discovery/build success is not UI or live execution evidence. Before enabling desktop E2E in CI, validate a dedicated interactive Windows session and publish failure artifacts. Do not execute untrusted public PR code on a developer PC or credentialed self-hosted runner.

For future UI tests, assign stable `AutomationProperties.AutomationId` values, prefer condition-based waits over fixed sleeps, keep desktop execution serial, and use screen/page objects as journeys grow. Address virtualized rows by stable item identity, not visible row index. Clipboard automation must restore prior content where practical. Text injection does not prove Japanese IME composition behavior; keep a real IME acceptance check alongside automated tests.

Keep routine E2E isolated from developer credentials and business Projects. When storage arrives, provide an isolated test workspace and verify recovery using the real implementation. Live gh/API readback is separate from deterministic CI. GHEC + EMU authentication, policy, and network behavior remain unverified until checked in that environment.

Derive acceptance cases from each owning Issue. In particular, editing/switching/restarting must not write to GitHub; only changed fields may be submitted; conflicts and unknown creation results must not trigger blind overwrites or duplicate creation. [#12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12) owns cross-feature acceptance and 100-item performance evidence.

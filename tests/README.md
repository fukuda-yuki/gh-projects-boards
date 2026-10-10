# Tests

This is the single test policy for the repository; [AGENTS.md](../AGENTS.md), the [PR template](../.github/pull_request_template.md) and [decisions](../docs/decisions.md#testing) link here instead of restating it. Derive expected behavior from the owning Issue and the [specification](../docs/spec.md), not from the implementation.

## Test policy

The policy follows the classical school of *Unit Testing: Principles, Practices, and Patterns* (Vladimir Khorikov), adopted by the owner in [#141](https://github.com/fukuda-yuki/gh-projects-boards/issues/141).

### What a test verifies

- A test verifies a unit of **behavior** that the user or a caller can observe, not a unit of code. One case may exercise several collaborating classes.
- A valuable test protects against regressions, survives behavior-preserving refactoring, gives fast feedback and stays easy to read. Resistance to refactoring is not traded away: a test that fails when only the implementation changed is a defect in the test. Fix or remove it; never mock the behavior under test to make it pass.
- Prefer output-based tests (inputs to a returned value), then state-based tests (the resulting state or persisted output). Use communication-based assertions only at an unmanaged dependency, below.

### Dependencies

| Dependency | Examples | In tests |
| --- | --- | --- |
| In-process collaborators | Scheduler, document, history, validation, view models, real views and controls | Use the real ones. |
| Managed out-of-process | The app's own local storage, settings and journal | Use the real implementation with isolated storage per test. |
| Unmanaged out-of-process | GitHub through gh, OS clipboard, native file picker, Microsoft IME | Substitute at the outermost edge: the fake gh executable, `IGhProcessRunner`, the clipboard transport, the picker result. |
| Nondeterministic inputs | Today's date, the 状況日 | Pass them in as values. |

What the system sends to an unmanaged dependency is observable behavior. Assert it there: which writes were sent, that a failed mutation is not resent, that a read-only flow sends no mutation. Never assert communication between the application's own classes or with managed dependencies: no call counts, call order, internal method invocations or private state.

Do not restate a caller's behavior in its collaborator's test. Test a collaborator directly only for its own contract that the caller's test cannot isolate: invariants, boundary values, ordering, rounding, normalization and error mapping.

### Where behavior is tested

**Priority: logic-layer unit tests > UI-layer integration tests > E2E tests.** This is where behavior is verified, not a ratio of test counts.

- Domain rules and calculations (scheduling, lateness, load, merge, validation, Undo) get thorough logic unit tests.
- Orchestration (connection, reading, publishing, workspace flow) gets integration tests for the main path and for the failures that unit tests cannot reach.
- Trivial code (plain create/read/update/delete, pass-through mapping) is covered once at the integration boundary that exercises the real storage or adapter, with no parallel unit case.
- Code that is both complex and full of dependencies is split into logic and a thin coordinator before it is tested.
- UI integration verifies the real view, event and binding wiring and the rendered state. A view-model-only check does not prove that wiring.
- E2E covers a few representative whole-application journeys. Never leave a rule or UI state covered only by E2E when a lower layer can verify it.

### Coverage

Coverage means the agreed behaviors that have a case. A line or branch percentage is only a negative indicator: a low number can reveal untested logic, a high number proves nothing. There is no percentage, case-count or suite-size target, and coverage is not published. `scripts/Test-Coverage.ps1` produces a local HTML report for finding untested branches in `GhProjectsBoards.Core`.

### Writing cases

- Name each case for the condition and the expected outcome, not for the method.
- Arrange, act, assert. Use table-driven cases for input/output or branch matrices instead of near-identical copies.
- Keep test code readable; split a fixture or fake when a reader can no longer follow it.
- Do not compute expected values with the production logic under test.
- Start each change with a short behavior list: normal behavior, boundaries, failures and prohibited side effects, each assigned to the lowest reliable boundary. The agent accountable for the change reviews the list for necessity, duplication and boundary. The owner receives a summary only when agreed behavior changes.
- Work in short Red-Green-Refactor cycles and observe each Red for the intended reason; a build or environment failure is not a behavioral Red. A bug fix starts with a reproducer. When only a native or live check reproduces it, also add a lower-layer regression wherever that layer can detect the defect.

## Boundaries

| Level | Scope | Execution |
| --- | --- | --- |
| Logic unit | UI-independent rules, validation, differences, Undo, planning and state transitions with real in-process collaborators | Main development loop and CI; no UI or live GitHub |
| Adapter/storage integration | Real connection orchestration, gh process handling with the fake gh executable, actual persistence with isolated storage | CI |
| UI integration | A bounded collaboration of real views/controls, events/commands, presentation state and rendered results | Hosted WinUI suite on an interactive desktop |
| Desktop E2E | Representative workflows through the ordinary executable to a declared endpoint | Selected for a stated risk; isolated fake gh unless stated otherwise |

Physical IME, live GitHub, performance and human acceptance are additional evidence requirements, not test levels. Classify a case by the collaboration it exercises, its fixture, the dependencies it replaces and what it asserts, not by its tooling (UI Automation, a separate process, a test host or the project name). E2E through fake gh must state that endpoint and does not establish real-GitHub behavior.

Evidence must match the claim. Core tests do not establish control interaction. Native focus, IME, clipboard/picker and process-lifetime claims need the real facility. Unicode or TextBox assignment is not IME evidence.

## Selecting runs

Run what the change puts at risk: focused logic tests while implementing, then the affected UI integration and adapter tests, then E2E, physical IME or live checks only when they establish a risk the lower layers cannot. Full regression is for broad or shared-boundary changes and explicit acceptance gates. A targeted run is valid evidence for its stated scope and is never reported as full regression. Document-only changes need a consistency and link review, not product execution.

## Where tests run

- **Linux (Claude Code cloud sessions):** Core logic only. The [session hook](../.claude/hooks/session-start.sh) installs the .NET 10 SDK and restores the Core test project for `net10.0`, because NUnit skips the whole `net10.0-windows` assembly on Linux. Build and test with the same override and `--no-restore`; an implicit restore reverts the assets:

  ```bash
  dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-restore -p:TargetFramework=net10.0 --filter 'TestCategory!=LiveGitHub&TestCategory!=HistorySequence&FullyQualifiedName!~ThousandTasks'
  ```

  Adapter tests that start the fake gh as a Windows executable or expect Windows paths and credential storage fail there; they are Windows evidence, not Linux defects.
- **Windows desktop:** WinUI 3 cannot render without a desktop session, so the hosted UI suite and E2E need an unlocked interactive desktop that nobody uses during the run. The host records activation changes (`[WINDOW]`) and stray physical input (`[INPUT]`); an `[INPUT]` line in a routine run marks interference. Physical IME, native input and visual review need the PMO's real desktop.

## Routine verification

Run from the checkout root on Windows. Restore once in a network-enabled environment; missing assets or NU1301 block dependent builds, and a zero-run outcome is not a pass.

```powershell
dotnet build GhProjectsBoards.sln -c Release --no-restore
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-build --no-restore --filter 'TestCategory!=LiveGitHub&TestCategory!=HistorySequence&FullyQualifiedName!~ThousandTasks' --logger 'trx;LogFileName=core.trx' --results-directory TestResults/core
./scripts/Test-UiIntegration.ps1 -NoBuild
./scripts/Test-E2E.ps1 -Configuration Release
```

The default UI selection excludes the infrastructure, native-input and performance categories; see the [host guide](GhProjectsBoards.UiIntegration.Tests/README.md). The default E2E selection runs the workspace, publish and people journeys through fake gh, without IME. A long checkout path can cause MSB3030 during app-local packaging; use a shorter checkout or a verified junction to it.

## Opt-in runs

Each needs a stated reason and separate evidence.

| Run | Command | Needs |
| --- | --- | --- |
| Native keys, pointer and clipboard in the plan sheet | `./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanSheetNative'` | PMO desktop |
| Physical Microsoft Japanese IME: direct and F2 entry, confirmation versus cell commit, cancellation, reconversion | `./scripts/Test-E2E.ps1 -Configuration Release -Filter 'TestCategory=GridIme'` | PMO desktop with Microsoft IME |
| Full 200-step history sequences (routine runs use 20 seeds × 30 operations) | `dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-build --no-restore --filter 'TestCategory=HistorySequence'` | Time |
| Plan sheet frame timing and Core/storage timing | See [performance measurement](../docs/performance.md) | Idle machine |
| Live GitHub connection and Project read | `./scripts/Test-LiveGitHub.ps1`, `./scripts/Test-ProjectRead.ps1` | Sandbox authorization |
| Live publisher proof | Below | Sandbox authorization |

### Live GitHub

Live checks touch only the sandbox named in [AGENTS.md](../AGENTS.md#authorized-github-sandbox), and the current task must authorize them. The publisher proof uses the product publisher, exact sandbox identities and independent readback. It creates up to 250 owned Issues, measures three refreshes, three 300-cell updates and three 50-Issue creations, then deletes only its owned resources and verifies the original baseline. Budget and stop rules are printed before any mutation.

```powershell
# ArtifactsRoot must be absolute and, for Run, must not exist yet.
$evidence = Join-Path (Get-Location) 'TestResults/publisher-live-new'
./scripts/Test-PlanPublisherLive.ps1 -Mode Run -ArtifactsRoot $evidence
# After an interruption, clean up with the same evidence root:
./scripts/Test-PlanPublisherLive.ps1 -Mode Cleanup -ArtifactsRoot $evidence -NoBuild
```

Require nine passing samples and `CleanupComplete` in `plan-publish-live.json`. Keep `failure.txt`, schema responses, process timings, settling observations and cleanup retries. Core timings do not establish ordinary-screen latency, and an extrapolated 1,000-item refresh is not a measured one.

## Execution evidence

Record the source, environment, exact command, executed/passed/failed/skipped counts and artifacts. Keep failed attempts and distinguish exclusions from skips; zero-run, discovery-only and skip-only outcomes are not passes. The test scripts record source diffs, binary hashes and outcomes; E2E records the ordinary executable and the substituted endpoint. Inspect captured images before claiming a visual review.

## Test output retention

`TestResults/` is disposable local output. [Clear-TestResults.ps1](../scripts/Clear-TestResults.ps1) deletes every entry in it that has not been written for 14 days; inside the per-run folders of the test scripts (`coverage`, `e2e`, `evaluation`, `internal-distribution`, `live`, `project-read`, `ui-integration`) it judges each run separately. An entry counts as written when anything inside it changed, so a workspace or evaluation root in use is kept. The test scripts and the Claude Code session hook run it automatically; set `GHPB_TESTRESULTS_DAYS` to change the period. Nothing outside `TestResults/` is touched. Keep evidence that must outlive the period in the Issue or PR, or copy it outside `TestResults/`.

[Clear-TestResults.Tests.ps1](../scripts/Clear-TestResults.Tests.ps1) covers the rule with temporary folders and runs in CI. Locally it needs Pester 5: `Invoke-Pester ./scripts/Clear-TestResults.Tests.ps1`.

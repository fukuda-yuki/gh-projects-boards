# Tests

Sources: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2) and [#12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12).

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

Keep many cheap logic tests, fewer integration cases, and a small set of high-value E2E journeys. Add unit and integration projects with their first real behavior; do not create empty projects or placeholder passing tests. When the gh/storage implementations arrive, test argument and JSON handling, exit codes, timeouts, cancellation, persistence, and restart recovery at their own boundaries.

## Current executable smoke test

`GhProjectsBoards.E2E.Tests` uses NUnit 4 and FlaUI UIA3. It launches the normal app, checks its main-window identity/title/visibility, closes it through UI Automation, and verifies normal process exit. The application reference is build-only; the test does not call application internals. This is shell coverage, not acceptance of Project registration, grid editing, persistence, synchronization, or GHEC + EMU.

### Run on Windows

Install the .NET 10 SDK, sign in to an interactive Windows desktop, and run from the repository root:

```powershell
.\scripts\Test-E2E.ps1
# Optional debug build:
.\scripts\Test-E2E.ps1 -Configuration Debug
```

The script builds the solution and runs only the E2E category. It needs no WinAppDriver/Appium server and does not change execution policy, screen-lock settings, or machine-wide environment variables. Keep the desktop unlocked; do not interact with it during the test. A disconnected or locked remote session can invalidate UI testing. Match the app/test elevation; administrator access is not required by this skeleton.

Reports are written to a unique `TestResults/e2e/<run-id>/` directory. Failures attempt to attach a PNG of the app window, not the whole desktop. A covered window can capture overlapping content: use a clean desktop and synthetic data, and review artifacts before sharing. No screenshot is guaranteed if the window never appears or has already exited. Cleanup targets only the process launched by the test; it does not make a failed close assertion pass.

A plain `dotnet test` skips desktop E2E unless `GHPB_RUN_E2E=1` is explicitly set. The script supplies this flag, `GHPB_E2E_APP_PATH`, and `GHPB_E2E_ARTIFACTS` for its child processes and restores previous process environment values afterward. Skipped tests are not a successful E2E run. Visual Studio can discover the test; running through the script is the simplest supported entry point.

## CI and later acceptance

PR CI builds both projects and lists discovered tests. It deliberately does not launch the desktop UI yet. Discovery/build success is not UI execution evidence. Add unit/integration execution as those suites are implemented. Before enabling desktop E2E in CI, validate a dedicated interactive Windows session and publish failure artifacts. Do not execute untrusted public PR code on a developer PC or credentialed self-hosted runner.

For future UI tests, assign stable `AutomationProperties.AutomationId` values, prefer condition-based waits over fixed sleeps, keep desktop execution serial, and use screen/page objects as journeys grow. Address virtualized rows by stable item identity, not visible row index. Clipboard automation must restore prior content where practical. Text injection does not prove Japanese IME composition behavior; keep a real IME acceptance check alongside automated tests.

Before the app gains storage or GitHub access, provide an isolated test workspace and fake external boundary for routine E2E. Do not use developer credentials or real business Projects. Validate real gh/API behavior separately against explicitly designated test data, including read-back after mutations. Treat this as live-system verification, not a replacement for deterministic CI. GHEC + EMU authentication, policy, and network behavior remain unverified until checked in that environment.

Derive acceptance cases from each owning Issue. In particular, editing/switching/restarting must not write to GitHub; only changed fields may be submitted; conflicts and unknown creation results must not trigger blind overwrites or duplicate creation. [#12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12) owns cross-feature acceptance and 100-item performance evidence.

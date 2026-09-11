# gh-projects-boards

A Windows desktop application for preparing GitHub Issue and Project changes in a table. Requirements and acceptance criteria belong to [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and its linked Issues.

The current executable provides **GitHub CLI connection and permission diagnostics** and an **offline editing-grid prototype**. Project registration, production table editing, draft persistence, and manual apply are future features in their owning Issues.

## Build and run

Use Windows and the .NET 10 SDK. Run from the repository root:

```powershell
dotnet build GhProjectsBoards.sln --configuration Release
.\src\GhProjectsBoards.App\bin\Release\net10.0-windows\GhProjectsBoards.App.exe
```

The ordinary executable opens the Japanese connection screen. Launching it requires no GitHub login and makes no network request. Distribution and runtime packaging remain in [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

## Check a connection

1. Install [GitHub CLI](https://cli.github.com/). The app looks for `gh.exe` on PATH and under Program Files; browse or enter another executable if necessary. The current metadata contract has been verified with gh 2.100.0.
2. Enter the GitHub hostname, such as `github.com`. Optionally enter an Issue URL and a user/organization Project URL on that host.
3. Select **接続を確認 / 再確認**. Read the CLI version, account and stable ID, credential storage, scopes, and separate Issue/Project results. `不明` means the value has not been established; a successful login alone does not establish write access.
4. If login or additional scopes are needed, expand **ログイン・権限追加の手順**, copy the appropriate PowerShell command, and run it in your terminal. Complete browser authentication there, then recheck in the app. The app does not initiate login or change gh configuration itself.
5. After an intentional account, host, or executable change, review the destination and select **新しい接続として確認**. Rechecking an old connection never silently adopts another identity.

The screen only diagnoses access; it does not edit Issues or Projects. Inputs and connection state are kept in memory and reset when the app exits. Cancel stops the current check; closing the window cancels outstanding work before shutdown.

## Try the editing-grid prototype

From the connection screen, select **編集グリッド試作**. No connection check is required. The separate window starts with 100 synthetic rows; closing it discards every edit. It neither runs gh nor fetches, saves, or applies GitHub data. [#19](https://github.com/fukuda-yuki/gh-projects-boards/issues/19) owns the spike and its adoption decision.

1. Select a cell and press **F2** to edit. Title is required single-line text; Issue state is Open/Closed; Number accepts a decimal with `.`; Date accepts `yyyy-MM-dd`; Choice is empty, High, Medium, or Low.
2. Use **Tab / Shift+Tab**, **Enter**, and arrow keys to navigate. Enter during Japanese conversion confirms the IME without moving; the next Enter commits the cell and moves down. Esc cancels uncommitted input.
3. Select a rectangle with Shift and arrow keys, then use **Ctrl+V** or **貼り付け** with rectangular TSV. Empty pasted cells mean no change. An invalid value, inconsistent row width, or destination overflow rejects the entire paste and displays its location and reason.
4. Use **値をクリア** or **Delete** outside editing to clear optional cells. Including a required field rejects the whole clear. Delete inside an editor deletes text.
5. Use **元に戻す** or **Ctrl+Z** outside editing to reverse one committed cell edit, paste, clear, or row addition. Ctrl+Z inside the text editor belongs to its uncommitted text buffer. **新規行追加** adds row 101 and focuses its required Title.

Select **標準DataGrid比較** inside the prototype to open a standard DataGrid with 100 writable, TwoWay-bound Title rows and a standalone TextBox. The comparison grid uses default text-column editors and has no prototype validation, operation Undo, or custom input handlers. It also discards its synthetic data on close.

**Adoption status: not adopted; IME investigation remains open.** On the evaluated Microsoft Japanese IME environment, typing directly into a selected cell loses the first key (`n`, `i` becomes `い`, not `に`). [#20](https://github.com/fukuda-yuki/gh-projects-boards/issues/20) also reproduces this in the standard comparison grid; F2 composition and the standalone TextBox work. Bounded lifecycle changes have not established a repair and are not included. WPF DataGrid remains a candidate, while the #19 prototype rejection and failing reproducer remain valid. See the Issues for evidence and unresolved causes. This does not complete #2, #7, or #12.

## Credentials and failures

The app uses gh's stored authentication. It excludes `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN`, and `GITHUB_ENTERPRISE_TOKEN` from its gh children and reports only which variable names are present. It does not modify the parent environment or extract, display, duplicate, or persist a token.

Windows credential-store (`keyring`) authentication permits guarded adapter writes. Plaintext `hosts.yml` storage is displayed with its path and blocks writes; unknown storage also blocks writes. Restore access to the Windows credential store and log in again without `--insecure-storage`, then recheck. Login/refresh commands copied into your own terminal follow that terminal's environment; remove any token-variable overrides there if you intend to update stored gh authentication.

Each gh process has a default 30-second timeout. Authentication, target access, rate limits, network errors, cancellation, and unknown write results are distinct. A dispatched write with an uncertain result is never automatically resent. Diagnostics omit request bodies and raw process streams.

See [connection behavior](docs/spec.md), [implementation boundaries](docs/architecture.md), and [technical decisions](docs/decisions.md). Company GHEC + EMU authentication, policies, and network conditions require separate validation.

## Test

```powershell
# Deterministic unit/integration tests; no live GitHub:
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj --configuration Release --filter 'TestCategory!=LiveGitHub'

# Ordinary executable, controlled gh boundary; unlocked desktop required:
.\scripts\Test-E2E.ps1

# Actual Microsoft Japanese IME; currently reproduces the adoption-blocking failure:
.\scripts\Test-E2E.ps1 -RealIme

# Standard DataGrid / prototype / TextBox comparison; retains failing controls:
.\scripts\Compare-GridIme.ps1
# Same input with buffered event observations:
.\scripts\Compare-GridIme.ps1 -TraceInput

# Warmup plus ten samples, with application and UI elapsed times separated:
.\scripts\Measure-Grid.ps1

# Real adapter writes and ordinary-executable diagnostics in the authorized sandbox:
.\scripts\Test-LiveGitHub.ps1
```

The live script targets only [fukuda-yuki/codex-sandbox](https://github.com/fukuda-yuki/codex-sandbox) and [user Project 3](https://github.com/users/fukuda-yuki/projects/3), creates disposable data, independently reads back each change, and deletes that data. Read the [sandbox scope record](https://github.com/fukuda-yuki/codex-sandbox/issues/1) before running. Failures retain evidence under `TestResults/live/`; inspect cleanup and uncertain results before another run.

CI executes unit/integration tests and only discovers desktop tests. CI success is not desktop or live-system execution evidence. See the [test policy and instructions](tests/README.md).

## Structure and sources

- `src/GhProjectsBoards.App/`: WPF application, connection orchestration, internal gh adapter.
- `tests/`: NUnit unit/integration tests, test-only fake gh executable, and FlaUI desktop tests.
- `scripts/`: separate deterministic desktop and live sandbox entry points.
- [Requirements](docs/requirements.md): product outline and Issue map.
- [Specification](docs/spec.md): agreed behavior and unresolved contracts.
- [Architecture](docs/architecture.md): implemented and future responsibilities.
- [Decisions](docs/decisions.md): resolved choices and their limits.
- [AGENTS.md](AGENTS.md): repository development rules.

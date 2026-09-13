# gh-projects-boards

A native Windows desktop application built with **C#, .NET 10 and WinUI 3 / Windows App SDK** for preparing GitHub Issue and Project changes in a table.

GitHub remains the source of truth. Editing is local; only an explicit apply operation publishes changes. Product requirements and acceptance belong to [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and its linked Issues.

## Build and run

Use Windows x64, the .NET 10 SDK and Windows SDK 10.0.26100.0. Run from the repository root:

```powershell
dotnet build GhProjectsBoards.sln --configuration Release
.\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe
```

The development executable is unpackaged with app-local .NET and Windows App SDK runtimes. Launching the connection screen requires no GitHub login and performs no network request. End-user packaging, signing, notice manifests and clean-machine acceptance are owned by [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13); a development build is not a distributable release.

The [dependency terms and source inventory](docs/dependencies.md) distinguishes Windows development use from binary redistribution. DWrite/Widgets redistribution applicability remains an unresolved #13 distribution blocker.

## Check a connection

Install [GitHub CLI](https://cli.github.com/). Use automatic detection, browse, or enter the path to `gh.exe`. Enter the GitHub hostname and optionally an Issue URL and a user/organization Project URL on that host.

Select **接続を確認 / 再確認** to inspect the CLI version, account and stable ID, credential storage, scopes, and separate Issue/Project permissions. `不明` means unverified; successful login does not by itself establish write access.

The **ログイン・権限追加の手順** section supplies PowerShell commands to copy and run in your own terminal. Complete browser authentication there, then recheck. The app does not initiate login or alter gh configuration.

After an intentional account, host or executable change, review the destination and select **新しい接続として確認**. Rechecking never silently adopts another identity. Cancel stops the check; closing the window waits for the owned gh operation to stop. Connection inputs and state are in memory.

## Credentials and failures

Child gh processes use stored authentication. The app removes `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN` and `GITHUB_ENTERPRISE_TOKEN` from those children without changing the parent environment. It reports variable names, never their values, and never extracts, displays or stores a token.

Recognized Windows credential-store (`keyring`) authentication permits guarded adapter writes. Plaintext or unknown credential storage blocks writes but remains diagnosable. Commands copied into your terminal use that terminal's environment; remove token overrides there when updating stored authentication.

Each gh process has a default 30-second timeout. Authentication failures, resource permissions, rate limits, network errors, cancellation and uncertain write outcomes are distinct. An uncertain write is never automatically resent. Diagnostics omit request bodies and raw process streams.

## Check native table input

The ordinary app includes a bounded, three-row/two-column input check using synthetic values:

```powershell
.\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe --input-check
```

Click a cell once and type Japanese directly, or use F2. The line below each editor shows the committed value: IME confirmation must leave it unchanged, and the following Enter commits the cell and moves down once. Arrow and Shift navigation select cells/ranges without changing values. The standard TextBox is a reference control. Data is discarded on exit; this screen does not connect to GitHub.

This keeps the native input method available for verification while full table editing, paste, Undo and 100-row behavior are implemented under [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7). Launch without the option for the connection screen. See [tests](tests/README.md) for automated and human input checks.

## Test

```powershell
# Core logic and adapter integration; no live GitHub:
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj --configuration Release --filter 'TestCategory!=LiveGitHub'

# Ordinary WinUI executable with isolated fake gh; unlocked desktop required:
.\scripts\Test-E2E.ps1

# Physical-key Japanese IME in the ordinary app's input-check window:
.\scripts\Test-ReadyInput.ps1

# Authorized real-GitHub validation; read the sandbox scope first:
.\scripts\Test-LiveGitHub.ps1
```

Read the [test policy](tests/README.md) before running. Live tests target only [the designated sandbox repository](https://github.com/fukuda-yuki/codex-sandbox) and [user Project 3](https://github.com/users/fukuda-yuki/projects/3). They create and clean up disposable data. Inspect retained failures and uncertain outcomes before another live run.

CI runs deterministic tests and discovers desktop tests without launching UI. It does not establish desktop, IME, live-system or company GHEC + EMU acceptance. Current execution evidence and incomplete feature scope belong to the owning Issues, not this README.

## Structure

- `src/GhProjectsBoards.Core/`: UI-independent connection orchestration and guarded GitHub CLI/API logic.
- `src/GhProjectsBoards.App/`: WinUI 3 presentation, window lifetime, native dialogs and clipboard.
- `tests/`: NUnit logic/integration tests, isolated fake gh, and FlaUI UIA3 desktop journeys.
- `scripts/`: deterministic desktop and live sandbox execution entry points.

[Requirements](docs/requirements.md) · [Specification](docs/spec.md) · [Architecture](docs/architecture.md) · [Decisions](docs/decisions.md) · [AGENTS.md](AGENTS.md)

# gh-projects-boards

A Windows planning editor for GitHub Projects, built with C#, .NET 10 and WinUI 3. [Requirements](docs/requirements.md), [specification](docs/spec.md) and [decisions](docs/decisions.md) define the product under [Epic #88](https://github.com/fukuda-yuki/gh-projects-boards/issues/88).

The ordinary app opens the Project workspace and settings page. It shows refreshed tasks with automatically mapped columns. The Project surface is an editable plan sheet with a row-aligned native Gantt. **担当者** compares daily, weekly and monthly load, whole-Project allowances and forecasts; expanding a person allows task corrections with the same Undo history. The **発行** review shows field changes and conflict choices before explicit confirmation; publication reports stages and retains failures for retry.

## Build and run

Use Windows x64, PowerShell 7, .NET 10 SDK and Windows SDK 10.0.26100.0. Restore dependencies once in a network-enabled development environment. Run these commands from the selected checkout:

~~~powershell
dotnet build ./GhProjectsBoards.sln -c Release --no-restore
$env:GHPB_DATA_ROOT = Join-Path (Get-Location) 'TestResults/workspace-evaluation'
& ./src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe
~~~

GHPB_DATA_ROOT must be absolute. Omit it to use the normal per-user root. The app writes under PlanningEditor/v1. A long checkout can cause MSB3030 during app-local packaging; if a short junction is needed, verify that it points to the selected checkout before using it. The development executable includes app-local .NET and Windows App SDK runtimes.

## Internal distribution

Run `./scripts/Publish-Internal.ps1 -ArtifactsRoot C:\w\ghpb-distribution` to build a self-contained Windows x64 ZIP in a new isolated output directory. The command audits the exact dependencies, notices and payload hashes; see [dependency terms](docs/dependencies.md). The ZIP includes the [Japanese distribution guide](docs/internal-distribution-ja.md). Recipients need GitHub CLI and Project access; they do not need the checkout or development SDKs. Package creation and ordinary-app smoke checks remain separate from clean-PC and human acceptance under [#92](https://github.com/fukuda-yuki/gh-projects-boards/issues/92).

## Offline evaluation

The evaluation launcher defaults to Release. Use `-Configuration Debug` to build or run Debug app and fixture binaries together; keep the same configuration when resuming.

```powershell
./scripts/Start-Evaluation.ps1 -NoBuild
# Continue the same evaluation; use the data root printed above:
./scripts/Start-Evaluation.ps1 -NoBuild -Resume -DataRoot '<absolute printed path>'
```

Omit `-NoBuild` to build the selected configuration with `--no-restore` first. The launcher prints its isolated root, prefills the fake gh path and strips token environment overrides from the child. Select **接続**, then **第2027.04版**. No network or real account is used. The ordinary app starts at **未発行 0 タスク**, with 40 requirements, 1,000 executable tasks and 20 people. The fixed 状況日 is 2026-10-05. One daily overload is hidden by the weekly average; another person exceeds their total allowance. The two-wave schedule keeps active weeks near capacity; `evaluation.json` reports every week, including the low-demand tail. Editing and publishing affect only the local fake endpoint. `-PrepareOnly` prepares the files without opening a window. A fresh run refuses an occupied root; `-Resume` preserves edits.

The shared WBS and reproducible sandbox commands are described in [version evaluation](evaluation/README.md). Live evaluation uses a **different**, fresh data root and real gh against Project 3. Reuse the existing R01–R39 Issues, complete the initial planning pass in the sheet, then simulate four weekly updates, adding R40 in week three. Live assignment evidence uses actual sandbox users; 20-person workload evaluation uses the offline fixture.

## Open a Project

Enter the gh executable and hostname, then select **接続**. The app uses gh's stored authentication and lists personal and organization Projects. Select one to add and open it; **URLで開く** is an alternative. Registered Projects appear on the left. **最新の情報に更新** reads current GitHub values; opening an existing local Project preserves its unpublished work.

For the authorized sandbox, connect to github.com with C:\Program Files\GitHub CLI\gh.exe, then select user Project 3. Its fallback URL is https://github.com/users/fukuda-yuki/projects/3. Use an isolated data root for evaluation. The explicit **不足する日程列を追加** button is the only remote schema operation on this settings page; connecting and ordinary settings changes do not write to GitHub.

**CSVから追加** imports new tasks with predecessors and parents as one local Undo operation. Use the Excel-friendly [template](templates/new-tasks.csv), also shipped in `templates/` beside the app. UTF-8 and Shift-JIS are accepted; errors reject the whole file. **発行** creates the Issues later. The [sandbox evaluation file](docs/evaluation/sandbox-plan.csv) contains 24 tasks for Project 3.

**設定** contains column mappings, calendar and holidays, rates and days off, optional Project start, default repository and settings export/import. Accepted changes autosave and recalculate; **元に戻す** reverses one operation. The [Japanese user manual](docs/user-manual.md) describes these flows.

## Validation

~~~powershell
dotnet test ./tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~PlanWorkspaceTests|FullyQualifiedName~PlanLivePreflightTests'
dotnet build ./tests/GhProjectsBoards.UiIntegration.Tests/GhProjectsBoards.UiIntegration.Tests.csproj -c Release --no-restore
./scripts/Test-UiIntegration.ps1 -NoBuild
./scripts/Test-E2E.ps1 -Configuration Release -Filter 'FullyQualifiedName~PlanningWorkspaceJourneyTests|FullyQualifiedName~PlanningPublishJourneyTests'
~~~

The hosted UI tests use real controls, Core and storage, with fake gh at the remote boundary and substituted clipboard or file picking in the relevant cases. The ordinary-executable journey uses an isolated fake endpoint and reopens the selected Project after restart and verifies publish recovery without duplicate Issues or resending verified writes. Neither establishes real GitHub, GHEC + EMU, physical IME or human PMO acceptance. See [test policy](tests/README.md) for boundaries.

## Structure

- src/GhProjectsBoards.Core: UI-independent connection, Project reading, scheduling, local documents and publication.
- src/GhProjectsBoards.App: the ordinary WinUI shell, settings and rendering.
- tests: logic, adapter/storage, hosted UI and ordinary-executable checks.
- docs: current contracts, design rationale and the user manual.

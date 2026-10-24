# gh-projects-boards

A Windows planning editor for GitHub Projects, built with C#, .NET 10 and WinUI 3. [Requirements](docs/requirements.md), [specification](docs/spec.md) and [decisions](docs/decisions.md) define the product under [Epic #88](https://github.com/fukuda-yuki/gh-projects-boards/issues/88).

The ordinary app opens the Project workspace and settings page. It shows refreshed tasks with automatically mapped columns. The Project surface is an editable plan sheet with a row-aligned native Gantt. **サマリー** compares phase and person × phase status against the Project end, with remaining capacity, forecast and monthly overload drill-down. Planning edits stay in 計画. The **発行** review shows field changes and conflict choices before explicit confirmation; publication reports stages and retains failures for retry.

GitHub milestone targets are read with the Project and shown on the Gantt and summary. Settings can associate each 工程 with a milestone; target dates remain managed on GitHub; task membership is edited through the configurable sheet column and published explicitly. Neither changes task scheduling.

## Build and run

Use Windows x64, PowerShell 7, .NET 10 SDK and Windows SDK 10.0.26100.0. Restore dependencies once in a network-enabled development environment. Run these commands from the selected checkout:

~~~powershell
dotnet build ./GhProjectsBoards.sln -c Release --no-restore
$env:GHPB_DATA_ROOT = Join-Path (Get-Location) 'TestResults/workspace-evaluation'
& ./src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe
~~~

GHPB_DATA_ROOT must be absolute. Omit it to use the normal per-user root. Data under `TestResults/` is deleted after 14 days without changes; see [test output retention](tests/README.md#test-output-retention). The app writes under PlanningEditor/v1. A long checkout can cause MSB3030 during app-local packaging; if a short junction is needed, verify that it points to the selected checkout before using it. The development executable includes app-local .NET and Windows App SDK runtimes.

## Internal distribution

Run `./scripts/Publish-Internal.ps1 -ArtifactsRoot C:\w\ghpb-distribution` to build a self-contained Windows x64 ZIP in a new isolated output directory. The command audits the exact dependencies, notices and payload hashes; see [dependency terms](docs/dependencies.md). The ZIP includes the [Japanese distribution guide](docs/internal-distribution-ja.md) as `使い方.md` and the [user manual](docs/user-manual.md) as `マニュアル.md`. The title-bar **ヘルプ** button or F1 opens the manual; development builds and the evaluation launcher carry it too. Recipients need GitHub CLI and Project access; they do not need the checkout or development SDKs. Package creation and ordinary-app smoke checks remain separate from clean-PC and human acceptance under [#92](https://github.com/fukuda-yuki/gh-projects-boards/issues/92).

For repeatable distribution, use the [release workflow](docs/releases.md): push a version tag on a commit already checked by main CI to produce a **draft** GitHub Release. It builds and tests the extracted self-contained ZIP before attaching it and its SHA-256 checksum. Publishing the draft is a separate maintainer action.

## Offline evaluation

The evaluation launcher defaults to Release. Use `-Configuration Debug` to build or run Debug app and fixture binaries together; keep the same configuration when resuming.

```powershell
./scripts/Start-Evaluation.ps1
# The twelve-month version, or another status date of the same version:
./scripts/Start-Evaluation.ps1 -NoBuild -Version 2027.10
./scripts/Start-Evaluation.ps1 -NoBuild -StatusDate 2027-02-17
# Continue the same evaluation; use the data root printed above:
./scripts/Start-Evaluation.ps1 -NoBuild -Resume -DataRoot '<absolute printed path>'
```

`-NoBuild` skips the build of the selected configuration (`--no-restore`). The launcher prints its isolated root, prefills the fake gh path and strips token environment overrides from the child. Select **接続**, then the version. No network or real account is used. The ordinary app starts at **未発行 0 タスク** in the middle of a waterfall version: 40 requirements, 1,000 tasks, 20 people, with progress replayed up to the 基準日 (default 2027-01-13 for 第2027.04版 and 2027-03-24 for 第2027.10版). Detailed design (PS) runs late after re-estimates, one person is overloaded and one open task has 残 0. `evaluation.json` records the summary measures for three status dates. Open **サマリー** for the phase totals and person × phase/month load. Select a cell to inspect its tasks; **この N タスクで計画を絞り込む** opens that exact set in the plan. Attention counts apply the filter directly. Editing and publishing affect only the local fake endpoint. `-PrepareOnly` prepares the files without opening a window. A fresh run refuses an occupied root; `-Resume` preserves edits.

The data and the sandbox commands are described in [version evaluation](evaluation/README.md). Live evaluation uses a **different**, fresh data root and real gh against Project 3. Live assignment evidence uses actual sandbox users; 20-person workload evaluation uses the offline fixture.

## Open a Project

Enter the gh executable and hostname, then select **接続**. The app uses gh's stored authentication and lists personal and organization Projects. Select one to add and open it; **URLで開く** is an alternative. Registered Projects appear in the title-bar Project picker. **最新の情報に更新** reads current GitHub values; opening an existing local Project preserves its unpublished work.

For the authorized sandbox, connect to github.com with C:\Program Files\GitHub CLI\gh.exe, then select user Project 3. Its fallback URL is https://github.com/users/fukuda-yuki/projects/3. Use an isolated data root for evaluation. The explicit **「開始日指定」「日程固定」を GitHub に作成** button is the only remote schema operation on this settings page; connecting and ordinary settings changes do not write to GitHub.

**CSVから追加** imports new tasks with predecessors and parents as one local Undo operation. Use the Excel-friendly [template](templates/new-tasks.csv), also shipped in `templates/` beside the app. UTF-8 and Shift-JIS are accepted; errors reject the whole file. **発行** creates the Issues later. The [sandbox evaluation file](docs/evaluation/sandbox-plan.csv) contains 24 tasks for Project 3.

**設定** contains column mappings, calendar, holidays and company days off, assignee rates and allowances, optional Project start and end, phase mapping and option counts, default repository and settings export/import. Accepted changes autosave and recalculate; **元に戻す** reverses one operation. The [Japanese user manual](docs/user-manual.md) describes these flows.

## Progress report

A private data repository calls the reusable workflow [progress-report.yml](.github/workflows/progress-report.yml) on a daily schedule. It commits one snapshot per 基準日 and rebuilds `REPORT.md` there; see [progress snapshots and report](docs/spec.md#progress-snapshots-and-report-262). The caller passes the Project URL, the settings file the app exported for that Project and a token that can read the Project as the `token` secret. To run the tool locally with gh's stored authentication:

~~~powershell
dotnet run --project src/GhProjectsBoards.Report -c Release -- snapshot --project <Project URL> --settings <exported settings> --data <folder>
dotnet run --project src/GhProjectsBoards.Report -c Release -- report --data <folder> --out REPORT.md
~~~

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
- src/GhProjectsBoards.Report: the console that writes progress snapshots and the report for scheduled workflows.
- tests: logic, adapter/storage, hosted UI and ordinary-executable checks.
- docs: current contracts, design rationale and the user manual.

# gh-projects-boards

A Windows planning editor for GitHub Projects, built with C#, .NET 10 and WinUI 3. [Requirements](docs/requirements.md), [specification](docs/spec.md) and [decisions](docs/decisions.md) define the product under [Epic #76](https://github.com/fukuda-yuki/gh-projects-boards/issues/76).

The ordinary app opens the new Project workspace and settings page. It shows refreshed tasks with automatically mapped columns. The task list is currently read-only; the editable sheet, Gantt, publish review and people view are not yet exposed in the ordinary app. Core scheduling, local operations and publishing are independently implemented.

## Build and run

Use Windows x64, .NET 10 SDK and Windows SDK 10.0.26100.0. Restore dependencies once in a network-enabled development environment. In the prepared worktree, use the short junction path:

~~~powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
$env:GHPB_DATA_ROOT = 'C:\w\g76\TestResults\workspace-evaluation'
& C:\w\g76\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe
~~~

GHPB_DATA_ROOT must be absolute. Omit it to use the normal per-user root. The new app writes only under PlanningEditor/v1 and leaves old files untouched. A long checkout can cause MSB3030 during app-local packaging; use a short path before building. The development executable includes app-local .NET and Windows App SDK runtimes. Distribution requirements remain separate; see [dependency terms](docs/dependencies.md).

## Open a Project

Enter the gh executable and hostname, then select **接続**. The app uses gh's stored authentication and lists personal and organization Projects. Select one to add and open it; **URLで開く** is an alternative. Registered Projects appear on the left. **最新の情報に更新** reads current GitHub values; opening an existing local Project preserves its unpublished work.

For the authorized sandbox, connect to github.com with C:\Program Files\GitHub CLI\gh.exe, then select user Project 3. Its fallback URL is https://github.com/users/fukuda-yuki/projects/3. Use an isolated data root for evaluation. The explicit **不足する日程列を追加** button is the only remote schema operation on this settings page; connecting and ordinary settings changes do not write to GitHub.

**設定** contains column mappings, calendar and holidays, rates and days off, optional Project start, default repository and settings export/import. Accepted changes autosave and recalculate; **元に戻す** reverses one operation. The [Japanese user manual](docs/user-manual.md) describes these flows.

## Validation

~~~powershell
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~PlanWorkspaceTests|FullyQualifiedName~PlanLivePreflightTests'
dotnet build C:\w\g76\tests\GhProjectsBoards.UiIntegration.Tests\GhProjectsBoards.UiIntegration.Tests.csproj -c Release --no-restore
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanWorkspace'
C:\w\g76\scripts\Test-E2E.ps1 -Configuration Release -Filter 'FullyQualifiedName~PlanningWorkspaceJourneyTests'
~~~

The hosted UI tests use real controls, Core, storage and a fake-gh subprocess; only file picking is substituted. The ordinary-executable journey uses an isolated fake endpoint and reopens the selected Project after restart. Neither establishes real GitHub, GHEC + EMU, physical IME or human PMO acceptance. See [test policy](tests/README.md) for boundaries.

## Structure

- src/GhProjectsBoards.Core: UI-independent connection, Project reading, scheduling, local documents and publication.
- src/GhProjectsBoards.App: the ordinary WinUI shell, settings and rendering.
- tests: logic, adapter/storage, hosted UI and ordinary-executable checks.
- docs: current contracts, design rationale and the user manual.

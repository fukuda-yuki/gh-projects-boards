# Architecture

## Production projects

| Project | Responsibility |
| --- | --- |
| `GhProjectsBoards.Core` (`net10.0`) | Connection orchestration, identity, authorization checks, Project read models/retrieval, structured API results and gh process ownership |
| `GhProjectsBoards.App` (WinUI 3, .NET 10, Windows x64) | Presentation, native controls, window lifetime, dialogs, clipboard and public UI Automation |

The app references Core. Core does not reference a UI framework or the app. Keep the internal logic surface limited to its app and test consumers. Do not add empty Domain/Application/Infrastructure projects or general-purpose frameworks.

## Connection boundary

| Component | Responsibility |
| --- | --- |
| `MainWindow` | Render connection state, collect user input, invoke operations, handle native dialogs/clipboard and keep the UI alive until owned work stops |
| `ConnectionViewModel` | Observable connection state, explicit checking/rebinding, cancellation and safe user-facing diagnostics |
| `GhConnectionService` / `ConnectionContext` | Stable viewer identity, serialized preflight/dispatch and credential-store write guards |
| `TargetDiagnostics` / `GitHubAddress` | Same-host URL validation and independent Issue/Project permission and scope reports |
| `GhApiTransport` / `ApiRequest` / `ApiResult` | REST/GraphQL construction and structured outcome/error classification |
| `GhProcessRunner` | Shell-free execution, child environment isolation, JSON stdin, stream drains, timeout and process cleanup |

UI code uses connection orchestration rather than duplicating authentication or dispatch rules. Feature code must use the guarded service with its bound context. Service serialization cannot lock external changes to gh authentication. There is no automatic write retry.

Presentation notifications are handled on the WinUI dispatcher. The UI owns any window-bound API; Core receives no Window, DispatcherQueue, visual tree or clipboard object.

Text controls own in-progress input. Diagnostic rendering does not write model snapshots back into editable fields: deferred native text notifications must not erase newer input in another control. The check action reads all current inputs before starting the Core operation. Native executable selection updates the path control through the same input boundary.

## Project retrieval boundary

`Projects/ProjectReader` reads one explicitly selected, connection-scoped Project through `GhConnectionService.SendAsync`. Per-read state holds field definitions, an Issue dictionary and independent Project items; it is discarded after producing the result. `ProjectQueries` contains fixed query documents with ID/cursor variables. Nested value pagination uses item IDs and verifies their owning Project.

`ProjectReadModel` separates scoped node identity, Issue title/state, Project field definitions/options, item values, availability and traversal results. Display names never select fields. The reader has no UI, persistence, draft, mutation or second authentication collaborator. Ordinary startup and `--input-check` remain independent entry points; wiring retrieval into navigation/grid presentation belongs to their owning Issues.

Raw API data stays transient. Safe read diagnostics contain outcome, stage, problem/failure classification and HTTP status, not response messages or content. See the [bounded read contract](spec.md#bounded-project-read-contract) for completeness and unsupported-type semantics.

## Native input boundary

`InputCheckWindow` hosts a bounded three-row/two-column input surface within the app. The explicit `--input-check` launch option opens it; ordinary startup opens `MainWindow`. The input surface has no connection or storage collaborator and uses discard-on-exit synthetic values.

Each native TextBox keeps its committed value separate from the editor text. Cell selection prepares native focus and replacement selection before typing. Actual composition/text changes or F2 begin application editing. The window owns cell/range navigation; IME confirmation and cell commit remain separate transactions. Normal close releases text focus through the public window-closing event before native teardown. This is the executable input boundary, not a complete grid model or a general input framework.

## Test boundary

`GhProjectsBoards.Tests` references only Core and supplies the synthetic gh executable. Its real collaborators verify logic and process behavior without a UI runtime.

`GhProjectsBoards.E2E.Tests` uses NUnit and FlaUI UIA3. App and fake-gh project references are build-only. Tests drive the ordinary executable, verify that its process loads WinUI, and assert observable journeys. Deterministic cases use isolated synthetic data; live cases are separately authorized. See [tests](../tests/README.md).

## Feature responsibilities

Project registration/navigation, grid editing, draft storage and apply lifecycle are specified in [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4) through [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11). Add real code boundaries when those features need them. Keep Issue identity, Project-item identity and local work state distinct.

The editable-grid input gate does not block independent shell, tooling or test-infrastructure work. A rejected component is not a reason to duplicate core logic. Accepted behavior belongs in [specification](spec.md); unresolved work and evidence belong in Issues.

# Architecture

## Production projects

| Project | Responsibility |
| --- | --- |
| `GhProjectsBoards.Core` (`net10.0`) | Connection orchestration, identity, authorization checks, URL parsing, structured API results and gh process ownership |
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

## Test boundary

`GhProjectsBoards.Tests` references only Core and supplies the synthetic gh executable. Its real collaborators verify logic and process behavior without a UI runtime.

`GhProjectsBoards.E2E.Tests` uses NUnit and FlaUI UIA3. App and fake-gh project references are build-only. Tests drive the ordinary executable, verify that its process loads WinUI, and assert observable journeys. Deterministic cases use isolated synthetic data; live cases are separately authorized. See [tests](../tests/README.md).

## Feature responsibilities

Project registration/navigation, grid editing, draft storage and apply lifecycle are specified in [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4) through [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11). Add real code boundaries when those features need them. Keep Issue identity, Project-item identity and local work state distinct.

The editable-grid input gate does not block independent shell, tooling or test-infrastructure work. A rejected component is not a reason to duplicate core logic. Accepted behavior belongs in [specification](spec.md); unresolved work and evidence belong in Issues.

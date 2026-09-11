# Architecture

Source: [#1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2). This is a responsibility outline, not a commitment to separate assemblies or a completed design.

## Current structure

`GhProjectsBoards.sln` contains one .NET 10 WPF application and two test projects. All production connection logic remains internal to `src/GhProjectsBoards.App`; no public API or additional application assembly is introduced.

| Implemented component | Responsibility |
| --- | --- |
| `MainWindow` / `ConnectionViewModel` | Ordinary connection screen, manual checks, copyable login guidance, cancellation and shutdown |
| `GhConnectionService` / `ConnectionContext` | Authentication metadata, stable viewer identity, serialized preflight and target dispatch, credential-store write guard |
| `TargetDiagnostics` / `GitHubAddress` | Explicit same-host URL parsing and separate Issue/Project access and scope reports |
| `GhApiTransport` / `ApiRequest` / `ApiResult` | REST/GraphQL request construction and structured response/error classification |
| `GhProcessRunner` | Shell-free process execution, child environment control, JSON stdin, concurrent stream drains, timeout and process cleanup |
| `GridPrototypeWindow` | Separate offline WPF DataGrid, native text/IME editors, choice templates, selection and keyboard commands, validation feedback |
| `StandardGridWindow` / `GridInputTrace` | Independent default DataGrid/TextBox comparison; optional buffered input observations, written only when the synthetic window closes |
| `GridPrototypeViewModel` / `GridPrototypeRow` / `GridFieldRules` | Deterministic rows with stable IDs, immutable committed values, complete-operation validation and staging, in-memory operation Undo |

UI diagnostics reach GitHub through the connection service. The low-level transport has no user-facing entry point; future features must use the guarded service with their bound context. The service serializes its own work but cannot lock external changes to gh authentication. It exposes no automatic retry or persistence.

`GhProjectsBoards.Tests` exercises production collaborators and provides a synthetic gh process at the nondeterministic boundary. `GhProjectsBoards.E2E.Tests` has build-only references and drives the ordinary executable through UI Automation. Opt-in live cases use the real adapter and CLI against exact sandbox identifiers. See [test boundaries](../tests/README.md).

The grid prototype has no dependency on the connection service or gh adapter. Its editors hold temporary input and commit through the ViewModel; row recycling does not own operation identity. Text columns retain WPF's native input handling through a small internal column subclass, while cell commit reads the editor document to preserve native reconversion. The evaluated prototype is rejected under [#19](https://github.com/fukuda-yuki/gh-projects-boards/issues/19); [#20](https://github.com/fukuda-yuki/gh-projects-boards/issues/20) retains WPF DataGrid as a candidate and provides a standard control comparison. Failed lifecycle experiments are not part of the application. Project registration, production editing, draft storage, and apply queues have not been implemented.

## Responsibility boundaries

| Responsibility | Purpose | Owning Issues |
| --- | --- | --- |
| Windows UI | Project navigation, table editing, and review of changes | [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4), [#5](https://github.com/fukuda-yuki/gh-projects-boards/issues/5), [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7) |
| Application logic | Identity, validation, field differences, conflicts, and operation state | [#6](https://github.com/fukuda-yuki/gh-projects-boards/issues/6), [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8), [#9](https://github.com/fukuda-yuki/gh-projects-boards/issues/9), [#10](https://github.com/fukuda-yuki/gh-projects-boards/issues/10), [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11) |
| Local storage | Registered Projects, baselines, drafts, and recoverable operation history | [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4), [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8) |
| GitHub CLI adapter | Authentication diagnostics and structured `gh api` calls | [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3) |

Introduce code boundaries when an implementing Issue needs them. Do not create empty layer projects or speculative interfaces for this table.

## Data and process design

Connection and API result contracts are defined in [specification](spec.md). Project/Issue editing data contracts, storage schema, and recoverable apply lifecycle remain pending. Preserve their distinct responsibilities and record resolved technologies in [decisions](decisions.md).

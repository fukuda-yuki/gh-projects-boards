# Architecture

## Workspace presentation boundary

`MainWindow` owns workspace/connection navigation, display-aware initial window bounds, local restoration and normal-close settlement. The workspace uses `RegistrationPanel`'s identity strip and connection-request event; the connection page owns the return navigation. `RegistrationPanel` owns native pane layout, account/Project context, membership-based tree reconciliation, contextual settings and full status presentation. It retains tree nodes for unchanged navigation membership, clears discovery state on profile/binding transitions and dismisses contextual surfaces on detach. These controls delegate to the existing `RegistrationWorkspace`; they introduce no additional authentication, transaction or persistence authority.

`EditingGrid` composes the Core row projection and column layout into stable native row containers. A separate header follows the data viewport's horizontal offset and width. Native command overflow keeps editing actions reachable, while compact markers and optional selected-cell details read the same workspace state. Settings restore the intended workspace focus after their native dialog closes. Pending asynchronous commands retain their captured identities and generation guards; active native composition defers operations that would replace editors.

Checkpoint v7 persists scoped row/column definitions together with the existing coherent work and history. Pane visibility, tree focus, active cell selection and viewport position remain transient UI state. The presentation has no second view-state store, conflict engine or Apply planner.

## Existing-field execution boundary

`RegistrationWorkspace` owns preparation and lifecycle of Apply. `EditingWorkspace` produces revision-bound immutable field plans and keeps the journal separate from Undo. Preparation uses a complete `ProjectReader` result. `ApplyRemote` uses the existing `GhConnectionService`, guarded transport and the reader's operation-scoped field observation for dispatch validation and independent verification. It traverses field definitions and the exact target item's values, returning no complete Project snapshot. It does not own credentials or launch processes directly. See the [predicate and completeness mapping](performance.md#scoped-observation-safety-mapping).

`ApplyExecutor` holds a per-profile filesystem execution lease throughout reads, waits, dispatch and acknowledgement, and checks the durable revision before proceeding. The writer lock remains a separate short critical section. Version 7 of the authoritative `DraftRecord` contains execution history, local rows and scoped row/column preferences; v1–6 remain readable. Each journal transition passes through the same validated, flushed, atomic checkpoint boundary as local work. Failed acknowledgement stops further dispatch and leaves durable Running as recovery evidence. `.execution.lock` contains no authoritative queue data; OS ownership is released on process termination.

The ordinary registration panel uses native row-selection and review dialogs plus a history/resume dialog. The grid retains pending native text separately. Stable Apply control IDs support UI automation at both scoped UI integration and whole-application boundaries. Test rules in Core and presentation collaboration at the UI integration boundary; use whole-app journeys and authorized live checks for their distinct remaining risks.

## Production projects

`CreationOperation` is a separate typed member of `ApplyBatch`; it never fabricates remote IDs for `ApplyOperation`. `CreationRemote` extends the guarded adapter with stable Repository resolution, Issue-by-ID/URL observation and Project addition. `CreationExecutor` runs under the same profile execution lease and `DraftSession.CommitAsync` path as existing-field Apply. Received IDs, verified identity, membership, field baselines, attempts and later setup approvals are persisted separately. The journal itself is the durable local-to-remote lineage mapping across batches.

`RegistrationCreation` owns public URL preview/confirmation, duplicate-risk retry and known-Issue setup review. `RegistrationApplyDialogs` exposes per-batch resume and per-creation recovery through native dialogs. Composition-aware deferred rendering prevents asynchronous promotion from replacing an active native editor. Promotion reads the latest local row inside the coherent checkpoint commit and transfers remaining work only against complete observations.

| Project | Responsibility |
| --- | --- |
| `GhProjectsBoards.Core` (`net10.0`) | Connection orchestration, identity, authorization checks, Project read models/retrieval, structured API results and gh process ownership |
| `GhProjectsBoards.App` (WinUI 3, .NET 10, Windows x64) | Presentation, native controls, window lifetime, dialogs, clipboard and public UI Automation |

The app references Core. Core does not reference a UI framework or the app. Keep the internal logic surface limited to its app and test consumers. Do not add empty Domain/Application/Infrastructure projects or general-purpose frameworks.

## Connection boundary

| Component | Responsibility |
| --- | --- |
| `MainWindow` | Navigate workspace/connection surfaces, render connection state, collect user input, invoke operations, handle native dialogs/clipboard and keep the UI alive until owned work stops |
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

## Registration boundary

`ProjectDiscovery` performs guarded, query-only owner/repository/Project discovery and stable URL resolution. It pages GitHub repository associations independently from ProjectReader's item traversal. It has no mutation path or second authentication mechanism.

`RegistrationWorkspace` owns the selected saved profile, registrations, current attempt and cancellable work. It reuses `ProjectReader`, publishes success after durable save, and settles work on context/profile changes and local removal. Connection context is live-only; saved profile identity is never promoted into authentication.

`RegistrationStore` owns version 1 JSON per scoped Project. Explicit snapshot storage records flatten Issue dictionaries into lists; restoration validates schema, identities and availability. Hash-derived filenames avoid remote-name paths. A root lock serializes processes; write-through temporary files are flushed, read back and validated before same-directory move/replacement. Backup/interrupted files are preserved for diagnosis rather than silently accepted as current data.

`MainWindow` owns connection transitions and normal-close settlement. `RegistrationPanel` contains only WinUI presentation, navigation/dialog coordination and event wiring. It clears discovery controls when profile/binding changes and uses EditingGrid for complete registered data and immutable preview strings for partial results. EditingGrid uses native TextBoxes, ComboBoxes and a scrolling ListView with stable per-row containers. The input-check window remains independent. Before migration caches are separate records; after migration the profile checkpoint owns both caches and drafts.

## Native input boundary

`LocalRow` records live in `EditingWorkspace`, outside the reader's Issues/Items and remote `DraftField` collection. `LocalTitle`, `LocalRepository` and `LocalSelect` keys route only to local records. `Open` composes the presentation and retains missing local field definitions for diagnostics. Local validation derives from current cached definitions without constructing remote observations. Creation revision and ordinal preserve row placement across unrelated removals and recovered Undo.

`EditTransaction` holds remote field changes and optional local-row before/after records. Addition, removal, duplication, append and mixed cell edits share its ordering and state guards. Remote reconciliation/acknowledgement separates a mixed operation's still-valid local Undo remainder before invalidating remote baseline restoration. `Snapshot`/`Restore` carry these records through every `DraftSession.CommitAsync`, including Apply journal commits. Before first local addition, `RegistrationWorkspace` migrates all profile registrations under the existing legacy-state guard. No separate local-row file or save authority exists.

`EditingWorkspace` owns UI-independent scoped cell identities, pinned baselines, buffers, field differences, TSV validation, atomic transactions and guarded Project Undo. Its reconciliation partial compares typed observations per stable key, preserves original conflict provenance, validates revision-bound resolution and identifies structural changes. It has no network collaborator. `DraftStore` writes one whole profile revision through a checked temporary file and atomic replacement; `DraftSession` serializes saves, prepares isolated candidates and publishes them only after durable commit. The version 5 checkpoint embeds the whole profile registration set when local rows, refresh or Apply are prepared; once present, this is the sole authoritative cache/draft checkpoint. `RegistrationStore` overlays valid checkpoints over retained legacy files and diagnoses damaged profiles independently. `RegistrationWorkspace` restores from the same validated checkpoint records, protects lifecycle transitions and checks local revision/connection generation at the commit boundary. First migration compares legacy records under the common root lock; legacy writers reject a migrated profile.

`EditingGrid` owns native text/choice controls, rectangular selection, focus, clipboard and presentation. Each row container keeps its captured keys for its lifetime, with horizontal/vertical scrolling. Native text/composition events feed recoverable buffers synchronously, while local saves run asynchronously. Pending clipboard operations are invalidated on transitions. The close path cancels pending UI work, releases text focus and disables editing before draining durable saves; failure keeps the window open.

`InputCheckWindow` hosts a bounded three-row/two-column input surface within the app. The explicit `--input-check` launch option opens it; ordinary startup opens `MainWindow`. The input surface has no connection or storage collaborator and uses discard-on-exit synthetic values.

Each native TextBox keeps its committed value separate from the editor text. Cell selection prepares native focus and replacement selection before typing. Actual composition/text changes or F2 begin application editing. The window owns cell/range navigation; IME confirmation and cell commit remain separate transactions. Normal close releases text focus through the public window-closing event before native teardown. This is the executable input boundary, not a complete grid model or a general input framework.

## Test boundary

The design priority is **logic-layer unit tests > UI-layer integration tests > E2E tests**. Keep rules and orchestration independently testable so the ordinary application's UI is not the primary way to exercise them. Preserve real adapter/process/storage integration alongside logic unit coverage.

`GhProjectsBoards.Tests` references only Core and supplies the synthetic gh executable. Its real collaborators verify logic and process behavior without a UI runtime. Its unit and integration cases are classified by the exercised boundary, not merely the assembly name.

UI integration owns bounded collaboration among views/controls, events/commands, presentation state and rendered results. Tests claiming binding/control behavior exercise the actual relevant view/control and its wiring with the necessary WinUI runtime, UI thread and dispatcher/lifetime handling. ViewModel-only checks do not establish that wiring. An existing app/test runtime or a dedicated host can provide execution, driven directly or through UI Automation. A separate host or project is not part of the definition.

Inspect actual cases, fixtures and dependencies before identifying UI coverage gaps. Reuse suitable existing mechanisms; add a minimal test seam/host only for a concrete uncovered behavior. Do not infer that UI integration exists or is absent from project names, and do not make Core depend on WinUI or add speculative production layers.

`GhProjectsBoards.E2E.Tests` uses NUnit and FlaUI UIA3 with build-only app/fake-gh references. Its runner drives the ordinary executable and verifies the WinUI module. Classify cases by their actual scope, not that mechanism or assembly name: bounded UI collaboration can be UI integration; workflows through the app's principal layers to the declared endpoint are app-level E2E. Fake gh is a disclosed external substitution, not proof of either classification; live access is a separate environment dimension. Preserve real native focus, physical IME, picker/clipboard and restart/close observations wherever the asserted contract needs them. Audit, selection and coverage migration follow the [test policy](../tests/README.md).

## Feature responsibilities

Project registration/navigation, grid editing, draft storage and apply lifecycle are specified in [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4) through [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11). Add real code boundaries when those features need them. Keep Issue identity, Project-item identity and local work state distinct.

The editable-grid input gate does not block independent shell, tooling or test-infrastructure work. A rejected component is not a reason to duplicate core logic. Accepted behavior belongs in [specification](spec.md); unresolved work and evidence belong in Issues.

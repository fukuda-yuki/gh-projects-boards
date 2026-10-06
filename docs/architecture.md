# Architecture

## Production boundaries

One UI-independent GhProjectsBoards.Core library and one GhProjectsBoards.App WinUI 3 application implement the [specification](spec.md). Core owns authentication orchestration, scoped identity, retrieval, validation, scheduling, local storage and publication. The app owns controls, presentation, window lifetime, file pickers, clipboard and UI Automation. No UI framework enters Core.

## Workspace and settings (#81)

MainWindow hosts PlanWorkspaceView, sizes the ordinary window and cancels UI-owned remote operations, waits for their owned processes and local saves on close. Failed saves keep the window open. PlanWorkspaceView contains connection, Project discovery, the registered-Project list, the editable plan sheet and row-aligned Gantt and one settings page. Native control events invoke Core operations. Save recovery bypasses pending-input application until the retained save has been retried. Company/personal day-off editors use native calendar selection and explicit date-list changes. Pending text is committed before navigation or normal close; obsolete settings controls cannot apply late events after replacement.

PlanWorkspace reuses GhConnectionService, ProjectDiscovery, ProjectReader, PlanSession, PlanStore and PlanPublisher. It retains one session per scoped Project and writes an atomic workspace catalog beneath PlanningEditor/v1 before adopting a changed Project selection. Opening a new Project reads its complete snapshot and matches typed columns. Opening an existing Project restores its local document; refreshing is explicit. Authentication is always rechecked; a saved Project does not imply a connected identity. The catalog does not read old registrations or checkpoints.

Column mappings, calendar, people rates and days off, Project start and repository are ProjectPlanSettings. Each accepted setting operation goes through ReplacePlanSettings, the same scheduler and bounded Undo history. Settings export/import use the existing Core JSON contract. Refreshed assignee display names are remote metadata in PlanSync, separate from locally chosen rates. File pickers remain in the view; the test substitution replaces only picking a path.

## Planning editor Core

PlanRow and PlanBaseline retain typed day-level input and GitHub values. PlanScheduler produces derived dates, roll-ups, reasons and warnings from Remaining, rates, calendars, predecessors and hierarchy. Its exact internal work endpoints never become persisted minute dates.

PlanSession applies immutable operation patches, guards atomic rejection and owns the 200-step Undo history. PlanStore checks identity and data, serializes durable saves with optimistic fingerprint checks, writes and verifies temporary files, and atomically replaces the prior checkpoint. Failed saves retain the in-memory document for retry.

PlanSnapshot translates complete Project reads. PlanMerge reconciles baseline/local/remote values. PlanPublishPlan and PlanPublisher provide ordered, batched serial writes, duplicate-guarded Issue creation, verification and durable recovery. Their ordinary UI is separate from the current workspace/settings delivery. See the [publish contract](spec.md#current-planning-editor-refresh-and-publishing-contract-79).

## GitHub boundary

GhConnectionService binds host, stable viewer and executable, serializes operations, rechecks identity and guards writes by authentication storage/scopes. GhApiTransport uses shell-free arguments and UTF-8 JSON stdin. GhProcessRunner owns process cancellation/timeouts and token-environment isolation. No token is retrieved, stored or displayed.

ProjectDiscovery pages user/organization Projects and resolves explicit same-host URLs. ProjectReader / ProjectQueries read fields, all items, assignees, blocked-by edges and parents with complete pagination and scoped identities. Incomplete observations are never accepted as complete snapshots.

## Native plan surface (#78)

PlanSheetView owns the selected cell/range, pending native TextBox input, clipboard, column visibility, title filter, zoom and independent horizontal offsets. PlanSheetRow realizes only the virtualized visible rows; cells, bars and dependency segments share DPI-aware compact row geometry. One ListView owns vertical scrolling. Native composition events guard IME confirmation separately from cell commit. Native key overrides return synchronously; asynchronous commits use captured managed values without retaining routed event arguments. Inactive row presentation is released after the native unload callback returns; focused or composing editors are retained. Final window close is queued after the cancellable native closing callback returns.

PlanSheetEditing translates visible rows/columns and text/clipboard into typed Phase 3 commands using stable PlanRow identities. It has no legacy EditingWorkspace, DraftSession or Apply journal. PlanSession remains the single state/history/save owner. The native rendering and frame callback approach replaces the isolated prototype model; no prototype executable route remains.

Legacy Core editing and adapter regression contracts remain independently testable. Phase 7 publishing and Phase 8 people-view delivery use PlanPublisher, PlanSession and the current scheduling model; they do not require retaining old Boards/Gantt/Summary UI.

## Validation boundaries

The [test policy](../tests/README.md) prioritizes logic, then UI integration, then representative E2E. Workspace adapter tests use real fake-gh subprocesses and isolated storage. Hosted UI tests exercise actual views, events and rendered values with the same Core. E2E drives the ordinary executable through public UI Automation, including restart. Fake endpoints do not establish live GitHub, physical IME, performance or human acceptance.

# Architecture

## Production boundaries

One UI-independent GhProjectsBoards.Core library and one GhProjectsBoards.App WinUI 3 application implement the [specification](spec.md). Core owns authentication orchestration, scoped identity, retrieval, validation, scheduling, local storage and publication. The app owns controls, presentation, window lifetime, file pickers, clipboard and UI Automation. No UI framework enters Core.

## Workspace and settings (#81)

MainWindow hosts PlanWorkspaceView, sizes the ordinary window and cancels UI-owned remote operations, waits for their owned processes and local saves on close. Failed saves keep the window open. PlanWorkspaceView contains connection, Project discovery, the registered-Project list, the editable plan sheet and row-aligned Gantt and one settings page. Native control events invoke Core operations. Save recovery bypasses pending-input application until the retained save has been retried. Company/personal day-off editors use native calendar selection and explicit date-list changes. Pending text is committed before navigation or normal close; obsolete settings controls cannot apply late events after replacement.

PlanWorkspace reuses GhConnectionService, ProjectDiscovery, ProjectReader, PlanSession, PlanStore and PlanPublisher. It retains one session per scoped Project and writes an atomic workspace catalog beneath PlanningEditor/v1 before adopting a changed Project selection. Opening a new Project reads its complete snapshot and matches typed columns. Opening an existing Project restores its local document; refreshing is explicit. Authentication is always rechecked; a saved Project does not imply a connected identity. The catalog does not read old registrations or checkpoints.

Column mappings, calendar, people rates, allowances and days off, Project start and repository are ProjectPlanSettings. Each accepted setting operation goes through ReplacePlanSettings, the same scheduler and bounded Undo history. Settings export/import use the existing Core JSON contract. Refreshed assignee display names and Issue links are remote metadata in PlanSync, separate from locally chosen rates. File pickers remain in the view; the test substitution replaces only picking a path.

PlanPeople aggregates the selected document's scheduler output and resource calendar into daily, weekly or monthly load and whole-Project forecasts. The scheduler exposes daily work quantities while retaining exact internal time endpoints. PlanPeopleView owns period navigation, expansion and controls; task edits use PlanSheetEditing and EditPlanCells, and allowance/rate edits use ReplacePlanSettings. Both use the existing PlanSession history and persistence. It has no remote writer or leveling operation.

## Planning editor Core

PlanRow and PlanBaseline retain typed day-level input and GitHub values. PlanScheduler produces derived dates, roll-ups, reasons and warnings from Remaining, rates, calendars, predecessors and hierarchy. Its exact internal work endpoints never become persisted minute dates.

PlanSession applies immutable operation patches, guards atomic rejection and owns the 200-step Undo history. PlanStore checks identity and data, serializes durable saves with optimistic fingerprint checks, writes and verifies temporary files, and atomically replaces the prior checkpoint. Failed saves retain the in-memory document for retry.

PlanCsvImport decodes and validates whole files, resolves file keys and loaded Issue references, and prepares one InsertPlanRows command. Repository and assignable-user catalog reads use the active Core connection lease; the view owns only native file selection, error presentation and repeat-import confirmation. A row's original-file SHA-256 is local provenance, follows normal row history/identity promotion and is never a GitHub field. New resource identities are added in the same command, preserving existing rates. Publication resolves repository placeholders by their destination rather than by one global default.

PlanSnapshot translates complete Project reads. PlanMerge reconciles baseline/local/remote values. PlanPublishPlan and PlanPublisher provide ordered, batched serial writes, duplicate-guarded Issue creation, verification and durable recovery. PlanWorkspacePublish presents field differences and conflict choices and starts writes only on explicit confirmation. It reports publisher stages without a second outcome store. Closing the review leaves the workspace-owned operation running; window close cancels and awaits it. Failed and unverified rows come directly from PlanSync. See the [publish contract](spec.md#current-planning-editor-refresh-and-publishing-contract-79).

## GitHub boundary

GhConnectionService binds host, stable viewer and executable, serializes operations, rechecks identity and guards writes by authentication storage/scopes. GhApiTransport uses shell-free arguments and UTF-8 JSON stdin. GhProcessRunner owns process cancellation/timeouts and token-environment isolation. No token is retrieved, stored or displayed.

ProjectDiscovery pages user/organization Projects and resolves explicit same-host URLs. ProjectReader / ProjectQueries read fields, all items, assignees, blocked-by edges and parents with complete pagination and scoped identities. Incomplete observations are never accepted as complete snapshots.

## Native plan surface (#78)

PlanSheetView owns the selected cell/range, pending native TextBox input, clipboard, column visibility, title filter, zoom and independent horizontal offsets. PlanSheetRow realizes only the virtualized visible rows; cells, bars and dependency segments share DPI-aware compact row geometry. One ListView owns vertical scrolling. Containers from a replaced item source can stay loaded with their old task until the list discards them, so focus requests resolve a task's row through the list's current container for that task. Native composition events guard IME confirmation separately from cell commit. Native key overrides return synchronously; asynchronous commits use captured managed values without retaining routed event arguments. Inactive row presentation is released after the native unload callback returns; focused or composing editors are retained. Final window close is queued after the cancellable native closing callback returns.

PlanSheetEditing translates visible rows/columns and text/clipboard into typed local operations using stable PlanRow identities. PlanSession remains the single state/history/save owner. Rendering and frame instrumentation use the same native controls as ordinary editing.

The planning editor uses PlanPublisher, PlanSession and the current scheduling model exclusively. Only the new scoped checkpoint format is loaded. Calendar resources and Project retrieval remain independent of editing and publication.

## Validation boundaries

The [test policy](../tests/README.md) prioritizes logic, then UI integration, then representative E2E. Workspace adapter tests use real fake-gh subprocesses and isolated storage. Hosted UI tests exercise actual views, events and rendered values with the same Core. E2E drives the ordinary executable through public UI Automation, including restart. Fake endpoints do not establish live GitHub, physical IME, performance or human acceptance.

## Evaluation boundary

The test executable generates the offline evaluation data using PlanOperations.Schedule, persists an ordinary PlanStore document and serves the matching remote rows through FakePlanEditor. Start-Evaluation.ps1 launches the ordinary app with an isolated data root and child-only PATH pointing to that fake executable. There is no product evaluation branch, network fallback or additional dependency. Resume retains both local edits and the fake remote state.

# Source and design review

Reviewed against AGENTS.md, DESIGN.md, tests/README.md and the #65 bounded decision. Installed WinUI development, design, code-review and UI-testing guidance informed the work; repository test policy selected the actual executions. No new framework, project, dependency, platform API outside the existing native boundary, or machine configuration was introduced.

## User task and presentation

The user edits task values, observes the resulting Gantt schedule and returns to an unfinished Boards edit. That unfinished text, field identity and caret must survive. The repair preserves the existing native input surface and concise labels; it does not substitute a read-only cell or add explanatory text to mask a slow interaction. Only Project settings retain the mapping-driven row reconstruction. Selection/fill affordances are created before their actual visible use, using existing theme styles and automation IDs.

Source checks found no new remote communication, Apply, draft contract, assignment/identity relaxation or implicit cell commit. Task dialog validation/error handling still precedes presentation. Revision-aware Gantt refresh retains operation status updates even when no projection rebuild is needed. Explicit view transitions still carry the selected row.

## Native lifecycle and realization

Offscreen columns retain their layout slots. Reveal synchronously creates the real fixed-identity editor, marker and title identity content, then applies the current state. Selection paint ignores an unrealized slot until reveal applies it. Frames and handles retain their original native interaction/event routes when created. No active, buffered or composing editor is rebound to another row/field. Existing protected rows and the 64-row inactive cache remain unchanged; no product GC or timer eviction was added.

Related focused-scroll, context/header/horizontal reveal and bulk selection/fill paths pass the 31-case scoped UI execution. Gantt editing/settings/Undo paths pass the 13-case development execution. Exact later source differences are disclosed in the execution ledger. Final ordinary/direct-F2/sustained-IME checks cover the native/process risks that hosted state checks alone cannot establish.

Optional CPU accounting is gated behind both the diagnostic sink and `GHPB_SHEET_THREAD_TIMING=1`. It calls read-only GetThreadTimes on the UI thread and retains cumulative/delta OS accounting, without synchronizing threads or changing cache lifetime. The six final timing runs leave it off. Core spans are inert without a PerformanceTrace scope; canonical validation/staging/scheduling behavior is preserved.

The ownership probe uses weak sample references and a temporary direct ownership census for diagnosis, not an assertion about private calls. GC exists only in the opt-in test. Teardown and replacement-view results do not establish a unique native root because additional idle/GC accompanies the focus transition.

## Disposition

No unresolved source-review defect was identified in the bounded correction. Static rendered snapshots confirm the inspected dark-theme cells/header/selection remain legible and the pending edit returns intact. Scrolling performance remains a concrete product defect; source review and successful state checks do not accept that experience. Native template/layout CPU attribution, old retention-count variation, other themes/scales/accessibility and human evaluation remain unverified as described in the receipt.

Microsoft's [ListView/GridView optimization guidance](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview) supports inspecting repeated visual construction and element count. It is context for the selected investigation, not causal proof for this app; conclusions above come from the recorded implementation, controls and timing observations.

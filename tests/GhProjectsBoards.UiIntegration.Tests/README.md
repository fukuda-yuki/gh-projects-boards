# Hosted WinUI integration

The host inherits the ordinary application's compiled resources and XAML metadata. It mounts real product controls on the UI dispatcher and records asynchronous event failures. Tests use public native control state, UI Automation selection/invoke patterns, focus and rendered geometry. Ui.Idle drains tracked continuations; native focus/layout observations additionally wait for the resulting visible or saved state.

- PlanWorkspaceHostedTests: connection/discovery/open/switch through the title-bar picker, settings return navigation, selected view tabs, workspace Undo/Redo controls, light theme and status counts, settings/mappings/calendar files, serialization during pending saves, safe catalog failure, refresh presentation and process cancellation. Only the external gh executable and file picker are substituted.
- PlanSheetHostedTests: the actual plan sheet, range commands and row-aligned chart using real PlanSession, scheduler and isolated durable storage. Routine clipboard cases substitute only the OS clipboard. Standalone sheet history checks use its command boundary for pending-input and rendered-state assertions; workspace cases establish the actual Undo/Redo button wiring. The PlanSheetNative category adds physical keys/pointer and must run on the PMO desktop.
- PlanSheetPerformance: 1,000 tasks, 20 people, ten-task chains and 20 commit-to-Rendered samples, with every outcome retained. It is an explicit performance run, not a routine timing assertion.
- People cases in PlanWorkspaceHostedTests: daily/week/month load, allowance editing, contributing tasks, overload markers and retained pending input.
- Publish cases in PlanWorkspaceHostedTests: review, conflicts, failures, retry and operation lifetime through actual workspace controls.
- InfrastructureTests: deliberate runner failures, excluded from normal execution.

Use the commands and evidence boundaries in [test policy](../README.md). Build Release with --no-restore, then invoke scripts/Test-UiIntegration.ps1 -NoBuild. The default excludes infrastructure, native-input and performance categories. The ordinary app never references the host.

Native physical IME is exercised by PlanSheetImeTests through the ordinary app and fake gh, selected with TestCategory=GridIme. Neither TextBox assignment nor toolbar invocation proves physical keyboard/composition behavior. Live sandbox and human review are separate from both suites.

RenderedEvidence captures native XAML after layout for design review. It is not a desktop/compositor latency measurement. Tests assert state/geometry separately from capture timing.

Unmount failures report the test and lifecycle phase, root attachment, loaded state, focused control, open popups, sheet command queue, drag timer, frame subscription and tracked dispatcher work. A missing Unloaded signal is distinct from a process deadline or an unresponsive dispatcher. Case start/end and lifecycle diagnostics are written immediately to the UTF-8 stderr stream, outside NUnit case buffering, and remain available when an external deadline prevents results.xml.

Each case ends with a bounded full collection and finalizer pass. A detached view's WinRT references release its native XAML tree, and the memory pressure CsWinRT adds for each reference, only once they are collected and finalized. Without that case boundary, the sheets mounted by successive cases accumulate, and the pressure forces increasingly long blocking gen2 collections on the UI thread. Those pauses stall dispatch, layout and rendering in later cases. The [END] line records whether the pass completed, the cumulative GC pause and private memory.

The host window shares the interactive desktop. [WINDOW] lines record activation changes and [INPUT] lines record physical keys or pointer presses reaching the host. Routine cases inject no native input, so an [INPUT] line in a routine run marks desktop interference that can invalidate focus and input observations.

The default host deadline is 900 seconds for the combined routine suite; the per-view Unloaded deadline remains 10 seconds. Increase the process deadline explicitly for performance selections. Process deadlines do not establish a view lifecycle failure.

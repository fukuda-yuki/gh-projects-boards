# Hosted WinUI integration

The host inherits the ordinary application's compiled resources and XAML metadata. It mounts real product controls on the UI dispatcher and records asynchronous event failures. Tests use public native control state, UI Automation selection/invoke patterns, focus and rendered geometry. `Ui.Idle` drains tracked continuations; native focus and layout observations additionally wait for the resulting visible or saved state. The ordinary app never references the host.

Only unmanaged dependencies are substituted: the gh executable, the OS clipboard and the native file picker. Core, scheduling, history and isolated durable storage are real. The test names list what each suite covers; the [test policy](../README.md) owns boundaries, selection and evidence.

## Running

Build Release with `--no-restore`, then run `./scripts/Test-UiIntegration.ps1 -NoBuild` from the checkout root. The default selection excludes these categories:

- `Infrastructure`: deliberate runner failures.
- `PlanSheetNative`: physical keys, pointer and clipboard; run it on the PMO desktop.
- `PlanSheetPerformance`: 1,000 tasks, 20 people, ten-task chains and 20 commit-to-Rendered samples with every outcome retained; see [performance measurement](../../docs/performance.md).

The process deadline defaults to 3,600 seconds for the routine selection. A deadline expiry is not a view lifecycle failure; the per-view Unloaded deadline remains 10 seconds.

`RenderedEvidence` captures native XAML after layout for design review. The launcher sets NUnit's work directory to the run's evidence directory, so screenshots are retained under `screenshots/` with the logs and results and included in the hosted workflow artifact. The capture name, saved path and completion time are recorded in the output. Image generation has a 20-second diagnostic deadline within the UI dispatch deadline; it is not a desktop or compositor latency measurement, and tests assert state and geometry separately from capture timing.

## Desktop interference

The host window shares the interactive desktop. `[WINDOW]` lines record activation changes and `[INPUT]` lines record physical keys or pointer presses that reach the host. Routine cases inject no native input, so an `[INPUT]` line in a routine run marks desktop interference that can invalidate focus and input observations.

## Lifetime diagnostics

Unmount failures report the test and lifecycle phase, root attachment, loaded state, focused control, open popups, sheet command queue, drag timer, frame subscription and tracked dispatcher work. A missing Unloaded signal is distinct from a process deadline or an unresponsive dispatcher. Case start/end and lifecycle diagnostics are written immediately to the UTF-8 stderr stream, outside NUnit case buffering, and remain available when an external deadline prevents `results.xml`.

Each case ends with a bounded full collection and finalizer pass. A detached view's WinRT references release its native XAML tree, and the memory pressure CsWinRT adds for each reference, only once they are collected and finalized. Without that case boundary, the sheets mounted by successive cases accumulate, and the pressure forces increasingly long blocking gen2 collections on the UI thread. Those pauses stall dispatch, layout and rendering in later cases. That pass only helps when unmounted views are unreachable. The `[END]` line records whether the pass completed, the cumulative GC pause, private memory and how many unmounted views are still alive; only the fixture's current view should remain. A loaded WinUI TitleBar keeps its header content after it unloads, so the workspace attaches its title-bar content only while loaded, and `PlanWorkspaceLifetimeHostedTests` checks that an unloaded workspace is collected. The TitleBar also leaves non-client regions on the shared host window; the host clears them before closing the window, which otherwise fails fast in Microsoft.UI.Input.

The workspace lifetime case uses the ordinary window's custom-title-bar setup: content extension before mount and registration of the workspace TitleBar. It removes the window's title-bar reference before unmounting and checking collection, and restores the prior window mode after the check.

The host closes its only window after NUnit finishes and lets the [last-window lifecycle](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle) terminate the application. It does not request application exit again while that close is underway. XAML exception diagnostics include the event message and current managed stack; unobserved task failures are labelled separately. Window-close markers show whether a failure occurred inside or after `Window.Close`. The process result still checks all recorded failures and pending asynchronous work after `Application.Start` returns; passing NUnit cases alone do not establish a successful host shutdown.

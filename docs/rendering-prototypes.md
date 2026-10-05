# Rendering experiment contract (#77)

The isolated ordinary-app switches `--prototype winui` and `--prototype webview` serve the PMO's task of editing effort while comparing the resulting schedule. They never connect, publish or read workspace data. Both use the same 1,000-row C# model, 20 synthetic people and chains of ten finish-to-start tasks. Title, Remaining and Start date are editable. The stand-in uses calendar days, eight hours per day, rounded up, and an exclusive finish boundary; it is deliberately not the #78 scheduler.

Use a single vertical viewport for sheet and chart. Keep concise headers and calculated dates distinct; arrows connect each task to its predecessor. Day width is 24 logical pixels. Enter commits and advances; IME conversion Enter must not commit. Invalid input retains its row/column text and a short error across focus loss and other cell commits. Only correcting that cell clears its pending error.

`GHPB_PROTOTYPE_METRICS` selects a JSON-lines file. Samples start at the cell commit event, include C# recalculation and UI update, and end at WinUI's next Rendered callback or the web page's second requestAnimationFrame acknowledgement. Web samples include both bridge trips. These are rendering-pipeline observations, not physical display presentation or input-device latency. A superseded edit must not be reported as a rendered sample. Record missing samples explicitly. Physical display/blank-row/IME acceptance needs separate desktop observation.

UI integration uses real mounted controls: native TextBox edits through focus-loss commits, and DOM input/keyboard events through ExecuteScriptAsync for the web candidate. DOM events do not establish native keyboard or physical IME behavior. Assert both displayed dates and bar geometry after the edit.

Range selection, rectangular clipboard, fill handle and Ctrl+D may be assessed rather than implemented in this timeboxed prototype. Neither candidate is accepted as a product grid by these tests. No rendering choice or publish-time target is made here; #77 Phase 1B owns that decision.

The live measurement utility is limited to github.com, fukuda-yuki/codex-sandbox and fukuda-yuki Project 3. Plan mode has no mutations. Persist baseline identities and a unique marker before creating anything; never repeat uncertain creation. Cleanup discovers exactly marked Issues, removes their memberships and Issues, then checks preserved item order, field definitions and scope Issue #1. An unrecognized partial/error/rate-limited response stops measurement and leaves evidence for explicit cleanup. No automatic mutation retry.

## Running the comparison

Build with the repository's existing restored dependencies:

```powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
$env:GHPB_PROTOTYPE_METRICS='C:\w\g76\TestResults\prototype-winui.jsonl'
& C:\w\g76\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe --prototype winui
# Close that window before running the other candidate.
$env:GHPB_PROTOTYPE_METRICS='C:\w\g76\TestResults\prototype-webview.jsonl'
& C:\w\g76\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe --prototype webview
```

Use Remaining on row 1: change 8 to 16 and press Enter. Its end changes from 10/06 to 10/07, and row 2's start/bar move to 10/07. The next ten-row chain does not move. Try Title and a Start date in October 2026. Effort accepts 0–8000; dates use yyyy-MM-dd. The timeline starts at 2026-10-05 and exposes 50 days; values beyond this prototype horizon require horizontal presentation work in the chosen implementation. This limitation is not a scheduling rule.

For the desktop WebView2 check, run the ordinary executable above outside the restricted execution environment. Click row 1 Remaining, press Ctrl+A, type 16 and press Enter without DOM injection. Expect row 1 end and row 2 start to become 2026-10-07, the first bar to span 48 logical pixels and one successful `web-second-animation-frame` sample. Focus should advance to row 2 Remaining. Record a screenshot and the metrics file; a click that leaves only the host pane focused is a failure.

While that window remains open, run this read-only UIA check in **Windows PowerShell 5.1**:

```powershell
Set-Location C:\w\g76
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$prototypeProcess = Get-Process GhProjectsBoards.App |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -Last 1
$prototypeWindow = [System.Windows.Automation.AutomationElement]::FromHandle($prototypeProcess.MainWindowHandle)
$prototypeCells = $prototypeWindow.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit))
$prototypeCells | ForEach-Object {
    [pscustomobject]@{Name=$_.Current.Name; Focused=$_.Current.HasKeyboardFocus; Bounds=$_.Current.BoundingRectangle}
}
```

Record whether named Edit controls such as `行 1 Remaining` and `行 2 Remaining` are exposed. External UIA cell access is an unresolved limitation; the current web integration driver uses ExecuteScriptAsync DOM events. Re-run with the prototype as the only app instance if process selection is ambiguous. Record missing elements as unavailable UIA coverage even if native input works; do not replace this check with ExecuteScriptAsync. Native injected input success does not establish external UIA addressability. The hosted web test above separately asserts DOM dates and geometry and should pass alongside the three native cases (four cases total).

For Japanese IME, focus a title, enable Microsoft Japanese IME, type with physical keys and convert with Space. First Enter should leave the same cell focused and add no successful metric sample. Second Enter should move to the next row and add exactly one sample. Repeat direct typing and F2/native caret entry, cancellation, and reconversion. Record whether keys were physical or injected; DOM composition events and Unicode insertion are not physical IME evidence. WinUI also commits on leaving a cell; this provides a deterministic focus-path UI integration boundary. Web commits on Enter only in this experiment.

Scroll slowly, with large wheel steps and by dragging the scrollbar from row 1 to 500 to 1000 and back. Watch row numbers, bars and arrows together; capture video or a timestamped image sequence to detect blank/late rows. Repeat with an active editor. One viewport structurally aligns rows, but does not prove readable pixels or smoothness. The native candidate uses ListView virtualization; the web candidate has five overscan rows and retains the focused input when scrolling.

```powershell
dotnet build C:\w\g76\tests\GhProjectsBoards.UiIntegration.Tests\GhProjectsBoards.UiIntegration.Tests.csproj -c Release --no-restore
& C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'class == GhProjectsBoards.UiIntegration.Tests.PrototypeHostedTests'
dotnet test C:\w\g76\tests\GhProjectsBoards.Tests\GhProjectsBoards.Tests.csproj -c Release --no-build --filter FullyQualifiedName~PrototypePlanTests
& C:\w\g76\tests\MeasurePlanningSandbox.Tests.ps1
```

Web samples use the page's monotonic clock from its keydown commit to its second animation-frame callback, including the edit/data bridge roundtrip. `hostElapsedMs` additionally records receipt in C# to receipt of the acknowledgement. Native samples run from the commit callback through recalculation, visible-control refresh and `CompositionTarget.Rendered`. Neither proves compositor presentation, GPU completion, physical input delay, scrolling FPS or the later real scheduler's latency. Initial frames are not edit samples. Error/superseded/unloaded records are not successful frames.

## Remaining interaction work and licenses

Neither prototype implements rectangular selection, inter-cell clipboard, fill handle or Ctrl+D. Native text selection/clipboard is only within one TextBox. The native candidate would require a stable row/column selection model, an overlay and identity-owned editors, then connect the existing rectangular validation/transaction concepts to the new plan. The web candidate would require the same model and validation in C#, DOM pointer/keyboard selection and drag capture, TSV serialization and a host clipboard bridge. Both need operation-level Undo, editable/selected distinction and IME/focus tests. This is several days of work for either candidate; it cannot be inferred from a working text input.

For the rest of #78, both require real scheduling, all columns, row creation/hierarchy, warnings, start reasons and day/week/month scales. Native work additionally carries recycling/editor lifetime and UIA implementation risk; web work carries message ordering, DOM accessibility and a second runtime's focus/clipboard/IME risks. A rough UI-only estimate, assuming a ready Core model, is 5–10 developer days for either option plus verification; it is not a delivery promise or a rendering decision.

No dependency is added. Windows App SDK remains pinned by the app project. Its resolved WebView2 SDK is **1.0.3179.45** (`obj/project.assets.json`). The installed package's `LICENSE.txt` permits source/binary redistribution under BSD-style attribution, disclaimer and non-endorsement conditions, with no fee or company-size/revenue eligibility. Preserve the package's LICENSE/NOTICE when redistributing. HTML/CSS/JS here is original repository code, with no third-party web component. The installed Evergreen WebView2 Runtime is a separate Microsoft product; its redistribution terms must be checked by the PMO before Phase 1B licensing sign-off because the runtime's first-party license text was not available in the offline package. This prototype uses the already-installed runtime and neither installs nor redistributes it.

## Live measurement handoff

```powershell
Set-Location C:\w\g76
& C:\w\g76\scripts\Measure-PlanningSandbox.ps1 -Mode Plan
# Use the unique marker printed by Plan:
& C:\w\g76\scripts\Measure-PlanningSandbox.ps1 -Mode Run -RunMarker <marker>
# After interruption (honor recorded Retry-After/reset before running):
& C:\w\g76\scripts\Measure-PlanningSandbox.ps1 -Mode Cleanup -RunMarker <marker>
```

Plan is offline and performs zero mutations. Run resolves and prints the exact repository/Project node IDs, requires existing number/date fields, then uses ten-alias creation/addition batches. Each update series is **300 mutations over 100 cells (two fields on 50 Issues, three writes per cell)**, at the requested serial batch sizes. Values differ between series; final values are independently read back outside the write timer. Three full-refresh samples page items/fields and any overflowing nested values/assignees/blockedBy connections. Project item order is the returned connection order. Linear per-item extrapolation is labeled as an estimate, not a 1,000-item measurement.

`TestResults/live/<marker>/results.json` holds IDs, timings, dispatched-creation markers, rate headers/query rateLimit data, verification and cleanup status; it holds no credentials or raw Issue payloads. Mutation roots have no GraphQL rateLimit field, so mutation observations use response headers. `observedUsedDelta` may include other clients using the same account; it is not an isolated per-mutation cost. Timings include gh startup and evidence-file writes. The create-and-add timer also includes the bounded auto-add observation period; use per-request timings and observation records to separate that cost. Cleanup discovers exact titles/body markers through the repository connection (not search indexing), refuses preexisting items and #1, and compares the original Project item order, field definitions and #1 hash after deletion. A stopped rate limit leaves explicit cleanup pending; there is no automatic retry. A new Run cannot reuse an existing manifest. Concurrent unrelated edits can make baseline comparison fail; the tool never deletes unrelated data to make it match.

Every GraphQL document is checked before transport for balanced delimiters and nonempty selection sets, ignoring comments and string literals. This structural guard does not validate the GitHub schema. Request records retain GraphQL error message, path and type. Items with inaccessible or non-Issue content retain their baseline item IDs and skip Issue relation reads. A subsequent Run requires a fresh marker and preserves existing run directories.

The web host places runtime errors in a separate Auto row, collapsed when empty, with the browser in the remaining row. Standard HTML inputs carry Japanese DOM accessible names such as 行 1 Remaining; this does not guarantee their exposure through external UIA. Native pointer/keyboard input and WebView2 UIA exposure require a working desktop runtime; DOM-script success alone does not establish either.

Offline tests substitute the GraphQL transport and poll waits; the real response policy, document guard and orchestration execute. They do not establish schema compatibility, authentication, service limits or real-GitHub cleanup; the PMO must run Plan, review its target, and then Run. A successful run has `measurementCompleted`, `updatesVerified` and `cleanup.verified` true, eight timing entries, and no rate-limit failure. Preserve failed results as well.

The live tool measures a standalone gh workload, not the future #79 Core publish pipeline. Its adapter/identity-check overhead must be accounted for when Phase 1B sets product targets. Web initialization retains the runtime's normal process/GPU security configuration; a renderer/GPU process failure is an unavailable measurement, not grounds to disable the browser sandbox.

## Auto-add and partial mutation responses (#79 constraint)

Project workflows may asynchronously add newly created Issues before explicit add mutations finish. Publishing must reconcile each alias by content identity, retain successful aliases from partial responses, and never repeat Issue creation. Only the exact add error `UNPROCESSABLE / Content already exists in this project`, with a known alias path, is recoverable; other errors stop measurement.

For each creation batch, observe Project membership immediately after the creation response and at up to two further probes, with 1 s and 2 s delays. Stop observation early when all ten are visible. Record probe start/end times relative to the creation response, first-observed item IDs and elapsed times, and requested waits. These are sampled visibility bounds, not server creation timestamps or exact workflow latency. Absence within the observation window does not prove auto-add is disabled. The measurement then explicitly attempts all ten adds, including observed memberships, to measure duplicate/race behavior. Record successful and already-present aliases separately; reconcile only duplicate aliases with up to three membership reads using the same bounded delays. Never retry add or create mutations.

Cleanup deletes one Issue per request, verifies its echoed clientMutationId (or a scoped NOT_FOUND / HTTP 410 already-removed outcome), then independently verifies disappearance of all marked Issues and preservation of the baseline. Every Cleanup rediscovers remaining marked Issues. RESOURCE_LIMITS_EXCEEDED is recorded as a distinct stop reason. Ten-alias deleteIssue requests hit this resource limit in the PMO measurement; createIssue (10 aliases), addProjectV2ItemById (10) and updateProjectV2ItemFieldValue (up to 50) did not. These observed limits do not establish universal service capacity.

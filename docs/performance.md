# Performance measurement

## Current plan sheet (#78)

Run the prepared Release build serially, without another build/test workload. Use a new evidence directory per run.

~~~powershell
# From the checkout root; use a new evidence directory per run.
dotnet build GhProjectsBoards.sln -c Release --no-restore
$env:GHPB_PLAN_EVIDENCE = Join-Path (Get-Location) 'TestResults/plan-performance/run-01'
./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanSheetPerformance' -TimeoutSeconds 300
Remove-Item Env:\GHPB_PLAN_EVIDENCE
~~~

The fixture has 1,000 tasks, 20 people and ten-task finish-to-start chains. Twenty edits alternate row 1 Remaining between 8 and 16 hours. Each sample starts at the cell commit and ends at the next CompositionTarget.Rendered callback after local-operation acceptance/scheduling and refreshed visible dates/bars. Autosave runs concurrently and is still awaited before the next command or normal close. The test verifies the displayed date/bar for every sample. plan-frames.jsonl retains individual outcomes; plan-measurement.json reports median/max and whether all samples meet 200 ms. Superseded, rejected, unloaded and missing frames are not successful samples. This is hosted-control frame timing, not physical display latency or scrolling FPS.

For ordinary-app review use `./scripts/Start-Evaluation.ps1 -NoBuild`. Its synthetic GitHub dates match the calculated schedule and it starts with zero unpublished tasks. Inspect the sheet and day/week/month Gantt, scroll to rows 500/1,000 and back, and check focus, arrows, row alignment and column access. Use a separate real-gh root for sandbox Project 3.

## Version scroll responsiveness (#124)

The same `PlanSheetPerformance` selection also runs `VersionRowsScrollRoundTripRecordsFrameIntervalsAndViewportPopulation`. It uses the offline `EvaluationFixture` version `2027.04` at its default status date: 40 requirement rows and 1,000 task rows, with the published schedule and progress. The hosted window is resized to 1920 × 1080 physical pixels. Record the actual client area and rasterization scale; use the same idle Windows machine, display scaling, Release configuration and harness for both sides of a comparison.

The PMO runs the harness on the baseline with only the test-harness changes applied, then on the candidate with the product changes. Keep the baseline product unchanged and retain both source diffs and binary hashes from the hosted runner. Use separate evidence directories, for example `TestResults/plan-performance/before-124` and `TestResults/plan-performance/after-124`, in the command above. A candidate-only measurement is not a before/after comparison. Agents must not launch the host or the ordinary app on the owner's desktop.

Export the candidate's scroll case together with the baseline fixture's minimal setup change. Copying these two complete UTF-8 files avoids patch-context differences between LF and CRLF checkouts; the export does not copy product code or change baseline behavior assertions. Run from the candidate checkout, using a fresh output directory and baseline worktree:

~~~powershell
./scripts/Export-PlanScrollHarness.ps1 -Baseline 05fe629 -OutputPath ../plan-scroll-harness
git worktree add --detach ../plan-scroll-baseline 05fe629
$suite = 'tests/GhProjectsBoards.UiIntegration.Tests'
foreach ($file in 'PlanSheetHostedTests.cs', 'PlanSheetScrollPerformanceTests.cs') {
    Copy-Item -LiteralPath "../plan-scroll-harness/$suite/$file" -Destination "../plan-scroll-baseline/$suite/$file"
}
git -C ../plan-scroll-baseline diff --stat
dotnet build ../plan-scroll-baseline/GhProjectsBoards.sln -c Release
~~~

Retain `plan-scroll-harness/manifest.json` with the measurements. Its baseline commit and file hashes identify the exact test overlay; `PlanSheetScrollPerformanceTests.cs` is identical on both sides. The baseline fixture differs only in being partial and selecting the 1,040-row version fixture for the new case. Inspect the baseline diff before measuring. The harness locates the ScrollViewer hosting the list's ItemsPresenter, excluding the nested TextBox scroll regions.

For the scroll case alone:

~~~powershell
$env:GHPB_PLAN_EVIDENCE = Join-Path (Get-Location) 'TestResults/plan-performance/after-124'
./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'method == VersionRowsScrollRoundTripRecordsFrameIntervalsAndViewportPopulation' -TimeoutSeconds 300
Remove-Item Env:\GHPB_PLAN_EVIDENCE
~~~

`plan-scroll.json` is written beside `plan-measurement.json` when the full category runs. The case scrolls vertically top → bottom → top in 560-effective-pixel steps (20 rows), then across the Gantt and back in 240-effective-pixel steps, including each exact endpoint. Each request waits for a rendered frame and its requested offset, clamped to the current native scrollable extent, without waiting for row population to catch up. Virtualization can correct its estimated extent at the last row; initial, per-frame and final extents remain in the evidence. Rendering work, row-population inspection and dispatcher overhead are included equally on both builds. There is no agreed scroll threshold, so the case reports timing without asserting a performance target. Timeouts fail the case and retain partial samples and the exception.

| Field | Meaning |
| --- | --- |
| `schemaVersion`, `version`, `rows`, `statusDate` | Evidence format and exact version fixture context |
| `outcome`, `failure` | Completed run or incomplete/failed attempt; never treat partial output as a successful round trip |
| `windowPixels`, `clientWidth`, `clientHeight`, `scale`, `environment` | Requested physical window dimensions, actual logical client dimensions, rasterization scale, OS, runtime, process architecture and processor count |
| `verticalStep`, `horizontalStep`, `verticalExtent`, `horizontalExtent` | Step sizes and measured scrollable extents in effective pixels |
| `frameCount`, `longFrameThresholdMs`, `longFrames`, `unpopulatedFrames`, `maxFrameMs` | Total sampled frames, strict >50 ms interval count, frames with missing/stale viewport rows, and maximum interval |
| `frames[].ElapsedMs`, `IntervalMs` | Stopwatch time since sampling started and interval between `CompositionTarget.Rendered` callbacks (first interval starts at sampling start) |
| `frames[].Phase`, `RequestedOffset`, `VerticalOffset`, `HorizontalOffset` | Round-trip phase and requested/observed scroll positions |
| `frames[].VerticalExtent`, `HorizontalExtent`, `finalPosition` | Native extent corrections and the final observed position (schema version 3) |
| `frames[].UnpopulatedRows` | One-based visible slots lacking a loaded row with the expected identity/title and viewport position (2 effective-pixel rounding tolerance); includes the append slot when visible |
| `maxRealizedRows`, `frames[].RealizedRows` | Loaded row population, to distinguish bounded viewport caching from accumulation as the scroll moves deeper (schema version 2) |

Population detection compares expected slots from the current pixel offset and the sheet's fixed row height, including partly visible rows, against real containers. This detects unrealized slots and recycled/stale rows, rather than counting only the rows that happened to load. It is a hosted XAML diagnostic, not pixel analysis: compositor artifacts, occlusion and physical-display flicker still require the PMO's 1920 × 1080 screen capture. Report those observations alongside both scroll JSON files and the unchanged edit-to-screen measurement. A remaining rendering bottleneck informs a separate cell-renderer decision; the harness does not authorize that redesign.

For the ordinary-app acceptance recording, type `PS-00` into the title filter on the same version fixture, checking that each character appears while typing and that the results and count change after the pause. Check Enter, clearing, and rejected cell input without a vertical sheet shift. Record vertical and Gantt scrolling plus the project/column flyouts and zoom dropdown with Windows animations enabled; inspect native control feedback as well as the app's transition settings. Repeat with the same window size and display scaling as the frame evidence. A hosted property assertion or a successful build does not establish animation-free physical output.

## Interpreting responsiveness evidence (#154–#158)

Run the bounded version comparison with `./scripts/Test-PlanPerformance.ps1 -NoBuild` after a Release build. It makes five independent scroll attempts, runs version-data hosted edit/filter diagnostics and then the ordinary executable through the offline fake endpoint. It continues after a failed scroll attempt and reports an overall failure when any required measurement cannot complete. Raw frame results, source changes, binary hashes and failures remain under `TestResults/plan-performance`, `TestResults/ui-integration` and `TestResults/e2e`.

The `plan-responsiveness` workflow runs when this harness changes, or on demand with a baseline revision. A pull request compares against its exact base commit. It overlays only identical measurement files and the version-fixture setup onto the baseline, rejects product-source changes there, builds and measures each side serially on one 1920 × 1080 Windows runner, then runs the affected hosted regressions and synthesized wheel/menu input checks. `harness-manifest.json` and `harness-overlay.patch` identify the overlay. The runner is not the PMO's desktop.

The ordinary-app case records native wheel scrolling in both directions, then edits row 144 six times and captures the End-date text region on a separate thread. It saves every capture timestamp and each distinct PNG after recording, validates the changed date independently through UI Automation, and reports the first frame matching the final stable pixels with its preceding frame. Title typing at 200 and 700 ms intervals records each native key's injection timestamp and verifies the final text and result count; scroll, filter and menu image sequences require visual inspection. The menu case records Windows client-area animation state; it may enable and restore that setting only on the isolated GitHub runner. Local runs require it to be enabled already.

Hosted product-stage timestamps are cumulative from operation start. Repeated stage names represent the latest occurrence before the final rendered callback; do not sum them as exclusive durations. `accepted` includes synchronous operation/history validation, while persistence can continue in the background. Retain realized-row counts with the timings.

Keep UI-thread work, hosted rendered frames and ordinary-app screen capture as separate boundaries. A `CompositionTarget.Rendered` callback does not prove that the operator has seen the new pixels. A screen recording must retain the input timestamp, individual frame timestamps, capture interval and resulting displayed values; report the uncertainty of the capture cadence. A GitHub runner comparison is evidence for that runner, not certification of the PMO's physical display.

The 100 ms typed-character and 200 ms edit-to-screen goals remain reference goals on an idle 1920 × 1080 PMO machine. Report every sample, median, maximum and missed goals rather than making a slower shared runner's elapsed time a universal gate. Enter bypasses the filter's 300 ms debounce; it does not promise that arbitrary matching and rendering work completes synchronously. Evaluate fast typing, pauses longer than the debounce, Enter and clearing separately. A faster average does not excuse lost text, stale results, an unresponsive window or blank/stale rows.

Use one unchanged baseline and one candidate with the identical harness and fixture. Run five complete scroll attempts serially, retaining partial and failed attempts with source provenance. Compare both directions, dense-dependency sections, long frames, visible-row population and maximum realized rows. Preserve edit-to-screen measurements separately. Reconsider a numeric target only with the measured stage costs and ordinary-app result; do not remove editing, IME, Undo or accessibility to satisfy a timing number.

## Refresh and publish (#79)

Use the product publisher measurement command and source/environment/artifact requirements in the [test policy](../tests/README.md#live-github). The baseline sandbox measurements, batch limits and targets are in [decisions](decisions.md#throughput-targets-for-79). Core throughput, hosted rendering and ordinary-desktop responsiveness are separate observations. Preserve failed attempts and report unavailable measurements explicitly.

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

`plan-scroll.json` is written beside `plan-measurement.json` when the full category runs. The case scrolls vertically top → bottom → top in 560-effective-pixel steps (20 rows), then across the Gantt and back in 240-effective-pixel steps, including each exact endpoint. Each request waits for a rendered frame and its requested offset, without waiting for row population to catch up. Rendering work, row-population inspection and dispatcher overhead are included equally on both builds. There is no agreed scroll threshold, so the case reports timing without asserting a performance target. Timeouts fail the case and retain partial samples and the exception.

| Field | Meaning |
| --- | --- |
| `schemaVersion`, `version`, `rows`, `statusDate` | Evidence format and exact version fixture context |
| `outcome`, `failure` | Completed run or incomplete/failed attempt; never treat partial output as a successful round trip |
| `windowPixels`, `clientWidth`, `clientHeight`, `scale`, `environment` | Requested physical window dimensions, actual logical client dimensions, rasterization scale, OS, runtime, process architecture and processor count |
| `verticalStep`, `horizontalStep`, `verticalExtent`, `horizontalExtent` | Step sizes and measured scrollable extents in effective pixels |
| `frameCount`, `longFrameThresholdMs`, `longFrames`, `unpopulatedFrames`, `maxFrameMs` | Total sampled frames, strict >50 ms interval count, frames with missing/stale viewport rows, and maximum interval |
| `frames[].ElapsedMs`, `IntervalMs` | Stopwatch time since sampling started and interval between `CompositionTarget.Rendered` callbacks (first interval starts at sampling start) |
| `frames[].Phase`, `RequestedOffset`, `VerticalOffset`, `HorizontalOffset` | Round-trip phase and requested/observed scroll positions |
| `frames[].UnpopulatedRows` | One-based visible slots lacking a loaded row with the expected identity/title and viewport position (2 effective-pixel rounding tolerance); includes the append slot when visible |

Population detection compares expected slots from the current pixel offset and the sheet's fixed row height, including partly visible rows, against real containers. This detects unrealized slots and recycled/stale rows, rather than counting only the rows that happened to load. It is a hosted XAML diagnostic, not pixel analysis: compositor artifacts, occlusion and physical-display flicker still require the PMO's 1920 × 1080 screen capture. Report those observations alongside both scroll JSON files and the unchanged edit-to-screen measurement. A remaining rendering bottleneck informs a separate cell-renderer decision; the harness does not authorize that redesign.

For the ordinary-app acceptance recording, type `PS-00` into the title filter on the same version fixture, checking that each character appears while typing and that the results and count change after the pause. Check Enter, clearing, and rejected cell input without a vertical sheet shift. Record vertical and Gantt scrolling plus the project/column flyouts and zoom dropdown with Windows animations enabled; inspect native control feedback as well as the app's transition settings. Repeat with the same window size and display scaling as the frame evidence. A hosted property assertion or a successful build does not establish animation-free physical output.

## Refresh and publish (#79)

Use the product publisher measurement command and source/environment/artifact requirements in the [test policy](../tests/README.md#live-github). The baseline sandbox measurements, batch limits and targets are in [decisions](decisions.md#throughput-targets-for-79). Core throughput, hosted rendering and ordinary-desktop responsiveness are separate observations. Preserve failed attempts and report unavailable measurements explicitly.

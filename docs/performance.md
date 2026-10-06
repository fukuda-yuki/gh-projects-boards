# Performance measurement

## Current plan sheet (#78)

Run the prepared Release build serially, without another build/test workload. Use a new evidence directory per run.

~~~powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\GhProjectsBoards.sln -c Release --no-restore
$env:GHPB_PLAN_EVIDENCE = 'C:\w\g76\TestResults\phase6\measurement-05'
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanSheetPerformance' -TimeoutSeconds 300
Remove-Item Env:\GHPB_PLAN_EVIDENCE
~~~

The fixture has 1,000 tasks, 20 people and ten-task finish-to-start chains. Twenty edits alternate row 1 Remaining between 8 and 16 hours. Each sample starts at the cell commit and ends at the next CompositionTarget.Rendered callback after local-operation acceptance/scheduling and refreshed visible dates/bars. Autosave runs concurrently and is still awaited before the next command or normal close. The test verifies the displayed date/bar for every sample. plan-frames.jsonl retains individual outcomes; plan-measurement.json reports median/max and whether all samples meet 200 ms. Superseded, rejected, unloaded and missing frames are not successful samples. This is hosted-control frame timing, not physical display latency or scrolling FPS.

For ordinary-app review use `C:\w\g76\scripts\Start-Evaluation.ps1 -NoBuild`. Its synthetic GitHub dates match the calculated schedule and it starts with zero unpublished tasks. Inspect the sheet and day/week/month Gantt, scroll to rows 500/1,000 and back, and check focus, arrows, row alignment and column access. Use a separate real-gh root for sandbox Project 3.

## Refresh and publish (#79)

Use the product publisher measurement command and source/environment/artifact requirements in the [test policy](../tests/README.md#planning-editor-publication-79). The baseline sandbox measurements, batch limits and targets are in [decisions](decisions.md#throughput-targets-for-79). Core throughput, hosted rendering and ordinary-desktop responsiveness are separate observations. Preserve failed attempts and report unavailable measurements explicitly.

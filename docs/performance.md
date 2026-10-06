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

The run also creates synthetic-1000, containing fake-gh remote data and a current PlanStore document. For ordinary-app screenshot and physical interaction review, replace the evidence directory below with the actual run directory:

~~~powershell
$env:GH_CONFIG_DIR = 'C:\w\g76\TestResults\phase6\measurement-05\synthetic-1000'
$env:GHPB_DATA_ROOT = "$env:GH_CONFIG_DIR\data"
$env:GHPB_PLAN_METRICS = 'C:\w\g76\TestResults\phase6\ordinary-plan-frames.jsonl'
& C:\w\g76\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe
~~~

In the ordinary connection page, set gh.exe to C:\w\g76\tests\GhProjectsBoards.Tests\bin\Release\net10.0-windows\GhProjectsBoards.Tests.exe, connect, and open 開発計画. Use the sheet and each day/week/month zoom, scroll to rows 500/1,000 and back, and inspect focus, cell values, arrows, row alignment and column access. Change row 1 Remaining to 16; dates and bars update through the real plan scheduler. Restore the shell environment variables after closing. These endpoints are synthetic; use a separate data root and real gh for authorized Project 3 review.

## Refresh and publish (#79)

Use the product publisher measurement command and source/environment/artifact requirements in the [test policy](../tests/README.md#planning-editor-publication-79). The baseline sandbox measurements, batch limits and targets are in [decisions](decisions.md#throughput-targets-for-79). Core throughput, hosted rendering and ordinary-desktop responsiveness are separate observations. Preserve failed attempts and report unavailable measurements explicitly.

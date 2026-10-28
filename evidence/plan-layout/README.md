# Issue #328 execution evidence

Before: `d25de2fc5c9db7ce35523cfbfbd6bc03999591d1` (fetched origin/main, rechecked after the fix).
After: `236c94c0537b4cb0e84d0280bfe7ed0cbe0666d1`.
App DLL SHA-256: `72BB385B1080893567664FDF2FF7EF07D4B138628AAC8219C57E0187D2066C3B`.

## Ordinary app

Windows 10.0.26300 desktop, .NET SDK 10.0.401, Release ordinary executable launched with `scripts/Start-Evaluation.ps1 -NoBuild` (then `-Resume` with the same isolated data root). Offline fake gh; 40 requirements, 1,000 tasks, 20 people; status date 2027-01-13. No live GitHub Project writes. Screenshots are unedited JPEG captures. The original `before-default.png` was saved with an incorrect extension; `before-default.jpg` contains exactly the same bytes.

- Before: Gantt checkbox overlaps Insert at initial size and maximized. `before-default.jpg` records the initial-size defect.
- After: `after-default.jpg` and `after-default-hidden.jpg` show separate controls at 1267 x 794 captured effective size, with Gantt shown/hidden. Pointer toggling and focused Space restore the chart without changing 1,000 tasks or unpublished 0.
- `after-maximized.jpg`: separate controls at 2752 x 1104 captured effective size.
- `after-insert.jpg`: clicking Insert adds a new editable row, task count 1,001, unpublished 1. Empty title is visibly marked. Escape then Ctrl+Z restores task count 1,000, unpublished 0 and disables Undo; no chart toggle occurs.
- `after-detail.jpg`: detail pane open after Undo; controls remain separate and task count is 1,000, unpublished 0.
- `after-default-detail.jpg`: detail pane at initial size; row commands use overflow, and the table can horizontally scroll while preserving the chart minimum width.

Exact 1920 x 1032 ordinary-window acceptance remains unverified: native window-size adjustment did not reliably reach that size. The 1920-wide hosted regression is separate evidence. No merge/owner acceptance is claimed.

## Build and targeted hosted UI validation

`dotnet build GhProjectsBoards.sln -c Release --no-restore`: success, 0 warnings, 0 errors (`build-final.log`).

Final command:

```powershell
./scripts/Test-UiIntegration.ps1 -Where 'cat == CommandLayout or cat == DisplaySettings or test =~ SelectionBandKeeps or test =~ Overflow or test =~ OpenDetailPaneKeepsTheGanttMinimum' -TimeoutSeconds 240
```

`ui-runs.zip` preserves raw results, metadata, source snapshots and host logs. Tests ran before the final commit; metadata/source hashes identify the working-tree content subsequently committed.

| Run suffix | Executed / passed / failed / skipped | Meaning |
| --- | --- | --- |
| 154652-483-4df95fb2 | 4 / 0 / 4 / 0 | Behavioral Red: overlapping Insert and Gantt targets before production fix |
| 154730-487-cd2b6768 | 19 / 13 / 6 / 0 | Initial attempt; hit-test/layout synchronization and overflow-menu reopen timing failed |
| 155009-723-648c7586 | 4 / 0 / 4 / 0 | Hit-test layout synchronization still unresolved |
| 155105-591-41c2f2ac | 4 / 4 / 0 / 0 | Geometry and hit-test checks after waiting for settled layout |
| 155311-260-2b93939c | 20 / 19 / 1 / 0 | Existing overflow-delete menu reopen timeout |
| 155517-529-780119f3 | 1 / 0 / 1 / 0 | Isolated reopen failure |
| 155632-512-46367635 | 1 / 1 / 0 / 0 | Isolated rerun; did not by itself establish stability |
| 155703-074-fdb14192 | 20 / 20 / 0 / 0 | Final: public menu Collapsed state awaited before reopening; host exit 0 |

The final selection covers real WinUI geometry/hit targets at 640/900/1280/1920 widths, Gantt display preferences, selection band, overflow operations and detail-pane layout. Native overflow tests intentionally generate input. This is a targeted run, not the full UI/Core/E2E suite. Live GitHub, physical IME, dark/high-contrast review and human acceptance were not run for this local layout fix.

# Manual image capture (#172)

The Japanese [user manual](../user-manual.md) is the single shipped source. All images are uncropped PNG captures of the ordinary Release app, using the offline evaluation fixture. Do not draw, crop or replace images by hand. Image filenames follow manual reading order; capture execution may visit the states in another order.

## Rebuild every image

From the repository root in PowerShell 7 on an unlocked Windows desktop, after dependencies have been restored:

```powershell
./scripts/Capture-ManualImages.ps1
```

This builds Release with `--no-restore -m:1 -nr:false`, prepares one fresh data root for the run, launches the ordinary executable against fake gh, captures every image and closes that process. `-NoBuild` uses an already built Release executable. Do not operate the desktop during capture. No display resolution, scaling, theme or account setting is changed. The window is sized by `SetWindowPos` to 1600 × 1000 effective pixels using its actual DPI; physical PNG dimensions vary with display scaling. The existing `shot` action captures the complete app window. The automation uses UI Automation, the keyboard and the mouse wheel; keep the application unobscured while pointer actions run. A blocked desktop or inaccessible control fails the run instead of changing machine settings.

The fixture uses version `2027.04`, status date `2027-01-13` and read day `2027-01-14`. It starts with zero unpublished tasks. Refresh therefore demonstrates the next read day without depending on the machine clock. A local edit of R09 SS-003 creates a real unpublished review. No publish confirmation, GitHub connection, fixture-file edit or external browser launch is needed for capture.

Images are first staged in `TestResults/manual-images/<run>/images` and copied to `docs/manual/images` only after all captures succeed. The script stops on an unavailable control or state; it never substitutes an older PNG. `record.json` includes the invocation, data root, commit and dirty state, source/binary hashes, DPI/window size, screenshot hashes, completion/failure and app-close result. Retain this record with Issue evidence. A hash is capture provenance, not visual acceptance.

After capture, inspect every image against its cited step, run the manual link tests, and rebuild the app so the new PNGs are copied beside `マニュアル.md`. Keep the generated PNGs in Git. An empty `images/.gitkeep` is only a directory marker: missing captures must fail the manual-image tests and release validation. Do not make placeholder PNGs to satisfy them.

## Capture plan

All interaction uses `scripts/Invoke-EvaluationUi.ps1` (`click`, `invoke`, `type`, `key`, `scroll`, `select`, `setvalue`); state checks use its `tree` output, and every capture uses `shot -Out`. IDs come from `PlanWorkspaceView`, `PlanSheetView`, `PlanSheetRow`, `PlanSummaryView` and `PlanSheetCommandMenu`. Settings and summary are scrolled with the mouse wheel until the named control is near the top; no data-root files are changed. Popup menus are not captured because the window capture omits them.

| Image | State and UI operation | Manual section |
| --- | --- | --- |
| `01-help.png` | Fresh opened plan; open `PlanProjectPicker`, Escape, then Tab to the adjacent `PlanHelp` question button; capture without invoking the external viewer | はじめに |
| `02-connection.png` | Initial `PlanGhPath`, `PlanHost`, `PlanConnect` before connecting | 起動と接続 |
| `03-project-chooser.png` | Invoke `PlanConnect`; wait for `AvailableProjects` and 第2027.04版 | プロジェクトを開く |
| `04-plan.png` | Click the exact 第2027.04版 title; wait for `PlanTasks`, zero unpublished tasks and the Gantt | 画面の見方 |
| `05-selection.png` | Set `PlanSheetFilter` to R09 SS-003; select that row's Title cell; read the reason line | 画面の見方 / シートとガント |
| `06-states.png` | After the Actual/Remaining edit and refresh, filter R09; selected row, state markers, changed dates and Gantt lateness | 画面の見方 / シートとガント |
| `07-effort-edit.png` | On R09 SS-003, set Actual 24 and Remaining 32 using F2, ValuePattern and Enter (independent of IME mode); retain the selection | 毎週の更新 |
| `08-refresh.png` | Invoke `PlanRefresh`; wait for `PlanRefreshResult` and the completed next-day refresh | 最新の情報に更新 |
| `09-publish.png` | Invoke `PlanPublish`; wait for `PlanPublishLines` and `PlanPublishConfirm`; capture, then `PlanPublishClose` | 発行と競合の確認 |
| `10-summary.png` | Clear the filter, select `PlanShowSummary`, wait for `SummaryFinish` | サマリーと担当者の確認 |
| `11-people.png` | Wheel-scroll the summary until `SummaryMode` is near the top; choose 月別 using Home, Down, Enter | サマリーと担当者の確認 / 担当者ごとに見る |
| `12-period.png` | Open `PlanShowSettings`; wheel-scroll until `PlanProjectStart` is near the top, showing start and end | 設定 / 期間 |
| `13-milestones.png` | Wheel-scroll until `PlanPhaseMilestone_SA` is near the top, exposing the phase mappings | 設定 / 工程とマイルストーン |
| `14-calendar.png` | Wheel-scroll settings until `PlanCompanyDaysOffDate` is near the top | 設定 / カレンダー |
| `15-columns.png` | Reopen settings from the plan and wheel-scroll until `PlanColumnReset` is near the top (the overflow route opens a popup the window capture omits) | 設定 / 表示列 |

The review capture illustrates real local differences, not a fabricated conflict. Conflict recovery remains documented; the offline fixture has no ordinary UI action for simulating a concurrent remote edit. UI integration tests own that wiring evidence. The initial period/milestone derivation is described from the first-open contract; the evaluation fixture already seeds its period.

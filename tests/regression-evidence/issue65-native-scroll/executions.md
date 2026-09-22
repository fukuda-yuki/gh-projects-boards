# Scoped execution ledger

All product/test sources for the correction are in `94922ffd17489b464bf8f23376e702ad1ef8600f`. Later evidence edits do not change them. Windows 10.0.26200, .NET SDK 10.0.401, x64, 125% display scale; installed WinUI 1.8 is pinned by the repository. The source worktree is `C:\Users\mwam0\.copilot\repos\gh-projects-boards`. The sustained driver/producer run from the preserved `C:\Users\mwam0\.codex\worktrees\i65-residual` checkout.

## Executable identity

The frozen candidate `TestResults/issue65-native-preflight/trial-app/GhProjectsBoards.App.exe` was copied from the successful initial candidate UI-host build. Production file contents are those committed as `94922ff`; compilation preceded that commit. Its App.dll SHA256 is `DEEB7F5387AD67C84E6476890738597740269E6CD5384B03201A3FB012AC7470`. The first UI execution retains the exact uncommitted production diff. All seven unprofiled candidate diagnostic runs use this immutable executable, not an executable rebuilt between repetitions.

The later ordinary-app native/process tests rebuilt at committed `94922ff`, with App.dll SHA256 `B80A0D377A08B25C21ABDE31F856D035364635824E18DBA5F49882CF657A81A4`. These are distinct binaries, with the same production source content; no binary equivalence is inferred from the source SHA. Each runner retains its actual binary hashes and source/command metadata. Baseline hashes were checked against the preserved residual run before native capture.

## Real-control UI integration

Run paths are under `TestResults/ui-integration/` in the source worktree. Real views, TextBoxes, ScrollViewers, event paths, workspace and isolated store collaborate. No live GitHub.

| Run | Executed / passed / failed / skipped | Boundary |
| --- | --- | --- |
| `run-20260922-151617-567-38d63a42` | 2 / 2 / 0 / 0 | Initial correction: existing focused/pending scroll roundtrips. Before adding the long-title test. |
| `run-20260922-151920-517-abf4ea18` | 3 / 3 / 0 / 0 | Same production correction plus new long-title caret/internal-scroll preservation. |
| `run-20260922-153114-950-a9bbaf44` | 12 / 12 / 0 / 0 | Final scoped selection: three text/scroll cases, four Light/Dark/theme-pending cases, drag/cancel and range navigation, Gantt pending-editor Save, Gantt timing, original retention guard. |

Do not sum overlapping selections as distinct coverage. The final command was:

```powershell
./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'class == GhProjectsBoards.UiIntegration.Tests.SheetFocusedScrollHostedTests or class == GhProjectsBoards.UiIntegration.Tests.ThemeHostedTests or name == TaskDetailSavePreservesThePendingBoardsEditorAndCaret or name == ThousandTaskReplanPublishesExpectedBarAndReportsCalculationAndSaveSeparately or cat == ReviewRetention or name == BodyDragSelectsAndEscCancelsFillWithoutUndoingTheSource or name == ShiftArrowsExtendTheSameColumnAcrossTheViewportWithoutLosingTheAnchor' -TimeoutSeconds 300
```

Gantt: two warmups, ten samples, all independently expected endpoint counts 29. Public Save through matching text/dialog close plus two rendering events: maximum **279.7578 ms**, scoped <=1,000 ms PASS. Separate complete checkpoint saving: maximum **806.9167 ms**. Neither is pixel scanout; this is a regression check, not a matched Gantt speedup experiment. Original 80-roundtrip retention probe: **4 <=65**, scoped PASS. No new claim about historical 1,298.9222 ms or 78/78 outliers; progression ownership investigation was not repeated.

## Ordinary native/process boundary

`TestResults/e2e/20260922-153244-35d8d78f164f47758f4f4529827c44bf/e2e.trx`: **3 executed / 3 passed / 0 failed / 0 skipped**.

```powershell
./scripts/Test-E2E.ps1 -Filter 'FullyQualifiedName~OrdinaryGanttThousandTaskEditProjectSwitchAndRestartRetainOnePlan|(FullyQualifiedName~ProjectViewSwitchDoesNotEndNativeCompositionOrCommitTheCell&Name~Gantt)'
```

One ordinary-app 1,000-task Gantt/edit/Project-switch/restart journey verifies the real isolated checkpoint and normal process lifetime. Two physical direct/F2 Japanese composition cases verify native composition across Gantt switching. The external endpoint is isolated fake gh. This is not live GitHub, human IME acceptance, or execution of the entire IME suite. The build succeeded with zero errors and the two existing CS0436 UI-host initializer warnings.

## Sustained input and scrolling

Each run is one ordinary-app local diagnostic with **1 executed / 1 passed / 0 failed / 0 skipped** for state/persistence assertions. Performance FAIL remains separate. Raw paths: `i65-residual/TestResults/issue61-65/<run>/`. The final six have complete app traces. Commands and hashes are in each environment/plan record.

```powershell
./scripts/Test-SustainedInput.ps1 -Executable '<frozen absolute executable>' -SourceRevision '<product source>' -RunId '<unique run>' -Condition cold -Mode standard -TraceDetail light
./scripts/Measure-SustainedInput.ps1 -Observations '<run>/observations'
```

Warm changes only `-Condition warm`, retaining the existing extra ten-second title phase. All runs keep the 20-second title, 20-second NUMBER, 20-second alternating native scroll schedule, synthetic fixture and viewport. The retained producer/driver HEAD differs from candidate source; it is recorded rather than relabeled. Exact repeat sequence is cold-01, warm-01, cold-02, warm-02, cold-03, warm-03. No run was replaced for a better timing.

| Run suffix (prefix `native-`) | Title / NUMBER p95 ms | Max callback gap ms | >=100 ms gaps | Corroborated delayed commands | Capture max ms |
| --- | --- | --- | --- | --- | --- |
| baseline-short-01 | 48.6410 / 49.4168 | 247.6988 | 20 | 5 | 79.6328 |
| candidate-short-01 | 47.2067 / 46.6765 | 162.9851 | 6 | 1 | 80.1598 |
| final-cold-01 | 49.1821 / 46.6109 | 189.3126 | 4 | 1 | 80.5577 |
| final-warm-01 | 47.2984 / 46.9576 | 148.9955 | 5 | 2 | 79.9303 |
| final-cold-02 | 45.9827 / 47.1898 | 151.9441 | 4 | 2 | 93.6933 |
| final-warm-02 | 31.3813 / 46.3880 | 203.1784 | 6 | 2 | 80.0862 |
| final-cold-03 | 49.2361 / 50.8400 | 213.6853 | 12 | 2 | **281.8219** |
| final-warm-03 | 48.2643 / 48.8962 | 131.2619 | 5 | 1 | 83.2645 |

All six final scroll results FAIL. Their 36 >=100 ms intervals are individually retained in `native-scroll-intervals.json`; eight of ten corroborated delayed commands follow the final durable save. Six are new destinations (command 12 in each run), two are requested returns (cold-03 command 31 and warm-02 command 91). Requested return is a command-history classification, not native cache proof. Seventeen visible delays have incomplete application attribution. Cold-03 has four capture gaps >=100 ms and two commands with no changed capture. Most remaining command capture brackets straddle 100 ms and cannot be classified as a visual pass or failure.

## Attribution, failed attempts and omissions

`native-attribution-cold-01` uses the baseline under elevated WPR, while the ordinary app stays non-elevated: state 1/1 PASS, scrolling FAIL, max callback 296.8798 ms, capture max 586.0076 ms. It is not a pooled acceptance sample. Native record: `TestResults/issue65-native-preflight/capture-20260922-150303/`; zero ETW events lost, valid QPC alignment, exact XAML module/PDB identities, partial symbol resolution. Full ETL and ETLX are private. The public subset contains only synthetic-app attribution for the selected interval.

Retained failed prerequisites: non-elevated WPR policy failure, raw XAML access denial, candidate UAC cancellation (no recording). The first offline decoder attempt failed to serialize an infinite layout constraint; it was fixed to preserve non-finite values as named strings and rerun against the unchanged ETL. This is an analysis-tool failure, not a product failure or a second acquisition. Out-of-range/unmatched symbols remain marked. The installed winapp command was unavailable; installed pinned templates, repository build/host runners and raw native events provided the relevant supported paths without installing a tool.

Core behavior was unchanged, so no extra Core regression campaign was run. High Contrast, other DPI, full input matrix, live service, release environments and human acceptance were not executed. Candidate native profiling was cancelled before start; candidate residual native attribution is incomplete, not proven impossible due to missing WPR or permissions. No remote or main-integration acceptance follows from this ledger.

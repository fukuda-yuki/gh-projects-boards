# Issue #73 product refinement evidence

This package retains the observed local results for the eight authorized roadmap items and the screenshot-based user manual. It is a **local validation record**, not an Issue closure or human acceptance claim. It includes the clean-HEAD lifetime comparison, weekly recovery Core Red/Green, all completed E2E attempts through `e2e-185441`, manual restart/connection observations, and the review-layout Red/Green. Two distinct ordinary-app journeys passed: holiday import on v3 at 18:37 and weekly bulk/publication on the final v4 candidate at 18:54. The v4 screenshot confirms the auxiliary-button clipping is fixed. Earlier v3 results remain identified by their actual candidate; the pre-existing lifetime failure, unrun live/performance checks and human acceptance remain separate.

## Source and environment

- Base commit: `654930a3f7a8810695a806a13a86f3c9d28f9ed4`.
- Implementation and manual were committed locally as `7ef475fd8c04f57c5ed5b664771411997c97a5b9` before this evidence closeout. Raw run receipts remain unchanged: they truthfully identify the earlier base plus dirty source, not that later commit. The user's `App.xaml.cs` change is excluded from the implementation commit.
- Branch: `codex/issue-73-pmo-polish`, with uncommitted implementation and test changes. Each UI run's original `metadata.json` records its dirty files, command, test source hashes, binary hashes, start time, and process result. Different runs used different intermediate binaries; the base SHA alone does not identify the tested candidate. For the prebuilt 18:19 comparison, use its `compiled-source-receipt.json` as the compiled-source authority: runner source capture describes the restored working directory, not that comparison binary.
- The pre-existing `src/GhProjectsBoards.App/App.xaml.cs` crash-diagnostic change was preserved and compiled into the tested app. It is excluded from the requested delivery changes. These results do not establish a clean-source reproduction with that change removed.
- Host recorded by the UI runner: Microsoft Windows NT `10.0.26200.0`, .NET SDK `10.0.401`; Release WinUI app and test host. Core tests use .NET 10; the ordinary app and hosted controls use the native Windows desktop.
- Core and hosted UI cases use deterministic fixtures and isolated local storage. The ordinary-app manual session uses synthetic Project `P1` and synthetic tasks/people. The five live GitHub cases were not executed.
- Test count summaries are derived from actual case records, not from filter names, project names, discovery, or prior prose. Overlapping runs must not be added together as unique coverage.
- The clean-HEAD lifetime comparison used a separate clean checkout of the base commit with the unchanged original test. It establishes that the retained-view failure is pre-existing; it does not make that failure a pass or establish whole-application lifetime acceptance.

## Acceptance map

| Item | Authorized behavior | Retained evidence and current limit |
| --- | --- | --- |
| P0-1 | Concise normal surfaces; supplementary help on demand; actionable errors and uncertainty remain visible | `KeyboardHelpDisclosesBulkEditingWithoutIdleInstructionParagraphs` passed in the 17:43 UI run. Connection layout passed at widths 1280 and 960; ordinary default-size 1266x794 recapture shows it fully visible. Corrected review controls passed bounds checks at two window heights and are fully visible in the final v4 ordinary review screenshot. v4 also shortens the unselected Gantt status to `タスク未選択`. |
| P0-2 | Weekly Actual/Remaining rectangles, Ctrl+D and fill; one reporting date, per-target worker, atomic validation and one Undo | Core bulk Red 7/11, Green 49/49, stale-source Red 7/8, combined Green 57/57. Native weekly paste, rejected ambiguous worker, copy-down/fill and Undo passed in the 17:45 UI run. The final ordinary-app weekly journey passed: 1000 rows, 100-row/200-cell paste, Undo/re-paste, 2-row filter, normal restart, explicit approval and exact retained hidden work. |
| P0-3 | Known overload has a critical marker and color; incomplete comparison remains explicitly unknown | All 13 Summary cases passed in the 17:43 UI run, including Light/Dark markers and allowance/Remaining correction. The synthetic ordinary-app report and observation record show Owner overload of 0.625 person-days. Human acceptance is not run. |
| P1-1 | Move/resize complete current Manual bars by calendar days, retain time-of-day, preview/cancel/stale guards, one Undo | Core Red 8/12 and Green 27/27. All six `GanttDragHostedTests` cases passed in the 18:14 class-selected run: move/start/finish preview-commit-Undo and Escape/capture/revision interruption. The ordinary-app observation also records body drag, finish resize and Undo. Earlier category filters missed this fixture. The separate lifetime failure reproduces on clean HEAD with the unchanged original test and remains an existing defect. |
| P1-2 | Weekly Apply selects only changed Actual/Remaining in displayed existing rows; hidden rows need explicit inclusion; fresh review and final approval remain | Valid Core Red 1/4, Green 39/39, reconciliation Red 0/1, repaired Green 8/8; combined weekly coverage 57/57. History recovery failed Core 0/1 and UI 0/2, then the fix passed Core 9/9 and all four weekly UI cases. The final ordinary-app journey published exactly four displayed effort fields through fake-gh with independent readback, retaining the exact 196 hidden values, all 100 Actual reports, two Titles and the unrelated Estimate/Start/Finish changes. No live mutation is claimed. |
| P1-3 | Explicit Markdown clipboard report of dated cumulative totals, overload and contributing work; no invented weekly increment | Report Core Red 0/5 and Green 22/22. Native clipboard command passed in the 17:43 Summary run. The ordinary-app clipboard readback is retained as `manual-weekly-report.md`: 20 tasks, incomplete totals, unavailable weekly increment and unfilled mitigation fields. |
| P2-1 | Planning assumptions are directly available from the Project toolbar shared by Boards/Gantt/Summary | `PlanningIsDirectlyAvailableWithoutOpeningInfrastructureSettingsAndReturnsToWork` passed in the 17:43 UI run. The broader 18:02 planning run passed 83/84 planning cases, with the separately reproduced pre-existing lifetime failure explicitly retained. |
| P2-2 | Explicit official holiday CSV import, provenance and difference preview, adoption/save, preservation of protected planning work | Parser Core Red 8/10 and Green 10/10; all six real-control import/preview/Cancel/Save UI cases passed in the 17:45 run. The ordinary-app holiday journey passed at 18:27 and again at 18:37 with the strengthened visible-preview requirement: real native picker selection/cancellation, readable preview/provenance, explicit adoption/save and normal restart with retained work. |
| Manual | Detailed Japanese Markdown task guide with actual ordinary-app screenshots | [User manual](../../../docs/user-manual.md), [initial receipt](raw/polish-20261004/manual-receipt.json), [observation](raw/polish-20261004/manual-observation.json), [native clipboard report](raw/polish-20261004/manual-weekly-report.md), and [v3 final receipt](raw/polish-20261004/manual-final-receipt.json). All nine inspected images are present; image 08 matches the final v4 ordinary review capture. Synthetic agent observation is separate from live GitHub and human acceptance. |

## Executed Core results

The full [Core TRX](raw/polish-20261004/core-all/core-all.trx) contains **929 case records: 924 Passed, 0 Failed, 5 NotExecuted**. Its summary counters say `executed=924` and `notExecuted=0`, while the individual results and [console log](raw/polish-20261004/core-all.log) identify five skipped live cases. The individual outcomes are the authority for the five not-executed cases; they are not passes.

That full run predates the later weekly history recovery correction. The nine-case targeted Green below verifies that subsequent change; it is not a second full-suite execution.

Original full Core command, run from the repository root:

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=core-all.trx' --results-directory TestResults/polish-20261004/core-all
```

| Retained targeted result | Case records | Passed | Failed | Classification |
| --- | ---: | ---: | ---: | --- |
| `gantt-drag-red/gantt-red.trx` | 12 | 8 | 4 | Observed unimplemented drag-rule Red |
| `gantt-drag-green/gantt-green.trx` | 27 | 27 | 0 | Core schedule/related regression Green |
| `holiday-import-red/holiday-red.trx` | 10 | 8 | 2 | Observed unimplemented CSV-import Red |
| `holiday-import-green/holiday-green.trx` | 10 | 10 | 0 | Parser Green |
| `issue73-summary-red/weekly-report-red.trx` | 5 | 0 | 5 | Observed empty-report Red |
| `issue73-summary-green/weekly-report-green.trx` | 22 | 22 | 0 | Report/related Summary Green |
| `polish-20261004/bulk-tests/red/weekly-bulk-red.trx` | 11 | 7 | 4 | Actual-cell bulk editing rejected as read-only |
| `polish-20261004/bulk-tests/green/weekly-bulk-green.trx` | 49 | 49 | 0 | Weekly bulk/related regression Green |
| `polish-20261004/bulk-tests/source-red/weekly-source-and-apply.trx` | 8 | 7 | 1 | Stale source input was incorrectly accepted |
| `polish-20261004/bulk-tests/final-green/weekly-bulk-and-apply-green.trx` | 57 | 57 | 0 | Combined weekly regression Green |
| `polish-20261004/weekly-apply-red/weekly-apply-red.trx` | 4 | 0 | 4 | Fixture revision setup failed before the intended weekly assertions; not valid product Red |
| `polish-20261004/weekly-apply-red/weekly-apply-red-valid.trx` | 4 | 1 | 3 | Valid Red: unrelated fields were included in weekly operations |
| `polish-20261004/weekly-apply-green/weekly-apply-green.trx` | 39 | 39 | 0 | Weekly selection/payload Green |
| `weekly-decision-red/weekly-decision-red.trx` | 1 | 0 | 1 | External Actual reconciliation candidate was missing |
| `weekly-decision-green/weekly-decision-green.trx` | 8 | 7 | 1 | Test helper assumed a single value; `Sequence contains more than one element`; the directory name is not a passing result |
| `weekly-decision-green2/weekly-decision-green.trx` | 8 | 8 | 0 | Repaired reconciliation case Green |
| `polish-20261004/weekly-recovery-red/weekly-recovery-red.trx` | 1 | 0 | 1 | Historical continuation lost `WeeklyEffort` and therefore its field scope |
| `polish-20261004/weekly-recovery-green/weekly-recovery-green.trx` | 9 | 9 | 0 | Corrected historical weekly continuation and all selected weekly cases passed |

Paths in this table are relative to `raw/`. Individual TRX records retain source assemblies, test identities, times and failures. Only the full Core command above is included as a recorded shell invocation; targeted TRX files do not capture their original shell command lines. No command is reconstructed and presented as an original invocation.

The initial `weekly-apply-red.log` records a compilation failure (`PlanningContract.SameTask` was unavailable during concurrent implementation), so it provides **zero behavioral executions**. Its later failed fixture attempt and valid Red are preserved separately. Other build output, including the existing nullable warning in the Core test suite, is retained without converting warnings or failed attempts into successful behavior evidence.

## Executed UI integration results

These runs exercise real WinUI views, controls, event/binding paths and relevant native input/clipboard in a scoped host. Their exact commands, source captures and binary hashes are in each run's unmodified `metadata.json`, subject to the explicit compiled-source correction for 18:19 below. Each directory also retains `results.xml`, `stdout.log`, and `stderr.log`.

| Run under `raw/ui-integration/` | Case records | Passed | Failed | Meaning |
| --- | ---: | ---: | ---: | --- |
| [17:35](raw/ui-integration/run-20261004-173530-125-03fa612b/metadata.json) | 19 | 11 | 8 | Intermediate run: two unfinished workspace behaviors, four weekly interaction timeouts, and two Summary helper lookup failures. Retained as failed, not all classified as product Red. |
| [17:43](raw/ui-integration/run-20261004-174328-686-706a2c69/metadata.json) | 27 | 19 | 8 | Workspace 2/2, Summary 13/13 and WeeklyApply 2/2 passed. Six holiday helper lookups failed; two weekly copy/fill assertions incorrectly assumed task enumeration order. |
| [17:45](raw/ui-integration/run-20261004-174536-958-9149f5b9/metadata.json) | 10 | 10 | 0 | All four WeeklyBulk and six HolidayImport cases passed after helper/assertion correction. |
| [18:02](raw/ui-integration/run-20261004-180215-221-d2d3e0d5/metadata.json) | 86 | 85 | 1 | Connection layout 2/2 and planning 83/84. `NativeEditorRetentionProgressionAndViewTeardown` failed because the replaced view was still retained. |
| [18:05](raw/ui-integration/run-20261004-180554-831-e9654dc3/metadata.json) | 1 | 0 | 1 | Isolated reproduction of the retained-view lifetime failure. |
| [18:08](raw/ui-integration/run-20261004-180813-990-6e941c4d/metadata.json) | 1 | 0 | 1 | Another isolated attempt still failed the same lifetime assertion. |
| [18:11](raw/ui-integration/run-20261004-181115-993-e8653157/metadata.json) | 1 | 0 | 1 | Bounded comparison with `GanttView.cs` and `EditingGrid.Gantt.cs` temporarily restored to base also failed the same lifetime assertion; the drag implementation was subsequently restored. |
| [18:14](raw/ui-integration/run-20261004-181404-102-a6902599/metadata.json) | 9 | 6 | 3 | All six explicitly selected native Gantt drag cases passed. Both new weekly history recovery cases failed because the review reverted to ordinary Apply; the retained-view lifetime case also failed. |
| [18:19](raw/ui-integration/run-20261004-181919-419-825010aa/metadata.json) | 1 | 0 | 1 | Prebuilt comparison with only the new GridInputHelp construction removed still failed the same lifetime assertion. See the compiled-source receipt below. |
| [18:26](raw/ui-integration/run-20261004-182616-517-c0077309/metadata.json) | 4 | 4 | 0 | Both original weekly review cases and both weekly history recovery variants passed with the fix. |
| [18:48](raw/ui-integration/run-20261004-184854-664-47a7c00c/metadata.json) | 1 | 1 | 0 | Initial clipping regression used an insufficient outer-viewport assertion and passed the unfixed product. Diagnostic only; not accepted layout coverage or Red. |
| [18:50](raw/ui-integration/run-20261004-185009-489-996517eb/metadata.json) | 1 | 1 | 0 | The same insufficient assertion still passed; added ancestor diagnostics exposed the smaller actual content presenter. Diagnostic only. |
| [18:51](raw/ui-integration/run-20261004-185156-288-93ea3842/metadata.json) | 1 | 0 | 1 | Valid layout Red with actual ancestor bounds: `ApplyCheckAgain` bottom was 600, exceeding the allowed 587.4 including tolerance. |
| [18:52](raw/ui-integration/run-20261004-185231-442-6c54492a/metadata.json) | 11 | 11 | 0 | Five WeeklyApply cases, including the corrected two-height clipping regression, and six native Gantt drag cases passed. |

All fourteen task-source runs contain zero skipped cases. Two early layout passes are explicitly diagnostic because their assertions did not detect the observed clipping. There is no retained passing replacement for the lifetime assertion at this snapshot. The 18:11 and 18:19 bounded comparisons did not isolate all other dirty source changes; the subsequent complete-HEAD comparison below establishes the baseline failure. The missing Gantt drag fixture was found by inspecting actual case records; merely including `cat == GanttDrag` in earlier filters did not execute it. The 18:14 and 18:52 results contain all six cases and establish their observed passes.

For 18:19, the [compiled-source receipt](raw/ui-integration/run-20261004-181919-419-825010aa/compiled-source-receipt.json) identifies the actual comparison source and hashes. Its compiled `EditingGrid.cs` SHA-256 is `DE160221E0A1B98D13FE657895299B33EC53DF4A73F23957583971BFD4D9D24D`, and its compiled-source diff hash is `A33A4D5986D407EB3072EFD8C56ADE9A109F627C6DA71C5BCD520EB0FFDF1FB9`. The runner captured a later restored working directory. The comparison [build log](raw/polish-20261004/input-help-lifetime-baseline/build.log) and [binary hashes](raw/polish-20261004/input-help-lifetime-baseline/binary-hashes.json) are retained; the source snapshots remain at the local paths recorded in the receipt.

The [clean-HEAD comparison receipt](raw/polish-20261004/full-head-lifetime-baseline/build-source.json) and [run metadata](raw/polish-20261004/full-head-lifetime-baseline/run-20261004-182402-972-db2d5756/metadata.json) both identify base commit `654930a3f7a8810695a806a13a86f3c9d28f9ed4` with zero working-tree changes. The unchanged original test file is retained and its SHA-256 matches `4440FC665F09A31C292FAB48ED78E82D628F096DA14B42E82CD3382116B69AB6`. The [raw result](raw/polish-20261004/full-head-lifetime-baseline/run-20261004-182402-972-db2d5756/results.xml) contains **one executed case, zero passed, one failed**, with the same replaced-view collectibility assertion. Build log, binary hashes, original test source and copy verification are retained together. This isolates the pre-existing failure from the current refinement changes without weakening or skipping the assertion. The temporary managed baseline worktree was archived after these records were copied; its former executable path is provenance, not a delivered runtime.

## Ordinary-app E2E attempts

These attempts use the ordinary WinUI executable, FlaUI UIA3, native clipboard/picker interaction, isolated local data and an isolated fake-gh endpoint. The metadata identifies the app, Core, test and fake-gh binaries. Attempts through 18:21 used `final-app-v2`, before weekly history recovery. Attempts from 18:27 through 18:42 used `final-app-v3`, including the history fix; their [source manifest](raw/polish-20261004/e2e-182701/source-manifest.json) records that product-source snapshot. The final 18:54 run used `final-app-v4`; its [source manifest](raw/polish-20261004/e2e-185441/source-manifest.json) records 143 product/test source files, all checked against the final working files. These are not live GitHub validations. Earlier driver repairs did not change the v3 app; v4 adds the bounded review-layout and Gantt wording changes.

| Run | Executed cases | Passed | Failed | Classification |
| --- | ---: | ---: | ---: | --- |
| [e2e-181650](raw/polish-20261004/e2e-181650/metadata.json) | 0 | 0 | 0 | Testhost failed before case execution because the colocated runtime lookup found no .NET 10 x64 framework. The run error is retained in its TRX/log; zero execution is not a pass. |
| [e2e-181747](raw/polish-20261004/e2e-181747/metadata.json) | 2 | 0 | 2 | Both ordinary-app cases executed and failed. Weekly helper waited for `ProjectInformation` without expanding the collapsed section; holiday helper assumed `GridCell0_6` was exposed despite being off viewport. These helper failures prevent the workflows from establishing their intended endpoints. |
| [e2e-182154](raw/polish-20261004/e2e-182154/metadata.json) | 2 | 0 | 2 | Weekly helper looked for the disclosure before its UI was ready. Holiday helper encountered transient UIA COM timeout `0x80131505` while the native picker closed. Both raw cases failed. |
| [e2e-182701](raw/polish-20261004/e2e-182701/metadata.json) | 2 | 1 | 1 | Holiday native-picker/adoption/save/restart journey passed. Weekly reached 100-row paste/Undo/re-paste/filter, then failed because the helper still looked for the hidden `DraftStatus` control. |
| [e2e-183059](raw/polish-20261004/e2e-183059/metadata.json) | 2 | 0 | 2 | Both helpers timed out acquiring the intended title editor after coordinate-based clicking; inspection showed the neighboring row selected. This did not complete either journey. |
| [e2e-183338](raw/polish-20261004/e2e-183338/metadata.json) | 2 | 0 | 2 | Holiday's new scroll helper requested missing Automation ID `PlanningSettingsPage`. Weekly's review helper read unsupported `AutomationId` on an anonymous element. Both are retained driver failures. |
| [e2e-183749](raw/polish-20261004/e2e-183749/metadata.json) | 2 | 1 | 1 | Holiday passed, including the stronger readable preview capture. Weekly's registration helper threw early on `ProjectSummary` before its outer readiness wait could complete. |
| [e2e-183929](raw/polish-20261004/e2e-183929/metadata.json) | 1 | 0 | 1 | Weekly reached publication/readback, then a test assertion incorrectly requested a `Length` property from a lazy enumerable. Retained as a failed test-driver attempt. |
| [e2e-184051](raw/polish-20261004/e2e-184051/metadata.json) | 1 | 0 | 1 | Weekly reached the retained-work assertions, then the replacement property-based `Count` assertion failed on the lazy enumerable. Retained as a failed test-driver attempt. |
| [e2e-184216](raw/polish-20261004/e2e-184216/metadata.json) | 1 | 1 | 0 | Weekly passed after using an evaluated predicate count, including exact payload/readback and preservation of all unrelated work. |
| [e2e-185441](raw/polish-20261004/e2e-185441/metadata.json) | 1 | 1 | 0 | The same complete weekly journey passed on final v4 after the layout correction; the ordinary review screenshot shows all three auxiliary buttons fully visible. |

The failure classifications follow the control visibility, readiness and property assumptions inspected during test-helper repair; raw outcomes remain Failed. A startup/helper repair is not a completed product workflow. The final results establish **two distinct completed journeys in separate runs**: the 18:37 holiday case on v3 and the 18:54 weekly case on v4, both with zero skipped cases. Earlier holiday and weekly passes are repeated journeys, not additional unique coverage, and no aggregate of overlapping attempts is presented as a new test-suite total. The holiday workflow was not rerun for the unrelated final review-layout/Gantt wording change; its recorded candidate remains v3.

The [holiday journey receipt](raw/polish-20261004/e2e-182701/registration-4584e7a262d94532ab6ecfc93f5c1720/holiday-picker-journey.json) records real CSV selection and cancellation, explicit adoption/save and normal restart. The fixture already contained the same official calendar, so the imported difference was **zero dates**; do not describe this run as changing holiday dates. The input SHA-256 is `CEC37A743C96995CDB9CB52B685C9003634682A9B0E1A640A6B9B96881FE964A`. Parser and hosted cases cover changed/invalid/cancelled input separately.

The final [holiday receipt](raw/polish-20261004/e2e-183749/registration-a7956724c4884668a608c2841d51334f/holiday-picker-journey.json) and [visible import preview](raw/polish-20261004/e2e-183749/registration-a7956724c4884668a608c2841d51334f/holiday-import-preview.png) retain the strengthened repeat, again with zero changed dates. The final [v4 weekly receipt](raw/polish-20261004/e2e-185441/registration-d39010acd3084cc9b48c28776ac0714f/weekly-journey.json), four exact fake-gh request records and fake-gh scalar state accompany the passing TRX. Both normal process-exit receipts are retained for its real restart. The earlier v3 weekly receipt and outputs remain separately preserved.

Inspected final ordinary-app screenshots show [100-row weekly edit](raw/polish-20261004/e2e-184216/registration-ca053034289b4005b3d40389eb5513e5/weekly-100-pasted.png), [weekly review restricted to two rows/four effort fields](raw/polish-20261004/e2e-184216/registration-ca053034289b4005b3d40389eb5513e5/weekly-final-review.png), and [published effort with 201 other local changes retained](raw/polish-20261004/e2e-184216/registration-ca053034289b4005b3d40389eb5513e5/weekly-published-effort-local-other-changes.png). Earlier screenshots of the pasted/filtered state and holiday restart remain as intermediate evidence. They contain synthetic app data and show those states only; they do not retroactively change failed run outcomes. Native picker screenshots containing personal folders are excluded.

The v3 weekly review screenshot also reveals that the auxiliary action row is clipped by the footer. Its passing publication journey did not verify those buttons' rendered bounds. This was a product presentation defect, distinct from the earlier driver failures. Replacing the imposed content height with a maximum height fixes the measured clipping. The valid Red, subsequent 11-case Green, and passing v4 ordinary weekly rerun retain that progression. The inspected [final v4 review screenshot](raw/polish-20261004/e2e-185441/registration-d39010acd3084cc9b48c28776ac0714f/weekly-final-review.png) shows all three auxiliary actions and both footer actions fully visible and matches manual image 08 byte-for-byte.

The [hosted Red render](raw/ui-integration/run-20261004-185156-288-93ea3842/weekly-review-commands-962.png), [Green render at the taller size](raw/ui-integration/run-20261004-185231-442-6c54492a/weekly-review-commands-962.png), and [Green render at the shorter size](raw/ui-integration/run-20261004-185231-442-6c54492a/weekly-review-commands-682.png) are `RenderTargetBitmap` captures of real hosted controls, not ordinary-app desktop screenshots. The test resizes the logical window from 1400x1000 to 1400x720 and checks each auxiliary command against every containing visible surface, keyboard focusability, primary-button availability and no writes. The [Green build log](raw/polish-20261004/host-layout-green-build.log) records zero warnings and zero errors. The original clipped ordinary-app image remains preserved.

The recorded runner invocation for `e2e-182701` was:

```powershell
dotnet test TestResults/polish-20261004/e2e-driver-v3/GhProjectsBoards.E2E.Tests.dll --filter $filter --logger 'trx;LogFileName=e2e.trx' --results-directory $runRoot -- NUnit.NumberOfTestWorkers=0 RunConfiguration.TestSessionTimeout=600000
```

Here `$filter` was the exact filter in the run metadata, and `$runRoot` was this checkout's `TestResults/polish-20261004/e2e-182701` directory. `GHPB_RUN_E2E=1`, `GHPB_E2E_APP_PATH` selected `final-app-v3/GhProjectsBoards.App.exe`, `GHPB_E2E_FAKE_GH_PATH` selected the built `GhProjectsBoards.Tests.exe`, `GHPB_E2E_ARTIFACTS` selected the run directory, and `GHPB_DATA_ROOT` selected its `isolated-default-data` child. `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN`, and `GITHUB_ENTERPRISE_TOKEN` were removed from the process environment before launch. Later drivers use separate output folders; each run's test assembly hash identifies the actual driver.

The final v4 [command record](raw/polish-20261004/e2e-185441/command.json) retains the exact driver command, resolved app/fake-gh paths and argument array. Its metadata separately identifies app DLL, app executable, Core and driver hashes; the unchanged executable bootstrap hash does not imply an unchanged app DLL.

## Ordinary app and build evidence

The [initial final app build log](raw/polish-20261004/final-app-build.log), [v2 build log](raw/polish-20261004/final-app-v2-build.log), [v3 build log](raw/polish-20261004/final-app-v3-build.log), and [v4 build log](raw/polish-20261004/final-app-v4-build.log) record zero warnings and zero errors. v3 includes weekly history recovery; v4 additionally corrects the review content height and shortens the unselected Gantt status. The v3 build command was:

```powershell
dotnet build src/GhProjectsBoards.App/GhProjectsBoards.App.csproj -c Release -o TestResults/polish-20261004/final-app-v3
```

Build success is compilation evidence. It is separate from the hosted, ordinary-app, live and human boundaries.

The manual receipt identifies the synthetic ordinary executable and app/Core hashes. Its `firstClose` retains a failed native-picker targeting attempt that required forced cleanup while settings were unsaved. The later observation records a different process instance and an observed normal exit; it does not erase the failed attempt or prove garbage collection of replaced views. The ordinary-app observation includes Manual move/resize/Undo, displayed overload, native Markdown copy and zero journal batches. Those initial manual records do not claim a completed picker workflow; the separate passing holiday E2E results supply that later endpoint. Weekly publication is verified against the isolated fake-gh endpoint; no live GitHub mutation is claimed.

The [v3 final manual receipt](raw/polish-20261004/manual-final-receipt.json), [Gantt restart screenshot](raw/polish-20261004/manual-gantt-restarted.png) and companion UIA text record reopening the saved profile and Project without network access, the first task's retained Manual interval `2026-10-06 09:00–13:00`, and normal Alt+F4 exit of process 33280. The connection page was recaptured at the default 1266x794 window with its inputs fully visible. In this receipt, `app` is the executable SHA-256 `FE3DC83F1CF32E415F1FB4D496DEB9C853B915F7899198A80E815019971B4765`; the E2E metadata's `app` is the app DLL SHA-256 `FD3CFA55F13F0FA130093A362F3F6489DCA976FCF89E8E988E04A5FEE22DBB01`. Both files belong to the same v3 output, and the Core hash agrees. These are restart/process-exit observations, not a replacement for the failing collectibility assertion.

## Remaining validation and delivery status

| Boundary | Status at this snapshot |
| --- | --- |
| Core automated behavior | Earlier full run 924 passed / five live not executed; later weekly history fix 9/9 targeted pass |
| Selected UI behavior | Final scoped run 11/11: weekly review/history/layout five cases and native Gantt six cases. Broader lifetime assertion remains a reproduced pre-existing failure. |
| Native Gantt hosted cases | Six actual cases passed in the 18:14 class-selected run |
| Ordinary-app agent observation | Synthetic Gantt/overload/report evidence retained; v3 connection recapture and persisted Manual Gantt interval after restart observed |
| Representative fake-gh E2E | Two distinct completed journeys: holiday at 18:37 on v3 and weekly at 18:54 on v4. Earlier repeated passes and all failed attempts retained separately. |
| Final review auxiliary-button layout | Valid Red 0/1, corrected scoped Green 11/11, v4 build and ordinary weekly rerun passed; image 08 replaced with the verified v4 capture |
| Weekly Apply history/recovery follow-up | Core Red 0/1 to Green 9/9; hosted history Red 0/2 to full weekly Green 4/4 |
| Native system file picker | Selection/cancellation, adoption/save and restart passed; enhanced readable-preview capture passed in the 18:37 repeat |
| Complete-HEAD lifetime comparison | Clean original HEAD and original test reproduced the same failure, 0/1 passed; pre-existing lifetime defect remains open |
| Live GitHub | Not run for this snapshot |
| Performance qualification | Not run; test elapsed times are not a workload-qualified performance result |
| Human acceptance | `not_run` |
| Branch publication / PR / main integration | Not established by this evidence package |

## Artifact integrity and exclusions

[artifact-index.json](artifact-index.json) maps each copied raw artifact to its original `TestResults/` path or capture's absolute temporary path, byte size and SHA-256. [run-summary.json](run-summary.json) is a derived case-level count index, retaining every failed test name and explicitly marking the two insufficient layout assertions as diagnostic-only. Raw artifacts were copied byte-for-byte and checked against their originals. `manifest.sha256` covers every file in this package except the manifest itself; regenerate it after any later closeout additions.

[manual-images.json](manual-images.json) records the paths, byte sizes and SHA-256 hashes of the nine documentation images, which remain under `docs/assets/manual/`. The final v4 source manifest's 143 entries, all raw artifact hashes and the image 08 match were checked at closeout. Repository attributes preserve this evidence package's original bytes so Git newline conversion does not invalidate its manifest.

Runtime binaries, package assets, coverage payloads, user data/checkpoints, file-picker screenshots, and the user's pre-existing source patch are intentionally excluded. The small final fake-gh request/scalar records contain only the synthetic four-field dispatch and are retained as endpoint evidence. Task-checkout source snapshots and binaries remain in their local `TestResults/` folders; the archived clean-baseline worktree is the exception described above. Raw copied results have not been rewritten. The user manual describes current product operations; this evidence package owns the execution chronology and validation boundaries.

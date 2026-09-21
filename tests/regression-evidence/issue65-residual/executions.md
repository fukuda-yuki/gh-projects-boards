# Execution ledger

All new runs use `C:\Users\mwam0\.codex\worktrees\i65-residual`; raw relative paths below are present inside `raw-evidence.zip`. Commands, source status/diff, hashes and environment are retained per run. A passing state diagnostic does not pass its timing observations. No selected case was skipped. Do not combine the entries below into one acceptance total or count repeated cases as new coverage.

## Builds and Core

`TestResults/residual/final-build.log`: frozen source `da838adc2fcb5981b270bca615ff0aae186b7010`, Release build, zero errors and two existing CS0436 UI-host/WinAppSDK initializer type-conflict warnings. The ordinary E2E driver was then built at the same unchanged source. Earlier baseline/candidate build logs are retained separately. No dependency or machine setup changes.

`TestResults/residual/core/core.trx`: **50 executed / 50 passed / 0 failed / 0 skipped**, final frozen source. Lowest-layer existing Gantt projection, column and bulk-edit contracts remain real:

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~GanttProjectionTests|FullyQualifiedName~BulkEditingTests|FullyQualifiedName~ColumnTests' --logger 'trx;LogFileName=core.trx' --results-directory TestResults/residual/core
```

## Real-control UI integration

Every row is an actual WinUI host execution, not discovery. Run directory prefix: `TestResults/ui-integration/`. Product classes, native controls, events and binding paths are real; data/services are isolated synthetic fixtures. No live GitHub.

| Run | Executed / passed / failed / skipped | Source and purpose |
| --- | --- | --- |
| `run-20260921-134528-683-c951ee6c` | 2 / 2 / 0 / 0 | Baseline observer development tree (later `6762676`); Gantt timing plus initial ownership progression. No next-view-focus assertion in this early probe. Gantt max 455.2883 ms. |
| `run-20260921-134744-853-3d50befc` | 1 / 1 / 0 / 0 | Baseline isolated public Gantt replan, with diagnostic trace. Primary before comparison: max 466.8759 ms. |
| `run-20260921-134803-241-b6667b58` | 1 / 1 / 0 / 0 | Baseline unchanged original 80-roundtrip retention probe: 27 unloaded wrappers, limit <=65. |
| `run-20260921-135418-804-35069de0` | 1 / 0 / 1 / 0 | New editor-preservation test setup ERROR: eager lookup before expanded content loaded. Not a behavioral Red. Fixed the readiness predicate; no product repair is inferred from this failure. |
| `run-20260921-135516-652-986c1a82` | 1 / 0 / 1 / 0 | **Observed behavioral Red:** baseline frozen App/Core with the new test assembly. Public task detail Save replaced the original pending native TextBox (`SameAs` failure). BinaryRoot and binary hashes are retained; the source tree already had uncommitted repairs, so its HEAD alone is not the executed product identity. |
| `run-20260921-135527-300-49d2d613` | 31 / 31 / 0 / 0 | Candidate development tree: the new preservation case plus focused-scroll, post-merge sheet, bulk-edit and sheet-context cases. Real native editor/selection/fill/header/reveal collaboration; exact diff/hashes retained. Later optional CPU diagnostics and final lifetime assertion changes had not yet been added. Do not relabel this as execution at the final commit. |
| `run-20260921-135701-957-e2478e71` | 13 / 13 / 0 / 0 | Candidate development tree, GanttHostedTests: task edits, independent dates/bars, Undo, dialogs and settings. Includes timing cases; task-detail max 257.7583 ms. Exploratory observation, not substituted for the frozen final timing. |
| `run-20260921-135735-257-d200f045` | 1 / 1 / 0 / 0 | Candidate development ownership progression; next-view collection observed before final zero assertions were added. |
| `run-20260921-140141-474-d68d7358` | 1 / 1 / 0 / 0 | **Final frozen source:** original retention test unchanged, 5 unloaded wrappers vs <=65. |
| `run-20260921-140218-536-b1e989ad` | 1 / 1 / 0 / 0 | **Final frozen source:** 20/80/160 progression, 14 sampled alive/5 loaded/all 14 app-owned at each point; total app-owned TextBoxes 465. Unmount leaves 14; next-view focus plus idle/diagnostic GC gives zero and collectible old grid, now asserted. |
| `run-20260921-140319-157-4359ab3c` | 1 / 1 / 0 / 0 | **Final frozen source:** isolated public Gantt replan, max 331.7202 ms. Two warmups, ten samples; all ten independently expected endpoint counts 29. |

The two primary Gantt traces are `TestResults/residual/baseline-gantt.jsonl` and `final-gantt.jsonl`; their derived `gantt-comparison.json` contains complete samples and nested span statistics. These hosted traces have complete Save spans but no process-level `trace-end`, so they are not labeled fully drained process traces. The legacy timing output text says "includes planning and UI rebuild"; it describes the old path. Final task-value saves have zero Boards rebuilds, as the retained spans show. Neither rendering events nor the separate checkpoint-save sample proves composited pixels/physical scanout.

Representative final selectors (full exact commands in metadata):

```powershell
./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'cat == ReviewRetention' -TimeoutSeconds 240
./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'cat == ReviewRetentionProgression' -TimeoutSeconds 480
./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'name == ThousandTaskReplanPublishesExpectedBarAndReportsCalculationAndSaveSeparately' -TimeoutSeconds 180
```

## Ordinary application and native boundaries

`TestResults/e2e/20260921-141236-59ae91159cdd4849b817cd1bcfe0a454/e2e.trx`: **3 executed / 3 passed / 0 failed / 0 skipped** at final frozen source. One whole-application 1,000-task Gantt/Project-switch/normal-exit/restart journey reaches a real isolated checkpoint and verifies retained plan/pending work without automatic GitHub requests. Two physical direct/F2 Japanese IME cases cover composition across a Gantt switch. The process/native boundary, not the test project's name, is the reason for these executions. External endpoint is fake gh; no real GitHub evidence is implied.

```powershell
./scripts/Test-E2E.ps1 -Filter 'FullyQualifiedName~OrdinaryGanttThousandTaskEditProjectSwitchAndRestartRetainOnePlan|(FullyQualifiedName~ProjectViewSwitchDoesNotEndNativeCompositionOrCommitTheCell&Name~Gantt)'
```

## Sustained diagnostics

Directory prefix `TestResults/issue61-65/`. Each listed run executes **1 state/persistence diagnostic: 1 passed, 0 failed, 0 skipped**. All complete app traces have `traceComplete: true`; measurements and per-command/callback ledgers retain performance failures separately.

| IDs | Executed product / purpose | Performance disposition |
| --- | --- | --- |
| `residual-baseline-cold`, `residual-baseline-warm` | `6762676` baseline observer; same frozen fixture producer | Scroll FAIL both; max callback 311.6332 / 298.2166 ms. |
| `residual-trial-cold` | Candidate production sources later frozen at `da838ad`; final lifetime assertions/build freeze followed | Narrow causal trial, not one of final six. Scroll FAIL; max 168.0076 ms. |
| `residual-final-cold-01`, `warm-01`, `cold-02`, `warm-02`, `cold-03`, `warm-03` (full prefix `residual-final-`) | Final frozen source, three cold/three warm | Native title/NUMBER p95 PASS; scrolling FAIL in all six; warm-01 capture gap FAIL. No retries selected for a better timing. |
| `residual-final-ime` | Final source, 61.964 seconds / 20 physical composition-conversion-confirm cycles; writer-lock failure, explicit retry, offscreen state/history and normal close | Relevant input/recovery state PASS. No capture stream in this mode; not pixel-latency evidence. |
| `residual-final-thumb` | Final source, scrollbar thumb to I1000, immediate edit, horizontal and wheel return to original position | State PASS; single distant edit native-value readback **108.4632 ms** and four >=100 ms callback gaps (max 167.9162 ms). Do not call timing PASS. |
| `residual-final-thread` | Final source, separate cold run with optional UI-thread CPU accounting | State PASS / scrolling FAIL, max callback 185.2009 ms. Diagnostic extension outside the six-run comparison. |

Invocation pattern, with a fresh run ID, explicit full source and immutable paths:

```powershell
./scripts/Test-SustainedInput.ps1 -Executable '<worktree>/TestResults/residual/final-app/GhProjectsBoards.App.exe' -SourceRevision da838adc2fcb5981b270bca615ff0aae186b7010 -RunId residual-final-cold-01 -Condition cold -TraceDetail light -SeedExecutable 'C:/Users/mwam0/.codex/worktrees/issue65-stabilize/gh-projects-boards/TestResults/post68/baseline/producer/GhProjectsBoards.Tests.exe'
# warm uses -Condition warm; supplemental modes add -Mode ime or -Mode scroll.
# CPU extension only: GHPB_SHEET_THREAD_TIMING=1 in the dedicated run's environment.
```

Per-run manifests retain the actual concrete command/environment instead of this illustrative path token. Frame times, native value readback and application viewport events are different boundaries. The single optional thread run is not pooled with final acceptance repetitions.

## Retained failures and analysis limits

- The previous increment's Gantt 1,298.9222 ms and native retention 78/78 failures remain untouched in `../issue65-post68/`. This session's baseline already passes both narrower probes; absence of reproduction is not proof those historical failures were false or repaired.
- A malformed exploratory `SourceRevision` invocation failed argument validation before product execution. No product run directory/result was produced; the next invocation used `git rev-parse`'s full SHA. It is not counted as a test execution.
- The first new UI regression attempt failed on test readiness; the next attempt reached the actual editor-identity failure. Both XML/logs are retained, and only the latter is behavioral Red.
- The existing standard-wheel `analyze-scroll.py` cannot parse thumb mode because that driver has no `scroll-phase-start` event (`StopIteration`). The product run and its ordinary measurements remain intact. Supplemental reporting uses the existing thumb events/measurements, not a manufactured wheel ledger or a rerun.
- The offline summary's visited-destination bookkeeping was corrected to remember requests before the final durable commit. The initial derived JSON/log remain in the archive; raw data is unchanged. The final 24 post-save application delays still split 6 new / 18 revisited vertical destinations. "Visited" here denotes a previously requested offset, with actual viewport events retained for inspection.
- Full test suites, live GitHub, other environments/scales/High Contrast, broad accessibility, P1/P2/release and human acceptance were not executed. Scope was selected for the corrected native grid/Gantt/lifetime boundary; no inherited correctness campaign was repeated.

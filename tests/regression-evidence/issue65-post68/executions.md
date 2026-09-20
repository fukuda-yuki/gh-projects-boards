# Executed checks and retained failures

Source: integrated base `ab292d5`, corrective product commit `a776073c5221ce7184208a5adfb8890031a47bff`, final test/ordinary source `948dd5393aed7992f1b058301532989771921a90`. The last commit changes test interactions only; its `src/` is identical to a776073. Evidence edits after freezing do not change product code. Windows/.NET environment and per-run hashes are in the linked machine/binary receipts and archived metadata.

## Latest relevant executions

| Scope / mechanism / endpoint | Executed / passed / failed / skipped | Outcome and source boundary |
| --- | --- | --- |
| Core, non-live unit plus real isolated storage/process collaborators; `dotnet test ... --filter 'TestCategory!=LiveGitHub'` | 659 / 659 / 0 / 0 | PASS, 3m27s, `post68/core-final/core-final.trx`. Development tree immediately before the final Summary ordering fix. TRX alone does not supply a dirty-source binary hash; this is not relabelled a full run on 948dd53. |
| Core affected final selection: `FullyQualifiedName~PostMergeStabilizationTests\|FullyQualifiedName~Summary` | 40 / 40 / 0 / 0 | PASS, 12s, `post68/core-order-final/core-order-final.trx`, after the last Core change. The corresponding ordering regression first failed. Do not invent a 660-case full run. |
| Actual Planning/Gantt/Summary/PostMerge/bulk/context/viewport/column/focused-scroll controls in native UI host | 88 / 84 / 4 / 0 | FAILED attempt on clean a776073, `run-20260920-230021-119-38116cfb`, 164.324s. Three test-operation failures were corrected below; Gantt timing failure remains. |
| Those three actual-control operations on final 948dd53 | 3 / 3 / 0 / 0 | PASS, `run-20260920-230436-739-9265d5e7`, 1.387s. Dates are revealed through the real horizontal viewport; Actual action waits for its real enabled state. No direct-handler substitute. |
| Additional combined regressions: duplicate Gantt appearances (equal/conflicting), Summary alias/reattribution/missing row, native keyboard through unseen columns/rows | 4 / 4 / 0 / 0 | PASS, `run-20260920-225300-885-d8bfe560`, 1.616s. Development source before the final ordering-only Core change. Retained duplicate/selection Reds were executed. |
| Native retention, separate final 948dd53 host; actual 80-hop Boards/Gantt loop and existing GC-only lifetime assertion | 1 / 0 / 1 / 0 | FAIL, `run-20260920-232925-268-df21d275`, 66.515s: 78 unloaded wrappers against <=65. Original pending field/caret checks are retained. |
| Same strengthened retention probe on observer-only ab292d5 host | 1 / 0 / 1 / 0 | FAIL, `run-20260920-225114-125-a6f3e3a8`: also 78. It is not a new lazy-editor regression or a disposal pass. |
| Ordinary app E2E through principal layers, UIA/physical keys, isolated fake gh endpoint, final 948dd53 | 2 / 2 / 0 / 0 | PASS, 51s, `e2e/20260920-230508-1531f4589a404eb9b4f1d20420ce8269/e2e.trx`. Setup, Manual minute endpoint, dependency, Actual/Remaining, Gantt, same task, Project switch, 1,000-task far edit, narrow viewport, normal restart and reviewed fake publication. Not live GitHub. |
| Final sustained-input repetitions, fresh ordinary processes/roots, light observer | 6 / 6 / 0 / 0 state tests | State/persistence PASS; scroll performance FAIL. Three cold and three warm; exact runs in performance.md. These tests intentionally report timings rather than converting long intervals into failed NUnit cases. |
| Final trace-disabled companions | 2 / 2 / 0 / 0 state tests | State/persistence PASS; timing/attribution limits retained. |
| Final physical IME and thumb/distant/horizontal/return | 2 / 2 / 0 / 0 state tests | Functional/native PASS; timings remain separate, including the 143.19 ms distant edit. |
| Separate final EventPipe/sampled profile | 1 / 1 / 0 / 0 state test | Diagnostic collection completed, zero lost events. Excluded from six-run comparative timing. |
| Release solution rebuild | Build success / 0 errors | `frozen-build.log`, 13.37s, two inherited UI-host CS0436 warnings. Later launcher/E2E rebuild receipts identify their actual binaries. A build is not behavioral acceptance. |

The full [59-attempt ledger](executions.json) records each TRX/NUnit result, timestamps, counts, failures, command/source metadata where available and original hashes. Repeated selections are not summed into an all-pass suite. Some early Core TRX files lack an independent dirty-source manifest; that provenance limitation is explicit, not reconstructed from current HEAD. Final frozen native runs and ordinary E2E have clean source and binary receipts.

## Failure dispositions

The initial Core save/collision/duplicate cases were observed failing before their corrections. The second review produced two more Reds: a locked rejected first-save candidate and an existing per-edge dependency intent overwritten by verified promotion. Both passed after the narrowly scoped fixes (19 selected cases). A later Summary order regression failed before moving deterministic row selection inside each canonical group; the final affected 40 passed. All older 656-case/full and targeted runs remain separate.

The final 88-case UI attempt had four failures:

1. `GanttHostedTests.ThousandTaskReplanPublishesExpectedBarAndReportsCalculationAndSaveSeparately`: actual timing failure, max 1,298.9222 ms >1,000 ms. Still open; not rerun to erase the outlier.
2. `PlanningHostedTests.ClosingMinuteInputShowsTheLatestDurableBufferInTheBoardsCell`: tried to locate an offscreen date editor. Real horizontal reveal now precedes the original current-cell assertion; subsequent pass.
3. `PlanningHostedTests.ContextualActualUpdateAndNextRowPasteShareTheConfirmedDateAndRetainUndo`: attempted the Actual action before it became enabled in the loaded popup. Wait for the actual command, then preserve the original behavior assertions; subsequent pass.
4. `PlanningHostedTests.DateCellPasteShowsManualBeforeCommitAndCalendarTimeControlsRetainMinutePrecision`: same unrealized-date lookup; real reveal before focus, then original minute/manual assertions; subsequent pass.

Earlier UI attempts include real duplicate-key and lost-selection failures, the Gantt viewport regression, and test mechanics that entered a date-only editor when expecting task details. They are not relabelled successful. The native-retention investigation also exposed two vacuous passes with zero unloaded samples and later host teardown timeouts after explicit GC. The strengthened case now requires actual destination geometry, an unloaded inactive row and ordinary message-pump time. Its contractual <=65 assertion is unchanged. Temporary extra queue/GC/event-detachment experiments did not clear retention and are not adopted. The final ordinary/native measurement executions use fresh processes; no poisoned test host supplies their evidence.

The earlier trace-disabled baseline attempt failed native title readback before reaching scrolling; focus interference is only a hypothesis. Full-observer baseline, successful trace-disabled baseline, observer-light correctness candidate and lazy-editor intermediate runs remain exploratory source-specific records. They do not substitute for the final six repetitions. The original runtime profile was a separate partial baseline capture; it is not attributed to the final app.

## PASS / FAIL / NOT RUN / BLOCKED boundaries

Corrective behavior and the focused ordinary synthetic workflows have the scoped PASS evidence above. Scroll performance, Gantt timing and native-retention checks are FAIL. Human workflow reevaluation, full P1/P2, complete Unit D/Summary, other display/scaling/High Contrast/accessibility, physical scanout, broader recovery, #51 throughput, release packaging and live GitHub are NOT RUN/not newly accepted here. Summary expansion remains held by the owning decision. Source review is complete for the supplied snapshots with [recorded dispositions](review-disposition.md); it is not an independent certification of every later line. Publication/CI/main integration are reported by the PR/Issue handoff, not inferred from local tests.

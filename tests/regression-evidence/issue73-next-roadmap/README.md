# Issue #73 next-roadmap evidence (NX1–NX5)

This package records the local implementation and selected validation of NX1–NX5 on `main`, as explicitly requested by the user. The final selected hosted run passed **43/43**, and the final ordinary-app run passed **3/3**, after the compact-Summary correction. The broad non-live Core run passed **949/949 executed cases**. This is a bounded engineering evidence record, not closure of Issue #73, a live-GitHub result, or human acceptance. The final source receipt identifies the inputs in the containing commit. Earlier results retain their actual source and outcome.

The owning record is [Issue #73](https://github.com/fukuda-yuki/gh-projects-boards/issues/73). Current behavior is documented in the [specification](../../../docs/spec.md), [planning contract](../../../docs/planning.md), [performance boundaries](../../../docs/performance.md), and [Japanese user manual](../../../docs/user-manual.md). Validation follows the repository [test policy](../../README.md).

## Receipts and source boundaries

The companion [execution ledger](execution-ledger.json) records commands, individual failed cases, counts, build results, source/binary manifest references, and hashes. [Performance analysis](perf-analysis.json) retains the calculated timing boundaries; [pixel readback](perf-review-readback.json) identifies the exact 20 inspected original PNGs and their hashes. The [final source receipt](delivery-source-receipt.json) binds 345 build/test inputs by raw SHA-256 and normalized Git blob, plus five executed product binaries. [Core](results/core-final.trx), [hosted](results/hosted-final.xml), [ordinary](results/ordinary-final.trx) and both projection timing results are preserved here. Large raw traces, image sequences, binaries, and all original failed attempts remain under local `TestResults`; this package does not copy those trees.

- Starting commit: `237ec0fd51c72e0b9f73fa612aa1353abb9ae4e0`; local branch: `main`. A run's base commit plus an unspecified dirty checkout is not an exact source identity. Use that run's metadata, patch, source receipt and executed-binary hashes together.
- Environment: Windows `10.0.26200.0`, .NET SDK `10.0.401`, Release .NET 10 / unpackaged WinUI 3. Native executions were serialized on the local desktop. Ordinary journeys use FlaUI/UIA3, isolated stores and synthetic `gh`; they do not authenticate to or mutate live GitHub.
- The baseline app was built from the retained clean archive of the starting commit. Its lifetime run uses an external `-BinaryRoot`; the runner's dirty-file list describes the invoking checkout, not that clean baseline app.
- NX5 candidate `b` and `c` are different immutable app outputs. Candidate `c` includes the guide-reference and allocation-popup fixes, but predates the later Gantt-overlay wheel and compact-Summary corrections. The provisional filename `candidate-final-receipt.json` remains the original performance receipt; it is not evidence for the later final application.
- Build warnings and zero-execution build failures are recorded separately from executed tests. The retained baseline build has 3 warnings / 0 errors; the pre-implementation build used for hosted behavioral Red has 4 warnings / 0 errors. Neither establishes product acceptance.
- The final ordinary build reports 0 warnings / 0 errors; the final hosted build reports 2 warnings / 0 errors. Both ran `GhProjectsBoards.App.dll` with SHA-256 `7F0928CEFA84916D8440FB28A501139E83162A4EF81E121222E81AC73767C9F9`. The complete final source/binary receipt is a separate closeout binding.
- Counts are per run. Overlapping targeted runs and the broad regression must not be summed as unique coverage. Not-executed and discovery-only cases are not passes.

## Behavior and evidence map

| Item | Implemented behavior / final adjustment | Lowest reliable evidence and remaining boundary |
| --- | --- | --- |
| NX1 — overload visibility | Known negative headroom has a themed critical background as well as warning text, color and emphasis; zero and incomplete comparison remain distinct. Compact Summary keeps comparison and task rows visible with correction and filter actions reachable. | The final hosted selection includes all 13 `SummaryHostedTests` plus the 944×352-DIP compact-view geometry/interaction case. The final ordinary 1,000-task Summary correction/isolation/restart journey passed. Earlier successful journey pixels had exposed the additional short-viewport row problem, which received its own observed Red/Green. |
| NX2 — first-use guide | After empty-store restoration, the existing workspace offers connection verification → Project registration → Boards. The guide can be postponed/reopened; cached work and pending input are retained. Capability state comes from the current connection. Focus restoration uses a weak control reference. | Hosted real connection/registration views verify CLI/URL correction, skip/reopen and retained work. Ordinary first-use registration plus offline restart passed with fake gh. The weak reference is a source ownership correction, not a measured heap-improvement claim. |
| NX3 — Gantt dependencies | A separate finish-side connector previews and creates an FS link; explicit edge selection/removal and one Undo preserve unrelated work. Current identity/revision, duplicate, self-link and cycle guards precede adoption. Interactive overlay regions forward wheel input to the existing scroll surface. | `GanttDependencyEditTests` verifies rules, atomic state and real store/Undo readback. Hosted native dragging checks preview/cancel/adoption/removal and coexistence with date resizing. The wheel regression verifies delivery over both the link button and line plus actual vertical scrolling, with selection/state unchanged. These cases and the ordinary native creation/delete/Undo/restart journey passed on the final selected app. |
| NX4 — multiple-worker allocation | The existing breakdown popup edits cumulative Actual and independent Remaining shares together, shows task Remaining and unallocated balance, retains dated/historical reports and Estimate shares, and commits one coherent local operation. Cancel, explicit removal, invalid input and one Undo retain their distinct meanings. No equal split is invented. | `WorkAllocationInputTests` and existing planning-input tests verify totals, null versus zero, pending/conflict/stale rejection, persistence and Undo. Hosted controls verify two workers, invalid shares, many-worker scrolling, Actual-only fallback, and visible bounds at 1280×800 / 960×600 DIPs. Ordinary Cancel/Update/Undo/reapply/restart and independent JSON readback passed; the post-fix ordinary image shows the previously clipped right-side controls. |
| NX5 — large-Project work | The read-only Apply/status candidate projection associates rows with relevant fields by identity instead of rescanning every workspace field for each row. Existing ordering, orphan recovery, shared-title identity and no-write behavior remain covered. Diagnostic preparation now accepts up to 5,000 rows. | Core candidate/Apply regressions and exact-set timing tests cover the changed calculation. Four matched successful ordinary diagnostics cover 2,000 rows, one select field and three displayed columns. Supporting a 5,000-row diagnostic input is not evidence that 5,000 rows were exercised or accepted. Timing, sampled pixels, native lifetime and human usability remain separate evidence. |

The new deterministic rules live in Core; the popup, guide, themed rendering and pointer/scroll wiring are checked with real WinUI controls. The ordinary journeys add process startup, principal-layer collaboration, durable independent readback and normal restart. Their synthetic external endpoint does not make them live-GitHub tests.

## Core execution

The broad non-live regression executed **949 cases: 949 passed, 0 failed**. Its TRX reports `total=950`, `executed=949`, and `notExecuted=0`, while one individual Explicit benchmark result is `NotExecuted`. That benchmark was executed separately through an exact-name filter; it is not a pass in the broad run. The later corrections were in UI/test paths, not the Core candidate or allocation/dependency rules.

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj --configuration Release --filter 'TestCategory!=LiveGitHub' --logger 'trx;LogFileName=nx-core-final.trx' --results-directory TestResults/next-roadmap/core-final
```

| Result ID / TRX basename | Executed | Passed | Failed | Meaning |
| --- | ---: | ---: | ---: | --- |
| `dependency-behavioral-red` | 9 | 0 | 9 | Dependency API scaffold deliberately did not implement the operation. |
| `dependency-green` | 26 | 26 | 0 | Dependency edit plus existing schedule/dependency regression. |
| `dependency-local-green` | 28 | 28 | 0 | Includes additional local-task/storage cases. |
| `nx4-red` | 12 | 5 | 7 | Existing Actual-only path did not satisfy coherent Remaining allocation. |
| `nx4-green`, `nx4-green-reviewed` | 28 each | 28 each | 0 | Allocation plus existing planning-cell input coverage. |
| `nx4-clear-red` | 1 | 0 | 1 | Explicit blank Remaining buffer was incorrectly rejected. |
| `nx4-final-green` | 29 | 29 | 0 | Blank-buffer correction plus the full selected allocation/input set. |
| `nx5-core-baseline` | 6 | 6 | 0 | Candidate regressions only; the partial-name OR filter did not run the Explicit benchmark. |
| `nx5-candidates-green` | 29 | 29 | 0 | Read-only candidates, Apply confirmation/information and weekly Apply. |
| `nx5-projection-baseline`, `nx5-projection-candidate` | 1 each | 1 each | 0 | Exact-name Explicit timing experiment; one warmup and seven measured samples each. |
| `nx-core-final` | 949 | 949 | 0 | Broad non-live regression; one additional individual benchmark record is NotExecuted. |

Core raw results are under `TestResults/next-roadmap/nx4-core`, `nx5-core`, and `core-final`; the NX3 TRX files are under `tests/GhProjectsBoards.Tests/TestResults/dependency-*.trx`. Initial NX3 and NX4 compilations failed because the new `GanttDependencyEdit` type was absent. A still-earlier NX3 output-redirection attempt did not start `dotnet` because its output directory did not exist. Those are zero-execution attempts, not behavioral Red or passing tests.

## Hosted and native collaboration

All run IDs below are local 2026-10-04 timestamps. Full IDs, exact commands, build results, case names and hashes are in the ledger. The count columns describe executed cases, not discovery.

| Hosted run suffix | Executed | Passed | Failed | Selected evidence / retained failure |
| --- | ---: | ---: | ---: | --- |
| `204737-262-ef5a8b29` | 0 | 0 | 0 | Compilation failed before execution while the Gantt API was absent. |
| `204828-001-a7ed97af` | 1 | 1 | 0 | Unchanged native editor-retention/lifetime case against the clean baseline archive. |
| `205007-301-57f49a3f` | 4 | 0 | 4 | Valid pre-implementation Red: overload background, guide and dependency connector absent. |
| `205346-231-47fbaf35` | 36 | 29 | 7 | Retains control-readiness failures, three stale connection-label expectations, removed native edge button during Click, and deferred allocation TextChanged assertion. |
| `205810-333-c40ce0b7` | 38 | 36 | 2 | All 13 Summary cases, six new dependency cases and allocation cases passed. Onboarding readiness and one existing Gantt body-drag preview failed. |
| `210107-167-ca41ef60` | 8 | 8 | 0 | Two onboarding cases and all six existing Gantt date-drag cases. The preceding intermittent date-drag failure remains retained; its root cause was not established. |
| `210743-329-5bc5658d` | 1 | 1 | 0 | Current native editor-retention/lifetime case. Since the clean baseline also passed, this does not establish a repaired lifetime defect. |
| `211119-517-7cfb5ab0` | 2 | 0 | 2 | Popup geometry Red at both sizes: right edge 536 exceeded the visible ancestor width plus tolerance, 454.6. |
| `211304-398-9f808eb1` | 7 | 7 | 0 | Popup geometry plus existing allocation and onboarding after presenter-width correction. |
| `212159-078-f3c59687` | 2 | 0 | 2 | Native wheel reached the new edge button/line, but the underlying list did not scroll. |
| `212457-511-46ab29f8` | 14 | 14 | 0 | Wheel routing plus dependency and existing date-drag regression after the overlay repair. |
| `213108-740-c9e6a778` | 1 | 0 | 1 | Compact-Summary Red: no complete comparison data row was visible in the declared short viewport. |
| `213539-912-04f70c66` | 43 | 41 | 2 | Combined selection retained a many-worker allocation scroll assertion failure and a compact-Summary row-height shortfall. |
| `213726-459-0e3e5443` | 1 | 1 | 0 | Compact Summary shows complete comparison/task rows with correction and filtering reachable. |
| `213946-304-378ff776` | 43 | 43 | 0 | Final combined selection: guide/connection, Summary/compact layout, dependency/wheel/date drag, allocation/geometry and existing Actual cases; 0 skipped. |

Hosted raw directories are `TestResults/ui-integration/run-20261004-<suffix>`. A real mounted-view geometry assertion was added only after ordinary screenshot inspection exposed the popup clipping; existing UIA success was not relabeled as visible-layout acceptance. The same distinction caused the final compact-Summary follow-up when a successful journey's narrow screenshot showed only the comparison header.

The many-worker allocation test previously treated `VerticalOffset > 0` as completion immediately after `ChangeView`, which did not establish that the last worker was visible. It now awaits the real input event and error-layout update, makes one scroll request, and waits for the bottom offset plus the complete last input bounds within the viewport. Original invalid-input and unchanged-work assertions remain. The final case passed without a production change for this observer correction. Intermediate `ViewChanged` values were not retained in the runner output, so an exact intermediate-offset cause or a repaired production scroll race is not claimed.

## Ordinary application journeys

| Ordinary run suffix | Executed | Passed | Failed | Scope / retained outcome |
| --- | ---: | ---: | ---: | --- |
| `210533-08c703c7499d45ddafcfa2cc1612abfc` | 0 | 0 | 0 | New journey compilation failed on a missing FlaUI automation-elements import. |
| `210637-acd8b2e597ca4867961dcd3c8582fc24` | 2 | 2 | 0 | First-use guide/offline restart and native dependency/allocation/Undo/restart. Screenshot inspection subsequently found allocation-popup clipping. |
| `210857-f1f56dac5c8a41029f91570b423b740b` | 1 | 0 | 1 | Summary journey retained incomplete native title input before navigation. |
| `211339-06dbbc50b3d44789a767240ed3d3a52a` | 3 | 2 | 1 | Guide and dependency/allocation passed with the popup fix; Summary failed at the obsolete `PlanMode` target. This run is not all-pass. |
| `211657-cba2f5606f1c413f8a4b43b40f464f0f` | 1 | 0 | 1 | Summary stopped at disabled `SummaryAllowance` for the initially selected unattributed person. |
| `212045-1ea6b82a7cfc45e990c2d9e3d18983bd` | 1 | 0 | 1 | Summary first workflow and normal close completed; restart assertions had not selected the intended first task/person. |
| `212314-2de95aa5db5840b187a21d94204b4f81` | 1 | 1 | 0 | Summary 1,000-task edit, Project isolation, normal close/restart and independent retained values after public selection/input corrections. Predates the final compact layout. |
| `213830-902ed9a0c9524762b2addea0fd33c704` | 3 | 3 | 0 | Final guide/offline restart, dependency/allocation/Undo/restart, and Summary correction/isolation/restart; 0 skipped. App.dll hash matches the final 43-case hosted run. |

The Summary driver was corrected to await real focus and complete input, use the current details/Remaining controls, explicitly select the intended person/task, and identify persisted Projects by ID. Expected saved values, Project isolation and normal restart checks were retained. Every earlier failure remains in its original run directory under `TestResults/e2e/20261004-<suffix>`.

The new dependency/allocation journey verifies native connector dragging, no save during preview, explicit edge deletion and Undo, two-worker Actual/Remaining Cancel/Update/Undo/reapply, normal restart, independent JSON checkpoint readback and zero fake-gh requests. These are local planning operations. The guide journey verifies first registration through synthetic gh and cached restart; it is not a real authentication or permission-grant test.

## NX5 measurement and comparison

The causal implementation change is limited to `ReadApplyCandidates`: a per-row scan of all workspace fields was replaced by identity-based association while retaining original field ordering and recovery behavior. The two measurement layers below have different workloads and timing boundaries.

The Core experiment uses 2,000 rows / two cells, exactly one pending title and one changed title, and asserts the exact candidate set and unchanged workspace snapshot. Timing excludes fixture setup and assertions. One warmup precedes seven samples:

| Core projection | Median time | Measured range | Median allocated bytes on the executing thread |
| --- | ---: | ---: | ---: |
| Before | 255.0570 ms | 234.7195–284.7021 ms | 709,494,296 |
| After | 9.9238 ms | 7.6731–12.8938 ms | 7,375,000 |

The ordinary diagnostic uses 2,000 items, one select field and three displayed columns (Title, select field and Repository). It exercises focused native input, wheel bursts/return, individual wheel movement, scrollbar dragging, range/local actions, Undo, Project roundtrip, durable readback and normal close. The fixed `paced-selection-v3-hit-target` driver waits for the intended native hit target before making one cell click. Within baseline `d` / candidate `b`, and baseline `e` / candidate `c`, the test-assembly hashes match. Baseline `d` and `e` use the same app binary. Candidate `b` and `c` are different binaries, so their results are not repetitions of one identical executable.

All four matched v3 diagnostics passed 1/1 and exited normally. Each contains 14 `update-aggregate-presentation` spans across the scripted mix of editing, Undo and Project switching:

| Ordinary diagnostic | Span count | Median synchronous UI-thread span | Maximum span | Median thread allocation |
| --- | ---: | ---: | ---: | ---: |
| `nx5-baseline-2000-d` | 14 | 501.8680 ms | 858.1586 ms | 710,957,144 bytes |
| `nx5-candidate-2000-b` | 14 | 9.6343 ms | 21.3774 ms | 8,801,688 bytes |
| `nx5-baseline-2000-e` | 14 | 442.0438 ms | 804.2434 ms | 710,948,768 bytes |
| `nx5-candidate-2000-c` | 14 | 9.6141 ms | 21.9409 ms | 8,801,800 bytes |

The matched early phase before title commit has one aggregate span per run: 352.4682 → 17.0260 ms in `d/b`, and 351.4031 → 17.1167 ms in `e/c`; allocation is 710,920,256 → 8,801,088 bytes in both pairs. This bounded observation supports reduced synchronous calculation work during the selected ordinary editing path. It is not input-to-display latency, and nested span durations/allocations must not be added together.

The early-phase row-rebind count remains 226 in every run, with medians around 0.207–0.209 ms. Rendering callback gaps remain about 33.3 ms, and GDI sample intervals about 63 ms. No improvement in frame rate, physical scanout latency or universally smooth scrolling is established. Exactly 20 listed PNGs were visually inspected: sampled burst/return/wheel/bottom rows were populated and readable, with no full-viewport blank in those samples. Unsampled intervals and images are unclassified.

Earlier diagnostic failures remain retained: baseline `a` used an unsupported optional UIA Name read; baseline `b` expected a pending title although existing blur behavior had committed it; candidate `a` routed the intended post-Undo cell click to a still-closing overflow Add Row button. The corrected v3 driver records that blocker and waits for the intended cell. Baseline `c` also passed, but is excluded from the matched v3 comparison. Raw data is under `TestResults/sheet-diagnostic/<run-id>`; the ledger includes all eight attempts.

The performance candidate predates the subsequent Gantt-only wheel routing and compact-Summary layout changes. The unchanged sheet aggregation implementation and original measured binaries are the authority for these numbers; they must not be relabeled as a measurement of the eventual final executable.

## Reproduction entry points

Run from the repository root. Exact original arguments, app paths, hashes and selected/nonselected builds are in the ledger; the ordinary diagnostic commands below are templates requiring the intended retained executable path.

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter 'FullyQualifiedName=GhProjectsBoards.Tests.ApplyCandidatePerformanceTests.TwoThousandRowsKeepTheExactCandidateSetAcrossRepeatedReadOnlyProjection'

./scripts/Test-UiIntegration.ps1 -Where 'class == GhProjectsBoards.UiIntegration.Tests.GanttDependencyWheelHostedTests or class == GhProjectsBoards.UiIntegration.Tests.GanttDependencyHostedTests or class == GhProjectsBoards.UiIntegration.Tests.GanttDragHostedTests' -TimeoutSeconds 180

./scripts/Test-E2E.ps1 -Filter 'FullyQualifiedName~OrdinaryFirstRunGuideRegistersProjectAndRestartUsesOfflineCache|FullyQualifiedName~OrdinaryDependencyDragAndTwoWorkerAllocationCancelUndoAndRestartKeepOneLocalPlan|FullyQualifiedName~OrdinarySummaryThousandTaskEditRoundtripProjectIsolationAndRestart'

./scripts/Test-SheetDiagnostic.ps1 -RunId NEW_UNIQUE_RUN_ID -ItemCount 2000 -SelectFieldCount 1 -NoBuild -Executable ABSOLUTE_RETAINED_APP_EXE -Trace -Frames
```

For the baseline diagnostic, additionally pass `-SourceRevision 237ec0fd51c72e0b9f73fa612aa1353abb9ae4e0`. This argument is a caller declaration; the retained binary and source hashes still determine the executed artifact. The analysis command used on the four matched raw runs was:

```powershell
C:/Python314/python.exe tests/regression-evidence/issue73-next-roadmap/analyze-nx5.py nx5-baseline-2000-d nx5-candidate-2000-b nx5-baseline-2000-e nx5-candidate-2000-c
```

The packaged analysis script locates the repository root and reproduces the retained `perf-analysis.json` byte for byte from the four original runs. It requires the local raw traces described above. Final ordinary pixels were inspected at 1600×1000 and, for Summary, [1200×750](summary-ordinary-narrow.png) on the current 125% display scale. Both complete comparison/task rows and the correction commands are visible in the narrow Summary. The manual images retain their original bytes and [source hashes](manual-images.json).

The source review checked native controls, theme resources plus text markers, independent Core rules, current-identity/revision guards, one-operation Undo, explicit publication and event/control lifetime. No dependency, migration layer or alternative execution shell was added. The inspected sources and shared contracts have no remaining identified contradiction. The historical whole-view lifetime failure was not reproduced in either the clean baseline or the selected current run; no causal lifetime repair is claimed.

## Final validation and delivery

| Required binding / outcome | Current state |
| --- | --- |
| Compact-Summary behavioral Red and subsequent Green | Red `213108`: 0/1; intermediate `213539` still failed; Green `213726`: 1/1; included in final 43/43. |
| Final combined hosted selection | `run-20261004-213946-304-378ff776`: 43 executed / 43 passed / 0 failed / 0 skipped. Exact selection and source/binary hashes are in the ledger. |
| Final ordinary guide, dependency/allocation and Summary journeys | `20261004-213830-902ed9a0c9524762b2addea0fd33c704`: 3 executed / 3 passed / 0 failed / 0 skipped. The four manual images were refreshed from this run. |
| Exact final app/source receipt and source-to-local-commit binding | [Delivery receipt](delivery-source-receipt.json); its Git blobs identify the build/test inputs in the commit containing this package. All five product binary hashes match the final ordinary run; App.dll also matches final hosted execution. The separate NX5 measurement receipt retains its earlier scope. |
| Companion JSON copies and link verification | Included; JSON, relative file links, retained-result hashes and source correspondence checked. Raw originals remain local. |
| Local implementation commit / remote publication | Delivered in the containing local `main` commit. Its SHA and current status are recorded in Issue #73. No push, PR or remote-main integration of these changes is claimed. |
| Human acceptance | Not run. |
| Live GitHub / physical Japanese IME / High Contrast acceptance in this task | Not run. Earlier unrelated evidence is not reclassified as current acceptance. |

The selected runs do not establish an exhaustive device/DPI/theme/input matrix, arbitrary Project sizes, production GitHub behavior, or overall completion of every open planning/performance issue. Failed original attempts, the unresolved intermittent legacy date-drag observation, and the measured-source limits remain part of the handoff.

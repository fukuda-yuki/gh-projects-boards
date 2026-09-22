# Issue 65 execution-reset decision

**User-visible problem: UNRESOLVED. No product correction is accepted or delivered.** The two predeclared mechanism tests have completed. A save-only explanation is rejected; simple inactive-text substitution is also insufficient. The decision needed is a bounded change to inactive-row recycling and protected native-editor ownership. The final acceptance campaign's entry gate is not met.

Authority: [execution reset after #70](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5775270518), current #65 body, and [routing](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). Starting and retained production source: `fd45ecbb40a67fb0395bfb52e32baf9fc326f893`. Historical results remain in [the original receipt](../issue65-post69-native/README.md), unchanged.

## The two mechanism tests

The user's task is to type pending values and immediately scroll to further tasks. The unchanged target is independently observed readable movement within 100 ms, sustained scrolling, and retained input/identity. A prompt callback or first changed frame alone does not meet it.

Before patching, cold-01/0 and warm-03/0,1 were selected. Their records show 12–13 newly realized rows. Snapshot construction takes about 0.5 ms and follows the first delayed viewport response. Worker checkpoint work overlaps; one 38 ms row span overlaps collection counts including generation 2. Neither overlap establishes causation or a measured GC pause.

1. **Save-overlap test:** compare the same frozen product with immediate scroll versus a diagnostic saved-status wait plus 250 ms. Refutation of a save-only cause: late pixels remain with no overlapping checkpoint work. One cold and one warm replay per condition. Actual input-end to first-scroll separation: immediate 0.575/0.777 ms; settled 487.108/424.469 ms. Waiting is not a workaround or acceptance sample.
2. **Inactive-presentation test:** substitute lightweight TextBlock presentation for new inactive text cells, retaining the native initial 13 rows, active/pending editors, choice controls, ListView containers, cache and saving. No native editor is rebound. This deliberately read-only diagnostic does not implement arbitrary-cell promotion or complete accessibility. Refutation: visible lateness remains or only internal work improves. One immediate cold and warm replay; input/scroll separation 0.561/0.987 ms.

Each replay preserves 20 s Title / 20 s NUMBER input, plus 10 s history for warm. Five original scroll commands run at the original 200 ms pacing: two new vertical destinations, two returns, and one horizontal move. Recording starts during the final NUMBER second without inserting a wait at the scroll boundary; PNG encoding follows the replay. Maximum capture start gaps are 47.5–48.6 ms, versus roughly 80 ms and occasional longer gaps in the retained campaign. Some 100 ms brackets remain unresolved. Compare these new runs to each other, not as a historical speedup estimate.

The original plan's generic `schedule` string still describes standard mode; `earlyScroll`, its explicit `earlyScrollBoundary`, and actual driver phase records identify this shorter experiment. Raw records are preserved. `wheelMs` is the routed wheel-handler observation after requesting ChangeView, not an exact native message-arrival timestamp.

## Observed effect

All times are milliseconds from native input dispatch start. V is the app's viewport event. [L,U] means unchanged pixels still observed at/after L and first changed capture completed by U. These are conservative GDI sampling bounds, not exact presentation/scanout timestamps or complete-readable-frame acceptance.

| Comparison | Command 0: V; [L,U] | Command 1: V; [L,U] |
| --- | --- | --- |
| Current, immediate cold | 106.27; [109.98,162.88] | 142.52; [141.62,192.14] |
| Current, settled cold | 96.25; [109.87,160.31] | 83.41; [94.51,156.79] |
| Current, immediate warm | 90.82; [125.98,185.19] | 73.45; [95.10,150.23] |
| Current, settled warm | 90.42; [94.92,158.58] | 92.47; [95.53,155.34] |
| Inactive-text diagnostic, cold | 48.90; [47.60,109.13] | 36.72; [79.06,138.91] |
| Inactive-text diagnostic, warm | 87.00; [92.34,151.29] | 94.69; [108.67,169.86] |

Settled cold remains visibly late with **zero overlapping checkpoint spans**. This rejects saving as a sufficient explanation, not every possible save effect. The lightweight diagnostic shortens cold destination work and advances pixel bounds: command 1's new upper bound is below the baseline's unchanged lower bound. Warm is not consistently better: command 1 remains visibly late. Its 40.027 ms row span overlaps two generation-0, two generation-1 and one generation-2 collections; these counts neither measure pause duration nor attribute allocations to the writer. Do not average away this counterexample.

The observed expensive operation is first-destination creation/layout, with unresolved warm-load cost and an interval between viewport publication and captured pixels. Current row spans total 24.66–34.80 ms per first destination; the diagnostic ranges from 13.23 to 49.85 ms. They are nested in native layout. Retained native extracts on the earlier post-69/pre-CacheLength source show early frames around 120 ms, 54 TextBoxes, and 103.54/106.29 ms MeasureElement union. Those extracts support the mechanism but are **not exact native attribution of the corrected binary**. GetThreadTimes is coarse accumulated CPU evidence, not native running/ready/wait stacks. No new privileged recording was needed or attempted.

## Structural decision

The two tests end the tuning branch. Neither provides a shippable correction of the selected defect.

| Alternative | Evidence and trade-off | Disposition |
| --- | --- | --- |
| Keep ownership; wait/defer saves or tune cache again | Saving-settled pixels still fail. Waiting violates immediate work. No evidence selects another cache setting. | Reject. |
| Keep ownership; substitute inactive text only | Cold improves, warm remains late. Arbitrary-cell editing and complete accessibility are absent. | Reject as a product; retain as cost-isolation reproduction. |
| Data-backed native ListView recycling for inactive presenters; separate ownership for protected native editors | Avoids repeatedly constructing/retaining per-row inactive trees. Native editing can remain identity-bound. This complete alternative is not yet implemented or measured. | **Recommend one bounded structural correction.** |

The precise additional implementation scope is replacing `BuildRows`/`EnsureRow`/`ReleaseRow`'s per-row visual arrays and dormant-editor cache with recycled inactive presenters, together with `Select`, viewport reveal and protected native-editor ownership. Keep WinUI ListView/ItemsStackPanel and public APIs. The present code creates 1,000 row containers but defers editor content; it does not eagerly create every TextBox or wholly disable virtualization. [Microsoft's collection guidance](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview) supports generated-container recycling and element reduction, not a speed guarantee for this proposal.

An existing active, pending or composing native editor must retain its host, field identity, text, caret/selection and composition. Recycle only inactive presentation; never rebind or reparent a protected editor to another field. Prepare native focus/replacement selection during cell selection, before direct typing; do not replay the first character or toggle editability on it. Ownership uses existing account/host, Project, item and column/field identity, not a recycled index. Preserve durable unseen buffers; a presenter pool must not cap protected work.

**Decision requested:** adopt this bounded ownership/recycling change as the next implementation slice, including its input-activation, focus, drag/fill and UI Automation/accessibility changes. This is the explicit structural branch required by the assignment, not a request for another generic investigation. The current read-only diagnostic is not the candidate to merge.

That review unit must retain long-title scrolling, caret/selection, direct/F2 physical IME, pending/composing identity on both axes, immediate distant-row editing, range/fill/Undo, theme/focus/accessibility behavior, native lifetime guards and durable close/readback. Reconcile architecture/decisions after adoption. No Core/save algorithm, checkpoint schema, scheduler, framework, dependency, security change, new feature, merge or release is requested. The comparison motivates this smaller WinUI alternative; it does not prove it will meet 100 ms. Stop it if behavior regresses or warm visible delays remain. Only a corrected editable candidate with explicit uncertainty dispositions proceeds to the retained final comparison and separate continuous-scrolling check. No user profiling campaign is requested.

## Existing residuals and validation

The derived ledger links all 17 original long intervals to demand, delivery, viewport and pixel brackets: 3 intervals overlap the 3 demonstrated delayed commands; the other 14 have insufficient observation. No callback-only gap becomes a physical-display failure or PASS. Overlapping populations are not summed.

| Original visible-uncertain command | V / unchanged-through ms | Disposition |
| --- | --- | --- |
| warm-01/1 | 85.59 / 110.36 | Visible lateness; attribution incomplete, unresolved. |
| warm-01/22 | 21.65 / 109.55 | Post-save horizontal lateness; attribution incomplete, unresolved. Pixels inspected. |
| warm-02/1 | 98.67 / 110.84 | Post-save vertical lateness; attribution incomplete, unresolved. |
| cold-03/1 | 86.71 / 111.10 | Visible lateness; attribution incomplete, unresolved. |

The original FAILs and unknowns remain. New five-command samples cannot pass continuous scrolling, these four observations, full sheet acceptance or P1/P2. Inspected images confirm destination rows; some first-change text is clipped, so a changed ROI alone also does not establish complete readable presentation.

All six diagnostic executions passed their one **state/persistence** case: 6 executed, 6 passed, 0 failed/skipped. They exercised an ordinary WinUI process, physical key/wheel driver, isolated synthetic data, normal unforced close and independent pending-buffer/history/planning readback. All traces drained with continuous sequence IDs. These are not six performance passes or new full IME/lifetime acceptance.

Builds: initial driver PASS; first prototype FAIL (TextBlock is sealed; the attempted subclass caused C#/XAML errors); corrected prototype PASS; restored product PASS; final driver PASS. Original failure logs remain. Successful builds have zero warnings/errors. After the runs, camera-exception cleanup was tightened to dispose retained frames; that exception path was not executed. Successful-run measurement logic is unchanged; exact executed driver source is archived. No product acceptance follows from compilation.

Environment: Windows 10.0.26200 x64, .NET SDK 10.0.401, Windows App SDK 1.8.260804001, observed 125% scale, 1080x760 window, 1,000 tasks/20 fixture people. Runs were serial with no agent-started competing build/profiler; app/driver were non-elevated. Token variables were stripped from the synthetic child. No live GitHub, new human, High Contrast/other DPI, Narrator, Gantt/lifetime campaign or full Core run occurred. Unchanged evidence retains its original boundary. No machine settings changed.

## Exact sources and artifacts

Frozen current app: source `b66d8f9d4099d320f32465d7b6b183fb6efcc586`, production/test sources unchanged through `fd45ecb`; App DLL SHA256 `5D5243363C0A96D4193D35F17DD0A8DF4FA24DE946D5CFE9277FDD193EA3DDF6`.

Rejected diagnostic: `fd45ecb` plus [inactive-presentation-diagnostic.patch](inactive-presentation-diagnostic.patch), patch SHA256 `b62fca5f075788d0abe170422c192de5e8350c91932b846a776b9033212ec718`; App DLL SHA256 `D1594153A75171BB410D89FE2BA6AA3B92D45A44A618D70E4706290B1290B23A`. Native XAML DLL hashes match. Rebuilt Core hashes differ, but all 4,635 method signatures/IL bodies match. This is not binary identity. Driver and fixture producer are identical across all six runs.

The prototype patch was reversed and the ordinary product rebuilt. Final `src/` is unchanged. The diagnostic binary is isolated under ignored `TestResults/issue65-reset/inactive-app`. Local branch: `codex/issue65-execution-reset`. No push, PR, merge, Issue closure or release occurred. Summary remains held; #61's human track remains separate.

[Artifact index](artifact-index.json) records paths, SHA256 and sizes. [Raw evidence](raw-evidence.zip) retains the six synthetic runs, source/binary manifests, exact executed driver, failed/successful build logs, analysis scripts, existing-ledger derivation and replay summaries. Original #70 raw records are referenced in place. No system ETL/ETLX, symbols, real user data or runtime binaries are included.

Reproduce with the existing Release E2E build and `scripts/Test-SustainedInput.ps1`: absolute frozen executable, full source revision, fresh run ID, `-Condition cold|warm -TraceDetail light -EarlyScroll immediate|settled`, process-local `GHPB_SHEET_THREAD_TIMING=1`. Default `-EarlyScroll none` retains standard behavior. Apply the diagnostic patch only in an isolated `fd45ecb` checkout, freeze its build and record hashes. The archive holds exact executed commands and fixtures. Do not replace the ordinary product with the diagnostic.

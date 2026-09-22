# Native-scroll correction: hidden text scrollbar chrome

Owner: [Issue 65 assignment](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5762304114), under [current routing](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). This is a bounded corrective changeset, not closure or smooth-scrolling acceptance.

## Cause and correction

The native trace identifies repeated visual-tree entry of retained row content, including TextBox/ScrollViewer visual states and theme references. This accounts for part of the scrolling cost, not every residual interval. A requested return is not assumed to be reuse: the selected post-save revisit has no managed `realize-row` call and no native `ElementCreated` event in its frame. All 54 measured TextBox element IDs appeared earlier and had no intervening ElementDestroyed event. Native `ApplyTemplate` calls alone do not prove template reconstruction. The same native elements are remeasured and re-entered as ItemsStackPanel places retained containers back in the live tree.

In one selected post-save revisit, the XAML frame lasts 116.7930 ms. Its 18 PlaceElement operations cover 51.3736 ms, with 54 TextBoxes and their inner ScrollViewers in the measured tree. CPU scheduling independently reports 116.3782 ms running, 0.4088 ms ready and 0.0060 ms waiting within the frame. These nested durations are not additive. Sample stacks include `ModernCollectionBasePanel::ContainerManager::PlaceInValidElements`, `CTextBoxBase::EnterImpl`, `CVisualStateGroupCollection::EnterImpl` and `CDependencyObject::UpdateAllThemeReferences`. Some symbol lookups are unresolved or marked out-of-range; those names are not promoted to reliable attribution.

The correction gives only sheet TextBoxes' inner `ContentElement` ScrollViewer a scrollbarless native template. Hidden scrollbar visuals and their states no longer enter/leave with each row. The TextBox itself, its standard template, clear button, native TextBoxView, selection, caret, context menu and composition paths remain intact. The ScrollContentPresenter and template-bound padding/content template remain native. This follows the same structure as the scrollbarless ScrollViewer template in the pinned WinUI generic.xaml, without changing a dependency or globally restyling ScrollViewers.

No cache-size tuning, delay, replacement grid, product GC, persistence change, instrumentation reduction or Summary expansion is part of the correction.

## Source and comparison boundary

- Baseline executable: frozen residual source `da838adc2fcb5981b270bca615ff0aae186b7010`; product sources are equivalent to starting main `14b617166a4bbf73e538be329de9631e3068e0f0`.
- Candidate product and test source: `94922ffd17489b464bf8f23376e702ad1ef8600f`.
- Both use the retained producer/ordinary physical-key and wheel driver in `i65-residual`, a fresh isolated synthetic 1,000-task/20-person fixture, pending work and Undo, 1080x760 window, and light app tracing. The driver revision is distinct from the candidate product revision; exact binary hashes are retained per run.
- The WPR baseline run is attribution evidence only. It has capture stalls and is excluded from the unprofiled comparison and six final repetitions.
- The short unprofiled before/after comparison reduced >=100 ms rendering-callback gaps from 20 to 6 and maximum gap from 247.6988 to 162.9851 ms. These are callback observations, not physical presentation or proof that all native work was removed.

Independent visible/application-delay classifications decrease from **5 to 1** in that short pair. For the same return command 19, public viewport response changes from 103.0687 to 75.0194 ms. Baseline captures remain unchanged through a capture beginning at 110.7593 ms and show the destination by 200.5892 ms; candidate bounds are 31.5911 and 126.6325 ms. That supports an observed earlier screen update, **not** an exact pixel latency or a proven <=100 ms visual response. Both commands follow the final durable save.

All six final candidate runs still **FAIL scrolling**, with maximum callback gaps 131.2619–213.6853 ms and 4–12 >=100 ms intervals per run. Ten commands have corroborated visible/application delay, eight after durable saving: six new destinations and two previously requested returns. Seventeen other visible delays have incomplete attribution. These populations overlap callback intervals; do not add them. Every remaining >=100 ms scroll interval has its own raw record, overlapping commands and managed-span union in the archive. Those unions do not identify remaining native/compositor/GC causes. Cold-03's 281.8219 ms capture gap and two commands without a changed capture remain limitations, not passed observations.

## Native tooling and permissions

Installed WPR 10.0.26100 has CPU and XAMLActivity profiles. Initial non-elevated WPR failed with 0xc5585011 and raw XAML logman capture was denied. The user then explicitly authorized WPR capture-only elevation. The first elevated recording succeeded and stopped successfully; the ordinary app/driver stayed non-elevated. Its ETL reports zero lost events and contains actual XAML events, sampled native stacks and CSwitch/ReadyThread data. Public PDB GUID/age matches the pinned Microsoft.UI.Xaml module, version 3.1.8.2608. Raw XAML Frame/Layout/Measure events and installed TraceEvent 3.1.27 were used; WPA/XAML Frame Analysis plugin installation is not claimed.

The existing driver uses 10,000,000 QPC ticks/second. Its UTC clock-sync instant lies within the ETL-derived before/after bounds (a 0.002 ms bracket at serialized precision). Selected process/TID are 32668/30760. The diagnostic's managed thread number 2 is not treated as an OS TID. In native command 81, screen captures at 155.0959–168.6546 ms still show rows 241–252 with the same hash as the pre-input image; the 218.8656–236.7547 ms capture shows rows 481–492. The later capture follows the next horizontal input and is not labeled an isolated command-latency upper bound.

A second UAC launch for candidate profiling returned "operation cancelled by the user" before recording. It was not retried. Candidate native-stack before/after attribution is therefore not available. That does not erase the successful baseline native attribution or establish a cause for every residual candidate interval. No ADK installation, perfcore.ini change or machine-wide persistent configuration change was made. Full system ETL/ETLX and symbol caches remain private under `TestResults/issue65-native-preflight`; they are not published evidence.

## Validation and disposition

The new bounded real-control test checks long native text at both caret endpoints, internal scrolling without sheet movement, unchanged committed state, retained buffer/focus and zero publication journal entries. Existing focused/pending scroll cases also execute. Their first candidate run is 3/3 passed; no new behavioral Red is claimed for this preservation test. The performance defect was observed independently before the correction.

Final execution counts, all failures and residual interval classifications are recorded in the accompanying execution ledger. Core logic is unchanged; UI collaboration, native input/process checks and the retained Gantt/lifetime guards are the relevant boundaries. Build success alone is not acceptance. Prior Gantt/lifetime outliers are not claimed repaired; no human, live-GitHub, other-DPI/High-Contrast, release or broader P1/P2 acceptance is inferred. Keep Issue 65 open and Summary held.

See [execution ledger](executions.md), [run summary](runs.json), [native attribution](native-attribution.json), [review](review.md), and [raw scoped evidence](raw-evidence.zip). Full system traces, PDBs and binaries are intentionally outside the public archive. Native attribution after the change and full scroll acceptance remain incomplete; the next discriminating targets are candidate command 12's first realization and warm-02 command 91's residual return, not another undirected campaign.

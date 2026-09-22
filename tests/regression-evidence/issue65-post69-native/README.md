# Post-69 native comparison and viewport correction

Owner: [Issue 65 post-69 assignment](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5772453123), under [current routing](https://github.com/fukuda-yuki/gh-projects-boards/issues/1).

## Candidate native evidence

The missing post-69 capture completed after explicit user readiness/consent and user-operated UAC. Only the bounded WPR controller was elevated; the ordinary product and retained driver ran non-elevated. The existing CPU/XAMLActivity profiles recorded one cold and one warm synthetic replay. The named session `GHPB-I65-Post69-Candidate-01` stopped successfully (exit 0), finalized a private ETL, and reported not recording. No security setting, SDK, ADK or service was installed or changed. Previous cancellation remains history; this acquisition succeeded.

The private trace contains 20,697,704 events, zero lost events, native call stacks and CSwitch/ReadyThread data. Candidate process/UI-thread identities are cold 8132/37316 and warm 2688/33044. Both driver QPC/UTC synchronization instants fall within the ETL-derived before/after bounds (about 0.003 ms at serialized precision). The XAML module has the same public PDB GUID/age as the valid retained baseline: `72f0bfb0-e2d3-cb2c-5f24-fe090eae2608` / 2. Only supported symbol ranges are used; unresolved and `??`-prefixed names remain unresolved. A folded generic event-wrapper name is not proof that a XamlRoot event fired.

The candidate is the immutable post-69 binary, source `94922ffd17489b464bf8f23376e702ad1ef8600f`, App DLL SHA256 `DEEB7F5387AD67C84E6476890738597740269E6CD5384B03201A3FB012AC7470`. Starting main `1854d4b41315d2ea267d9813474e156a25851857` differs only in the prior evidence directory. Same source is not a rebuilt-main execution. The retained baseline ETL and its original extracts were reused without recording another before-state.

| Native boundary | Retained baseline | Post-69 cold | Post-69 warm |
| --- | ---: | ---: | ---: |
| First destination, command 12: XAML frame ms | 193.1495 | 121.9870 | 121.4378 |
| First destination: new TextBoxes / all measured TextBoxes | 54 / 54 | 54 / 54 | 54 / 54 |
| Retained return, command 81: XAML frame ms | 116.7930 | 49.7785 | 52.0271 |
| Retained return: new / previously observed TextBoxes | 0 / 54 | 0 / 54 | 0 / 54 |
| Return to top, command 91: XAML frame ms | 123.0507 | 75.9280 | 76.1243 |
| Return to top: new / previously observed TextBoxes | 27 / 24 | 21 / 30 | 21 / 30 |

These are matched profiled attribution samples, not a repeated performance estimate. Each measured frame is overwhelmingly UI-thread running time, with small ready/wait intervals. Native element identity is tracked within its process using earlier appearance/creation/destruction. Pointers are never compared across processes. PlaceElement ItemIndex identifies the realized row ranges; not every TextBox pointer can be individually mapped to a row from this provider's payload, so no such mapping is invented.

At the retained-return boundary, PlaceElement union falls from 51.3736 ms to 16.9753 / 17.9726 ms. Samples containing EnterImpl fall from 49 to 16 / 16; samples containing UpdateAllThemeReferences fall from 19 to 5 / 3. These inclusive sample populations overlap; none of these named matches uses a `??` symbol. Baseline VisualStateGroupCollection / VisualStateGroup EnterImpl has 12 samples each; neither appears in the selected candidate frame samples. This is sampled-work evidence, not exhaustive proof that no visual-state processing occurred.

Measured Grid / Border counts fall from 311 / 198 to 257 / 144, a reduction of one each per 54 retained editors. The 54 TextBox / ScrollViewer / ScrollContentPresenter / TextBoxView identities remain, with zero new TextBoxes and zero intervening destruction after their latest creation. There is one measured outer ScrollBar in every frame; this does not establish whether hidden inner chrome was measured. The retained sheet-only template and native subtree/work comparison support the earlier correction. An ApplyTemplate event alone does not mean construction. That before-state's dominant path does not explain every remaining frame.

## Responsible residual operation and correction

The first destination places rows 717 through 734 (18 rows), while captured pixels show rows 721 through 732 (one-based) and public viewport height is 364 DIP. Native layout performs 54 new TextBox constructions, 1,170 total element creations, and about 108 ms of MeasureElement union. Managed EnsureRow work is nested inside that layout; it must not be added to the native duration. The return-to-top frame combines retained re-entry with 21 new TextBoxes, rather than being a pure retained-control return. The independent short replay reports seven managed row realizations there.

Correction source: **`b66d8f9d4099d320f32465d7b6b183fb6efcc586`**. The only behavior change is `SheetRowsPanel` ItemsStackPanel CacheLength from 0.5 to 0, plus its explanatory comment. Native layout now selects its viewport/anchor rows without the additional speculative cache. The separate 64-row dormant-editor cache, active/pending identity protection, row unloading, native TextBox template and post-69 inner ScrollViewer template are unchanged. No control is rebound to another field. This is a cause-backed viewport correction, not a cache-limit increase, dependency change or replacement input surface.

The short unprofiled warm pair uses fresh identical synthetic fixtures, the same retained producer/driver, 1080x760 geometry, light app tracing and the full preceding title/NUMBER/scroll history. It records:

| Boundary | Before | After |
| --- | ---: | ---: |
| Corroborated visible/application delayed commands | 2 | 0 |
| Command 12: managed rows realized | 18 | 13 |
| Command 12: public viewport response ms | 116.5001 | 85.5027 |
| Command 81: public viewport response ms | 54.4236 | 30.5686 |
| Command 91: managed rows realized | 7 | 0 |
| Command 91: public viewport response ms | 77.3866 | 35.1823 |
| Maximum rendering-callback interval ms | 137.2569 | 170.3013 |
| Rendering-callback intervals >=100 ms | 4 | 4 |

The short pair establishes a bounded effect and justifies the retained final campaign. It is not full scroll acceptance: capture brackets still straddle 100 ms, and the maximum callback interval did not improve. The native recorder is absent from this comparison. No heavy native after-trace of the viewport correction has yet been acquired; its changed row-realization work is measured by the unchanged app diagnostic.

## Validation and disposition

Initial real-control selection: 5 executed, 5 passed, 0 failed/skipped, including long-title internal scrolling, focused/pending scroll roundtrips, the real workspace viewport and keyboard range movement across the viewport. Build: zero errors, two existing CS0436 UI-host initializer warnings. The defect was observed before the correction in the native/ordinary boundary; no new logic-unit Red is claimed for a XAML viewport parameter. Existing behavioral tests are retained without weakening thresholds or changing assertions.

The final unprofiled campaign uses the same frozen corrected app, producer/driver and fixture, with three cold and three warm repetitions. Each state/persistence diagnostic passed (1 executed / 1 passed / 0 failed / 0 skipped); these state tests deliberately report performance separately.

| Run suffix (`post69-viewport-final-`) | Max callback gap ms | Scroll callback gaps >=100 ms | Corroborated delayed commands | Visible delays with incomplete attribution | Max capture gap ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| cold-01 | 120.9981 | 3 | 1 | 0 | 80.1721 |
| warm-01 | 127.1858 | 2 | 0 | 2 | 80.0345 |
| cold-02 | 109.3120 | 3 | 0 | 0 | 80.0284 |
| warm-02 | 117.2320 | 3 | 0 | 1 | 79.7145 |
| cold-03 | 120.9303 | 3 | 0 | 1 | 79.7740 |
| warm-03 | 148.3930 | 3 | 2 | 0 | 126.0402 |

**Scroll performance remains FAIL / unaccepted.** All 17 callback intervals at or above 100 ms occur in the scroll phase. These are callback intervals, not exact physical-presentation delays. Three separately corroborated delayed commands occur before the last durable save: cold-01 command 0 and warm-03 commands 0 and 1. Their public viewport responses are 115.6073 / 136.9072 / 111.6506 ms, and unchanged-capture lower bounds are 117.5967 / 109.5205 / 110.8856 ms. They are not added to the callback count. No post-save command meets this corroboration rule in these six runs; four other visible delays have incomplete attribution and many capture brackets straddle 100 ms. Neither missing corroboration nor the absence of a changed capture is a pass. Warm-03's 126.0402 ms capture gap remains a limitation.

Final scoped real-control guards passed 13/13; the additional viewport-edge drag/fill check passed 1/1. They cover long text/internal scrolling, caret and selection, focused/pending identity across both axes, keyboard range fill/Undo, pointer fill/cancellation, Light/Dark transitions, Gantt Save and editor lifetime. The ordinary executable selection passed 3/3: one Gantt edit/Project-switch/restart workflow and two physical Japanese IME cases (direct/F2 entry across Gantt switching). A separate ordinary native-scrollbar journey passed 1/1, reaching row 1,000, immediately editing, moving horizontally, returning and reading back durable pending data. These executions use isolated synthetic data/fake gh; they are not live GitHub or human acceptance.

Gantt's 1,000-task operation maximum is 274.1794 ms across ten samples after two warmups, with 29 independently expected changed endpoints each time. The boundary is public Save through dialog closure, expected text and two rendering events; it is not physical scanout. Separate sequential checkpoint-save maximum is 524.9119 ms; calculation maximum is 2.4636 ms. The original 80-roundtrip retention check observes 5 inactive sampled editors, within its <=65 bound. These are scoped guards, not new paired Gantt speedup or leak-repair claims. Earlier failures remain in their original evidence.

Keep Issue 65 open and Summary held. The next discriminating target is the remaining early scroll interval and the four uncertain visible delays on the corrected binary, preserving preceding input and durable-save boundaries. Save overlap alone does not establish cause. This increment does not justify another cache, SDK or framework change, or another unchanged six-run series. A new elevated acquisition would require its own bounded readiness/consent; none is needed to review this completed handoff. Human, High Contrast/other DPI, full P1/P2, live GitHub and release acceptance remain separate.

Whole-system ETL/ETLX, private conversion logs and symbol caches stay under `TestResults/issue65-post69-native/candidate-01/` and the retained baseline directory. Only reviewed synthetic-process extracts may enter the portable evidence bundle.

See [executions](executions.md), [source/design review](review.md), [delivery](delivery.md), [run summaries](runs.json), [native comparison](native-comparison.json), and [artifact manifest](artifact-manifest.json). `raw-evidence.zip` contains process-only native extracts, original run traces/capture pixels, controller receipts and scoped test outputs. [unpack-evidence.py](unpack-evidence.py) restores the losslessly deduplicated PNGs into a new directory.

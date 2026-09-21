# #65 residual repair candidate

Owner: [routing #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and [bounded repair decision](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5755462883). This is a corrective changeset with scoped execution evidence. **Keep #65 open: sustained scrolling still fails.** The parallel human interaction evaluation was not awaited or counted as acceptance; Summary remains held.

## Corrections and observed effects

| Demonstrated path | Correction | Observed effect and limit |
| --- | --- | --- |
| Saving Gantt task details rebuilt all Boards row containers, including an unrelated pending native editor. | Rebuild only when Project settings can change column mappings; refresh task values in the existing controls. | New public-control regression fails on the baseline because the pending editor is replaced, then passes with identical editor, text, caret/selection, buffer key and empty journal. In the isolated 1,000-task Save trace, 12 rebuilds become zero. |
| Gantt edit/Undo/settings continuations forced a second projection/presentation after the revision-aware refresh had already published it. | Honor the existing revision guard in those continuations. Explicit view switches still force presentation/selection. | Two warmups plus ten Saves produce 13 presentation spans including initial display, versus 25 on the baseline. Public Save -> closed dialog -> independently expected task/bar -> two rendering events: maximum **466.8759 -> 331.7202 ms** in this matched pair. The prior **1,298.9222 ms FAIL** is retained; this pair does not establish that every historical outlier is resolved. |
| Each realized row built offscreen cell layout/markers and inactive selection/fill adornments even when native editors were deferred. | Defer the entire unused cell content, selection frame and fill handle until reveal/selection needs them. Keep fixed row/field identity and the existing protected-editor/cache rules. | Post-save row-realization median **2.993–3.016 -> 1.909–1.940 ms**; recorded managed allocation **21.5–22.7 -> 10.3–11.6 MB** over the respective run windows (168–177 baseline vs 142–165 final row realizations). Final maximum scroll callback gaps **168.0–221.4 ms**, versus baseline **298.2–311.6 ms**. These are scoped work/timing observations; all six final scroll runs still FAIL. Header-sync work did not improve: deferred content also moves work into horizontal reveal. |
| Unloaded wrapper counts alone did not distinguish app ownership, native/test roots, or growth. | Add a separately selected 20/80/160 ownership progression and view replacement observation. No product GC, cache limit change, SDK change, or editor rebinding. | Final progression stays at **14 sampled live wrappers**, all app-owned, at each stage; zero sampled evicted wrappers survive those observations. Unmount alone leaves 14; after a new view takes focus and additional idle/diagnostic GC, zero remain and the old view is collectible. The original unchanged <=65 probe passes at **5**, versus this baseline's **27**. Earlier **78/78 FAIL** remains historical unresolved variability, not a proven fixed native leak. |

The Gantt changes are a coherent pair, not a factorial experiment isolating each change's share of latency. Candidate preparation, canonical validation/staging, scheduling and durable saving remain real. The baseline already passes the Gantt timing bound in this session. Final core commit mean is higher in the trace (82.12 -> 98.24 ms); this is not a scheduler speedup claim.

## Exact source

- Start: published main `bbf047fa8f987ea6aacd0ae708b63e68648ded09`.
- Baseline observer: `6762676f6299b2523dc558c42fc59454bffc6f84` (diagnostic spans and ownership probe; product behavior unchanged).
- **Frozen candidate product/test source: `da838adc2fcb5981b270bca615ff0aae186b7010`.** Subsequent commits in this increment only retain evidence.
- Branch: `codex/issue-65-residual-repair` in `C:\Users\mwam0\.codex\worktrees\i65-residual`. Original main and the old `issue65-stabilize` worktree are preserved.
- [Frozen binary manifest](frozen-candidate.json): App DLL SHA256 `0B7975F1D7739218CE9E5337014BEB40551A9FF0E36627E2553A7F524E749E50`; Core DLL `DA0DC36D54AB72B320294874C534410AEE94FDAAAC528860D36CEFF420610CB6`.
- Ordinary frozen app: `TestResults/residual/final-app/GhProjectsBoards.App.exe` in that worktree. Per-run manifests bind the executable, driver, source files, immutable seed producer and initial checkpoint hashes. The seed producer remains the previous `issue65-stabilize/TestResults/post68/baseline/producer` so this comparison does not change the workload generator.

## Remaining engineering blocker

The three cold/three warm standard runs all pass state/persistence assertions and measured title/NUMBER native-value p95 <=100 ms. **All six fail scrolling.** There are 25 independently corroborated application-viewport delayed commands; 24 begin after the final durable commit. Those 24 comprise 6 new vertical destinations and 18 returns to previously requested vertical destinations. Another 20 visibly delayed commands lack full application attribution. These command populations overlap rendering intervals and must not be added to callback counts.

After saving settles, horizontal reveal has 5 new-destination and 6 return commands with visible delay but insufficient application attribution, and no fully attributed application-delay command in these six runs. This is not proof that horizontal scrolling passes. The observer retains every >=100 ms callback interval, capture brackets, clamped/no-op and missing-frame populations in the raw ledgers. Of 123 long callback intervals, 114 start after the last durable commit; 69 remain unexplained by the classification. Warm-01 also has a **205.7159 ms capture gap**; its observation limitation is retained, not dropped by rerunning.

One additional CPU-instrumented cold run is outside the six acceptance repetitions. Four intervals correlated with delayed application viewport commands contain 78.125–140.625 ms of UI-thread CPU within 111.963–169.199 ms wall time; only 16.292–37.571 ms is inside the union of instrumented UI methods. This demonstrates substantial running work outside those methods. The rest of wall time includes waits/preemption and coarse OS accounting; it does not identify a GC, finalizer, lock or compositor cause. The trace does not resolve native template/layout call stacks, and a previous profile's inclusive finalizer time is not substituted for this measurement.

**Smallest next structural decision:** use the existing native row/viewport boundary to reduce repeated realization/layout of inactive rows while preserving pinned active/pending/composing editors. Before choosing a recycling change, obtain native UI-thread stack/layout attribution for one captured post-save revisit interval and distinguish construction from native layout/template work outside the measured methods. The present changes already remove unnecessary cell/adornment construction; increasing the 64-row cache or rewriting the framework is not supported by the remaining evidence. No broader structural implementation or SDK experiment is started here.

Lifetime disposition is narrower: the current probes pass and show a bounded sampled app-owned cohort, but neither native-root ownership after unmount nor the old 78-count variation is explained conclusively. Focus transfer, extra idle and diagnostic GC changed together. Keep the original probe and failure record; there is no basis yet to declare an upstream WinUI defect or an unbounded leak.

## Final six observations

All times below are milliseconds; "delayed" counts commands with full application-viewport attribution. Run IDs are prefixed `residual-final-`. See [scroll-comparison.json](scroll-comparison.json) for full distributions, allocations and separate horizontal/vertical/return classifications.

| Run | Title p95 | NUMBER p95 | Max scroll callback | Delayed | Visible/unattributed | Capture max | State / scroll |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| cold-01 | 51.3474 | 47.2575 | 221.4230 | 4 | 6 | 86.6634 | PASS / FAIL |
| warm-01 | 48.1182 | 47.0676 | 167.9873 | 4 | 5 | 205.7159 | PASS / FAIL |
| cold-02 | 47.8010 | 46.7291 | 172.6700 | 5 | 3 | 79.5086 | PASS / FAIL |
| warm-02 | 46.7421 | 47.4725 | 188.0311 | 3 | 3 | 79.3533 | PASS / FAIL |
| cold-03 | 51.4726 | 48.0341 | 219.7829 | 5 | 1 | 80.6334 | PASS / FAIL |
| warm-03 | 51.1028 | 48.4786 | 172.2545 | 4 | 2 | 80.1578 | PASS / FAIL |

No measured title/NUMBER sample exceeds 100 ms in these six runs. UIA native-value readback is not pixel latency, and callback timing is not physical presentation. Runs were serial on the frozen candidate, with no simultaneous rebuild or human evaluation. Standard workload: 1,000 tasks, 20 people, six fields/eight displayed columns, retained offscreen work and Undo, 16,125,099-byte initial checkpoint, 20 seconds each of title, NUMBER and wheel/horizontal input; warm adds ten unmeasured seconds. Window 1080 x 760 at 125%; Windows 11 Pro 26200, Ryzen 7 9700X, RTX 5070 Ti, Balanced power, .NET SDK 10.0.401/runtime 10.0.12. Machine manifest records WMI 29 Hz; that is not a measured scanout rate.

## Inspected pixels

Cold-02 command 91 returns from vertical offset 7296 to 0 after saving is settled. Native wheel delivery is observed at 2.1205 ms; the matching public viewport event is at 105.2553 ms. The [before image](pixels/cold02-scroll-0277.png) and [109.321–132.961 ms capture](pixels/cold02-scroll-0280.png) have identical SHA256 and still show rows 241–252. The [171.799–199.732 ms capture](pixels/cold02-scroll-0281.png) shows rows 1–12 and the original pending title/NUMBER cells. This is bounded capture evidence of a delayed return, not exact physical frame latency. [Capture provenance](pixel-evidence.json) retains original paths, timing brackets and unchanged hashes.

Hosted snapshots [bottom/right](pixels/focused-03-bottom-right.png) and [returned top/left](pixels/focused-04-return-top-left.png) were inspected: dense dark-theme rows and headers remain legible; pending title, caret/selection and frozen identity survive the return. Static snapshots do not establish natural scrolling or human approval. Other themes/scales/accessibility remain outside this increment's executed visual boundary.

## Validation and evidence

See [execution ledger and retained failures](executions.md), [source/design review](review.md), [declared comparison](comparison-plan.md), [machine-readable run metadata](executions.json), and [raw-evidence.zip](raw-evidence.zip). The [artifact manifest](artifact-manifest.json) contains per-file and archive hashes. This archive includes raw traces, XML/TRX results, commands, environment/source manifests and selected unchanged PNGs; it does not distribute all executable directories, original checkpoints or every PNG. Those originals remain at the recorded local paths. It is an inspectable evidence subset, not a self-contained app/fixture distribution.

Functional results, measured performance, lifetime observations, publication/main integration and human acceptance are separate. This receipt does not close #65, re-enable Summary or establish P1/P2/release/live-GitHub acceptance. Publication status belongs to the owning Issue/PR and handoff, not an inference from this local commit.

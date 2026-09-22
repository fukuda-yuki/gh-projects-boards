# #65 structural Gate A — NO-GO

**Gate A failed; do not expand this candidate into Gate B.** The editable recycling/ownership mechanism operates, but the first distant vertical wheel action immediately after sustained warm input produced the intended desktop image after **130.1855 ms** and **102.1499 ms** in two of three fixed-binary confirmation runs. The intervening run was on time at 93.8229 ms. The target remains 100 ms. Gate B, the final three-cold/three-warm campaign and continuous-scrolling acceptance were not started.

This is the local implementation/validation handoff for the [approved assignment](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5778775134), using [#1 routing](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). It is not implementation completion or product acceptance. #65 was found closed and was reopened under the assignment's explicit instruction to keep it open until acceptance. Summary remains normally disabled. No push, PR, main integration, merge, release or new human acceptance occurred.

## Candidate identity and scope

- Local branch: `codex/issue-65-recycled-editors`, isolated worktree `i65-recycle/gh-projects-boards`.
- Verified starting main: `63ce24f106eee0ebb435915c18a6e447a6616362`. #71 actually merged on 2026-09-22 at 17:09:14 UTC; the routing's OPEN/unmerged description was stale. Its reviewed driver/evidence head is `47da7cf01953b3d44834d2aded6079be05f6cc29`. Nothing was cherry-picked twice.
- Product implementation: `78dc5f0d5a59f957a5b12443f419effccefeb669`. The `src` tree is `c228504e5278dddefd4aea7678bd99b5b76ac4de`, identical at that commit, `17387f4` and handoff source `6b7088b`.
- **Actual decisive build identity:** App/Core informational version `1.0.0+17387f4b14f79ad4215d43ae8a7187e0340a3f39`, plus the archived uncommitted diagnostic observer source. The run's `source` argument names the product implementation commit; it is not a claim that all later diagnostic builds are byte-identical. Earlier exploratory binary hashes differ because builds incorporate repository revision metadata. The three decisive runs below have identical App, Core and driver hashes.

| Decisive binary | SHA-256 |
| --- | --- |
| App DLL | `67224b328bd9fa8de80646f0c24f342b662db80ef4923a5ce04dd1caf8904538` |
| Core DLL | `9e50c0df81d00aa5a7a31c44864cbb6fbc4b2f056f06125cc6e8f07e88936859` |
| Executed diagnostic driver | `17c0dd5158d2b397ea9e94e2c15e72d97a01dfe69b31b13c470c5d3d3d23048a` |

The opt-in `GHPB_RECYCLED_PRESENTATION=1` path uses native data-backed ListView/ItemsStackPanel containers and reusable inactive cell content. Identity-owned native Title/NUMBER/choice editors are attached to a separate clipped viewport layer; offscreen scrolling does not reparent their hosts. A row/field/column binding generation rejects stale presentation providers. Stored buffers remain workspace data and do not eagerly create native inputs. The ownership/lifecycle map is beside the implementation in [EditingGrid.Recycling.cs](../../../src/GhProjectsBoards.App/EditingGrid.Recycling.cs). The original default path is frozen as the gate comparison/fallback. Core behavior, dependencies, persistence schemas, scheduler and save algorithm were not changed.

## Gate evidence and visible outcome

The unchanged `GanttWorkload` retains 1,000 tasks, 20 people, mixed fields/planning state, an offscreen pending Estimate buffer and existing history. The ordinary app ran at 1080 x 760 physical pixels, 125% scale, Windows x64, .NET 10, with isolated synthetic storage and no live GitHub calls. Warm replays retained ten seconds of title warmup, twenty seconds of title input, twenty seconds of NUMBER input, then the original five physical scroll commands. No settled-save wait was added.

The decisive operation is command 0: offset 0 -> 7295.9999, displaying rows 241-252. At the source level the request reaches the correct viewport before 100 ms, but desktop presentation does not always follow within the remaining budget. This identifies the responsible operation; it does **not** isolate native layout, compositor scheduling or saving as a sole root cause.

| Warm replay | Correct viewport event | Last old desktop update | First intended desktop update | Pixels available to observer | Decision |
| --- | ---: | ---: | ---: | ---: | --- |
| `warm-05-desktop` | 57.5064 ms | 96.9485 ms | 130.1855 ms | 131.4243 ms | Late |
| `warm-06-desktop-confirm` | 45.2687 ms | 60.6934 ms | 93.8229 ms | 95.3119 ms | On time |
| `warm-07-desktop-reproduce` | 52.5422 ms | 67.3668 ms | 102.1499 ms | 104.1228 ms | Late, reproduced |

Run IDs have the prefix `structural-a-`. For the two late transitions, every desktop update from the pre-command image through the changed image reports `AccumulatedFrames=1`. Raw images show the old rows before the transition and the expected rows/titles, Todo, Estimate 16 and Remaining 4 afterward. The whole viewport was copied; this is not acceptance based only on a shifted gutter. An initial visual statement about missing values was withdrawn after original-image reread; **missing values are not the rejection basis**.

The added passive DXGI observer uses the [OS desktop-update QPC timestamp](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info) and independently timestamps CPU pixel availability. It runs alongside the retained GDI observer. It copies only the same app viewport, performs no input or machine configuration changes, and is not ETW/GPU profiling or physical scanout measurement. It is a disclosed observation repair, not the exact #71 observer binary. All three decisive runs use the same repaired observer. Across their 15 commands its image-change classifications are 2 late, 12 on time and 1 insufficient; the latter retains accumulated/missed desktop updates. These classifications are not a claim of full readability acceptance for all 15 images. The counterexample's before/after destination and values were inspected directly.

Native-value readback remains a separate result: measured title/NUMBER p95 values across these three runs are 31.3532-40.4952 ms, with 98-99 samples per phase and no >=100 ms typed-value samples. All three state/persistence checks and trace-drain checks passed. Rendering callback gaps also stayed below 100 ms; that did not establish timely desktop presentation.

**Selection readiness is not accepted.** Click/lookup through fresh focused-native-peer readback took 146.4426-523.4944 ms across the nine acquisitions. These bounds include UIA lookup, click and polling overhead and do not isolate physical selection latency. They are retained in `driver.jsonl`; cost excluded from the subsequent key-only metric is not claimed as a gain. Direct first-character and physical IME behavior passed the separate native checks below. No broad speedup over main is claimed.

## Behavioral validation and lifetime

| Boundary | Observed result | Evidence scope |
| --- | --- | --- |
| Existing logic/store collaborators | 90 passed, 0 failed/skipped | Editing, bulk editing, columns, row projection and draft lifetime; real Core and isolated storage, not UI or live acceptance |
| New actual-control collaboration | 3 passed, 0 failed/skipped | Data-backed container **and content** reuse; stale provider rejection; protected host/caret/pending identity; middle/last-row direct physical input; horizontal return; one wheel gesture applies once |
| Frozen default-path UI collaboration | 5 passed, 0 failed/skipped | Existing viewport, pending details, focused title/caret and return cases; gate disabled |
| Ordinary app/native input | Final 1 passed, 0 failed/skipped | Public ItemContainer/VirtualizedItem/ScrollItem realization to task 1000; direct physical NIHONGO conversion/confirmation across both axes; original native peer preserved; independent checkpoint readback and normal process close |

The reuse test observed the same 24 native containers and 24 content roots across ten separated viewports, with zero native editors created merely for visitation/stored buffers. The pending-input case retained three native title editors for three genuinely protected edits, including the original host, caret and item identity. This is scoped live-object evidence, not a native-heap plateau measurement. Long-term heap growth and the complete disposal/projection matrix were not accepted. Final ordinary-app verification uses the decisive product binary; the earlier hosted 3/3 run used the same product source tree before the informational-version rebuild. Exact binary/source records remain in each run's metadata.

Original tests/assertions were retained. The necessary public-route mapping is explicit:

| Previous observation | Replacement route and observed coverage |
| --- | --- |
| Keep the inactive cell's TextBox peer across activation | Select the stable cell identity, then reacquire its focused native peer. The original #71 driver failed before input on its retired presentation peer; the adapted replay preserves all later input/persistence assertions. |
| Wait for PointerReleased on the pre-activation presentation | Send the same physical click, then verify current native focus and the expected durable field buffer. Promotion changes the routed event target; first characters, original caret/host and wrong-row protection remain asserted. |
| Locate an offscreen native input before realizing a row | Discover the native ListView data item, use public VirtualizedItem/ScrollItem, focus/select the target, then resolve its native editor. No whole-dataset editor construction or first-13-row exemption is used. |

## Retained failures and uncertain observations

[run-ledger.json](run-ledger.json) contains every observed test result, including failures. The archive retains failed builds, initial teardown/observation mistakes, stale-peer acquisition, two reproduced composition-focus failures before their correction, and all reruns. Do not add these into an all-pass total.

| Replay | State result | Performance disposition |
| --- | --- | --- |
| `cold-01` (original #71 driver, main worktree) | Failed before input | Retired-peer observation; no timing evidence |
| `cold-02`, `warm-01` | Passed | **Invalid scroll comparison:** ordinary implementation bug dispatched both custom and native animated wheel handling; faster changed-ROI results are not gains |
| `cold-03` / `warm-02` | Passed / passed | GDI 1 on-time + 4 straddling / 5 on-time image changes; no Gate A pass inferred |
| `cold-04-observer` | Passed | Removing the camera sleep left 3 on-time + 2 straddling; no product change |
| `cold-05-copy-boundary` / `warm-03-copy-boundary` | Passed / passed | Nested BitBlt/GdiFlush clocks still left 1 / 2 uncertain commands |
| `warm-04-qualified-copy` | Failed before workload | Alternative GDI flags failed full-viewport pixel qualification; rejected observer patch retained |
| `warm-05/06/07` | All state checks passed | Fixed-binary desktop observations above reproduce the warm violation; no further candidate expansion |

The wheel defect was corrected with an actual one-detent regression over both presentation and protected input. Incidental collection focus during UIA scrolling was corrected after two ordinary physical-IME failures; the native editor now keeps composing focus across the scroll. A C++ observer build failed because existing compiler headers were absent; its source/log are retained. No components were installed. The working observer uses public COM APIs from the existing .NET test runtime.

## Gate B hold and review risks

Sorting/filtering/reorder and duplicate appearances, complete field/DATE/Actual routes, range/clipboard/autofill/Undo, Gantt/Project return/restart, delayed disposal events, complete assistive naming/status, Narrator, other themes/High Contrast/scaling, full lifetime and continuous scrolling are **not run/not accepted for this candidate**. A concrete source-review gap is `ResetRecycling`: projection rebuild still clears protected hosts; that is not a completed identity-preserving projection implementation. Dynamic accessibility names and the complete readonly/choice adapter contract also remain unqualified. These are held work, not waived requirements or work delegated to the user.

Review used the repository's DESIGN.md and test policy, plus the installed WinUI design, development, UI-testing and code-review guidance. The first-character/native-state scope was exercised; cross-theme, broader interaction and human acceptance remain explicit. App/test builds succeeded; the full-solution native check retained the two existing CS0436 host auto-initializer warnings. No package or framework change was introduced. Architecture/decisions documents remain the default product contract; they were not rewritten to describe a rejected candidate as accepted.

## Evidence and reproduction

[raw-evidence.zip](raw-evidence.zip) is the single new evidence package; [artifact-index.json](artifact-index.json) gives every file's hash and verified archive readback. It includes the exact decisive App/Core/driver binaries, executed observer source, original frames/timestamps, traces, synthetic initial/final checkpoints, commands/environment records, failed attempts and referenced hosted images. The inherited [#71 evidence](../issue65-reset/README.md) is referenced without duplicating its archive. Runtime seed-root duplicate copies remain local; the recorded fixture command recreates them. These are synthetic app observations, not user data or real-GitHub validation.

For offline audit, extract the archive to `raw` here. For each complete replay, run the unchanged `scripts/Measure-SustainedInput.ps1 -Observations <observations>` and `python classify.py <observations>`. For the three desktop runs, also run `python classify-desktop.py <observations>`. The screen-copy variant is `python classify.py <observations> --screen-copy`; it creates a separate derived file and never changes the original timestamps. `package.py <original-main-worktree>` documents collection and rejects overwriting a prior archive.

For an explicitly chosen new reproduction, use the retained candidate executable/source and a **fresh** run ID:

```powershell
$env:GHPB_RECYCLED_PRESENTATION='1'
$env:GHPB_SHEET_THREAD_TIMING='1'
$env:GHPB_SUSTAINED_DESKTOP_OBSERVER='1'
./scripts/Test-SustainedInput.ps1 -Executable <absolute-ordinary-executable> -SourceRevision 78dc5f0d5a59f957a5b12443f419effccefeb669 -RunId <fresh-id> -Condition warm -TraceDetail light -EarlyScroll immediate
```

The rebuilt diagnostic source is at `6b7088b`; the exact measured driver and its source patch are separately archived. Rebuilding can change informational-version hashes, so retain and compare actual binaries. This command is a reproducibility record, not a queued additional campaign. The next architectural decision is left with #65; this candidate is not recommended for broad migration.

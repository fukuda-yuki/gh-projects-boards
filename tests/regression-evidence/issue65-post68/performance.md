# Frozen combined-app measurements

**The corrected combined app still has application-caused scroll delays at the declared boundary. #65 performance remains open.** There is no physical-scanout or complete freeze-resolution claim.

## Boundary and source

The prospective [comparison](comparison-plan.md) was recorded before the final runs. Baseline is integrated `ab292d5` plus the published observer-only patch. Candidate ordinary binaries and driver are from `948dd5393aed7992f1b058301532989771921a90`; its product source is identical to corrective `a776073`. Candidate App.dll SHA-256 is `7DF9F4BCF99F6CE544F55F9FEF28719AA51DB654A823DC22524CFA2D5406C291`; Core.dll is `712E1454EF47A700A412A73C68AF97D7F0041CB0DB5741EC09B99CC51BC1F288`. The complete [manifest](binaries.json) identifies producer, driver, baseline and candidate files.

Every execution starts a new process and synthetic root: 1,000 tasks, 20 people, six Project fields, mixed plans/historical actuals, one offscreen pending field and one Undo operation. The frozen integrated-baseline producer creates a 16,125,099-byte initial checkpoint. Generated timestamps differ; this is a fixed workload, not byte-identical data. Standard input is 20 s title, 20 s NUMBER and 20 s alternating vertical/horizontal wheel input; warm adds 10 unmeasured seconds. No build or other experiment ran concurrently with comparative measurements. The profile is a separate run.

Environment: Windows 11 build 26200, Ryzen 7 9700X, 8 cores/16 logical processors, approximately 64 GB RAM, RTX 5070 Ti driver 32.0.15.9636, Balanced power, .NET SDK 10.0.401/runtime 10.0.12. App rasterization scale is 1.25; window 1080 x 760 physical pixels; captured sheet 1042 x 455 pixels (833.6 x 364 DIPs). WMI reports 3440 x 1440 and refresh value 29; that provider value is not independent physical-scanout timing. Other display configurations were not tested.

Native-value latency begins immediately before physical key dispatch and ends at the first exact native TextBox UIA readback, including observer cost. Scroll classification combines commanded movement, native wheel delivery, public viewport notifications and independently timestamped GDI sheet pixels. Rendering callbacks precede composition. The capture bracket includes sampling; it is not an exact frame-presentation timestamp. Durable commits and normal-close checkpoint readback are separate endpoints.

## Matched observations

All rows below completed state/persistence checks and drained traces without missing/dropped/failed records. All final captures exist and their hashes are retained. `Gaps` is the number of >=100 ms callback intervals **during commanded scrolling**; `App delays` counts commands meeting the conservative pixel/viewport criterion, not the same population as callback gaps.

| Run | Title p95 ms | NUMBER p95 ms | Max scroll callback ms | Gaps | App delays | Unexplained callback gaps |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| matched baseline cold 01 | 62.89 | 62.44 | 387.95 | 40 | 8 | 3 |
| matched baseline warm 01 | 62.75 | 57.61 | 370.97 | 45 | 8 | 4 |
| final cold 01 | 63.10 | 66.73 | 353.56 | 28 | 6 | 6 |
| final warm 01 | 61.75 | 61.63 | 342.08 | 33 | 7 | 6 |
| final cold 02 | 79.06 | 61.64 | 260.91 | 32 | 5 | 10 |
| final warm 02 | 69.32 | 62.24 | 289.43 | 28 | 6 | 7 |
| final cold 03 | 70.60 | 57.56 | 281.91 | 28 | 5 | 5 |
| final warm 03 | 65.57 | 62.62 | 333.33 | 33 | 8 | 2 |

Across the six final runs, 37 commands meet `application-viewport-delay`: expected movement, native wheel arrival under 20 ms, public viewport response at/after 100 ms and unchanged independent captures beyond 100 ms. Another 105 commands have stable pixels beyond 100 ms without that complete attribution. The 182 scroll callback gaps comprise 47 correlated application delays, 71 correlated unattributed visible delays, 36 unexplained gaps, 26 gaps with captured progress and 2 clamped/no-op gaps. These counts are different views of the observations, not additive failures or independent statistical samples. Missing pre-input frames, a missing native-delivery event and command/capture overlap remain explicit in the [per-run ledgers](scroll/).

The maximum capture spacing in the six repetitions is 88.73 ms; none has a >=100 ms capture gap. Two measured typing samples exceed 100 ms (100.3513 ms NUMBER in warm 01 and 109.6517 ms title in warm 02). Warmup outliers remain in the raw distributions. Do not turn the p95 pass into an all-input or visible-pixel latency pass.

Concrete example, final cold 01 command 19: vertical offset 7,295.9999 to 0; native wheel arrival 0.9918 ms; public viewport response 104.9300 ms. [Frame 0057](pixels/scroll-0057.png) shows row 241 before input. [Frame 0060](pixels/scroll-0060.png), captured from +125.7411 to +140.3610 ms, still shows row 241. [Frame 0061](pixels/scroll-0061.png), +187.2623 to +207.6151 ms, shows row 1. These are unchanged original PNGs. The first-change upper bound overlaps the next command boundary; it is not an exact presentation time. The unchanged >100 ms bracket and delayed public viewport are still established.

Trace-disabled cold/warm companions also complete normal close and durable readback. Their title/NUMBER p95 values are 58.47/58.40 ms and 61.72/61.78 ms; capture spacing maxima are 79.75/80.82 ms. They retain 28/26 commands with stable captures beyond 100 ms. With no app trace, clamped commands and application-side attribution cannot be fully separated; those runs are not claimed as 28/26 application freezes. They establish that removing the light trace does not remove every delayed captured response. Trace availability and commit counters are N/A, not zero or a failed drain.

## Current cause/fix map

| Observation | Correction and practical limit |
| --- | --- |
| Full diagnostic visual-tree walks allocate WinRT wrappers and cost UI time | Added full/light/off observer selection and matched the light baseline. Full remains available. This is observer isolation, not an application speedup. |
| Row realization constructs native editors for unseen columns | Create them when revealed/focused, preserving existing active/pending controls and field identity. Scroll-only realization spans fall from 1,307/1,325 ms total and 35.4/36.0 MB thread allocations on matched baseline to 579–906 ms and 23.4–26.5 MB in six final runs. These inclusive measured spans and changed realization counts demonstrate less work, not a causal user-visible speedup percentage. |
| Vertical-only ViewChanged repeats horizontal row-shell updates | Retain scrollbar synchronization but skip unchanged horizontal offset/width; refresh visibility when a cached row returns. Total header-span time does **not** improve in these mixed-direction runs: baseline 101.9/109.2 ms versus candidate 106.3–121.4 ms, including deferred creation on reveal. Do not claim this guard solved a pause. |
| Runtime allocation/GC and native finalization remain substantial | The separate final profile covers the full scroll phase with zero lost EventPipe events. It still samples EnsureRow, native editor construction and WinRT finalization. No forced GC, save suppression, priority escalation, cache reduction or gh concurrency is introduced. |
| Saving can overlap the start of scrolling | The last durable commit occurs 188–623 ms after scroll starts in the six runs. Each retains 26–31 long callback gaps after that commit. Saving cannot explain the entire remaining scroll period. |
| Native wrapper release remains unresolved | Both the matched baseline and final corrected host retain 78 sampled unloaded editors against <=65. App cache reduction and earlier zero-unloaded samples are not native-disposal acceptance. |

[Scroll-only spans](scroll-work.json) are inclusive wall intervals and per-thread managed allocation deltas. They do not measure layout/compositor CPU comprehensively. The [separate sampled stacks](profile-stacks.json) cover about 55.3 seconds, beginning during title input: UI-thread EnsureRow 1,099.9 ms, CreateCellEditor 339.3 ms and TitleCell construction 271.7 ms inclusive; WinRT IObjectReference finalization spans 46,790.1 ms on another thread. Inclusive samples include waiting and cannot be added across frames or threads as CPU time.

[Runtime events](profile-runtime.json) contain 549 observed GC starts (AllocSmall generations 0/1/2: 197/241/109; Induced/2: 1; InducedNotForced/2: 1). Boundary-clipped stop events remain in the original. Paired GC/GC-preparation suspension totals are 5,932.4/260.0 ms, max 158.87 ms. `SuspendOther` is separate: 25,414 short pairs, 729.7 ms total, frequent under sampling; these are not labelled application GC. Sampled managed allocation estimates are led by JSON ArgumentState 1.287 GB, strings 1.267 GB, byte arrays 773.8 MB and BitArray 746.1 MB during repeated checkpoint handling. These are neither retained heap size nor exact allocations.

The profile's largest scroll callback gap is 379.4546 ms: UI-span union 255.5111 ms and GC-suspension overlap 172.0020 ms overlap each other. Their union with other measured suspensions covers 256.9640 ms, leaving 122.4906 ms outside those measured scopes. The maximum such unexplained remainder among profiled gaps is 185.8266 ms. Raw per-gap union accounting is retained. Native scheduling, layout/composition and finalizer initiation are not fully attributed; this profile cannot assign a GC cause to an unprofiled run.

## Additional boundaries

Final physical IME executes 20 composition/conversion/confirmation cycles, including a writer-lock failure and explicit retry, preserving final/offscreen pending text, task history and normal-close readback. Trace is complete. It has six >=100 ms callback intervals during typing (max 136.67 ms); this is not a visible-pixel timing test.

Final thumb/distant-edit/horizontal/return reaches task I1000, delivers the edit to its original field, returns to the first row, preserves the first task and closes durably. The single far-row key-to-native readback is 143.1936 ms. Four >=100 ms callbacks during commanded movement are retained: 163.5966, 135.7719 and 204.3312 ms remain unexplained; 196.6025 ms has captured row progress. There is one 108.6578 ms capture gap. State assertions pass; no thumb latency or complete presentation acceptance is claimed.

The 1,000-task Gantt recalculation probe separately fails <=1,000 ms with a maximum 1,298.9222 ms (10 measured samples after two warmups), while its expected calculation/bar assertions pass. Its raw sample array remains in the result archive. P1/P2, broader Q1 timing, other scaling/High Contrast/accessibility, recovery/Apply throughput, physical scanout, live GitHub and human acceptance remain outside this acceptance.

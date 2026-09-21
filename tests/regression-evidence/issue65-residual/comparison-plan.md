# Residual repair comparison

Owner: [#65 decision](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5755462883), routed by #1. Starting product is published main `bbf047fa8f987ea6aacd0ae708b63e68648ded09`. `6762676f6299b2523dc558c42fc59454bffc6f84` adds diagnostic spans and an ownership progression only; the frozen baseline app/host under `TestResults/residual/` has those product sources. Existing post-#68 failures remain in the preceding evidence directory.

The correction targets unnecessary task-save row reconstruction, duplicate Gantt presentation, and construction of offscreen cell content and inactive adornments. Cache size, native input contracts, planning validation, persistence and the pinned SDK remain unchanged. Use one writer and serialize desktop/performance executions; the separate human evaluation is not a prerequisite.

## Selected observations

- Repeat the retained cold/warm sustained-input workload on the frozen baseline, using its existing producer from `issue65-stabilize/TestResults/post68/baseline/producer`. It creates 1,000 tasks, 20 people, six fields, one offscreen pending buffer, Undo history and a 16,125,099-byte initial checkpoint. Use the unchanged physical-key/wheel driver, light tracing, 1080 x 760 window and independent GDI sheet captures. Preserve the complete distributions and per-command classifications; isolate vertical, horizontal and visited-viewport return after the final durable commit.
- Use a narrow candidate trial to decide whether the targeted correction changes work and observed delays. Freeze the final candidate before three cold and three warm repetitions. No retry selection by timing. Native-value p95 <=100 ms, every >=100 ms scroll callback interval, independent viewport/pixel delays, and durable-close state remain distinct outcomes.
- Repeat the ten-sample Gantt public Save -> dialog close -> independently expected text/bar -> two rendering events boundary, after two warmups. Separate candidate preparation, validation/staging/scheduling, UI refresh/presentation, and independent checkpoint saving. The <=1,000 ms bound and the historical 1,298.9222 ms failure remain unchanged. A new baseline pass does not erase that failure.
- Preserve the original 80-roundtrip <=65 unloaded-wrapper probe. Supplement with a separately declared 20/80/160 progression (six-row stride), weak sample references, direct application ownership census, native idle, diagnostic-only GC, view teardown and next-view focus. This is an ownership diagnostic, not a throughput test or proof of disposal from Unloaded. Do not add product GC or increase the limit.
- Optional `GHPB_SHEET_THREAD_TIMING=1` records OS UI-thread CPU between rendering callbacks in a separate diagnostic run. CPU excludes waiting/preemption; the wall remainder is unaccounted time, not proof of a GC or finalizer cause. Keep this run outside the six acceptance repetitions.

## Behavioral validation

Real-control UI integration checks pending editor/caret preservation through task-detail save, header/identity alignment, horizontal realization, focused scroll, selection/frame/fill behavior, Gantt dates/bars/Undo and native retention. The added editor-preservation case must fail against the frozen baseline and pass with the correction. Keep setup errors separately from behavioral Red.

Retain applicable planning/column/bulk logic checks. Selected ordinary-app Gantt/restart and physical direct/F2 or sustained IME checks cover process, focus, composition, offscreen work, failure/retry and normal-close persistence. External GitHub is synthetic; live throughput, human acceptance, general Summary, High Contrast/other scales, accessibility and broader P1/P2/release gates are not established by this increment.

Each execution retains command, source/diff, hashes, environment, counts and raw artifacts. Functional PASS does not absorb performance or lifetime failures. Report the observed effect per correction and the smallest remaining decision rather than substituting a larger test total for a responsiveness result.

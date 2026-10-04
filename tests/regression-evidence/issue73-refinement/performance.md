# Sheet performance and preservation

The measured change reduces the synchronous work needed to present unpublished local work. It preserves the selected cached-sheet journeys at 50 and 1,000 rows. It does not establish faster horizontal scrolling, an input-to-display deadline, human acceptance, or faster remote Apply.

[Comparison JSON](performance/comparison.json) retains the individual aggregate spans, exact byte counts, selected capture intervals, binary hashes, process outcomes and original raw paths. Full traces and checkpoints remain in the four original `TestResults/sheet-diagnostic/` directories. The retained copies below are intentionally smaller than those originals.

## Source and measurement boundary

- Baseline: `bb2c8bc5d821e0b5518ce8d1336f1081c2fc6eaa`; frozen executable under `TestResults/planning-build-41654caf5fb44d00b0ae45400600f3d4/app/`. The [copied source/build receipt](performance/frozen-baseline-receipt.json) preserves `TestResults/issue73-refinement/baseline-load/diagnostics/launch-ace1f209e82649f989d720484b68f233.json`.
- Candidate: that base commit plus the Issue #73 working-tree changes, frozen under `TestResults/planning-build-5e284ca4f356402f82a4a1ed29689d0b/app/`. The [copied final-v2 receipt](performance/frozen-candidate-final-v2-receipt.json) preserves `TestResults/issue73-refinement/final-v2-load/diagnostics/launch-46e2cbfb2ae5491cbd1cc856a9e1d286.json`. It records source inputs and the executed binary set. The runner accepts only a 40-character `SourceRevision`; its base-commit field alone does **not** identify the candidate source.
- App DLL SHA256: baseline `63B761853FE8C0F9B6F4689977C8E729273F579C42CA95D1F6828F9E7EFD3F1D`; candidate `9322ECD1FE6A6C6BFC796B1FF466C8C24EA1164A622E425A0602B906D6BCC3D0`. Candidate Core: `6C30AF10F47127AA12E15B568FFA9BD4484DDC541CE4AFBC003FCD56170F7F5A`.
- Windows `10.0.26200.0`, .NET `10.0.12`, FlaUI UIA3 `5.0.0`, unlocked ordinary desktop. The native window captures were 1080x760 and 1400x900 pixels. Twelve single-select fields plus Title and Repository make **14 total columns**. Fixtures use two synthetic cached Projects and no connection, refresh or Apply.
- Each ordinary workload ran once. Selection/arrows use one warmup and five samples; the remaining actions are individual observations. App spans measure synchronous work and managed allocations, excluding native XAML allocation and composited presentation. Sparse GDI capture intervals include capture cost; they are not physical scanout timestamps.

There are two comparison limits. The candidate also changes the header and therefore the available sheet viewport. The bulk-correctness observer changed an O(n²) dictionary comparison to a sorted comparison, after the existing timing phases. Consequently the full driver durations, especially 79.42 seconds before versus 48.11 seconds after at 1,000 rows, are not product speed measurements. Fresh seed timestamps/hashes also differ; task/field identities, row counts and intended values match, rather than byte-identical initial checkpoint files.

## Causal Core check

The read-only path resolves candidate item IDs before materializing fetched rows. It retains complete related fields, actual shared-Issue appearances including duplicates, local rows, Project-scoped scalar/dependency work, removed fields, orphan recovery, conflicts, projection decisions, ordering and non-mutating reads. The initializing Apply review path remains unchanged. Root independently reviewed the three-file production change and found no major issue.

The opt-in Core workload is 1,000 items and 12 select fields in each of two Projects, one pending Title and one committed select value. Fixture setup and assertions are outside one warmup and seven measured reads.

| Per synchronous read | Before | After |
| --- | ---: | ---: |
| Managed bytes, min–max | 13,867,800–13,868,536 | 2,491,528–2,491,880 |
| Time, min / median / max | 27.64 / 29.99 / 47.49 ms | 7.11 / 7.21 / 7.83 ms |
| Explicit 8 MiB allocation budget | Failed | Passed |

The allocation reduction is about 82%. Timing was observed on a shared machine; it is not a user-visible speed ratio. Exact samples are in [the before TRX](performance/core/sparse-allocation-before.trx) and [after TRX](performance/core/sparse-allocation-after.trx). The relevant read-only semantics passed 7/7 before and after; the affected Apply/creation/planning/weekly-recovery collaborators passed 64/64 across logic and adapter/storage boundaries. Every selected final Core check executed with zero failures and skips. Two earlier fixture-construction failures are preserved in `performance/core/`; they are separate from the causal allocation Red.

## Ordinary runtime observations

The table summarizes all 16 `update-aggregate-presentation` spans in each run, including three cheaper unchanged-revision/save-status calls. These are mixed actions, not repeated identical latency samples. Individual reasons and values remain in the comparison JSON.

| Rows | Version | Managed bytes, median / maximum | Synchronous time, median / maximum |
| --- | --- | ---: | ---: |
| 50 | Baseline | 903,996 / 983,840 | 1.65 / 21.83 ms |
| 50 | Candidate | 372,708 / 988,440 | 1.08 / 7.11 ms |
| 1,000 | Baseline | 17,467,836 / 18,537,976 | 42.13 / 204.78 ms |
| 1,000 | Candidate | 6,629,436 / 8,788,440 | 14.73 / 96.05 ms |

The 1,000-row non-cached status reads dropped from roughly 17.5–18.5 MB to 6.6–8.8 MB. The bulk action touches every row in the 50-row fixture, so its maximum allocation does not decrease; sparse work is the optimized case. A 96.05 ms aggregate span still coincided with a generation-2 collection. Other candidate spans include a 243.05 ms grid constructor and a 103.87 ms synchronous flush call, also with generation-2 collections. Those remaining stalls are retained; this change does not establish a general 100 ms interaction guarantee.

## Values, navigation and lifetime

| Run | Executed / passed / failed / skipped | Paste targets / other fields preserved | Normal close |
| --- | --- | --- | --- |
| [Baseline 50](performance/refine-baseline-50x12/run.json) | 1 / 1 / 0 / 0 | 50 / 1,200 | PID 24180, exit 0 |
| [Candidate 50](performance/refine-candidate-final-50x12/run.json) | 1 / 1 / 0 / 0 | 50 / 1,200 | PID 40168, exit 0 |
| [Baseline 1,000](performance/refine-baseline-1000x12/run.json) | 1 / 1 / 0 / 0 | 100 / 24,900 | PID 46048, exit 0 |
| [Candidate 1,000](performance/refine-candidate-final-1000x12/run.json) | 1 / 1 / 0 / 0 | 100 / 24,900 | PID 17240, exit 0 |

All four runs report no omitted selected phase or unreached requested wheel endpoint. They preserve identified Title input through native wheel/drag and horizontal departure/return, and retain its text through Project switching. Pending buffer, committed value, Undo and rapid native navigation/input are independently read back from the checkpoint; after Project switching the retained Title is committed, not still a pending buffer. The final bulk phase pastes `Done` into exact `P1-status` item IDs, checks every target and every other field, then verifies one Ctrl+Z restores all prior values, buffers, observations and conflict states. Journal entries remain zero. Each original process exits normally without forced termination.

This ordinary-app collaboration ends at real isolated checkpoint storage. It is a selected E2E diagnostic with synthetic cached data, not live GitHub evidence. The optional physical Japanese IME phase was not selected here. Human acceptance remains unrun by this diagnostic.

## Pixel inspection

Reviewed candidate screenshots show readable attained viewports including row 1,000, a retained `diagnostic` Title at the horizontal endpoint, the pasted `Done` values, and `Todo` restored by Undo. No full-sheet blank was observed in the specific inspected frames. This is bounded visual review, not review of every captured frame or continuous smoothness acceptance.

| Image | Evidence |
| --- | --- |
| [50-row horizontal baseline](performance/refine-baseline-50x12/07-horizontal-right.png) / [candidate](performance/refine-candidate-final-50x12/07-horizontal-right.png) | Retained Title input and far-right fields |
| [1,000-row bottom baseline](performance/refine-baseline-1000x12/05-bottom-after-wheel.png) / [candidate](performance/refine-candidate-final-1000x12/05-bottom-after-wheel.png) | Attained last task and surrounding rows |
| [Candidate wide bottom](performance/refine-candidate-final-1000x12/15-wide-bottom.png) | Last task at the larger window size |
| [Candidate paste](performance/refine-candidate-final-1000x12/19-bulk-pasted.png) / [Undo](performance/refine-candidate-final-1000x12/20-bulk-undone.png) | Visible option transition agrees with exact checkpoint checks |

For 1,000-row horizontal departure, inspected frame 001 still shows the previous viewport and frame 002 shows the changed viewport. Baseline frame 002 was captured 103.96–126.46 ms after input began; candidate frame 002 was captured 113.51–125.76 ms after input began. Return frame 002 intervals were 100.21–121.96 ms and 107.85–126.54 ms respectively. These sparse observations show no demonstrated horizontal-presentation speedup. The copied 001/002 images and frame JSONs retain the capture bounds. In the extreme vertical-wheel sample, frame 002 shows rows around 820; a later endpoint step reaches row 1,000. The extreme wheel stimulus alone is not bottom-reachability evidence.

## Commands and retained attempts

The E2E driver was built with `dotnet build tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj -c Release -p:BuildProjectReferences=false`. The final driver build had zero errors and one nullable warning in `SummaryJourneyTests.cs:32`. The Core test build reported the existing nullable warning in `PlanningProjectFieldTests.cs:86`.

For each of the two frozen executables and row counts, the selected invocation was:

```powershell
./scripts/Test-SheetDiagnostic.ps1 -RunId <recorded-run-id> -ItemCount <50-or-1000> -SelectFieldCount 12 -NoBuild -Executable <absolute-frozen-executable> -SourceRevision bb2c8bc5d821e0b5518ce8d1336f1081c2fc6eaa -Trace -Frames -BulkCorrectness
```

The four recorded run IDs are the linked directories above. Each copied `run.json` records the selected commands and flags; `plan.json` records exact executed App and driver hashes. Current seed exe/DLL/Core hashes were checked against final-v2's frozen seed before candidate execution. An initial candidate invocation used explanatory text in `SourceRevision`; parameter validation rejected it before any app started. It was corrected to the accepted base SHA without changing product code or replacing a run artifact.

Core commands and their declared boundaries are also recorded in [the parent review](README.md#core-work-reduction). All before/after TRXs, including failed fixtures, are copied under `performance/core/`. No raw result was rewritten and no existing failed run was relabeled as passing.

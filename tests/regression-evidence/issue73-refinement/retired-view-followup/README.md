# Local main follow-up: table replacement lifetime

The user requested local main integration and an explanation for stopping with unresolved work. The reviewed refinement commit `e20d9b5562ec2e7097e4581eb21f22ec2e229560` was fast-forwarded into local main, with identical trees and a clean working directory. This follow-up starts from that commit. [Issue #73](https://github.com/fukuda-yuki/gh-projects-boards/issues/73) owns current delivery and acceptance status.

The earlier stopping decision was premature: a passing focused rerun did not identify the original retention failure, and a publication restriction did not prevent local investigation. Re-reading the original trace identified a concrete, removable cost rather than requiring another speculative grid replacement.

## Product correction

Constructing P2's table started a draft save while P1's table was still loaded. The saving notification made the outgoing table recalculate its status, taking 103.521 ms and allocating 8,173,840 bytes in the retained observation. The constructor now leaves the save to the existing Loaded handler. Existing IsLoaded checks reject presentation work for the outgoing table even when its Unloaded event arrives later. No new retirement state, data model, dependency or storage behavior was introduced.

The real two-view regression establishes the visible save state, failed-save retry and independent durable recovery of both Projects' pending buffers. The ordinary 1,000-row trace confirms the old aggregate is absent, and the replacement's nested notification update allocates 1,904 bytes. Total Project-switch and initial-display latency did not show a reliable improvement; the [performance report](performance.md) includes the slower observations and all timing limits.

Three independent reviewers checked the save/transition contract, IME and pending-work boundaries, failure recovery, source changes and the retained pixels. The final source review found no further consequential defect in this correction. This does not close the broader product acceptance gates.

## Executed checks

The [UI ledger](ui-ledger.json) indexes all eight hosted runs, including the behavioral Red, failed attempts and source counterfactual. Each run retains its exact command, source/binary hashes, raw result and source diff. Tests use real WinUI controls/events and isolated real stores, with declared synthetic external boundaries.

| Selection | Executed / passed / failed / skipped | Result boundary |
| --- | --- | --- |
| New replacement save-state case before the product change | 1 / 0 / 1 / 0 | Expected saved text changed to saving during unmounted construction |
| Same case after the product change | 1 / 1 / 0 / 0 | Mounted failure/retry, old presentation unchanged, both pending buffers recovered |
| Final replacement/navigation/remount/retry selection | 6 / 6 / 0 / 0 | [Final scoped run](ui/run-20261005-003937-650-06df570a/results.xml) |
| Same broad selection as the preceding refinement | 116 / 113 / 3 / 0 | [Broad run](ui/run-20261005-004427-295-f37c35ef/results.xml); retention case passed, three other cases failed |
| Those three cases plus both other date-warning variants | 5 / 5 / 0 / 0 | [Targeted final run](ui/run-20261005-005141-316-c2388bfe/results.xml) |
| Ordinary 1,000-row comparison and passive return observation | Each 1 / 1 / 0 / 0 | Same frozen product executable; exact bulk/Undo and normal close, no GitHub writes |

Counts overlap and are not a unique-case total. There is no claim of a later single all-green 116-case run. The six-case selection includes navigation/remount cases that were absent from the original broad selection because their class is the partial `HostedTests`, not `WorkspaceShellHostedTests`.

The driver repairs preserve their expected behavior:

- A valid title commits when focus leaves under the current specification. The failed-save navigation case now uses invalid whitespace and still checks the exact visible text and buffer, original confirmed value, selected Project, open navigation and no writes.
- Panel remount invokes the same add-row command through the existing native overflow helper and retains the row-creation assertion. An intermediate edit to a similarly named call was corrected; its failed run remains in the ledger.
- First-use readback waits for the actual saved presentation and matching durable/workspace revisions before independently reopening storage. It additionally checks readback Problems is empty. No direct flush is added to make the product's autosave pass. Writer-lock contention is the source-based explanation for the earlier empty read; the failed run did not retain its Problems list.
- Date-warning checks wait for the RadioButtons selection event to update the same warning before asserting absence/presence. All schedule, Journal and other correction expectations remain.
- The allocation-total poll no longer searches the main UI tree when its expected popup is absent. A continued absence still fails at the existing timeout. The targeted run passed without emitting the new one-time missing-popup diagnostic; the original lookup failure was not reproduced and no product-level cause is claimed.

## Retention and pixels

The original failed retention run reclaimed all 160 sampled editors but retained the removed EditingGrid after its bounded observation. This follow-up keeps the existing timeout, GC boundary and assertion unchanged and adds a failure-only census of known owners. The verdict is fixed before taking temporary diagnostic references. It records view/focus ownership, session subscribers, pending save state, dispatcher counters and runner/UI test-context identity, without input values or exception messages. Negative results would not prove a GC root.

In the same broad selection, the old view was alive before focus transfer and released afterward. The failure-only census therefore did not run. The earlier failure remains unexplained, not repaired by the successful rerun. Its original evidence remains in the parent package.

Direct pixel review also exposed blank immediate Project-return images in both old and new candidates. The added passive captures occur before UI Automation reads or any input. In the sampled run, rows were visible 152.5710–191.8449 ms after the original capture began, and stayed visible at the later samples. This establishes spontaneous recovery for that observation, not a continuous-frame or 100 ms guarantee. The original blank images are retained. Root independently inspected the first and final passive images.

## Scope and source

The final [source check](final-source-check.json) matches all 111 production inputs to the frozen tested application. Only EditingGrid's save-start placement changed from the preceding frozen source. The passive diagnostic's final post-run change is indentation only. Scoped attributes preserve the raw JSON/XML/TRX/JSONL/log/diff bytes; copied evidence is hash-checked.

Initial construction, horizontal presentation, user acceptance, comparative workflow value, GHEC/EMU and live Apply throughput remain distinct unverified boundaries. A possible repeated row-projection optimization was not mixed into this correction: first and subsequent initialization can differ in field warning state, so blind reuse would require separate semantic evidence. An unrelated running app from another checkout was left untouched.

No main push, release acceptance or Issue closure follows from local integration. The current commit and local integration result are recorded in the owning Issue.

# Independent review dispositions

The reviews inspect frozen public source and do not constitute Windows execution or human acceptance. Original responses and attachment manifests are retained in the portable package. The requested browser model was `gpt-5.6-sol` with heavy effort; the observed effort picker was Pro. This is picker evidence, not independent verification of the server-side model generation.

## Assignment and data review

Frozen source: `58fae0ab07c390232d1069ab6618b456c6d1829e`. Recovered response: `corrective-review/independent-review.md`. The initial browser transport disconnected; the same session was reattached rather than duplicated.

The attachment manifest contains 42 source/test files. Although the prompt also mentioned a full patch, that patch was not in this review's final attachment list; its review scope is the actual attached files.

| Finding | Disposition and observed replacement coverage |
| --- | --- |
| H1: empty breakdown Update clears an unattributed fetched total | Core rejects empty reports through Update; explicit Remove remains available. Core Red and actual-control error/preservation check. |
| H2: one endpoint edit clears the other observed day with unknown time | Candidate retains exact Manual metadata, never fabricates time from a day, and requires explicit confirmation/clearing for an unknown-time opposite endpoint. Core Red matrix plus actual editor day/time confirmation. A further two Core Reds found unrelated Actual/Estimate edits could clear an unchanged unknown Manual endpoint; projection now retains it. |
| H3: Details drops sparse contributors or a blanked historical report | Preserve known worker identity with nullable operands and null collection semantics. Removing an individual report uses the explicit breakdown action. Actual-control Red/Green preserves sparse work and rejects implicit history removal. |
| H4: legacy Unplanned owner changes implicitly | First Estimate does not initialize a legacy Unplanned task; Manual edits retain the legacy owner. Explicit Auto comparison adopts native assignment. Two Core Reds/Greens. |
| M1: contextual date buffer is newer than displayed Boards cell | Refresh realized peers sharing the same field during contextual edits. Actual-control Red/Green after separate save notifications and Close. |
| M2: non-Issue scheduling throws asynchronously | Check stable Issue/local-row eligibility before preparation. Hosted production event reproduced the asynchronous exception; Pull Request and Draft cases pass after the guard. No ordinary-process crash is claimed from that hosted result. |
| M3: detached arrays break retained-task preview equality | Compare scalar and nested collection content, allowing only semantic legacy tagging. Core Red/Green accepts unchanged retained tasks in detached preview and still rejects their modification. |
| M4: observed out-of-domain NUMBER throws on Actual selection | Keep raw remote text, show a correction problem, and require explicit worker selection. Three Core Reds/Greens and actual-control selection. |
| M5: modern unresolved assignment displays provisional 100% | Restrict legacy provisional presentation to legacy semantics. Two actual-control Reds/Greens for complete-empty and incomplete assignment. |
| M6: date picker clear leaves stale exact text | Synchronize picker clear with the exact input. A chosen day with no time remains incomplete rather than inventing a time. Actual-control Red/Green; final extended case covers choosing a day and then its time. |

The initial regression run observed **11 Core failures**. After those fixes, related **33 Core cases passed**. UI regression preparation initially failed to wait for a realized footer control; that failed attempt remains separate from the five reproduced UI behavior failures. A later related UI run passed **34/34** before the final retention/picker/delayed-view additions. The subsequent planning/Gantt selection passed **121 executed**, with **2 gated live cases skipped**; no live test ran. These are individual runs, not an invented aggregate all-pass count.

The prior performance-only review on `de18efd` identified four additional save/snapshot/IME defects. They were separately reproduced and fixed before `58fae0`: new requests during a failed shared flush, reentrant completion work not yet durable, aliased nested snapshot arrays, and undelivered IME-deferred refresh. The broader review confirmed those source fixes. Mutable workspace access remains on its caller/UI context; only detached snapshot validation and persistence run on a worker.

## Late UI and launcher review

Frozen source: `ef8fa45745bf9754a0635ffd5f3e1b7f949769cb`. Response: `late-ui-review/independent-review.md`.

- **Inactive control retention:** an 80-hop Boards/Gantt/Boards check retained 78 sampled unloaded editors. The first rendering-wait probe timed out and did not reach its retention assertion. Reconsidering previously protected rows alone still failed; removing an unrelated generation guard alone still failed. Temporary diagnostic inspection showed 938 owned unloaded rows, zero dormant entries and generation 0. A queued Low-priority barrier then timed out after ten seconds while normal callbacks continued. Normal-priority deferred cleanup reduced the same sampled unloaded count to **0** and passed the bound. That observed correction explains a delivery problem in cleanup, not every native rendering gap. The final test also returns to the original pending native editor and caret. Temporary reflection was diagnostic only and is absent from the retained test.
- **Stale launcher binaries:** the launcher rebuilds the ordinary app and fixture, retains build logs and source-content hashes, and rejects source changes during the build. HEAD alone is not treated as the binary's source.
- **Copied/invalid resume marker:** require synthetic/readback flags, a nonempty isolation GUID and matching normalized root. Four rejected-root probes passed with byte-identical original files. Fresh mode still rejects existing paths.
- **Delayed view change hypothesis:** preparation now also checks the originating Boards/Gantt view and anchor visibility. A bounded controlled-preparation test exercises the real command followed by view change. No unexecuted native exception is asserted.

The follow-up reviewed `6c932abb4ff21e28c28076a58bc3656ae2780a97` (12 attached files including the patch; response in `review-final-fixes/independent-review.md`). It found three further concrete edges. Corrections are committed at `ad56c06bfd8fbc314d6c92a5bc6692dcbb244dac`:

- A candidate could omit an absent retained task. Commit now compares the retained set in both directions. The omission regression failed before the fix and then passed with unchanged-snapshot rejection.
- A direct Manual candidate could clear an unknown-time observed day when the previous mode was Auto/Unplanned. The projection boundary now preserves unknown endpoints unless explicitly decided, independent of that prior mode. Both mode cases failed before the fix and then passed.
- Choosing time before date erased a known day; clearing and reselecting a date reused its old time. Both actual-control regressions failed before the fix. The editor now initializes a known day independently, preserves incomplete text on a time-only choice, and clears both native components for explicit removal. Both cases passed afterward.

Final related Core selection: **35/35**, no skips. Final actual-control planning/Gantt selection, excluding the already-passed 80-hop retention case and opt-in performance cases: **37/37**, no skips. The 618/618 broader Core run predates these three narrow fixes and is not relabelled as a full regression of `ad56c06`.

The broader UI run initially observed 61/78 passes: one overflow-command lookup error plus an unexplained async teardown timeout that contaminated 15 later cases. RowView passed 5/5 in isolation; a subsequent 51-case run passed all RowView/column/sheet/focus/viewport cases and failed one newly rendered Apply readiness check (50/51). Fixing the public command/readiness paths produced 7/7 selected passes. Original failures remain in the ledger; the transient teardown cause is still unproven.

## Design and limits

The task is to enter effort, dated actuals and exact endpoints in context, then inspect the same task in Gantt. The implementation keeps native controls, identity, pending text, concise local errors and explicit decisions. No new scheduler, workspace, reporting database, transport or publication path was added. The installed WinUI review skill was applied with repository precedence: existing view/dialog composition and the explicit Japan-minute contract were retained rather than introducing a framework/style/localization rewrite. High Contrast, other scaling, assistive readout and human reevaluation remain unverified. The WinApp analyzer workflow was unavailable; no machine setup change was made.

> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/checks/daily-progress/focused-validation-report.md`
> Original SHA256: `A9F397CF6D78AB27B9722F70AAF0C5B99FD3BA0F98C494AF54B34E03F5F19F06`

# Daily progress focused validation

The implemented path provides a first-class Gantt `実績・進捗` command, independent cumulative Actual and Remaining inputs, explicit progress and required actual dates, and the existing task-details route for complex attribution. Confirm uses one atomic planning operation. Ordinary raw input uses existing durable buffers. Cancel restores this editor's entry buffers; changing Project or unloading preserves raw input without adopting the compound candidate.

The conditional `変更後の日程を見る` route uses the existing selection reveal and does not scale or move the viewport automatically. Existing Actual contributors, unattributed reports and explicit zeroes remain in Details; an explicit Add worker action replaces twenty empty report rows. Multiple-report contexts do not invent a combined report date.

## Observed corrections

- Native scrolling settles asynchronously. The reveal test now waits for the same required visible-date condition before asserting it.
- ContentDialog popup removal precedes its asynchronous close/save completion. The Gantt command is disabled for the complete owned callback and re-enabled in `finally`; tests wait for this public enabled state when repeating the job.
- Cancel restoration and durable save complete after `ShowAsync`. The test waits for the required null buffers and durable revision; it still asserts unchanged adopted values and progress.
- The existing Weekly test used a throwing Single locator while an Expander child was not yet realized. Its predicate now searches for the expected loaded control without throwing during realization.
- The full repeated-task test exposed a real Undo defect: confirmed Remaining text returned as an unconfirmed buffer. The form now declares both owned inputs. Core accepts ordinary Estimate/Remaining consumption only for exact keys resolved from the same already-validated scalar value batch. Estimate shares the existing scalar batch contract; no additional UI or ownership route was introduced. Unowned, other-row and other-role consumption is still rejected atomically. Existing Actual/Start/Finish and Project checks remain.
- An unchanged Confirm is a dismissal. A same-value pending input is still explicitly confirmed. One Undo after reopening and confirming unchanged reverts the preceding actual update, with no empty transaction in front of it.

## Execution evidence

All paths below are repository-relative. Failed attempts remain original.

| Run | Boundary | Executed / passed / failed / skipped | Observation |
| --- | --- | --- | --- |
| `TestResults/ui-integration/run-20260927-195421-622-5d72afcd` | Parent-run UI integration | 7 / 4 / 3 / 0 | Original Daily failures; historical four passed. |
| `TestResults/ui-integration/run-20260927-200706-822-4cc72d8d` | Parent-run UI integration | 8 / 5 / 3 / 0 | Reveal/cancel/weekly observation boundaries failed. |
| `TestResults/ui-integration/run-20260927-200933-221-ea8784d0` | Daily three + Weekly one | 4 / 3 / 1 / 0 | Immediate repeat command preceded prior close completion. |
| `TestResults/ui-integration/run-20260927-201112-410-77df5b96` | Daily three + Weekly one | 4 / 3 / 1 / 0 | Full Undo endpoint exposed restored Remaining buffer `3`. |
| `TestResults/ui-integration/run-20260927-201333-862-32314b72` | Daily three + Weekly one | 4 / 3 / 1 / 0 | Existing typed-input guard rejected explicit Remaining consumption; dialog remained open. |
| `compound-consumption/red.trx` | New Core ownership cases | 5 / 3 / 2 / 0 | Exact guard exception observed before production fix. |
| `compound-consumption/green.trx` | Planning input, confirmed Undo, planning path and transitions | 57 / 57 / 0 / 0 | Owned scalar/Actual consumption and excluded ownership cases passed with existing affected coverage. |
| `TestResults/ui-integration/run-20260927-201826-361-798682ef` | Daily three + Weekly one | 4 / 4 / 0 / 0 | Final real-control collaboration passed, including repeat-task lifetime, no-op confirmation, Undo, cancel durability and sparse/multiple reports. |

UI runs use the production-sharing WinUI host and isolated local draft storage; no remote writes or native ordinary-app acceptance were performed here. Each host run retains metadata, build log, binary hashes and NUnit results. Final host build: zero errors; retained warnings include SDK auto-initializer conflicts and two object/record comparison sites in the form.

Exact inputs are retained in `final-focused-source/receipt.json` with 207 source/project/resource files, plus the run's tracked source diff and baseline commit. `final-source-verification.json` records the post-run hash comparison. Earlier corrections have separate before/after receipts; Core Red and Green commands and source hashes are in `compound-consumption`.

## Visual scope and remaining acceptance

`final-images/daily-progress-candidate.png` was inspected: task identity, adopted dates, ordinary inputs, progress/report context and Confirm/Cancel are visible at the hosted size. `daily-progress-sparse-actual-reports.png` captures the upper Details viewport; its report rows are below the image. The latter is not pixel evidence of the sparse breakdown even though real loaded-control assertions passed. Ordinary-app task completion, physical input, human readability, remote behavior and publication remain separate evaluation boundaries.

The UI host exited normally after the final pass. Native UI/build ownership was returned to the root agent. Pending-state clarification remains a read-only plan until the next candidate is frozen.

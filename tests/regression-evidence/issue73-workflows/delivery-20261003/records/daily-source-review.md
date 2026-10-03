> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/checks/daily-progress/independent-source-review.md`
> Original SHA256: `D5544CF384C10549B36E3CDF6F3D609D044E7B3E88472CBBEEA8DC08025166DE`

# Independent daily progress source review

Date: 2026-09-27. Branch: `codex/issue-73-weekly-recovery`. HEAD: `fd5732ad4f063665bbc23be96247dd8d620d80d2` plus the uncommitted D workflow changes.

Review boundary: `DailyProgressEditor.cs`, `EditingGrid.Gantt.cs`, `GanttView.cs`, `PlanningTaskDetails.cs`, the dialog cleanup line in `EditingGrid.cs`, `DailyProgressHostedTests.cs`, and related `PlanningHostedTests.cs` changes. Core planning/input/history code was read only to follow existing collaborators. Applied `AGENTS.md`, `DESIGN.md`, `tests/README.md`, the current daily-progress amendment and installed WinUI code-review skill. No production/test edits, build, test execution, native interaction or remote action were performed by this reviewer.

User job: update consecutive tasks' cumulative Actual, report context, independent Remaining and explicit progress from Gantt; adopt each update as one local operation; Undo that operation; retain pending work and the timeline; use Details for exceptional attribution.

## Findings

### D-SR-01 — Warning / P2: unchanged Confirm adds a visible no-op Undo step

**Reviewed source:** `src/GhProjectsBoards.App/DailyProgressEditor.cs:145-172`; collaborator `src/GhProjectsBoards.Core/Projects/PlanningWorkspace.cs:174-184`.

For an already initialized task with no pending buffers, open the daily dialog and press Confirm without changing anything. Both ownership flags are false, but the handler still calls `CommitPlanning`. The existing Core no-op escape requires a nonempty `consumeBuffers`; this call sends an empty array. `SetPlanning` advances the plan stamp and the normal path appends a planning history entry. The next Undo restores an equivalent plan, so the prior meaningful update requires a second Undo.

This is established from the current source path, not an observed UI run. Avoid committing an unchanged candidate without owned input, while retaining the existing confirmation path for preexisting raw buffers. Add a real-control regression that makes a meaningful update, reopens/Confirms unchanged, then verifies one Undo reaches the previous values. The coordinator has assigned the bounded correction; no corrected result is asserted here.

### D-SR-02 — Warning / P2: multiple reports display a reporting date that belongs to none of them

**Reviewed source:** `src/GhProjectsBoards.App/DailyProgressEditor.cs:62-83`; collaborator `src/GhProjectsBoards.Core/Projects/PlanningCellInput.cs:28-43`.

For multiple actual reports, `ActualInput` deliberately returns no single `ReportedThrough`. The dialog falls back to the last globally confirmed report day or Project cutoff and displays that value in the disabled `DailyReportedThrough` picker. Example: reports through October 8 and October 9 with a December 1 cutoff display December 1 as “report through,” although neither retained report has that date. A missing/unavailable Actual context takes the same fallback route. Reports are not mutated, but their presentation asserts an unrelated date.

Hide the combined worker/date editors when there is no single editable report context, or explicitly describe the distinct report dates and direct the user to Details. Preserve a single historical report's actual date and worker. Add a scoped UI case with different contributor dates and a different cutoff. The coordinator has assigned the correction; no corrected result is asserted here.

## Other inspected paths

- The normal changed candidate uses one `CommitPlanning`; independent Remaining remains a separate explicit value edit, and historical single-worker Actual is retained. Multiple reports are read-only in the compact dialog, so no automatic distribution path was found.
- Normal Cancel restores the exact entry Actual/Remaining buffers only while the same workspace, generation and revision are current. Invalid input does not mutate adopted values. Dirty navigation to Details requires explicit discard or continued editing. An invalidated/unloaded editor does not roll back newer workspace state; this distinction remains runtime-unverified.
- Unmapped/unavailable Actual produces read-only Actual plus an explanation; it does not dereference an absent Actual cell on the confirmation path because `ownsActual` is false. Completing a task still requires a usable Remaining 0 and actual endpoints. No silent role rebinding was found.
- `ShowChangedSchedule` records the changed task; `Present`, `UpdateSelection` and final `UpdateGantt` recalculate visibility without clearing that identity. The normal final update therefore does not erase the reveal state in source. The existing Present path retains the absolute timeline day and vertical position; actual pixels and native focus remain unverified by this review.
- Sparse Details initializes only retained Actual reports, including zero and unattributed rows. Explicit Add creates a worker row; validation and dirty tracking are registered through the existing real form helpers. Optional effort attribution remains separately disclosed.
- A lower-priority presentation issue remains at `EditingGrid.Gantt.cs:90`: the changed-schedule hint clears after `Run(Undo)` even when `Run` catches an Undo rejection. The unchanged offscreen schedule then loses this contextual hint, although the general reveal command remains available. Clear it after successful Undo only; this is not a data-loss finding.

## Evidence limits and test scope

The three new cases cover the two-task update/Undo/reveal flow, invalid/cancel/dirty-navigation behavior, and sparse contributor controls through real views and event paths. They do not yet cover the two findings above, preexisting-buffer Cancel, multi-report compact presentation, native IME, themes/scaling, durable restart, or ordinary-product usability. Source inspection cannot establish those outcomes. The coordinator reported the retained first independent run (`run-20260927-195421…`) had three failures; this reviewer did not execute or reinterpret that result, and makes no subsequent test-pass claim.

Reviewed hashes (SHA-256, before the separately assigned source-review corrections):

| File | SHA-256 |
| --- | --- |
| DailyProgressEditor.cs | `8F0007E2C35453EE72F5E6FAC481A59081B709100F3FF5ECC994F648CEFFE89D` |
| GanttView.cs | `26EA9FBB5BD45F39C28FA779518FD6C55333D8F021CB74DD770C8369FAB80FBE` |
| PlanningTaskDetails.cs | `8223525B5489637E005C7BD2E8AC01E0003E2FD60CF1F17B8A0EE081E4BA7801` |
| EditingGrid.Gantt.cs | `636E3691988B7C4787A61DBE7E28CBA439250BE8C97EF1FCA753C7FE4449F80B` |
| EditingGrid.cs | `BFBECCE7E4D80E73B291C7DB797D4D53EC3332380A5F07185A83EE500B1365E2` |
| DailyProgressHostedTests.cs | `F95C88A0E53F586993EC5F296DA09BC29DF70FB636597A4B97070BED68FAF90E` |
| PlanningHostedTests.cs | `77D778C7C7329A2F6C5068A28A752DF8A3FE5E79020246AF870285F715630289` |

# Corrective branch handoff

> Follow-up: the user requested local integration into the original Summary
> checkout. See the [local integration handoff](../issue64-local-integration/README.md)
> for its branch, merge source and validation. The standalone publication
> commands below describe the earlier repair branch, not the combined checkout.

Local implementation and selected validation are complete for this bounded repair. Remote publication is blocked by the execution policy, not missing user authorization. The rejected push did not run. No corrective PR, CI, merge or release exists.

- Branch: `codex/issue-61-65-input-repair`, no upstream. Main remains `3dcb3364d6c65f07f04b600e3b65f5ad53403557`.
- Final app/Core source: `ad56c06bfd8fbc314d6c92a5bc6692dcbb244dac`. Launcher-only correction: `802004a2a38abfea141a365e0e502a075390306a`. Later commits contain evidence/handoff only.
- Core 621/621; final planning/Gantt controls 37/37; representative ordinary app/fake-gh journeys 2/2. Cold/warm/physical-IME/save-retry/scroll observations and all earlier failures are retained. See [the evidence index](README.md), [review dispositions](review-dispositions.md) and [selected receipts](validation.json).
- Remaining: 100 ms+ scroll gaps (max 288 ms in the final cold run), unexplained earlier hosted teardown timeout, other scaling/High Contrast/readout, broad Q1 obligations and human reevaluation. These are not passed by the local counts.
- Unit D stays intact at `671011ad04beb7b7cce8df8328808d7de1aaf13a` on `codex/issue-64-summary`, draft PR #68. Do not edit its shared code concurrently. After the corrected loop's human reevaluation, resume only permitted Summary work and reconcile Draft 10/Planning 2 against this repair's 11/3 without importing evaluation data or replacing the canonical workspace.

## Manual publication

Run from this corrective checkout after reviewing the evidence. The commands target the Issue branch and create a draft; they do not merge or release:

```powershell
git status --short
git branch --show-current
git push --set-upstream origin codex/issue-61-65-input-repair
if ($LASTEXITCODE -ne 0) { throw 'Push failed; do not continue.' }
& 'C:\Program Files\GitHub CLI\gh.exe' pr create --repo fukuda-yuki/gh-projects-boards --draft --base main --head codex/issue-61-65-input-repair --title 'Restore contextual Boards planning and reduce sustained input work' --body-file tests/regression-evidence/issue61-65/PR-body.md
```

Attach the resulting PR to the task and post its evidence links to #61/#65. Use `Refs`, not `Closes`: human acceptance and remaining quality gates are open. CI is a separate environment and must be read after publication rather than inferred from these local results.

## Isolated human reevaluation

```powershell
./scripts/Start-PlanningCheck.ps1 -Scenario Weekly
# Initial setup, without preassigned task-planning metadata:
./scripts/Start-PlanningCheck.ps1 -Scenario Fresh
# Existing Gantt entry point, now using the same protected launcher:
./scripts/Start-GanttCheck.ps1
```

Use one app at a time; the launcher rebuilds the ordinary app. Select the printed saved profile/P1. The task goals are initial planning and consecutive weekly actual/remaining updates with understandable moved dates, not following hidden control IDs. Close normally; use `-Resume -DataRoot '<printed absolute root>'` for that current evaluation root. Old roots are retained and rejected by the protected launcher rather than overwritten. No GitHub connection is required. Human usability/readability and the continuation decision remain with the user; do not ask them to run the engineering regression suite.

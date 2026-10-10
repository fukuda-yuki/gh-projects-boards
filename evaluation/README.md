# Waterfall version evaluation (#126)

Two synthetic waterfall versions share one WBS shape: 40 requirement Issues with 25 tasks each (1,040 Issues) and 20 people. Titles and keys are the registered sandbox WBS identities.

| File | Version | Project | Default 状況日 | Recorded status dates |
| --- | --- | --- | --- | --- |
| `version-plan.json` | 第2027.04版 | 2026-10-13 to 2027-04-30 | 2027-01-13 | 2026-12-16, 2027-01-13, 2027-02-17 |
| `version-plan-12m.json` | 第2027.10版 | 2026-10-13 to 2027-10-29 | 2027-03-24 | 2027-02-03, 2027-03-24, 2027-06-02 |

Regenerate both with `python scripts/evaluation/generate_version.py`; `--check` verifies the tracked manifests. The test executable embeds them. Effort values are placeholders, not the owner's real template.

## Shape

- Every task has a 工程 value: SA, UI, SS, PS, PG, PT, IT, ST or OT. The fake gh serves it as the single-select Project field 工程.
- Each person owns two requirements end to end. Within a phase they finish the first requirement's tasks, then the second's, so phases run as windows across the version; UI and SS share one window. IT, ST and OT start on version-wide dates.
- Each phase has a milestone (`milestones`): the Friday of the week the baseline plan finishes it. The baseline meets every milestone.

## Simulated progress

The fixture replays the team's work day by day from the project start with the real scheduler. Each day tasks are processed in scheduled dependency order; work is recorded only after every predecessor has finished its work, including same-day handoffs. Hours blocked by a predecessor's re-estimate are not worked. The team records 実績 and keeps 残 as its current estimate (#131). A task's Issue is closed on the day its work is done, with 残 0 and 終了日 on that day. Each status date is a plan the PMO has just published, so a fresh evaluation opens at 未発行 0.

Deviations:

- PS for U3–U8 takes half as long again as estimated. 残 is raised once half the estimate is spent, so 見込 exceeds 見積 and PS finishes after its milestone, delaying later phases.
- U7 also builds the shared parts (製造 共通部品) of R02–R06 and is overloaded during PG.
- U15 and U16 finish the SA, UI and SS tasks with a quarter of the estimate unused.
- R09 SS-003 is open with 残 0.

`evaluation.json` in the data root records, for each recorded status date, the measures from the real scheduler: 見積, 実績, 残, 見込, 差異, progress (実績 ÷ 見込), closed tasks, open overruns and open tasks with 残 0, in total, per phase (window, forecast finish against the milestone in working days) and per person (overloaded days in the next 20 working days). Milestones are recorded there; the app does not read them yet (#133).

## Live sandbox

Read the [authorized scope](https://github.com/fukuda-yuki/codex-sandbox/issues/1). All script writes target `fukuda-yuki/codex-sandbox` and the owner's Project 3, 第2027.04版. The script selects these targets explicitly, removes token/host overrides from its child environment and uses gh's stored authentication.

From the repository root:

```powershell
python scripts/evaluation/version_sandbox.py register --evidence TestResults/version-sandbox
python scripts/evaluation/version_sandbox.py register --evidence TestResults/version-sandbox --apply
```

Without `--apply`, commands only read GitHub and write local evidence. Registration completes R01–R39 (1,014 Issues), or R01–R40 with `--through 40`, preserving existing Issue identities, planning values and unrelated or inaccessible Project members. It refuses duplicate WBS keys, changed titles, existing conflicting parents and mass recreation. Only missing Issues, memberships, hierarchy and WBS order are written.

The optional `inputs` command exports a first-pass TSV for the ordinary sheet, never a GitHub mutation. It defaults to the whole WBS (`--through 40`); `register` still defaults to 39. A selected range with an outside predecessor is refused before any file is written, naming the missing key and directing the caller to use `--through 40`. Its numeric predecessor references require the entire visible Issue order to match the selected WBS exactly; it refuses anything else before writing.

The live rewrite to the waterfall shape, with the 工程 field, milestones and weekly progress, follows the summary view (#118, Batch 2). Planning values are entered and published through the app, not by script.

## Evidence

Record source and binary identity, data root, real or substituted endpoint, row/task counts, operation results, screenshots and timings. Offline automated results, ordinary-app product review, real-GitHub readback and the owner's acceptance are distinct claims. Run `python -m unittest discover -s scripts/evaluation -p 'test_*.py' -v` for the sandbox script's safety contracts.

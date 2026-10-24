# Waterfall version evaluation (#126)

Two synthetic waterfall versions share one WBS shape: 40 requirement Issues with 25 tasks each (1,040 Issues) and 20 people. Titles and keys are the registered sandbox WBS identities.

| File | Version | Project | Default 基準日 | Recorded status dates |
| --- | --- | --- | --- | --- |
| `version-plan.json` | 第2027.04版 | 2026-10-13 to 2027-04-30 | 2027-01-13 | 2026-12-16, 2027-01-13, 2027-02-17 |
| `version-plan-12m.json` | 第2027.10版 | 2026-10-13 to 2027-10-29 | 2027-03-24 | 2027-02-03, 2027-03-24, 2027-06-02 |

Regenerate both with `python scripts/evaluation/generate_version.py`; `--check` verifies the tracked manifests. The test executable embeds them. Effort values are placeholders, not the owner's real template.

## Shape

- Every task has a 工程 value: SA, UI, SS, PS, PG, PT, IT, ST or OT. The fake gh serves it as the single-select Project field 工程.
- Each person owns two requirements end to end. Within a phase they finish the first requirement's tasks, then the second's, so phases run as windows across the version; UI and SS share one window. IT, ST and OT start on version-wide dates.
- Each phase has a milestone (`milestones`): the Friday of the week the baseline plan finishes it. The baseline meets every milestone.

## Simulated progress

The fixture replays the team's work day by day from the project start with the real scheduler. Each day tasks are processed in scheduled dependency order; work is recorded only after every predecessor has finished its work, including same-day handoffs. Hours blocked by a predecessor's re-estimate are not worked. The team records 実績 and keeps 残 as its current estimate (#131). A task's Issue is closed on the day its work is done, with 残 0 and 終了日 on that day; closedAt is an explicit UTC timestamp whose local date is the replayed close day. An open zero-remaining task is still incomplete and moves to the status-date boundary; successors that already worked retain their historical starts. Each status date is a plan the PMO has just published, so a fresh evaluation opens at 未発行 0.

Deviations:

- PS for U3–U8 takes half as long again as estimated. 残 is raised once half the estimate is spent, so 見込 exceeds 見積 and PS finishes after its milestone, delaying later phases.
- U7 also builds the shared parts (製造 共通部品) of R02–R06 and is overloaded during PG.
- U15 and U16 finish the SA, UI and SS tasks with a quarter of the estimate unused.
- R09 SS-003 is open with 残 0.

`evaluation.json` in the data root records, for each recorded status date, the measures from the real scheduler: 見積, 実績, 残, 見込, 差異, progress (実績 ÷ 見込), closed tasks, open overruns and open tasks with 残 0, in total, per phase (window, forecast finish against the milestone in working days) and per person (overloaded days in the next 20 working days). The fake gh serves repository milestones and assigns each task to its phase milestone; the app reads their targets and membership.

## Offline day simulation

Run the ordinary app against an isolated fake gh from the repository root:

```powershell
./scripts/Start-Evaluation.ps1 -NoBuild -DataRoot C:\ghpb-evaluation\read-date
./scripts/Start-Evaluation.ps1 -NoBuild -Resume -DataRoot C:\ghpb-evaluation\read-date -Today 2027-01-14
```

Optional `-Today yyyy-MM-dd` sets `GHPB_TODAY` only in the child app. Without it, the child uses the evaluation status date. Resume preserves the saved plan and fake remote; choosing a later Today simulates reopening on another calendar day. Reopening alone preserves 基準日 and dates. **最新の情報に更新** advances 基準日 to the simulated read day and recalculates. A PMO date pick holds until that next read. The App ignores an empty or malformed `GHPB_TODAY` and otherwise uses the local calendar day; the launch script rejects an invalid explicit `-Today`.

## Live sandbox

Read the [authorized scope](https://github.com/fukuda-yuki/codex-sandbox/issues/1). All script writes target `fukuda-yuki/codex-sandbox` and the owner's Project 3, 第2027.04版. The script selects these targets explicitly, removes token/host overrides from its child environment and uses gh's stored authentication.

The live version is the project-start baseline (基準日 2026-10-13): Actual 0, Remaining = Estimate from the manifest, manifest predecessors and start dates, and only person-U1 assigned to `fukuda-yuki`; all other assignees are empty. Requirement rows have empty phase and effort. Weekly progress remains offline: GitHub cannot backdate closedAt. Planning values, including 工程, must be pasted and published through the ordinary app after the summary view.

Run these commands from this checkout on the PMO's network-enabled machine:

```powershell
Set-Location C:\w\b126; python scripts/evaluation/version_sandbox.py setup --evidence TestResults/version-sandbox/setup-plan
Set-Location C:\w\b126; python scripts/evaluation/version_sandbox.py setup --apply --evidence TestResults/version-sandbox/setup-apply
Set-Location C:\w\b126; python scripts/evaluation/version_sandbox.py inputs --through 40 --evidence TestResults/version-sandbox/inputs
# After pasting and publishing in the ordinary app:
Set-Location C:\w\b126; python scripts/evaluation/version_sandbox.py verify --evidence TestResults/version-sandbox/verify
```

`setup` inventories all WBS Issues and plans the missing 工程 single-select field, milestones and memberships in `setup-plan.json`. Only `--apply` enables writes. Options must be SA, UI, SS, PS, PG, PT, IT, ST, OT in that order. Conflicting field types/options, duplicate milestone titles, different due dates and non-manifest milestone memberships refuse the whole operation before any write. Tasks receive their phase milestone; requirements have no milestone. REST milestone creation uses `due_on=YYYY-MM-DDT12:00:00Z`, because GitHub keeps only the date and midnight UTC reads back as the previous day; independent GraphQL readback compares the UTC calendar date, as the app does. Issue updates use at most ten aliases per request, followed by at least one second per mutation in the batch (single creates wait one second). Failures stop without retry; rerun from a fresh inventory to reconcile uncertain writes. `setup-result.json` records fresh readback, including mismatches.

`inputs` writes a headerless `first-pass.tsv`, in sheet order: title, 工程, 担当者, 見積, 残, 実績, 開始日, 終了日, 先行タスク. Dates calculated by the app have empty input cells. After setup, refresh the ordinary app and confirm 工程 is mapped. With all 1,040 Issues visible in the exported order and no filtering/sorting, first select 工程 through 先行タスク for each requirement's 25 task rows and press Delete to clear old inputs (40 task blocks; exclude the read-only requirement rows and leave titles intact). TSV empty cells mean no change in the app, so this clearing step is required to remove old assignees, effort, predecessors and dates. Requirement rows must already have empty phase/effort; if verify reports stored values there, stop for a separate app correction because summary rows reject those edits. Then paste starting at the first title cell; set project dates from the manifest, map each 工程 to its milestone in settings and set 基準日 to 2026-10-13, review the scheduled result, then publish. The export refuses missing predecessors or any mismatch in the entire visible Issue order before replacing files. It defaults to all 40 requirements; `--offline` instead exports synthetic assignees without contacting GitHub.

`verify` never writes GitHub. It independently reads every WBS item's 工程, Estimate/Remaining/Actual, Start date, native predecessors, assignees and open state, plus setup resources. It saves `verification.json` with each observed Issue/item identity, expected/actual values, read time, a canonical manifest SHA-256 and mismatches; it exits unsuccessfully on differences. It does not establish ordinary-app usability or owner acceptance. The app's configured planning-field mappings must use these field names.

`register` remains available for missing WBS registration (`--through 40` for the whole version; default 39). Without `--apply` it only plans. It preserves existing identities and refuses conflicting parents, changed titles, duplicate keys and mass recreation. Setup and verify always cover the entire manifest.

## Evidence

Record source and binary identity, data root, real or substituted endpoint, row/task counts, operation results, screenshots and timings. Offline automated results, ordinary-app product review, real-GitHub readback and the owner's acceptance are distinct claims. Run `python -m unittest discover -s scripts/evaluation -p 'test_*.py' -v` for the sandbox script's safety contracts.

The local settings seed both Project start and end from the version plan. 工程 auto-maps to its existing single-select field. The initial baseline and local phase values agree, preserving zero unpublished tasks. Summary calculations consume the same scheduled result as the plan; recorded evaluation measures provide independent expected values for regression tests.

## Starting and resuming the ordinary app

From the repository root in PowerShell 7, run `./scripts/Start-Evaluation.ps1` (or add `-NoBuild` after a Release build). Restore dependencies separately first; if a long checkout causes MSB3030, use a short checkout path. Keep the printed absolute data root. Choose **接続**, then **第2027.04版**; gh is already selected. This uses no real account or network, starts with 40 requirements, 1,000 tasks and 20 people, and has zero unpublished tasks. Resume with `./scripts/Start-Evaluation.ps1 -NoBuild -Resume -DataRoot <absolute path>`. Use `-Version 2027.10` for the year-long version or `-StatusDate 2027-02-17` for another recorded progress date. Per-phase measures are in the run's `evaluation.json`.

The historical CSV example in sandbox Project 3 is the 24 tasks #864–#887; do not reimport that CSV. The earlier whole-version registration used R01–R39 (39 requirements and 975 tasks, 1,014 Issues); the current manifest and setup cover R01–R40. Do not confuse the CSV example with the WBS. Project 3 is https://github.com/users/fukuda-yuki/projects/3; authentication, permissions and company-environment acceptance need their own live evidence.

## Manual images

[The capture plan](../docs/manual/README.md) owns the single rebuild command, UI states and image-to-section mapping. Captures use a fresh offline root and the ordinary Release executable; never edit fixture files or crop images manually. Developer build instructions remain in [README](../README.md#build-and-run). `GHPB_DATA_ROOT` can select an isolated absolute storage path; it is not an ordinary user setup step.

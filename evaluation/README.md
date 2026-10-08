# Version-shaped evaluation (#89 / #91)

`version-plan.json` is the shared synthetic WBS: 40 requirement Issues and 25 V-model tasks per requirement, with 20 synthetic assignee slots. Titles and keys retain the registered sandbox WBS identities. Effort and two start waves are placeholders, not the owner's real template. The generated workload is near capacity during active weeks; every week, including the low-demand tail, is reported by the real scheduler in the offline root's `evaluation.json`.

Regenerate with `python scripts/evaluation/generate_version.py`; use `--check` to verify the tracked manifest. The test executable embeds this same file. `scripts/Start-Evaluation.ps1` prepares and opens the ordinary app with an isolated fake-gh endpoint. Its fresh planned state contains 1,040 Issues and starts at zero unpublished tasks.

## Live sandbox

Read the [authorized scope](https://github.com/fukuda-yuki/codex-sandbox/issues/1). All script writes target `fukuda-yuki/codex-sandbox` and the owner's Project 3, 第2027.04版. The script selects these targets explicitly, removes token/host overrides from its child environment and uses gh's stored authentication. Repository automation may also auto-add newly created sandbox Issues to Project 5; this script neither edits Project 5 nor changes its automation.

From the repository root:

```powershell
python scripts/evaluation/version_sandbox.py register --evidence TestResults/version-sandbox
python scripts/evaluation/version_sandbox.py register --evidence TestResults/version-sandbox --apply
```

Without `--apply`, commands only read GitHub and write local evidence. Registration completes R01–R39 (1,014 Issues), preserving existing Issue identities, planning values and unrelated/inaccessible Project members. It refuses duplicate WBS keys, changed titles, existing conflicting parents and mass recreation. Only missing Issues/memberships, hierarchy and WBS order are written. Inspect the proposed counts before applying. Failures stop the run; rerunning inventories the actual state before deciding what is still missing. The stopped `C:\w\eval\v2027-root` checkpoint must never be published.

Open a **fresh live data root** with real gh, register Project 3, and confirm 未発行 0. The PMO enters Estimate, assignments, predecessors and phase-start constraints in the ordinary sheet, then reviews and publishes them. Use only real sandbox assignees; 20-person allocation is evaluated offline. Do not fill live planning fields by bypassing the app.

The optional `inputs` command exports a first-pass TSV for the ordinary sheet, never a GitHub mutation. Its numeric predecessor references require the **entire visible Issue order** to match the selected WBS exactly. It refuses extra non-WBS Issues, Issues from another repository, missing/archived WBS rows and a different order before writing the TSV. Drafts, pull requests, inaccessible content and archived unrelated items do not become sheet rows. `first-pass-order.json` records the accepted Issue sequence for comparison with the ordinary sheet before pasting. This is initial planning input; do not reuse it to overwrite an already progressed weekly plan.

After the first pass has been published:

```powershell
python scripts/evaluation/version_sandbox.py week --week 1 --evidence TestResults/version-sandbox
python scripts/evaluation/version_sandbox.py week --week 1 --evidence TestResults/version-sandbox --apply
```

Repeat for weeks 2, 3 and 4 in sequence. Status dates are 2026-10-21, 2026-10-28, 2026-11-04 and 2026-11-11. Ordinary progress consumes the elapsed working-day budget through the WBS, retaining carryover; R05 is delayed by two days and R07's first UI task needs 16 extra hours. The simulator writes Actual and Remaining, then independently reads them back. Week three adds only R40's 26 Issues after the existing plan, preserving PMO ordering. New R40 planning inputs still go through the app.

Keep the same evidence directory for the sequence. Receipts preserve the original intent of an interrupted stage and refuse conflicting intervening edits. A completed stage is a no-op, including its R40 setup. After each stage, refresh, set the status date, inspect and correct dates/assignments/load, review and publish through the ordinary app. Simulation is an external team update, not product publish evidence.

## Evidence

Record source and binary identity, data root, real or substituted endpoint, row/task counts, operation results, screenshots and timings. Keep initial-pass input time separate from remote waiting. Retain failed attempts. Offline automated results, ordinary-app product review, real-GitHub readback and the owner's acceptance are distinct claims. Run `python -m unittest discover -s scripts/evaluation -p 'test_*.py' -v` for simulator safety contracts.

# Weekly native-app review and concrete gap assessment

Owner: [Issue 73](https://github.com/fukuda-yuki/gh-projects-boards/issues/73), with integrated acceptance in [Issue 65](https://github.com/fukuda-yuki/gh-projects-boards/issues/65). Read the [earlier six-job map](../blind-review/current-job-map-20261003.md) for retained source-specific evidence and historical failures.

The interrupted four-task native-app exercise is complete through local correction, calendar/dependency replanning, normal restart, reviewed live publication and independent GitHub readback. The current candidate also fixes a reproduced stale date/time warning. This is a runnable local review, not whole-product or human acceptance.

## Premise and reference research

GitHub Projects Kanban is the closest existing experience but does not satisfy the accepted work. This is the owner's established premise, reaffirmed in [the Issue 65 direction correction](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5964925920). The completed [Issue 60 handoff](https://github.com/fukuda-yuki/gh-projects-boards/issues/60#issuecomment-5743310054) and [Issue 61](https://github.com/fukuda-yuki/gh-projects-boards/issues/61) remain authoritative. This review does not ask the owner to justify the repository again or reduce the accepted scope.

Official GitHub documentation was reviewed on 2026-10-03 before further product operation:

| Reference capability | Source-backed fact | Consequence for this app |
| --- | --- | --- |
| Kanban and table work | Boards support grouping, field visibility, filtering and field summaries; tables support editable fields and configurable views. [Board documentation](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/customizing-the-board-layout), [table documentation](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/customizing-the-table-layout). | A different native table or duplicated field editing is not sufficient differentiation. Evaluate the accepted planning/replanning job. |
| Roadmap dates and relationships | Roadmaps use date/iteration fields; Issue dependencies record blocked-by/blocking relationships. [Roadmap documentation](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/customizing-the-roadmap-layout), [dependency documentation](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/creating-issue-dependencies). | Do not describe dates, timelines or dependencies as missing GitHub capabilities. The app must apply the agreed effort, allocation, calendar and adopted-endpoint rules coherently. |
| Project field and workflow boundaries | Project date fields use calendar dates, documented as `YYYY-MM-DD`; built-in workflows automate supported item/status events. [Date fields](https://docs.github.com/en/issues/planning-and-tracking-with-projects/understanding-fields/about-date-fields), [built-in workflows](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-built-in-automations). | Preserve exact minutes, cumulative report attribution, calendar revisions and Manual intent locally; publish their agreed GitHub projections explicitly. This conclusion concerns the reviewed standard configuration, not every possible third-party integration. |

The configured sandbox Kanban view remains available at [weekly review view 6](https://github.com/users/fukuda-yuki/projects/3/views/6). Its setup observation is retained. The subsequent browser weekly-operation comparison was not run: further interaction was unnecessary for understanding the documented GitHub features and the owner requested problem identification first. No crossover reset was performed, so the sandbox retains the app's verified final values. There is no measured comparative speed, click-count advantage or human-effort claim.

## Findings and disposition

| Finding | Evidence and disposition |
| --- | --- |
| A stale warning contradicted completed date input. | R16 ordinary observation 96 showed valid Manual `2026-10-10 12:07` / `12:08` while still saying the time was unconfirmed. `SchedulingEditor` evaluated fetched values only when constructing the flyout. The current warning follows the current text and selected scheduling method. Completing/clearing both endpoints or choosing Auto removes it; a remaining/new date-only endpoint in Manual retains it. **Fixed within this change.** |
| Core weekly behavior needed a completed current live endpoint. | The earlier live baseline had stopped before the weekly edits. The resumed ordinary workflow now reaches the independently specified dates, preserves Manual/completed work, restores after normal restart and publishes only the intended differences. **Completed for these four owned tasks.** |
| Initial setup and local authority have a real handling cost. | The app required explicit mappings, worker allocation, date precision and attribution of the two fetched Actual totals. These were performed through the ordinary app, not injected as a prepared planning checkpoint. The established settings were reused in the weekly update. No claim of lower total user burden follows from this agent operation. |
| Existing orientation and wording refinements remain. | The earlier W details expansion shortens the table viewport; history/Repository labels retain scanning density. Those findings keep their prior qualifications. No new remedy or human acceptance is claimed here. |
| Normal workload and product acceptance remain broader. | Four live tasks with one worker do not replace the agreed approximately 1,000-task/fewer-than-20-person weekly workload. Historical W1-H stays **FAIL / NOT_ACCEPTED**; a new human acceptance is **not run**. Summary/baseline #64, workload #62 and CSV #63 remain outside this change and retain their accepted scope. |

## Candidate and bounded implementation

- Branch: `codex/issue-73-weekly-recovery`; base HEAD `fd5732ad4f063665bbc23be96247dd8d620d80d2` plus the frozen working tree.
- Ordinary and tested hosted App SHA256: `AF78DF1AAC8821C5B47BB7E957980C8DEAEE6020DD17C767336C188E8F98D4A5`.
- Core SHA256: `E7EF7179BED66C4794D46EA7038D2443E2CE9DF6F22D481B0811A956BD30540A`, unchanged from R16.
- [Source review](checks/warning-candidate-source-review.json): all 272 build inputs match the candidate; only `SchedulingEditor.cs` and `PlanningHostedTests.cs` differ from the R16 receipt. No dependency, scheduler, storage or GitHub mutation implementation changed.
- [Frozen build receipt](receipts/review-candidate.json): 516 runtime files, successful ordinary App and test-project rebuilds. The App build reported zero warnings/errors. Git file enumeration emitted long-path warnings for historical evidence paths; build inputs exclude that evidence directory and the explicit 272-file comparison found no source mismatch. This is not a claim that all historical evidence paths were enumerated successfully by Git.

Code/design review: this is presentation-state wiring inside the existing native flyout. The same text/selection event path refreshes the explanation before preview. It adds no remote or persistence effect, UI-thread wait, custom control, new input route or color dependency. The fixed Japanese minute format follows the existing planning contract; a generic skill's globalization preference does not change it. The affected user's decision is whether more time input is needed. DESIGN.md's proportionate feedback and clear correction principles require the explanation to stop contradicting valid input.

## New execution evidence

Windows `10.0.26200`, .NET `10.0.12`, Release. The ordinary app used the real authenticated CLI and only `fukuda-yuki/codex-sandbox` / owner `fukuda-yuki` Project 3. The root operator knew the expected outcomes; this is informed, untimed agent operation, not a blind or human study.

| Boundary | Observed result | Record |
| --- | --- | --- |
| UI integration reproducer | **0 passed / 3 failed / 0 skipped**, all three at the intended stale-warning assertion. | [Red](checks/warning-red/results.xml), [command/environment](checks/warning-red/metadata.json). |
| Real control/event regression and adjacent input behavior | **5 passed / 0 failed / 0 skipped**: the three correction/method cases plus date/time precision and preservation of independent pending input. | [Green](checks/warning-green/results.xml), [command/environment](checks/warning-green/metadata.json). No new logic suite was needed for an unchanged Core. |
| Initial live baseline, before the interruption | Four task setups and current native date/time picker operation completed on R16. Independent stored-state comparison: 141 pass, six exact Auto endpoints not stored. | [Baseline readback](checks/app-baseline-stored.json); original setup observations remain under the local evidence root. |
| Weekly edit, calendar, dependency, correction and return | Actual 5→7 through Oct9; independent Remaining4→3; worker Oct13 availability10:00–12:00; #101 blocked by #100; Estimate16→20 then one Undo→16. | [Input](observations/105-weekly-report-ready-0.png), [exception](observations/109-calendar-exception-ready-0.png), [replan](observations/112-successor-replanned-0.png), [Undo](observations/116-successor-corrected-one-undo-0.png), [reasons](observations/120-weekly-reasons-settled-0.png). |
| Normal process restart | Updated process closed normally, was absent, and a new process reopened the same checkpoint. All four exact adopted schedules remained. Saved account/Project selection was explicit; automatic selection restoration is not claimed. | [Close](checks/weekly-normal-close.json), [after restart](observations/122-weekly-after-restart-0.png), [stored state](checks/app-weekly-after-restart-stored.json). |
| Reviewed live Apply | One batch, six operations, one attempt each, all verified: #100 Actual, Remaining, Target date; #101 Start date, Target date, predecessor #100. No creation. | [Review](observations/127-publication-successor-review-0.png), [completed history](observations/133-live-completed-history-0.png), [final overview](observations/134-final-week-overview-0.png). The AX record lists all six outcomes; this one history image shows only its current viewport. |
| Independent final remote and journal comparison | **127 predicates pass**: exactly five scalar differences and one dependency addition; scope Issue1, all unrelated fields/items, titles, bodies, assignees and native states preserved; one verified attempt per operation; no remaining local field changes or pending text. | [Readback](checks/app-final-independent-readback.json), [read-only checker](checks/check-final-readback.py), [remote snapshot](checks/app-final-remote.json). |
| Post-publication local planning metadata | **142 predicates pass**, six exact Auto endpoints explicitly `not_stored`; Manual minutes, Actual attribution, calendar and planning progress retained. | [Final stored state](checks/app-final-stored.json). The recurring readbacks overlap and are not added into a larger test count. |

The six exact Auto endpoints come from ordinary UI observations, not a second invocation of the production scheduler by the JSON checker. Expected results were specified independently before execution in the [frozen packet](checks/independent-packet.json), SHA256 `E39432096292C175C013862A07CAF8AFB0BEEEE6150EAF06F07BB561EE40E22B`.

The [curation manifest](evidence-manifest.json) records original paths and matching SHA256 values for each copied evidence file. Original records were not edited. All ordinary observations 101–134 and the R16 warning observation 96 are retained here, including transitional and failed-action records. The copied Python checker is for source inspection; executing it requires the original evidence-root layout and inputs named in its result. The preserved baseline and final remote/checkpoint inputs are included under `checks/source-inputs` for independent inspection.

| Task | Adopted start → finish (JST) | Final raw E / R / A |
| --- | --- | --- |
| #100 carryover | Oct13 10:00 → Oct14 10:45 | 16 / 3 / 7 |
| #101 successor | Oct14 10:45 → Oct16 15:45 | 16 / 4 / unknown |
| #102 completed prerequisite | Oct5 09:00 → Oct6 18:00 | 16 / 0 / 5 |
| #103 Manual appointment | Oct10 12:07 → Oct10 12:08 | 16 / 4 / unknown |

Raw totals are Estimate64h, Remaining11h and reported Actual12h with two unknown Actual values. Unknowns were not turned into zeroes. The five GitHub changes are only day/number projections; exact minutes and planning metadata remain local.

## Failures, limitations and retained history

The original Red, initial read-only GraphQL node-limit failure, stopped Computer Use action and all R16/W/X/V evidence are preserved. After continuation, a fresh observation established that the interrupted daily-dialog click had not completed; it was not blindly replayed. The offscreen calendar-expander click returned `no cached bounds` and made no assumed input; scrolling the actual settings surface exposed it. A startup click returned `coordinate input geometry is unavailable`; fresh window/screenshot selection recovered it. A flyout transition screenshot is retained separately from its settled frame. These observations are not silently counted as product failures or successful operations.

The hosted RenderTargetBitmap images have incomplete native input/button compositing. They are not ordinary-window readability proof. Behavioral assertions use real controls/events; ordinary-window screenshots separately document the stated live flow. The new warning's transition is covered by the scoped hosted cases, not claimed as a new ordinary-window correction replay.

No new 1,000-task timing, physical IME, other-theme/scaling, corrupt-storage, partial/uncertain-live-failure, packaging or human acceptance was performed. Prior evidence retains its candidate and environment. Successful live publication does not replace the retained synthetic partial/uncertain recovery evidence.

## Runnable local handoff

The ordinary review app is left open on the final weekly Gantt. [Runtime identity](checks/review-candidate-running.json) binds process 142960 to the frozen ordinary directory and its loaded WinUI module. The real data root is isolated under `TestResults/issue73/weekly-value-20261003/live-app-data`; it is not the user's default profile.

For a later restart from this checkout, close that app normally and run:

```powershell
.\TestResults\issue73\weekly-value-20261003\launch-review-candidate.ps1
```

The [launcher](launch-review-candidate.ps1) verifies the frozen receipt and all runtime hashes, refuses a second ordinary process and uses the same isolated saved root. It does not send GitHub changes at launch. Choose the saved `fukuda-yuki` account and `codex-sandbox` Project; the final schedule, reports and history are available. The launcher copy here is for inspection; its path guard requires the original local evidence-root script.

The bounded agent work above is complete. Human usability/business-fit acceptance remains the owner's judgment against the already agreed requirements. Remote branch publication remains subject to the existing enforced push restriction; no push was retried. This handoff adds no commit, PR, CI, main integration or release. Branch upstream is unset, with repository-local `branch.autoSetupMerge=simple`, `push.default=simple`, `push.autoSetupRemote=true`; it is not tracking `origin/main`. Issues 61, 65 and 73 remain open.

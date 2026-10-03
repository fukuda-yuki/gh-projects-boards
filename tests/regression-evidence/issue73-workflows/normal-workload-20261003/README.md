# Current-candidate normal-workload weekly evidence

Owner: [#73](https://github.com/fukuda-yuki/gh-projects-boards/issues/73); integrated acceptance: [#65](https://github.com/fukuda-yuki/gh-projects-boards/issues/65).

**The declared 1,000-task weekly job is complete through correction and normal restart.** The unchanged current ordinary WinUI candidate performed one informed, untimed weekly update in the existing `Load` fixture: 1,000 P1 tasks, 20 people and 20 retained P2 tasks. This supplies the missing combined normal-size operation; it does not convert the earlier four-task live exercise or older scale records into broader acceptance.

## Reuse, source and boundary

The retained [#15 Gantt](../../issue15-gantt/README.md), [#61/#65](../../issue61-65/README.md) and [post-#69 native](../../issue65-post69-native/executions.md) records already establish their own Gantt, distant edit, Project/lifetime and weekly boundaries. Their earlier source identities differ. The current [six-job map](../blind-review/current-job-map-20261003.md) supplies W/X/V and related qualifications; the [completed four-task live record](../weekly-review-20261003/README.md) supplies the distinct real GitHub endpoint. None individually established this current combined 1,000-task weekly replan/correction/restart. They were reviewed before selecting this single missing journey, rather than rerun as a broad campaign.

- Source: `codex/issue-73-weekly-recovery`, HEAD `fd5732ad4f063665bbc23be96247dd8d620d80d2` plus the preserved working tree.
- App SHA256: `AF78DF1AAC8821C5B47BB7E957980C8DEAEE6020DD17C767336C188E8F98D4A5`; Core: `E7EF7179BED66C4794D46EA7038D2443E2CE9DF6F22D481B0811A956BD30540A`.
- All 272 build inputs still match the frozen receipt. No production or test source changed, no build or additional product suite was run. Both ordinary processes loaded the same real WinUI runtime: [first](checks/running-1.json), [restart](checks/running-2.json).
- Windows `10.0.26200`, .NET `10.0.12`, Release, fixed dark theme, captured window 1267 by 794. Native control/keyboard input used the Windows Computer Use driver; real controls, app orchestration and isolated disk persistence were exercised. Cached synthetic identity was unverified; no connection/Apply/creation command was invoked. This is one bounded ordinary-app E2E journey to persisted/reloaded state, with no live endpoint dispatch or performance measurement.
- Existing version-1 legacy planning owners were deliberately retained. The supported v3 representation marks them as legacy; no first-time native assignment at scale or implicit reassignment is claimed. Legacy attribution warnings remain visible.

## Independent packet and observed outcomes

The [packet](checks/packet.json), SHA256 `9E55FE2C46D0B1CEAFAB0A7056AA0F8A8199236BD845BBCC1129C4678F901733`, was frozen before ordinary operation. Independent arithmetic uses the accepted 09:00-13:00 / 14:00-18:00 workday, the fixture's already adopted Oct12 holiday and the existing 100/80/50% allocations. The checker does not invoke the production scheduler.

| Work / outcome | Execution evidence |
| --- | --- |
| Find I301 within 1,000 tasks; update Actual 5 to 7 through Oct9 and independent Remaining 4 to 3, retaining U1 history and InProgress | [Report ready](observations/14-weekly-report-ready-0.png), [saved result](observations/16-report-saved-0.png). |
| Replan after Oct9 18:00; change only U2's Oct13 availability to 14:00-18:00 | [Cutoff](observations/20-cutoff-entered-0.png), [identified exception before](observations/24-calendar-interval-visible-0.png), [edited interval](observations/25-calendar-entered-0.png). |
| I301 = Oct13 09:00-12:00; I302 = Oct13 14:00-Oct15 18:00; I303 = Oct16 09:00-Oct21 18:00, all JST | [I301 visible](observations/28-replanned-301-revealed-0.png), [I302](observations/29-replanned-302-0.png), [corrected I302/I303](observations/42-undo-settled-0.png); exact row observations are retained with each image. |
| Commit I302 Estimate 20 instead of 16; its finish becomes Oct16 15:00; one Gantt Undo restores 16 and the intended dependency results | [20 pending / 16 confirmed](observations/36-mistake-active-entered-0.png), [changed schedule](observations/39-mistake-gantt-0.png), [settled changed AX](observations/40-mistake-gantt-settled.json), [one Undo result](observations/42-undo-settled-0.png). |
| Keep completed I330 Oct5 09:00-Oct6 18:00 and Manual I350 Saturday Oct10 12:07-12:08; keep I1000 March15 2027 12:07-13:00 | [Completed](observations/43-completed-retained-0.png), [Manual](observations/44-manual-retained-0.png), [distant Manual](observations/45-far-manual-retained-0.png). |
| Reveal I1000's existing `24未確定`, keep confirmed Estimate 16 and exclude pending text from the calculation | [Before close](observations/47-pending-before-close-0.png), [after restart](observations/52-restart-pending-revealed-0.png), [settled pending/confirmed AX](observations/53-restart-pending-settled.json). |
| Close normally, establish process exit, reopen the same root and retain all intended results | [Exit](checks/process-exit-1.json), [I301](observations/55-restart-301-0.png), [I302](observations/56-restart-302-0.png), [I303](observations/57-restart-303-0.png), [visible final Gantt](observations/59-restart-selected-visible-0.png). |

## Data preservation and execution result

[Independent JSON readback](checks/independent-readback.json): **27 predicates executed, 27 passed, 0 failed, 0 skipped**. These are evidence checks for one journey, not 27 product tests. [The script](checks/check-normal-workload.py) uses only standard-library JSON/file reads and is preserved for inspection. Its input is the original evidence root below.

All 1,000 task identities, complete registration/relationship snapshots, all 20 people/weights, the 20-task second Project, field baselines/provenance, unrelated raw values and original Undo history were retained. Across 7,922 field records, the only changed non-DATE values were I301 Actual 5 to 7 and Remaining 4 to 3. The global cutoff/calendar edit changed 616 derived DATE values; this audit independently checks exact Auto endpoints for the three named tasks, not the correctness of all 616 projections. All 21 Manual and 33 completed task contracts, historical attribution and exact pending text remained. The two retained new operations are the daily report and Project planning settings; the mistaken estimate was undone.

Documented v1-to-v3 assignment representation is checked explicitly: every null legacy assignment becomes `Assignees=[]`, `Complete=false`, `Legacy=true`; owners and semantics are unchanged. Task array order is not an identity. This is semantic preservation with a declared representation change, not byte equality of the pre-operation and final checkpoint. The **after-close and after-restart checkpoints are byte identical**, SHA256 `A682204AEEE2D0F89D62137279AAC87460B660CE9D30C285D833B313F0FBA40B`.

Original raw root: `TestResults/issue73/normal-workload-20261003`. Large inputs are retained there once: [before](../../../../TestResults/issue73/normal-workload-20261003/before.json), [after close](../../../../TestResults/issue73/normal-workload-20261003/after-close.json), [after restart](../../../../TestResults/issue73/normal-workload-20261003/after-restart.json). [Identity check](checks/identity-after.json) retains their hashes and confirms all three files of the prior live root unchanged. [Curation manifest](evidence-manifest.json) verifies the copied observations/checks against their originals. All 59 original observations, including transient states, remain in the raw root; selected milestones are copied here.

Commands executed from the repository (absolute roots are in the receipts):

```powershell
./scripts/Start-PlanningCheck.ps1 -Scenario Load -DataRoot '<raw-root>/session' -FrozenLaunch '<weekly-root>/review-candidate/diagnostics/launch-4d8af16624c14a24ad97ba9879ae313c.json' -PrepareOnly
./scripts/Start-PlanningCheck.ps1 -DataRoot '<raw-root>/session' -Resume -FrozenLaunch '<same-frozen-receipt>'
# The same Resume command was used after normal process exit.
python TestResults/issue73/normal-workload-20261003/check-normal-workload.py
```

The two [launch logs](checks/launch-normal-1.log) and [restart log](checks/launch-normal-2.log) identify the exact isolated root and candidate. Launch establishes no authenticated connection and sends no work. The checkpoint journal remains empty; this is not instrumented proof of every possible network attempt.

## Failed attempts and remaining scope

The first UIA SetValue on the inactive Estimate cell returned [`Pattern not found (0x80004003)`](checks/failed-set-value-35.json). Reobservation showed that the editor had activated but still held 16. The [observed active editor](observations/35a-after-inactive-cell-set-value-error-0.png) accepted 20, which was then committed normally. The initial failure is retained as an automation attempt, not silently marked successful. A date editor opened during navigation was closed without applying. Immediate post-action AX snapshots sometimes precede rendered/settled state; subsequent observations establish outcomes. The 250 ms capture-settling delay is not an application timing measurement. An immediate window listing after Alt+F4 was transient; process absence independently established exit before restart.

**Gap disposition:** current cached normal-size weekly update, multi-person calendar/dependency effects, correction and restart are now observed. Previous four-task live publication stays a separate boundary. Normal human workflow acceptance, comparative handling effort, first-time native assignment at scale, 1,000-task live throughput, other environment/IME/timing claims and unexercised recovery combinations are not established. Historical W1-H FAIL / NOT_ACCEPTED and timing results are unchanged. #64 Summary, #62 workload and #63 CSV retain their owned scope. No new commit, push, PR, CI, main integration or release occurred; the enforced publication restriction was not retried.

## Runnable handoff

The ordinary app is open on this isolated normal-size Project's final Gantt. The completed live root remains intact and closed. To resume this normal-size state later, first close the app normally, then run from the repository:

```powershell
./scripts/Start-PlanningCheck.ps1 -Resume -DataRoot 'C:/Users/mwam0/.copilot/repos/gh-projects-boards/TestResults/issue73/normal-workload-20261003/session' -FrozenLaunch 'C:/Users/mwam0/.copilot/repos/gh-projects-boards/TestResults/issue73/weekly-value-20261003/review-candidate/diagnostics/launch-4d8af16624c14a24ad97ba9879ae313c.json'
```

Select the saved `viewer / github.com / ID 42` account and P1 explicitly. The existing launcher verifies frozen runtime identity and preserves the checkpoint. To inspect the separate live four-task state, use its earlier guarded restart command after closing this process. Neither launch implies permission to publish new work.

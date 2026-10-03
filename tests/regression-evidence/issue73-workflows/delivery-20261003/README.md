# Local branch delivery and current acceptance map

Owner: [Issue 73](https://github.com/fukuda-yuki/gh-projects-boards/issues/73); planning and integrated acceptance remain in [Issue 61](https://github.com/fukuda-yuki/gh-projects-boards/issues/61) and [Issue 65](https://github.com/fukuda-yuki/gh-projects-boards/issues/65).

**The declared engineering and ordinary-operation work is complete and committed locally. Human acceptance, remote publication and broader product acceptance are separate.** Historical W1-H remains **FAIL / NOT_ACCEPTED**. The established premise is that GitHub Projects Kanban does not meet the agreed planning job; this handoff does not reopen the reason for building the product or remove accepted capabilities.

## Review unit and executed candidate

The review range starts at `bcebff1d4e3765afb50f09692724b513bfe6a205` and follows `codex/issue-73-weekly-recovery` without rewriting history. Product, current contracts and focused regression changes are committed at **`138142df44f9314700827b675185d3dfbf39ceda`**, tree `e14458ef77bcf394458f43c792cdf0a8412ce24a`. Its parent is `fd5732ad4f063665bbc23be96247dd8d620d80d2`. This evidence handoff is a following commit in the same review unit.

[The source binding](source-binding.json) checks every one of the frozen candidate's **272 build inputs** against its original raw SHA256 and the corresponding committed Git blob. Git's configured text normalization is explicit. No source bytes were changed for this delivery, and no build or behavioral suite was rerun merely to create commits.

- Current ordinary/tested hosted App: `AF78DF1AAC8821C5B47BB7E957980C8DEAEE6020DD17C767336C188E8F98D4A5`.
- Core: `E7EF7179BED66C4794D46EA7038D2443E2CE9DF6F22D481B0811A956BD30540A`.
- [Frozen receipt](../weekly-review-20261003/receipts/review-candidate.json) identifies the ordinary executable and all 516 runtime files. It is a build-time receipt, so its original pre-commit HEAD is preserved.
- [Delivery source review](review.md) records reused reviews, their corrected findings and the final source boundary. One preexisting test-file EOF blank is retained and disclosed; a completely clean whitespace check is not claimed.

## Supported jobs and remaining decision

| User job | Completed, inspectable evidence | Remaining action within this delivery |
| --- | --- | --- |
| Establish a first plan and understand dates | [Q's initial plan](records/first-plan-q.md) and [Q/R/S outcomes](records/progress-qrs.md), with their older candidate and context qualifications. The current [four-task live workflow](../weekly-review-20261003/README.md) also used ordinary initial mappings, allocation, date precision and Actual attribution. | No identified unverified changed transition requires another engineering campaign. The owner judges whether setup and date explanation fit the agreed job. A new full first-use journey at 1,000-task scale is not claimed. |
| Report Actual/Remaining/progress and replan | [Current live weekly workflow](../weekly-review-20261003/README.md): independent date expectations, Manual/completed preservation, one correction Undo and normal restart. [Current cached normal-size workflow](../normal-workload-20261003/README.md): 1,000 tasks, 20 people, changed worker exception, selected dependency chain, correction and restart. | The earlier normal-workload operation gap is resolved for the declared cached fixture. Human handling effort/business fit remains unaccepted. The normal fixture retains legacy owner semantics; it is not new native assignment-at-scale or live-throughput evidence. |
| Keep confirmed and unfinished work across navigation and restart | [U's retained continuation](records/restart-u.md), [W](records/range-w.md), and the latest normal-size restart keep their distinct evidence. The normal journey preserved all 7,922 field keys, 1,000 task contracts and the independent pending buffer. | No new repair follows from the recorded evidence. Automatic account/Project selection, corrupt-storage recovery and every native-input environment are not inferred. |
| Correct a range and Undo once | [W](records/range-w.md) completed grouped Estimate 8, one grouped Undo and final grouped 6, while preserving surrounding work and two unfinished values. [Range-input review](records/range-input-review.md) and [range-context review](records/range-context-review.md) retain their own executions. | Details can shorten the grid enough to hide the active bottom row while its identity/value/input remain in details. This is a retained orientation refinement, not an established wrong-target defect. One ineffective early input remains unclassified. |
| Publish selected changes and recover a definite partial failure | [X1/X2](records/publication-x.md) sent only the remaining Title after verified Status and returned to independent pending work. The current [live endpoint](../weekly-review-20261003/README.md) separately completed six approved operations once each, with independent remote readback. | Synthetic partial-failure evidence and live-success evidence remain separate. Historical wording/Repository density are qualified scanning refinements. No new live-failure matrix is required by this delivery. |
| Recover uncertain creation and resolve historical follow-up | [V3](records/creation-v3.md), [V4](records/creation-v4.md), [current-Core preserved-data interpretation](records/creation-current-core.md), and [historical handling review](records/historical-review.md) preserve original uncertainty, current completion and independent work. | V3/V4 used older binaries; the E7 read-only check establishes compatibility, not a new creation run. No change-impact finding requires repeating the creation dispatch. |

[The independent report-based judgment](records/pmo-judgment.md) and [independent twelve-image review](records/pmo-pixels.md) found no additional blocker within those sources. [The twelve images](records/pmo-image-packet.md) keep nine R16 frames separate from three older V4 frames. Static-image inspection is not behavioral or human acceptance. The separate ChatGPT design-conversation upload remains unsent because its unrelated draft is protected; that upload is not a substitute for the completed independent image review.

## Validation boundaries

The [live report](../weekly-review-20261003/README.md) retains the three observed stale-warning UI failures, subsequent five passing real-control/event cases, actual ordinary operation, normal restart, six reviewed GitHub operations and 127 independent remote/journal predicates. The [normal-size report](../normal-workload-20261003/README.md) retains one informed, untimed ordinary journey and 27 independent stored-state predicates. Predicate counts are not additional tests; exact independently expected Auto dates were checked for three selected tasks, not every recalculated task.

Earlier execution results remain source-specific: [daily validation](records/daily-validation.md), [range repair](records/range-input-review.md), and [publication validation](records/publication-validation.md) preserve failures, fixture corrections, zero-execution attempts and later passing results. Their overlapping selections are not pooled into one new pass total. No fresh all-suite, performance, physical-IME, theme/scaling or held-feature campaign was run during delivery.

## Runnable local review

The frozen ordinary app and isolated saved data remain under `TestResults`; executable trees are not checked into Git. The normal-size final Gantt was left open. The separate live four-task root remains intact and closed. [The guarded launcher](Resume-Candidate.ps1) is an inspectable copy; use the original local script from the repository:

```powershell
# Read-only runtime/data-root verification; allowed while the review app is open.
.\TestResults\issue73\local-delivery-20261003\Resume-Candidate.ps1 -State Normal -VerifyOnly

# After closing the ordinary app normally, resume either retained state.
.\TestResults\issue73\local-delivery-20261003\Resume-Candidate.ps1 -State Normal
.\TestResults\issue73\local-delivery-20261003\Resume-Candidate.ps1 -State Live
```

Only run one state at a time. Normal uses saved `viewer / github.com / ID 42`, Project P1; Live uses the saved `fukuda-yuki` account and `codex-sandbox` Project. Explicitly select the saved account and Project. The guard binds the receipt, runtime and exact isolated data root and refuses a second ordinary process. Starting the app does not publish changes. [Launcher verification](launcher-verification.json) is read-only preparation, not a new app run.

The precise human decision is whether the **initial-plan and weekly-update jobs fit the accepted work**: can the owner understand which task/person/report date/remaining effort governs a date, make a necessary correction with reasonable effort, retain Manual/completed and unfinished work, and know what will be published and how to recover? The engineering outcomes above are already checked. This is not a request for the owner to investigate routine defects, run an environment matrix, justify the repository again or accept a new scope reduction.

## Publication and other owners

[The draft PR description](PR-body.md) and [manual publication handoff](publication.md) describe one branch review unit. The previous execution-policy denial, `Pushing to a remote is denied; do it manually.`, continues to block automated branch publication. It was not retried or bypassed. There is no remote branch, PR, CI, main integration, release or human acceptance claim from these local commits.

Summary/baseline **#64**, workload **#62**, and CSV **#63** retain their accepted scope and owners; they are held/outside this delivery and were not started here. Existing performance, physical-input/environment, recovery and release obligations remain with their owning Issues. They are not silently marked passed, waived, or assigned as a whole test campaign to the human reviewer. Issues 61, 65 and 73 remain open.

## Evidence retention

[The curation manifest](curation-manifest.json) binds selected historical originals to clearly labelled link-only report derivatives and byte-identical small attachments. The live and normal packets keep their existing manifests and report bytes. This cut excludes duplicate full runtime trees, full repeated source snapshots, large original checkpoints and private model output; originals remain untouched in their recorded local locations. A local-only reference is explicitly printed as such instead of being presented as an available repository link. The full local archive is not a tracked standalone distribution, and the workspace is not described as completely clean while those retained files remain untracked.

Git attributes preserve bytes for the three newly committed evidence packets. Historical raw/portable ledgers remain unchanged; the older fifth ledger's OPEN status is not rewritten as a closed manifest. This delivery has its own bounded copy/link/hash audit and does not claim all historical files are published.

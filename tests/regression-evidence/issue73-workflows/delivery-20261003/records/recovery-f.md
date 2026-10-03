> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/README.md`
> Original SHA256: `903CB6D2CC10FC5EC3334EBBA589C7486DC77DDA351B5DCB5F61EDDD31F5142B`

# F: unaided partial-Apply recovery on candidate R1

**Observed outcome: C's interrupted recovery story completed without navigation or recovery assistance during F's UI session.** F found the saved work, distinguished completed Status from pending Title, explicitly reviewed and sent only Title, fetched the final values and closed normally. This is one fresh agent's Computer Use result against a synthetic service. Human acceptance is not run; whole-product readiness is not established.

## Input and source

F received the business story (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/story-card.md`), isolated running process identity and Computer Use mechanics, without source, specifications, previous reports, product instructions or diagnostic files. The original report (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/observations/report.md`), readable action log (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/observations/action-log.md`) and full action record (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/observations/action-log.json`) retain 45 observations/actions and 44 original PNGs, including an extra account-menu surface and transitional frames. Screenshots were not transformed.

The environment owner prepared and launched the app before the lease. F reports no business clarification, navigation hint or recovery assistance during it. The [launch receipt](../assets/A90AD5C0BC97-launch-62ececfce5124a519945eee3387f0e0a.json) identifies PID 33952, isolated fake CLI/config and the exact [R1 receipt](../assets/1E19ED15C288-candidate-frozen-receipt.json). That receipt byte-matches the [engineering receipt](../assets/1E19ED15C288-frozen-receipt.json), whose 235 source inputs are separately retained. R1 precedes the later unregister amendment.

Input was an unchanged isolated copy of C's pre-force-stop snapshot (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round1-apply-c/diagnostics/session2-close-stall-snapshot`). The [lineage record](../assets/67E016A18FB7-input-lineage-readback.json) binds all eight original, before/after, immutable-input and prepared-working files. Schema 12 revision 62 contains one pending Title difference, the original Failed/PermissionDenied attempt followed by Blocked review, and completed Status. The service already had restored permission and Status Done. No field, history or service-scenario repair was applied. input-snapshot (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/input-snapshot`) retains those bytes independently of the evaluated working data.

[Preparation](../assets/20DF08B5D676-preparation.json) and [static validation](../assets/509CABAE73C8-static-validation.json) still say `not_run` and candidate not selected at the preparer's earlier boundary. They were not rewritten after the coordinator launch; its receipt binds the actual candidate. [Environment.ps1](../assets/378B252DBC30-Environment.ps1) and [Launch.ps1](../assets/A83941208189-Launch.ps1) are operator scripts, not instructions supplied to F. Binaries are excluded.

## Route and remaining friction

| User decision | Observed evidence | Limit or friction |
| --- | --- | --- |
| Determine what already completed | F found old Title PermissionDenied and exposed completed Status in saved history. [Title detail](../assets/04BBA0F7C0BF-09-title-details-ready-0.png), [completed history](../assets/EDCB7C1DB726-11-completed-history-0.png). | Desired local Title, initial uncertainty wording and the one-cell footer needed interpretation. That wording does not establish a new Unknown dispatch; the input retains a known failure. |
| Resume safely | F inferred the connection prerequisite, verified example.test and fetched latest. [Connection](../assets/DD2E0579345C-19-access-verified-0.png), [refresh](../assets/6170FDF74FAE-22-refresh-complete-0.png). | The disabled history resume button lacked adjacent explanation; F took a connection-settings detour. |
| Review only unfinished work | [Review](../assets/027CF0600558-24-review-ready-0.png) exposed one Title difference and “再確認してやり直す,” explaining preserved completed work and no send at that step. F used it, then [selected only Title](../assets/B299E8860338-31-title-selected-ready-0.png). | Low-level IDs and attempt/readback terminology required interpretation. F identified repository/account/Project context but did not independently verify the complete Project URL in the UI. |
| Verify and continue | [Title readback](../assets/E5289681F736-38-title-readback-0.png) and a second [latest fetch](../assets/7048B7467F52-42-final-current-0.png) showed intended Title, Done, zero unapplied cells and local saved status. | Covers the supplied one-task recovery story, not every Apply scenario or unrelated rows. |
| Close normally | F used standard Close; the second window inventory found no target window. Actions 44–45 remain in the log (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/observations/action-log.md`). | Window absence is F's observation. The independent snapshot separately records PID absence. |

Repeated Project names under repository groups and default-hidden completed history remain in F's observations. Eventual success does not remove these interpretation costs. Transitional frames are not performance measurements.

## Independent after-close state

The [snapshot manifest](../assets/388C98363D80-snapshot-manifest.json) records PID 33952 absent and all eight data/config files equal before, after and at copy time. Backup and execution lock remain; writer locks were excluded. [Readback](../assets/5897970CCD1A-readback.json) and its note (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-recovery-f/diagnostics/final-snapshot/readback-note.md`) establish:

- Schema 13 revision 76, scope `example.test / 42`, Title `Weekly review completed`, Status `done`, no changed fields, pending buffers or conflicts.
- The original two fake mutation payloads remain an identical 144-byte prefix. Exactly one appended payload updates I1's Title; no Status or other field mutation was added.
- Old Title is Superseded with its Failed/PermissionDenied attempt unchanged. Original Status success and its one attempt remain unchanged. New Title has one successful attempt and independent readback verification.

Final checkpoint SHA-256: `3520EA8FB8D6EDD80C1C5C4E578804CC67580D9DB838072A361204197DBD175C`. This is durable-state/synthetic-service evidence after close, separate from F's UI observations and real GitHub.

The first diagnostic predicate used incorrect PowerShell property counting and reported `OnlyNewTitleRequest = false`. [readback-first-probe.json](../assets/554A8A8A297B-readback-first-probe.json) remains unchanged. The corrected readback uses an explicit property-name array; raw payloads were not altered. [Retention verification](../assets/AE3E305D54F2-retention-verification.json) independently checks eight input/eight final hashes, retained C input bytes, R1 receipt, unchanged request prefix and the sole new Title payload. The retention agent performed no UI, process, app, test or build action.

## Coverage boundary

F supplies a new unaided agent recovery success on R1 where baseline C required intervention. It does not rewrite C's failed close, explain E's crash, establish physical IME/live service behavior or grant actual-user acceptance. R2 is separate and not retained here. The [manifest](../assets/925D4CCE4C91-manifest.json) records every copied original's before/after/copy SHA-256. This README and `retention-verification.json` are coordinator records.

> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/checks/recovery-candidate-r2/README.md`
> Original SHA256: `AF20FC7CA30F162984D9C34FEAFE3CC5BD0A3C5336232E07762A01D2CFF98F21`

# Candidate R2 engineering record

This retains the source-specific amendment described in [Issue #73 comment 5854680370](https://github.com/fukuda-yuki/gh-projects-boards/issues/73#issuecomment-5854680370). It extends the R1 candidate with completed-original-creation unregister handling and legacy known-failure explanation. Scoped Green results do not establish release readiness: the independent source review found two unresolved guard paths, and [G's ordinary-app recovery crashed](creation-g.md).

## Frozen source and preparation

[frozen-receipt.json](../assets/9AB80CC9A4B3-frozen-receipt.json), [source-retention.json](../assets/D0F52076B3C7-source-retention.json) and source (local archive: `tests/regression-evidence/issue73-workflows/blind-review/checks/recovery-candidate-r2/source`) retain all 237 declared build inputs, checked against both receipts. Base HEAD is `498840b1eea42bfb344c53195f7e5473fe68e020` plus the recorded uncommitted candidate, frozen at 2026-09-27 18:27 JST. This is R2 before subsequent R3 repairs, not the latest working tree or a complete repository archive.

App SHA-256: `627849AEE81C4FD78E0279FB19570FAF6B492284E6E89F8610F5559FF9E7BF1A`. Core SHA-256: `F6B991DC6D3BE2BCD372AAC9EC0D15B1B5E6FD832DAF7952C510C3B877F15587`. These match the scoped UI run and G launch binding. [App build log](../assets/9B160A20C83E-GhProjectsBoards.App.log) and [fixture build log](../assets/76C0EB9ACE81-GhProjectsBoards.Tests.log) accompany the frozen receipt's original build commands, both exit zero. [prepared-first-h-receipt.json](../assets/9AB80CC9A4B3-prepared-first-h-receipt.json) is byte-identical to the frozen receipt and has `preparedOnly=true`; it is not an executed or accepted H evaluation.

## Executed checks

Counts are executed / passed / failed / skipped. Selections overlap; do not sum them into unique coverage or replace earlier failures with later successes. Original logs, TRX/NUnit results, metadata and source diffs are retained.

| Record | Counts | Meaning |
| --- | --- | --- |
| [Creation unregister Red](../assets/A956425A17DF-red.trx) | 8 / 5 / 3 / 0 | Completed explicit original binding prevented the intended retain/discard unregister and save-retry paths. Other protection cases already passed. |
| [Creation unregister Green](../assets/42339B3591B0-green.trx) | 8 / 8 / 0 / 0 | Scoped real workspace/isolated-storage cases after the narrow waiver; preserved history, remote payloads and remaining-work guards. |
| [Legacy failure explanation Red](../assets/548AFFAA6AD0-red.trx) | 28 / 27 / 1 / 0 | Saved Blocked text described a known Failed attempt as uncertain. |
| [Combined R2 Core selection](../assets/05DFB730A8ED-green.trx) | 58 / 58 / 0 / 0 | Includes the preceding legacy explanation case, now passed, and its explicit scoped regressions. There is no invented standalone legacy Green file. |
| [UI selection 18:26](../assets/1FD2D692CCA3-metadata.json) | 5 / 5 / 0 / 0 | Legacy diagnostic presentation, current-bound creation completion, unknown-creation preservation and selected failed/blocked-title refresh. Bounded native UI integration, not Computer Use or whole-app acceptance. |

The UI metadata records Windows `10.0.26200.0`, SDK `10.0.401`, exact command/selection, actual binary hashes, exit zero and zero inconclusive results. The Core outputs record Release/.NET 10. Synthetic external boundaries and isolated state are not live GitHub. Exact Core invocation text was not present in the supplied log/TRX/source records at retention time. It was requested from the coordinator; no command was reconstructed or test rerun to fill that gap. The UI invocation is retained verbatim in its original metadata. [engineering-summary.json](../assets/B52C91E50366-engineering-summary.json) records checked counts and frozen-source/binary matches.

## Independent source findings remain open at R2

source-review.md (local archive: `tests/regression-evidence/issue73-workflows/blind-review/checks/recovery-r2/source-review.md`) and its [identity receipt](../assets/99E664D7D952-source-review-identity.json) predate R3 edits. The reviewer found two concrete preexisting guard omissions: a Superseded existing-field operation can retain unresolved dispatch without blocking unregister, and retired unknown setup fields can evade the guard after a normally acknowledged creation when original-response uncertainty is false. These paths were not covered by R2's eight storage cases. They are source-derived findings in this record, not newly executed reproductions or proven causes of G's crash.

The review's six product/test source hashes match the retained R2 inputs. Its specification hash is retained as review provenance; the build-input snapshot does not claim to include every reviewed document. Later R3 reproduction/repair belongs to a separate candidate and must not be attributed to R2 or G.

## Later command-provenance supplement

The subsequent retention appended [R2 commands](../assets/98E41FFEA313-commands.txt), [unregister provenance](../assets/B0F66E4B7F5E-commands.txt) and [legacy failure provenance](../assets/9347F87354A3-commands.txt). R2's invocation is coordinator transcription after execution, not recovered stdout. Full original commands for the older unregister/legacy runs remain unavailable. This supplement does not change any R2 result or make earlier incomplete provenance complete.

## Acceptance limits

R2 technical checks do not erase G's crash after Confirm and resume. G's persisted setup completion does not prove its missing settled-history, explicit fresh-fetch or normal-close steps. No new E2E, physical IME, live service, performance or human-acceptance result is established here. Retention performed no build/test, native UI, launch, source edit, commit or remote mutation. Copied originals are hashed in the [parent manifest](../assets/925D4CCE4C91-manifest.json); this README and summary are coordinator records.


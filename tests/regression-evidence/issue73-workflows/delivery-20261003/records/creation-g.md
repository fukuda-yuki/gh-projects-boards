> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/round2-creation-g/README.md`
> Original SHA256: `71658F2E8D95C4ABE50908613C05EBCEE0A268BB2EECE64722C72FE3B039EA44`

# G: creation recovery on R2 ended in a crash

**The requested journey failed.** G found and associated the administrator-supplied existing Issue, then clicked Confirm and resume once. It saw current setup completion during progress, followed by unexpected window loss. It did not click Fetch latest afterward or close normally. Independent durable state confirms Issue/Project setup completion without duplicate creation, but that does not satisfy the missing user steps.

## Independent evaluator and source

G received the business story (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-creation-g/story-card.md`), running process identity and Computer Use mechanics. It consulted no product source, specifications, tests, prior reports or usage guides. The administrator's known Issue URL was a business fact in the original story, not a navigation hint. G independently chose its route; no further clarification or recovery assistance was provided during the lease. The original report (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-creation-g/observations/report.md`), action log (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-creation-g/observations/action-log.json`), [exact UI inputs](../assets/CCA41E65F151-exact-ui-inputs.json), 28 numbered observation records and 37 original JPGs are unchanged.

The [launch receipt](../assets/4F37891D4B76-launch-7a0d597f5f2d462a85e029271e822c85.json) binds PID 24404 to the [frozen R2 candidate](../assets/9AB80CC9A4B3-candidate-frozen-receipt.json), with App SHA-256 `627849AEE81C4FD78E0279FB19570FAF6B492284E6E89F8610F5559FF9E7BF1A`. This is distinct from F's R1 and the original E executable. The [R2 engineering package](recovery-r2.md) retains all 237 declared inputs and source-specific test/review records. Later R3 work is not this executable. Candidate context is recorded in [Issue #73](https://github.com/fukuda-yuki/gh-projects-boards/issues/73#issuecomment-5854680370).

The [input lineage](../assets/A6E7BC695948-input-lineage-readback.json) starts from E's session-1 held snapshot (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round1-create-e/diagnostics/session1-held-snapshot`), schema 12 revision 214. One create request had already produced synthetic `created1 / #1001`, but no response or Project membership had been confirmed. All eight immutable input files remain unchanged in input-snapshot (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-creation-g/input-snapshot`). Preparation removed only `loseCreationResponse` from the working-copy scenario to represent restored service. It did not reset the journal, repair fields or mark the old response successful. [Preparation](../assets/07849E442915-preparation.json), [static validation](../assets/8EA90837032B-static-validation.json) and operator [Environment](../assets/378B252DBC30-Environment.ps1)/[Launch](../assets/A83941208189-Launch.ps1) scripts retain their original boundaries; their earlier `not_run` statements are not rewritten by the later launch.

## Visible sequence and remaining friction

| Decision | Original evidence | Outcome / limit |
| --- | --- | --- |
| Find uncertain local work | [Next problem](../assets/94FEF4BB20CA-006-0.jpg), [history](../assets/9EC34D1F5EBF-008-0.jpg) | G found Review follow-up and understood that creating again could duplicate work. |
| Establish access | [Connected identity](../assets/3C8879775531-013-0.jpg) | G inferred the connection prerequisite. Disabled recovery actions lacked a nearby reason, requiring a detour. |
| Resolve the existing Issue identity | [Resolution choices](../assets/E5C251C0B539-018-0.jpg), [entered URL](../assets/E993ABEC7EE2-021-0.jpg), [verified Issue](../assets/4F941239C8EA-023-0.jpg) | G chose explicit URL verification and association. Native `set_value` is not physical keyboard or IME evidence. |
| Complete remaining Project setup | [Membership still pending](../assets/2131DB1FE21F-026-0.jpg), [progress and completion message](../assets/AF131CAF4AA4-028-0.jpg) | One Confirm and resume click started work immediately. There was no separate review dialog. G had expected a possible review but did not send another input. |
| Inspect settled results, fetch latest and close | [Capture error](../assets/FB6C9491F4C5-capture-error.json), [window loss](../assets/C8A1351F3333-window-loss.json) | Not reached. G stopped without relaunch or replay after unexpected disappearance. |

The original report also records the temporary “New (local)” identity after association, prominent earlier-attempt uncertainty after current setup completion, and technical attempt/Issue identifiers. These observations are retained alongside the blocking crash. Transitional frames are not performance measurements.

## Stable crash evidence and independent readback

[Windows events](../assets/0486F6B2ABE4-disappearance-events.json) contain Application Error 1000 at `2026-09-27T09:32:54.8180632Z`, matching PID `0x5F54` (24404), the launched R2 executable, Microsoft.UI.Xaml.dll and `0xc000027b`. Related WER entries identify combase.dll / `80131509`, report `05b0922a-eecb-40cf-8d79-f26ba438ddb3`. This confirms an app crash. The retained events contain no managed stack establishing its code cause; similar window loss does not establish the same cause as E. No protected WER files were accessed.

The [snapshot manifest](../assets/7ACB00AD7053-snapshot-manifest.json) records PID absence before/after and all eight data/config files stable before/after/copy. Backup and execution lock remain; writer locks alone were omitted. [Readback](../assets/859ACF9382C6-readback.json) preserves the full before/after journal, source binding, mutation payloads and model-state checks; its note (local archive: `tests/regression-evidence/issue73-workflows/blind-review/round2-creation-g/diagnostics/disappearance-snapshot/readback-note.md`) explains the boundary.

- The original CreateIssue request is an identical 225-byte prefix. One AddToProject request was appended; total CreateIssue 1, AddToProject 1, other mutation 0. The synthetic service has one created Issue, now a member. No Status change was sent; its unspecified/default value remains Todo.
- Final checkpoint schema 13 revision 223 retains the same creation and batch. `Received`/`ReceivedId` remain null; explicit existing-Issue verification/user binding, `item-created1` and current `Completed=true` coexist with `EarlierUncertain=true`.
- Local rows, changed fields, buffers and conflicts are zero. The cached Project has 102 Issues/items, including the created Issue/member, and an added-item structural change. That cached retrieval occurred as part of the operation; it is not evidence that G performed the planned explicit Fetch latest action.

Final checkpoint SHA-256: `29BA4148DB50EE39076754C268FBA44D4A1EE2F3D8483E3D3DF506BB3351A5AD`. Current durable completion and mutation safety are distinct from the failed UI journey. Human acceptance, real GitHub, settled-history inspection and normal close are not established. The [parent manifest](../assets/925D4CCE4C91-manifest.json) retains original bytes and copy hashes; this README is coordinator interpretation.

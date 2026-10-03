> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/checks/historical-disposition/independent-app-review.md`
> Original SHA256: `CB7EC92C131BABEA46BDC3D5DD3C0687742AC6D0D255E4DEFDDD2481CC2A8B97`

# Independent historical-disposition App review

Date: 2026-09-27. Branch: `codex/issue-73-weekly-recovery`. Base HEAD: `fd5732ad4f063665bbc23be96247dd8d620d80d2`, with the uncommitted historical-disposition changes.

Reviewed `RegistrationHistoricalFields.cs`, `RegistrationApplyDialogs.cs`, `RegistrationApplyConfirmation.cs`, the three App `Attention` callers, and `HistoricalDispositionHostedTests.cs`. Existing RegistrationWorkspace, disposition and presentation collaborators were read to follow the App contract. No source/test edits, build, test run, native interaction or remote actions were performed. This review applies the repository design/test policy and installed WinUI code-review skill.

User job: understand what is known now about an uncertain historical field operation; finish handling that old operation without changing present drafts, or explicitly continue current intended work through a fresh review and approval. Preserve the historical Unknown result in either case.

## Result

No additional must-fix App safety defect was established in the inspected source. The coordinator's identified stale historical reason has been moved under the existing details disclosure in the current `RegistrationApplyDialogs.cs:260-273` source. The visible text now qualifies the historical send as unknown and its approval as withdrawn; `AddHistoricalHandling` separately explains current handling completion. This wording correction is source-reviewed only; the previously reported four passing UI cases predate it and do not establish its rendered result.

### Note / P3: dependency values use select-option wording in the new comparison

`RegistrationHistoricalFields.cs:123,128` uses `ApplyValue` for historical intended and current local values. `RegistrationApplyDialogs.cs:236-242` handles Title/Number/Date directly, then treats every other kind as a Select lookup. For Dependency's retained `present` value, it produces `present（選択肢名は未確認）`, whereas the fresh remote value correctly uses `依存関係あり` through `HistoricalValue`.

This is a preexisting formatter limitation newly exposed by the historical comparison, not a data/dispatch defect. Add a narrow Dependency branch so all three values use the same relationship-present/absent vocabulary. This reviewer did not implement or execute it.

## Source checks

- **History entry and attention:** all three App callers pass the workspace and therefore the disposition records: `EditingGrid.ApplyResults.cs:45`, `RegistrationApplyDialogs.cs:63`, `RegistrationApplyResults.cs:39`. Default attention can omit settled handling while all-history retains original Unknown evidence and the separately saved decision. The completed creation's retired fields get the same action from their exact target path.
- **Read/decision boundary:** the review shows exact target context, prior intended value, newly observed current value and time, plus retained local confirmed and pending work. Primary/secondary decisions call the separate confirmation flow after closing the choice dialog. Its second scoped observation and checkpoint guards are retained; changed evidence returns a refreshed visible choice and feedback. The App does not call normal conflict acceptance or copy historical Intended into drafts.
- **Failure boundary:** read failure offers recheck or return to history. A failed decision save retains a visible choice/error. The distinct saved-decision/newer-local-work failure state offers a local-save retry and does not ask the user to repeat the historical decision. Deferred/failed decisions do not claim current completion.
- **Lifecycle:** `Current` binds panel lifetime, workspace owner, profile, selected Project and DraftSession. `ShowDialogAsync` and `Detach` hide/ignore stale dialogs. The App rechecks context before continuing from awaited reads and choices. Core connection-revision and reviewed-revision checks remain responsible for preventing stale decisions; an enabled-looking stale choice is not treated as authority.
- **Connection return:** history adds connection settings for the new remote-read actions through the existing shared remote-actions list. It disables them while busy/disconnected, returns to the same history controls after connection work, and invalidates the return on owner/profile/Project/session changes. The continuation review uses the existing connection-return loop and queues a fresh check on return.
- **Continue:** the saved decision's exact Project is selected before ordinary review. The historical target row is initially selected; hidden-target inclusion is explicit in the review control state. The fresh review reads current committed drafts and carries the decision ID into the existing atomic approval/link path. Pending text and historical Intended do not become payloads. Cancelling leaves the Continue obligation and its history action available. A no-difference or missing matching field is blocked, with the existing history action available for a new current-state decision.
- **Scope of approval:** the reused review may include other current changes on a selected row or rows the user explicitly adds. Those changes are visible before the ordinary final GitHub approval; only the exact matching operation links back to the historical obligation. No automatic mutation or reuse of the historical approval was found.
- **Wording:** historical send outcome, current handling completion, current observation and pending local edits are distinct in source. Detailed immutable execution reasons remain available. No claim that accepting current state proves the historical request succeeded was found.

## Validation limits

The coordinator reports four passing scoped UI cases from the retained 19:54:21 run: two origins for defer/finish and preserved evidence/input, one changed-observation/save-retry case, and one Continue/cancel/reopen case. This reviewer read their source but did not run or independently re-evaluate them. The cases use real views/events, real workspace/storage collaborators and a substituted external service.

They do not establish current rendered wording after the subsequent reason-disclosure change, native keyboard/IME behavior, narrow/scaled/theme readability, connection/account switching during this new dialog, absent-field display, post-decision newer-local-save failure, cross-Project continuation, durable ordinary-product restart/unregister, or a live follow-up dispatch. Those are not passing by inference. No UI/UX or human acceptance is claimed.

Reviewed SHA-256 values:

| File | SHA-256 |
| --- | --- |
| RegistrationHistoricalFields.cs | `E969DB29CDCB92DF1F01EA838737510CE1AEA10D148F44F194776535EE2A27D6` |
| RegistrationApplyDialogs.cs | `93442C01F736B2292D7E8AA5D40B44AA2CC07098E3B8262CC34C0395A92A6810` |
| RegistrationApplyConfirmation.cs | `729C112978023ADAAFEC5007DBA13D695EF5AE448DC75383F7A52C2E24A26D3F` |
| EditingGrid.ApplyResults.cs | `BDC8E53CDDF5E93107F158BE8F0E833EB6CF9EF9E964B080AFD11930C7FAF073` |
| RegistrationApplyResults.cs | `B45F5DBDD35D48734856D3187C230686765B2D3B26E7B40DECD56D4B7CF7B524` |
| HistoricalDispositionHostedTests.cs | `76C094382D187D79D794EC36B25CFE88F205D720DA8B6EAD5EE04335DDAE50FD` |

# Interrupted Apply recovery

The user's saved state reproduced the blocked workflow in the ordinary WinUI app. Connection and complete Project retrieval succeeded. A prior cancelled batch retained 40 verified successes, one uncertain Select result and 30 unsent operations. Its remaining approval blocked a new 28-row review even after current edits were changed. Before selection, the selection hint also concealed this blocker. The original data root was read and copied, never modified; confirmation on the copy performed no remote writes.

The confirmation now displays the unfinished batch's remaining field counts before selection and offers **再確認してやり直す**. This explicit action reads the complete current Project, reconciles current edits and withdraws earlier existing-field approvals in one durable checkpoint. It preserves successes, attempts and pending text. It sends nothing. The final reviewed Apply remains a separate decision. Uncertain historical attempts remain visible in history; withdrawal does not assert that the original request failed or succeeded. Recovery involving another Project or creation work stays in history.

## Observed validation

Windows x64, .NET 10, unpackaged Release; real native WinUI views and ordinary executable. Counts are per execution, **not additive coverage totals**. [validation.json](validation.json) records source hashes, commands, counts and artifact hashes. Private originals remain in the listed ignored `TestResults` directories.

| Boundary / endpoint | Executed / passed / failed / skipped | What it establishes |
| --- | --- | --- |
| Logic and real isolated storage / scripted process boundary | 54 / 54 / 0 / 0 | Apply, recovery and result regression selection; includes the initial six recovery cases |
| Recovery logic and isolated storage / scripted process boundary | 9 / 9 / 0 / 0 | Adds cancellation/disconnection during the actual reader response and rejection of a stale recovered approval |
| Native UI integration / hosted production views | 7 / 7 / 0 / 0 | Recovery plus selected conflict, incomplete-read and connection recovery paths |
| Final recovery UI / hosted production views | 2 / 2 / 0 / 0 | 960×600 and 1280×800 logical window sizes; visible reason and action, selection retained, no dispatch during re-review |
| Whole application / external fake gh | 1 / 1 / 0 / 0 | Cancel a dispatched Title operation with an unsent Select, recover current review, explicitly apply and verify the durable results |
| Whole application / real gh and GitHub sandbox | 1 / 1 / 0 / 0 | One owned Issue: Title update, Select set, Select clear; independent readbacks; reopen without automatic writes |
| Ordinary application / copied user checkpoint and real read-only GitHub | Manual self-check | Same 28 rows: blocker visible before selection; recovery enables final Apply. Final Apply was not pressed |

The live lifecycle used **seven writes**: create/add, three product updates, remove/delete. The reviewed frozen fixture harness at `21a9884` prepared exactly one owned item and independently verified its membership. The ordinary product used the current repair. Cleanup confirmed deletion and complete baseline preservation. A separate immutable plan and budget were recorded before dispatch. No fixture or product mutation was replayed after uncertainty. No bulk setup or timing sample was run; #51's separate 50-field performance adoption gate is unchanged.

The final change after live execution only places the recovery explanation beside its button to retain table space at small window sizes; the final two-case hosted run and inspected images cover that presentation adjustment. No physical IME, High Contrast or human acceptance is claimed. No new IME behavior, API dispatch behavior or framework dependency was introduced.

The committed images use synthetic data: [reason and recovery action at 960×600](recovery-960x600.png), [current review after recovery at 960×600](reviewed-960x600.png). User data and raw GitHub responses are excluded from this extract.

## Retained failures and review

- The first new logic fixture held an obsolete workspace object across a checkpoint replacement. Its six setup failures were retained, then corrected to inspect the accepted workspace. The proper Red run had two expected behavioral failures and four passing safety cases.
- One subsequent logic assertion compared nested deserialized record/array identity; it was corrected to compare persisted states and attempt evidence. This was a test defect, not a product failure.
- The UI Red reproduced the missing recovery button. An initial Windows PowerShell invocation stopped on native stderr handling before execution; another build failed because the accessibility enum namespace was missing. Both attempts were retained and corrected.
- The first ordinary-app attempt exposed a helper assuming that a hidden stop control always exists. The next attempted a second Title unsupported by that fake endpoint; it returned the expected unhandled-process failure rather than proving recovery. The final journey uses its supported Title plus Select contract and passed. No contractual assertion was weakened.
- A separate read-only reviewer inspected the implementation and tests, found no implementation blocker and requested in-flight cancellation/identity plus stale-review coverage. Those cases were added and passed; the reviewer confirmed that the gap was closed. Source review is distinct from execution.

Retained private failure artifacts, relative to the repository root:

| Attempt | Artifact |
| --- | --- |
| Initial logic fixture setup | `TestResults/issue51-live-regression/red/recovery-red.trx` |
| Behavioral Red: two failures, four passes | `TestResults/issue51-live-regression/red/recovery-red-2.trx` |
| Persisted-value assertion correction | `TestResults/issue51-live-regression/green/recovery-green.trx` |
| Native UI Red: missing action | `TestResults/ui-integration/run-20260919-184234-324-8b581a27/results.xml` |
| Accessibility namespace build failure | `TestResults/ui-integration/run-20260919-184433-251-22588fbd/build.log` |
| Ordinary-app stop-control helper failure | `TestResults/e2e/20260919-184809-08a0785faced4921947c70fd04354e03/e2e.trx` |
| Unsupported fake-endpoint fixture | `TestResults/e2e/20260919-184939-74bef6ec305e4a56ac494372c18fff2d/e2e.trx` |
| Passing first size run whose pixels exposed reduced table space | `TestResults/ui-integration/run-20260919-185904-431-32315841/results.xml` |

## Delivery

The follow-up branch starts from freshly fetched main `13d8bd7` (PR #58). Product changes and evidence are local. See [handoff.md](handoff.md) for maintainer publication. Push, PR creation, merge, release and Issue closure remain user-owned. Refs #51 and #10; this is not completion of either owning Issue's broader acceptance.

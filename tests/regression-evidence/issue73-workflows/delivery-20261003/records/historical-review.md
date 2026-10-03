> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/checks/historical-disposition/independent-review-follow-up.md`
> Original SHA256: `45CC7BD0212D74C4D10B20DBB35C14874153BBC2175FCBE5BDBD66BB96A1ACA5`

# Historical disposition: independent review follow-up

The independent subagent performed a source-only review. It did not edit, build, run tests, operate UI, or access GitHub. Its two findings were restore-validation gaps: old observation/readback timestamps could authorize settlement, and a later decision could reopen a previously settled Continue operation. These were not represented as executed failures until the implementing agent reproduced them below.

`core/red-temporal.trx` records three executed failures in real DraftStore loads: an observation before the original dispatch, a linked successful readback before its fresh approval, and a new choice after the linked success. All failed because loading the contradictory record did not throw. The test preserves and compares the malformed file bytes; rejection never rewrites evidence.

`HistoricalFieldHandling.ValidateObservation` now rejects an observation earlier than the original approval, any original attempt, or the original verification. Full-record sidecar validation rejects a linked successful readback earlier than its approval or attempts. It also compares the immediately preceding effective decision for the same target: a later choice cannot follow a success already observed by that time.

This is deliberately not a permanent terminal flag for every eventually successful Continue. An earlier successor can finish after a later choice was already made. `EarlierSuccessAfterALaterChoiceDoesNotReplaceThatChoiceOnRestore` uses the actual confirm/checkpoint, later decision, executor resume, and restart path to preserve that later open choice. A final success on an older link does not replace it.

`core/green-temporal.trx` records 19 passed, 0 failed, 0 skipped; `core/safety-attempt1.trx` adds the broader sidecar safety cases with 37 passed, 0 failed, 0 skipped. These are Core and isolated-storage results, not UI or ordinary-application acceptance.

The chronology checks detect inconsistent persisted evidence. Wall-clock timestamps are not cryptographic provenance or a durable-success revision. A backwards clock that contradicts the saved timeline is rejected conservatively; no timestamps are clamped and no original uncertainty is silently waived. Existing legacy journal validation is unchanged. The first two initial attempts (`red-accept`, `accept-attempt1`) predate the per-attempt source-copy script; their commands/logs/TRX are retained, but no contemporaneous source-hash capture is claimed. Later attempts have exact copied sources and manifests under `core/<attempt>-source`.

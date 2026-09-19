Fresh per-field Apply validation and readback launch 21 gh boundary invocations on the simple successful path. Combine the initial definition/item queries and consolidate immediate preflight to reduce this to 11, while checking the bound viewer in every contributing response and retaining complete pagination, fresh mutation authorization, serial pacing and durable intent/readback.

This is a **review candidate with adoption blocked**, not a completed performance acceptance. The ordinary app uses A+B without experiment flags and retains the Apply completion/results journey. A separate read-only reviewer inspected implementation and tests and found no unresolved B-specific safety issue. Cross-process credential-store races remain a pre-existing limitation; observations are not transactional snapshots.

The same corrected harness on current main and the candidate observed 1,050 → 550 execute invocations for 50 Title changes, with unchanged six preparation invocations and 102 checkpoint commits. The synthetic three-sample medians were 50.468 → 50.248 seconds; this small sequential-run difference is mainly in checkpoint time and does not establish causal or live speedup. No-change, one-change, paginated Select and mixed-field controls passed.

Fixture preparation, verification, measurement, restoration and cleanup are separate guarded stages with retained ownership, binary/source pins, lifecycle budgets and uncertainty handling. One single-item live diagnostic passed and all four create/add/remove/delete writes were independently reconciled. It did not reproduce or explain the historical Project-add failure. The fixed counterbalanced 50-Title live comparison remains Not run pending a supported correction or validated containment. No bulk retry was attempted.

Validation (executed/passed/failed/skipped):

- Focused Core/adapter/storage regression: **195/195/0/0**; final identity/lifecycle/privacy selection: **71/71/0/0**, overlapping selections reported separately.
- Real WinUI UI integration: **8/8/0/0**; representative ordinary executable journeys: **3/3/0/0**, with isolated external fake gh. Screenshots inspected; no human or physical-IME rerun acceptance claimed.
- Fixed synthetic comparison: **14 measured + 10 warmup samples**, all successful, exact paired seed/remote hashes checked.
- Live lifecycle diagnostic: **1 attempt, 4 logical writes**, separate verification/cleanup passed and original complete sandbox snapshot restored. **Zero current live Apply timing samples.**

Failed build/test/selector attempts and two SDK-generated host warnings are retained in the evidence. No dependency, transport-token, concurrency, mutation-batch or UI-input redesign is included. [Committed report, source/binary hashes, raw metric extract, security review and support packet](https://github.com/fukuda-yuki/gh-projects-boards/blob/codex/issue-51-apply-candidate/tests/performance-evidence/issue51/2026-09-19-candidate/README.md).

PR #57 is separate; its prepared Testing replacement references only #53/#54 and has not been applied. Publication, main integration, enterprise validation and release acceptance remain separate.

Refs #51

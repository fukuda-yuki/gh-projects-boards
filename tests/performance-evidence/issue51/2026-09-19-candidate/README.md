# Issue #51: reviewed local A+B candidate

**Disposition: A+B is implemented in the ordinary product path and ready for source review. Optimization adoption remains blocked by unresolved live fixture preparation; no current-candidate live 50-field throughput or integration/release acceptance is claimed.** Publication, PR creation, merge and Issue closure remain maintainer-owned.

Freshly fetched main was `5e5fb12b24e453dcd41d993b23b29ce78ec7f81c`, with no subsequent changes at baseline selection. The old experiment/worktree and nine verified source archives were preserved. The current candidate binary is from `21a9884cacc1b8035ec70d83f79c9db5aed48e4d`. A combines the fresh initial definition/item pages; B consolidates immediate preflight and validates the bound viewer in every contributing response. There is no experiment switch or cross-field credential/permission cache. C/D/E were not implemented or rerun; their earlier design gates/dispositions remain in #51.

## Evidence obtained

Both sources used identical corrected harness source and common trace instrumentation, frozen outputs, byte-identical initial checkpoints/remote fixtures, production pacing and real durable storage. Four small controls supplement the declared 50-Title workload. Per source: one warmup per case, three measured 50-field samples, one measured sample per control. **24 successful synthetic samples total: 14 measured and 10 warmups.** No failures/exclusions in these fixed comparisons. This is in-process synthetic gh response evidence, not real process/network latency.

| Current-source 50 Title metric | Main | A+B |
| --- | ---: | ---: |
| Execute boundary invocations, each sample | 1,050 | 550 |
| Prepare boundary invocations, each sample | 6 | 6 |
| Logical mutations, each sample | 50 | 50 |
| Durable checkpoint commits, each sample | 102 | 102 |
| Prepare + execute median | 50.468 s | 50.248 s |
| Prepare + execute min–max | 50.455–50.532 s | 50.235–50.316 s |
| Execute median | 50.452 s | 50.233 s |
| First durable success median, from confirmation | 46.484 ms | 50.979 ms |
| Mandatory pacing wait median | 48.596 s | 48.601 s |
| Inclusive checkpoint time median | 1.823 s | 1.594 s |

The measured work reduction is **21 to 11 boundary invocations per changed field**, with unchanged preparation count and durability. Invocation count is not HTTP request count. The synthetic median sum is 0.221 s (0.44%) lower in this fixed run, and these observed ranges do not overlap. However, the dominant wait is unchanged, the difference appears mainly in checkpoint time rather than synthetic observation time, and all main samples preceded all candidate samples. This does not isolate an optimization-caused elapsed benefit or establish live speedup. Inclusive spans overlap and must not be added together. Do not transfer the old single 1/10-field live timings to this candidate.

No-change retained zero writes and zero new journal batches. The one-Title and one-Select controls each changed 21 to 11 execute invocations. The 101-item Select case crossed the complete retrieval pagination boundary. Two rows with both Title/Select changed performed four mutations and changed 84 to 44 execute invocations. Those controls have **one measured sample each**, so they establish correctness/work counts, not repeatability or Select/mixed throughput.

The independent read-only security review found no outstanding B-specific safety issue. Focused Core/adapter/storage regression passed 195/195; the final evolved identity/lifecycle/privacy selection passed 71/71. Real UI collaboration passed 8/8. Representative ordinary executable journeys passed 3/3 with an isolated external fake gh, preserving PR #57's success/table return, retained history/restart and mixed-result correction path. See [validation and failed attempts](validation.md), [security findings](security-review.md) and [ordinary-app evidence](ui-evidence.json).

## Live evidence and blocker

One predeclared single-item diagnostic used exactly four logical writes. Create/add, independent Verify, and separate Cleanup all passed; membership went 0 → 1 → absent, the created Issue returned 410 Gone after deletion, and the complete original sandbox snapshot was restored. The old error did not recur. It remains unclassified because its original detail was not retained; one successful isolated add does not validate a containment for bulk setup. No second attempt or 50-item preparation was made.

The staged runner retains explicit identities, full baselines, binary/source pins, shared lifecycle ledgers, service cooldown and rolling-budget checkpoints. Restoration uses a persistent Apply journal; uncertain operations are not blindly replayed. Completed stages can continue independently across budget windows. A failed/uncertain measurement is retained and blocks a new timed sample; it is not silently replaced. Cleanup remains a separately invoked, ownership-checked stage.

The [support packet](support-packet.md) is prepared and **not submitted**. The exact next action is to obtain a supported correction or validated containment for Project-add, then run the fixed **main, candidate, candidate, main** comparison of 50 existing Titles with restoration after each sample. The full lifecycle is 600 planned mutations and must span admitted budget windows. No arbitrary speed percentage was added as an acceptance threshold.

| Required question | Answer supported by current evidence |
| --- | --- |
| Is invocation reduction present in the ordinary candidate? | Yes in source/runtime corroboration and all fixed synthetic samples: 1,050 → 550 for 50 fields. |
| What portion of live elapsed time changes, versus moving into setup/restoration? | **Unmeasured.** Current live work only diagnosed fixture lifecycle. Synthetic preparation work did not move; lifecycle stages are outside timed Apply. |
| How much variation remains between valid paired live samples? | **Unmeasured:** zero current paired live samples. The counterbalanced plan is fixed prospectively. Synthetic ranges are above; they are not live variation. |
| What is established for 50 fields? | Correct synthetic settlement, stable count reduction and preserved durable writes. Current live 50-field latency/error behavior, Select/mixed throughput and adoption remain unverified. |

## Inspectable files

- [Prospective plan](plan.md), [exact commands and continuation rules](commands.md).
- [Source/binary mapping](source-binary-map.json), [common harness hashes](common-harness-files.json), [recovered archive verification](recovered-archives.json).
- [All measured numeric samples](samples.json), [summaries](summary.json), [measured and warmup outcomes/categories](sample-outcomes.json), [real `gh --version` calibration](process-calibration.json), [environment](environment.json).
- [Single-item readback](diagnostic-readback.json), [sanitized lifecycle metadata](diagnostic-stages.json), [private original sample hashes](private-original-hashes.json).
- [Prepared PR #57 Testing replacement](pr57-testing-replacement.md), based only on its #53/#54 evidence. PR #57 was not edited and is not the optimization delivery.

Original private archives, raw measurements, checkpoints, fixture manifests, complete snapshots, TRX/logs and screenshots remain under ignored `TestResults`. The extract excludes private Project/title/account content and credentials. Source, comparison, UI, live lifecycle and human acceptance are separate evidence boundaries. No remote branch, new PR, main integration or release was created by this increment.

# Focused observation and lifecycle review

Disposition: retain A+B for maintainer review. A separate read-only reviewer inspected the production changes and corresponding test implementations, then inspected the retained TRX results. The reviewer did not execute tests or make writes. No outstanding B-specific security finding was reported. This is not service throughput or human acceptance.

## Evidence and lifetime

| Consolidated check | Replacement and lifetime |
| --- | --- |
| Separate pre-observation `RecheckAsync`, then first `SendAsync` | `RecheckAndReadAsync` performs current version, auth-status/store/scope, identity/context checks and the immediately following read under the same local connection gate. Evidence lasts for that dispatch only. |
| Redundant separate current-user lookup immediately before the first scoped query | Every contributing GraphQL response, including definition/value continuations, supplies `viewer.databaseId`. The reader accepts data only when that positive integral ID equals the bound context. It does not trust an earlier successful lookup as proof of the response's principal. |
| Two initial definition/item requests | One aliased request supplies both initial connections. Both existing validators and all required continuations run; missing/partial/error data cannot establish an observation. |
| Continuations | Each continuation still performs fresh guarded dispatch. Each response independently proves its viewer and item/Project identity. Nothing is reused across fields or batches. |
| Mutation authorization | Unchanged independent full mutation preflight: current identity, credential store, required scope and cancellation. Observation evidence never authorizes a later mutation. |

A mismatched response viewer invalidates the bound context; switching back does not revive it. Missing/malformed viewer evidence rejects the read. Tests cover a cross-account response that appears already converged (must not acknowledge without writing), a switched post-write response (must remain Unknown), and definition/value continuations. Version, keyring, scope, cancellation and changed credentials at mutation dispatch remain covered. Read-only full-Project retrieval remains outside this optimization.

The local semaphore cannot lock another process's credential store or make auth-status metadata and server execution atomic. That residual TOCTOU existed on main; same-response principal proof closes the cross-account observation gap without claiming atomic credential-store/scope attestation. Pages are not a transactional snapshot, and mutation/readback is not compare-and-swap or exactly-once execution.

## Review-driven lifecycle corrections

The independent reviewer identified and inspected corrections for stage output escaping shared ledgers, cross-stage service cooldown, malformed success envelopes, nonresumable restoration, quoted credentials, and uncertain cleanup replay. Final review found no material blocker for the declared `Diagnostic=true`, `Fields=1`, empty-order diagnostic and its guarded verification/cleanup. That disposition explicitly does not clear bulk fixture preparation.

Corrections preserve intent before writes, retain the restoration Apply journal, reject uncertain cleanup replay while a target remains, reconcile independently confirmed absence, parse HTTP-date Retry-After, carry mutation pacing across stages, and restrict diagnostics to bounded allowlisted words. Sensitive-content tests include mixed quotes, unquoted credential markers, URLs and oversized messages. No general raw-response logging was introduced.

Final review inspected `harness-final-v3/results.trx`: 71 executed, 71 passed, 0 failed, 0 skipped. Earlier failed attempts and their separate causes are listed in the validation extract. Review findings were resolved before the first live diagnostic dispatch.

The separate final evidence review also checked all 92 original sample hashes, 24 outcome projections (14 measured/10 warmup), 12 TRX ledger records, both source archives and six frozen binaries, three diagnostic process/result records, 0-to-1 membership, four mutation intents and byte-identical cleanup baseline. It corrected a report-only 404 statement to the observed HTTP 410 Gone and identified the archive line-ending distinction now documented in the source map instructions. No remaining material factual, claim or privacy finding was reported. This second review also made no builds, test executions, edits or network calls.

## Scope limits

No claim of cross-process credential locking, enterprise schema acceptance, repeated live 50-field throughput, physical IME or human usability acceptance. A successful isolated add cannot classify the historical failures. Bulk setup remains gated by a supported correction or validated containment.

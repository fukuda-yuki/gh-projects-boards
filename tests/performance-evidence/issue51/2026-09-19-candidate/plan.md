# Fixed candidate validation plan

Recorded before new live dispatch. Owning Issue: [#51](https://github.com/fukuda-yuki/gh-projects-boards/issues/51).

The freshly fetched comparison baseline is `5e5fb12b24e453dcd41d993b23b29ce78ec7f81c`. Its PR #57 completion/results behavior is retained. Product A and B are separate local changes; no experiment environment switch selects the ordinary product. C/D/E are outside this increment. Push, PR creation, merge, release and closure remain maintainer-owned.

## Diagnostic gate

The historical add requests used the supported `addProjectV2ItemById(projectId, contentId)` operation, explicit IDs and stdin JSON. Installed CLI `v2.100.0` [item-add source](https://github.com/cli/cli/blob/v2.100.0/pkg/cmd/project/item-add/item_add.go) uses the same input. It does not supply a different transport remedy. Retained error metadata and manifests establish post-error membership, not the cause; original error messages are unavailable.

One initial diagnostic attempt is declared: create one run-owned sandbox Issue, independently read it and the complete Project membership before attempting any add. If already present, retain that fact and skip add. If absent, dispatch exactly one add, capture bounded allowlisted message words, protocol code and request ID, stop on error and independently verify in a separate read-only stage. This tests the unresolved pre-add-membership/isolated-add hypothesis; it cannot retroactively classify either historical error. A second diagnostic may be used only with a new supported hypothesis, never as automatic retry. Maximum two single-item attempts across this increment. No configuration changes or bulk setup are diagnostic methods.

Each diagnostic plans at most four logical mutations: create, add, remove, delete. Preparation reserves cleanup. A failed/uncertain add is never replayed; verification and cleanup are separate stages. A single successful isolated add alone does not resolve intermittent historical failures or authorize bulk setup. Without a supported correction or validated containment, prepare a support packet and leave the 50-field live comparison Not run.

## Fixed measurements after the gate

Use exactly 50 independently verified, run-owned existing items, Title only, for two paired repetitions in fixed order: **main, candidate; candidate, main**. Restore exact baseline titles after each sample using the frozen main binary. Reuse one fixture and identical edited checkpoint bytes; verify its entire declared remote snapshot before every sample. No live warmup and no extra sample based on results. Record every failure. Any change to this plan requires a new prospective record, never silent substitution.

Full lifecycle: 50 creates + 50 adds + 200 measured changes + 200 restores + 50 removals + 50 deletions = **600 planned mutations**. This exceeds the existing local rolling-hour ceiling of 480: preparation, samples, restoration and cleanup must span permitted windows. Each stage reserves restoration/cleanup; budget deferral writes a checkpoint, earliest local continuation time and exact command arguments, without sleeping/recreating fixtures. Include old failed attempts and cleanup through explicitly recorded ledger roots. Primary response headers and resource-body reserves must also pass. Limits are guards, not a throughput target or knowledge of other clients.

Interpretation: publish per-sample prepare, first durable success, settlement, sum excluding human review, actual gh categories, inclusive process/network wall time, waits, parsing/checkpoint spans, bytes, errors and durable results. Keep preparation/restoration separately timed. Show both paired differences, min/median/max and order/temporal variation. Work-count reduction is distinct from elapsed benefit; no arbitrary speed-percentage gate or p95. Two repetitions remain small. Title timing does not establish Select/mixed throughput, human acceptance or enterprise compatibility.

## Current-source deterministic and product checks

Use identical corrected harness source for main and candidate binaries; preserve build/source mappings. CandidateCheck synthetic workload: 50 Title changes (one warmup + three samples), no-change and one-change controls, one Select across 101 items, and two-row mixed Title/Select (one warmup + one measured sample each). Historical A/B/AB matrix remains supporting evidence; it is not rerun. Production pacing and durable I/O remain enabled.

Focused logic/adapter tests cover viewer provenance (including convergence/readback and continuations), complete aliases/pages, fresh mutation credentials, Apply/creation recovery and prohibited writes. Lifecycle tests use real orchestration/storage with only the external process replaced: stage separation, fixed identities/seed, uncertainty, ownership, diagnostic sentinels and deferred budgets. Ordinary executable verification uses representative existing Apply completion/results journeys with fake gh; live throughput remains at the adapter boundary.

Public extracts exclude private Project content, titles, account metadata, raw responses, machine-local user paths and credentials. Retain originals privately and publish hashes and minimal metrics. All evidence is scoped to its actual source and execution boundary.

# Post-#68 stabilization receipt

Owner: [#65 post-merge assignment](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5749646159), with [#64 containment/correctness](https://github.com/fukuda-yuki/gh-projects-boards/issues/64#issuecomment-5749649696) and [#61 focused workflow reevaluation](https://github.com/fukuda-yuki/gh-projects-boards/issues/61#issuecomment-5749652161). This changeset starts from integrated `ab292d50f02d4e373871c48f408ce1151bf7a169`. It does not republish the old repair or downgrade the checkpoint.

## Corrective scope

- Same-session retry of a rejected first checkpoint preserves original recovery bytes, including a locked candidate. Unknown orphan files remain recovery problems for another writer or restart.
- Verified creation/binding checks protected baseline identities, local dependency collisions and existing per-edge draft intents before mutation.
- Conflicting Project-item appearances of one Issue remain present but cannot supply canonical labor, generated dates/actuals, mapped publication or a replacement baseline. Registration changes invalidate cached calculation. Pending text remains separate from committed scheduling input.
- Normal Summary is disabled and reads `Summary（準備中）`; remembered Summary returns to Boards. Isolated evaluation keeps the retained consumer. Rollup detail, unknown hours, cutoff labels and row/attribution selection are corrected without expanding Unit D.
- Gantt retains duplicate appearances without a dictionary exception, excludes contradictory relation targets, shows source problems before warnings and retains a visible distant selection after an edit.
- Unseen sheet columns defer native editor construction. Original active/pending/composing controls remain protected. Vertical-only scrolling no longer reapplies horizontal header/column state across all row shells.

See [review dispositions](review-disposition.md), the original [combined-source review](reviews/combined-source.md), and the [corrective-source re-review](reviews/corrective-source.md). These are source reviews, not runtime or human acceptance. The later targeted fixes and their regressions are identified in the execution ledger.

## Measurement contract

The [prospective comparison](comparison-plan.md) fixes the workload, matched observer boundary and six final-candidate repetitions. The [observer-only baseline patch](observer-baseline.patch) changes instrumentation, not product behavior. Intermediate full/light/off attempts and separate profiles remain separate from final measurements. [Scroll classification](analyze-scroll.py) preserves every >=100 ms callback interval and independently brackets commanded viewport progress from unmodified GDI captures. Captured pixels, native value/UIA latency, public viewport notifications and durable saving have distinct clocks and endpoints. Physical scanout is not measured.

## Acceptance boundaries

An unresolved/reproduced application-caused visible freeze keeps #65 performance open. The native retention probe also remains failed: the matched baseline and candidate both retain 78 sampled unloaded wrappers against the existing <=65 condition. App-owned cache reduction is not proof that native wrappers were released. Earlier runs with zero unloaded samples did not certify disposal. That explicit-GC probe runs in its own host; GC is never used in the product or latency workload.

Focused human workflow reevaluation uses separate synthetic data and remains distinct from engineering measurements. Complete Summary acceptance/expansion, P1/P2, other display/scaling/High Contrast/accessibility environments, broader recovery, #51 throughput, release packaging, live GitHub and human acceptance retain their existing owners. This changeset does not close #65, #64 or #61.

## Frozen execution receipt

Product corrections are committed at `a776073c5221ce7184208a5adfb8890031a47bff`. Test-operation corrections at `948dd5393aed7992f1b058301532989771921a90` leave `src/` identical; this is the frozen ordinary-app/driver source for the six repetitions, IME, thumb, trace-disabled companions and separate profile. Later changes in this directory are evidence only.

**Scroll performance is FAIL / still open.** All six final light-observer runs retain application-side viewport delays corroborated by independent pixels. Native-value typing p95 is <=100 ms in each run; that does not clear scrolling. The 1,000-task Gantt calculation maximum is 1,298.9222 ms against <=1,000 ms. The final native-retention probe also fails, with 78 sampled unloaded wrappers. See [measurements and cause/fix map](performance.md).

The [execution receipt](executions.md) separates Core, actual controls, ordinary fake-gh workflows, native checks, retained failed attempts and NOT RUN boundaries. The [focused handoff](handoff.md) provides fresh and weekly isolated roots and the retained launcher. This prepares workflow reevaluation; human acceptance remains NOT RUN.

The [raw evidence archive](raw-evidence.zip) is a remotely deliverable artifact, not a local-path attachment claim. It contains original trace/driver/result bytes and 3,442 timestamped captures stored by content hash (48 unique PNGs), with a map back to every original run/name. [Artifact manifest](artifact-manifest.json), [binary manifest](binaries.json), [machine receipt](machine.json), [measurements](measurements.json), [all 59 result attempts](executions.json), [per-pause ledgers](scroll/) and original source reviews are readable alongside it. The large original synthetic checkpoints and runtime profile files remain local with hashes; their derived data and exact producer/source identity are included. No raw failure was repaired or regenerated.

Restore one complete run into a new directory with `python unpack-evidence.py raw-evidence.zip <new-directory> --run post68-final-cold-01`, then run `analyze-scroll.py` against its `observations` directory (Python + Pillow). A fresh extraction reproduced the complete original classification exactly. Original full-window captures with potentially unrelated edge/occlusion pixels and private review-session URLs are excluded from publication.

The combined post-#68 workspace could strand a rejected first checkpoint, collide with protected planning/dependency identities, and adopt conflicting Project-item appearances of one Issue. This corrective increment preserves rejected bytes and original identity/history on rejection, treats contradictory canonical inputs as unresolved, and repairs duplicate Gantt rendering and task selection across views. Normal Summary is disabled as `Summary（準備中）`; isolated access retains corrected unknown/rollup/cutoff/selection meaning without expanding Unit D.

Native editors for unseen columns are deferred until reveal/focus, and unchanged horizontal state is no longer reapplied during every vertical callback. Existing active/pending/IME controls, saving, cache limits, dispatcher priorities and gh serialization remain intact. Checkpoint v12/planning v4 and v10/v11 compatibility are preserved.

**Scroll performance remains failed, so this PR does not close the owning Issues.** Three cold and three warm runs of frozen `948dd5393aed7992f1b058301532989771921a90` retain application-side viewport delays confirmed by independent GDI pixels in every run. Typed native-value/UIA p95 remains <=100 ms, while maximum scroll callback intervals range from 260.91 to 353.56 ms. Callback gaps, visible stalls, GC/profile spans and durable saving are classified separately. The Gantt recalculation maximum of 1,298.9222 ms and native-retention result of 78 against <=65 also remain failed.

Validation is preserved as separate executions: Core 659/659 before the final ordering-only fix, then 40/40 affected cases; actual controls 84 passed/4 failed in an 88-case run, followed by 3/3 corrected test operations (the timing failure remains); four additional combined-control regressions passed; ordinary fake-gh planning/Gantt/restart journeys 2/2; final physical IME/save-retry and thumb/distant-edit/horizontal-return state checks passed. A separate final retention host still fails. No all-pass aggregate, live GitHub or human acceptance is claimed.

- [Corrective review dispositions and original independent reviews](https://github.com/fukuda-yuki/gh-projects-boards/blob/5865732411f6b085bf24d5d85846adc519b58d5d/tests/regression-evidence/issue65-post68/review-disposition.md)
- [Current-source cause/fix/measurement receipt](https://github.com/fukuda-yuki/gh-projects-boards/blob/5865732411f6b085bf24d5d85846adc519b58d5d/tests/regression-evidence/issue65-post68/performance.md)
- [Execution ledger, including failed attempts](https://github.com/fukuda-yuki/gh-projects-boards/blob/5865732411f6b085bf24d5d85846adc519b58d5d/tests/regression-evidence/issue65-post68/executions.md)
- [Fresh/Weekly isolated reevaluation handoff](https://github.com/fukuda-yuki/gh-projects-boards/blob/5865732411f6b085bf24d5d85846adc519b58d5d/tests/regression-evidence/issue65-post68/handoff.md)
- [Portable original evidence, manifests and per-pause ledgers](https://github.com/fukuda-yuki/gh-projects-boards/tree/5865732411f6b085bf24d5d85846adc519b58d5d/tests/regression-evidence/issue65-post68)

The corrective product commit is `a776073`; `948dd53` changes test operations only; later commits are evidence only, with identical `src/`. Independent reviews cover their identified combined snapshots, not every later line. The prepared isolated workflow is ready for focused human reevaluation; P1/P2, Summary acceptance/expansion, other display/accessibility environments, broader recovery, #51 throughput, release and live validation retain their owners.

Refs #65
Refs #64
Refs #61

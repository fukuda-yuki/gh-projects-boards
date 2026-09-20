# Unit C continuation

Checkpoint: app-connected timeline and complete editing/lifetime integration are
implemented on `codex/issue-15-gantt`, based on integrated PR #66 / main
`40489a2d6eb3edfa4ed4a15775bbbbc9b9052acd`. The acceptance map is maintained in
README.md; do not create a second map. No product decision is pending.

Observed evidence: Core projection 10/10 (`TestResults/issue15/review-core`);
ordinary Gantt plus linked planning/Apply 2/2 (`TestResults/e2e/20260920-101558-b1b74613a36b45a3baf222270dce03b0`);
broadened hosted selection 29 pass / 1 fail (`run-20260920-103615-284-0f87603b`),
with that save-retry test's premature readback corrected and passing alone
(`run-20260920-103741-152-bfcd94bd`). The 29/1 attempt is retained, not rewritten.

Independent review's original four findings are corrected: hidden save/Undo
errors, filtered Gantt selection on Project return, invented missing-owner weight,
and maximum-date overflow. F6 and actual bar/link geometry are also checked.
Light screenshot review found a transparent common selector on the capture's
black background; an explicit theme-resource background is added and needs its
new captures inspected.

Checkpoint update: the ordinary four-case selection including direct/F2 physical
Japanese composition passed (`20260920-103920-0b59472f4eee489394d26e1860f05258`).
The retained Gantt search now reveals an explicit incoming Boards selection.
Unfiltered row-1000 editing reproduced a viewport reset (`run-20260920-104121-415-3950352b`);
restoring vertical offset after row-record replacement passed the unchanged
regression (`run-20260920-104240-424-969ebeb6`). Both are consumer corrections.
Holiday/personal-exception context and corrected Light/Dark captures passed
(`run-20260920-104439-395-ba8ad201`); the Light image was inspected with readable
common-view labels and a continuous page background.

Source checkpoint: `27b94874affb607606c31a9839908c888c287475` contains the complete
product code. The broadened hosted selection passed 32/32 on that clean source
(`run-20260920-104616-223-39717512`). Final ordinary selection was 3 pass / 1 fail
(`20260920-104734-fc2a7fb9a54446c2b2255fab8786d1ad`): the new test toggled an
already-closing responsive pane open again. It now waits for the public closed
state; the ordinary 1,000-task case passed (`20260920-105213-cbca55e14dff43ffa3c135e1fec6525e`).
Hosted scale capture now also waits for the selected row to be onscreen and
reveals it after narrowing (`run-20260920-105125-599-6ad371e0`, 1/1). Earlier images
that captured the selection offscreen are retained but are not the final evidence.
These final changes affect the test driver only, not product behavior.

Next: commit these observation corrections and run the final scoped selections;
inspect their final normal/narrow images; finish evidence handoff;
pin final source and evidence including failed attempts and measurement boundaries;
prepare Unit C-only PR text and factual #15/#1/#65 updates. Keep publication,
PR creation, merge, Issue closure and release user-owned. Do not start P2.

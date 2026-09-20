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

Next: pin this source and run the final scoped hosted/ordinary selections;
inspect their final normal/narrow images; finish source review;
pin final source and evidence including failed attempts and measurement boundaries;
prepare Unit C-only PR text and factual #15/#1/#65 updates. Keep publication,
PR creation, merge, Issue closure and release user-owned. Do not start P2.

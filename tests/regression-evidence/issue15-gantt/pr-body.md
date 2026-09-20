Boards had adopted Auto/Manual plans but no timeline inspection path. This adds a common Boards/Gantt selector, exact day/week bars, contextual dependencies and linked same-task date editing over the existing plan, Undo and checkpoint lifecycle. Pending text, native composition, Project selection and distant-row position are retained; unknown/partial/stale work stays visible without fabricated bars. Summary remains unavailable until #64.

Dependency: PR #66 is merged in main at `40489a2d6eb3edfa4ed4a15775bbbbc9b9052acd`. This PR contains Unit C only; it does not republish A/B, add another scheduler or implement P2.

## Testing

Validated source/tests: `226220233ccdb3e0961d3671b7b89c9b71e600b4` (production identical to `27b9487`).

- Logic projection: 10 passed, 0 failed/skipped; independent minute coordinates, dates, missing/stale states and maximum date.
- Scoped WinUI integration: 32 passed, 0 failed/skipped; real controls, editing/Auto/Undo, filters/Project roundtrips, save retry, holiday/personal exceptions, Light/Dark, both axes and 1,000-task distant edit/navigation, plus affected existing planning/row/focus/theme cases.
- Ordinary/native: 4 passed, 0 failed/skipped; two whole-app workflows with isolated storage/external fake gh and direct/F2 physical Japanese IME cases. Weekly workflow includes reviewed Apply with nine asserted payloads. This is not live-GitHub evidence.
- Release solution build succeeded. Independent source/image review has no remaining must-fix finding. Normal/narrow ordinary pixels inspected separately.
- 1,000 tasks / 20 people, two warmups + ten samples: replan invoke through expected state/render events max 660.349 ms; separate complete checkpoint save max 475.559 ms. Declared engineering boundaries, not physical scanout or a speedup claim.

[Evidence index, failures, commands, evaluation and sanitized artifacts](https://github.com/fukuda-yuki/gh-projects-boards/blob/codex/issue-15-gantt/tests/regression-evidence/issue15-gantt/README.md).

Human acceptance, High Contrast/other DPI, #65's inherited latency/full-job gates and #51's 50-field throughput disposition remain outstanding. No release or all-P1 acceptance is claimed.

Refs #15. Refs #65.

## Related Issue

- Refs #

Use `Closes #...` only when the Issue's full acceptance criteria are satisfied.

## Summary

-

## Validation

Follow [the test policy](https://github.com/fukuda-yuki/gh-projects-boards/blob/main/tests/README.md): logic-layer unit tests > UI-layer integration tests > E2E tests. Select relevant boundaries, not a mandatory run of every tier. For document-only changes, record consistency/link review instead of claiming product execution.

- Changed behaviors and their lowest reliable test boundaries:
- Checks performed, including source, commands, environment, executed/passed/failed/skipped counts and artifacts:
- E2E/physical IME/live checks selected and the specific risk or acceptance need they establish beyond lower layers (or why none were relevant):
- Relevant coverage outside the selected scope, unavailable coverage, retained failures and blockers:

When moving coverage between layers, link the behavior mapping and observed replacement results. Do not label an external-driver E2E as UI integration or present a scoped pass as full regression.

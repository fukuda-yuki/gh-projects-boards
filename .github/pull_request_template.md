## Related Issue

- Refs #

Use `Closes #...` only when the Issue's full acceptance criteria are satisfied.

## Summary

-

## Validation

Follow [the test policy](https://github.com/fukuda-yuki/gh-projects-boards/blob/main/tests/README.md): logic-layer unit tests > UI-layer integration tests > E2E tests. Select relevant boundaries, not a mandatory run of every tier. For document-only changes, record consistency/link review instead of claiming product execution.

- Changed behaviors and their test scopes, with actual collaborators, real/replaced dependencies and entry/result boundaries:
- Execution mechanisms (direct calls, UI Automation, runtime/host) and environments, separate from scope classification:
- Checks performed: source, commands, executed/passed/failed/skipped counts and artifacts:
- E2E/physical IME/live checks selected and the specific risk or acceptance need they establish beyond lower layers (or why none were relevant):
- Relevant coverage outside the selected scope, unavailable coverage, retained failures and blockers:

Classify by the exercised collaboration and system boundary, not FlaUI/UI Automation, process separation, project names, a dedicated host, or fake/live service use. Reclassify existing cases only with case-level scope evidence. When moving coverage down, link the behavior mapping and observed replacement results before retiring redundant checks. Do not present a scoped pass as full regression.

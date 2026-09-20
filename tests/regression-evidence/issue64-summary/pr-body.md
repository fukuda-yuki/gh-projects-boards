Unit D needs an app-connected Summary with per-person raw labor, independent
remaining, scoped allowance and a protected baseline. This checkpoint preserves
that partial implementation and its tests while the revised #61/#1 input
prerequisite is repaired. **Draft only; do not merge or treat this as completed
Unit D.**

The native Summary consumes the existing plan/session/store. It includes the
four-value table, Project totals, contribution detail, allowance editing,
explicit baseline establishment/replacement and Undo. Current task editing and
assignment still depend on the rejected old planning interaction; integration
with the corrected #61 contract is outstanding. No new scheduler, workspace,
reporting database or remote publication path is introduced.

The agent received the new hold in
[#64 comment5747796216](https://github.com/fukuda-yuki/gh-projects-boards/issues/64#issuecomment-5747796216)
during implementation. Work and failure evidence are retained at
`tests/regression-evidence/issue64-summary/README.md`, with concrete continuation
and open review findings. This is a safe checkpoint, not a request to approve
the old global task modal or close #65/#51.

## Testing

- At implementation checkpoint `7f9818d05118dbb1c6aed0eb37b9836cc2ed6937`:
  Release solution build passed, two inherited UI-host CS0436 warnings;
  deterministic Core/adapter/storage **590 passed / 0 failed / 0 skipped**.
  The focused Summary/planning/Gantt projection selection was **58/0/0**.
- Earlier actual-control WinUI Summary selection: **5/0/0**, covering the narrow
  pending-text roundtrip, allowance failure/retry, baseline edit/replacement/Undo
  and Light/Dark normal/narrow captures. App/test source matches the checkpoint;
  the subsequently corrected Core metadata-validation and partial-actual logic
  was not rerun through the hosted UI after the hold. Exact binary identities
  and case-level receipts are in validation.json.
- Independent Oracle static review completed with unresolved findings. Two
  reported defects had already been corrected and covered by the final Core
  run; remaining selection/display/identity/storage boundaries and the revised
  assignment contract are listed in review.md. **No all-clear or human approval.**
- Isolated 1,000-task/20-person Summary fixture preparation/readback and launcher
  resume preparation/no-overwrite checks passed. Ordinary Summary launch,
  whole-app restart/P1-P2 journey, physical Summary IME, Summary performance and
  independent visible-result timing are **not run**. Those authored tests are
  compiled only. High Contrast, other DPI, assistive technology and human Summary
  acceptance are not run. No live GitHub rerun or machine setup change.
- Earlier Red, build failures, Core **585/586** and **57/58**, and hosted failures
  are retained separately; there is no combined all-pass total.

Refs #64

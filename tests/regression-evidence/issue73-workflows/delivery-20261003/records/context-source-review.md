> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/checks/context-focus/independent-source-review.md`
> Original SHA256: `25E98E11A16D3956AF462C74BCE7DB829BB3FE0B4E71D8E7AB956AF3C0DF0CB3`

# Independent review of pointer selection after pending-input recovery

Reviewer: `/root/v_creation_prepare`. This is retained-source and retained-evidence inspection, not a build, test execution, App launch, or live UI operation by this reviewer. The original bounded reveal review remains unchanged in `../u-context-reveal-repair/peer-review.md`; this addendum records the subsequently discovered failure.

## Reviewed identity and observation

The baseline is the frozen `round12-candidate-u-reveal/diagnostics/source-snapshot` associated with App SHA256 `ED4271BDCAA569CD906404E54FC1971C077B6C5227B75FB1FA60F0864402B20B`. The three inspected files matched that snapshot's manifest:

| File | SHA256 |
| --- | --- |
| `src/GhProjectsBoards.App/EditingGrid.cs` | `BFB783088A8A28C835F2066A98B15BA798C8BA1EC5C7536CF4D97766054B9BF7` |
| `src/GhProjectsBoards.App/EditingGrid.Recycling.cs` | `EB6A8C93E3C5ECDD0326E77A6CCD82E0C3B42995D6DD25DE39098009F28CFDD4` |
| `src/GhProjectsBoards.App/SheetInteraction.cs` | `595CABF6CD72D761EFDA33EF7950DAE8C84AF4E933ACDA3028CC70AA225E653E` |

Root's ordinary informed run observed that, after recovering pending I245 Title and I1000 Estimate, clicking visible I1000 Title and pressing Tab committed the older pending I245 title. The retained checkpoint reader reports 45 pass / 6 fail, with original U unchanged. This reviewer inspected the retained 018 and 019 PNGs: 019 shows I1000 Title selected and the Title footer. Its same-capture accessibility tree still describes Estimate, so those accessibility snapshots must not be treated as synchronized proof of native focus. The checkpoint establishes the unintended commit independently.

The other agent's retained `red-first-build/results.xml` contains 3 executed / 2 passed / 1 failed / 0 skipped: the pending-only and direct UIA SetFocus routes passed; native pointer selection followed by Tab failed. These are another agent's executions. The subsequent `red-frozen-sheet.jsonl` diagnostic trace was inspected directly; this review does not independently certify that diagnostic run's complete binary provenance.

## Mechanism supported by the diagnostic trace

This is a concrete High-severity selection defect because a click on one visible cell can cause the next key to confirm another cell's unfinished input.

`SheetInteraction.cs:73-88` selects the pressed identity first, then starts a captured range gesture. `DragReleased` unconditionally calls `UpdateDragTarget` at line 125. That method interprets the pointer against the current viewport and scroll offset, rather than preserving the pressed identity for an unmoved click. A context-panel height change during the initial selection can therefore change the cell under the same pointer coordinate.

Grid 2, sequences 477-507, makes that sequence concrete:

1. 477-488: the pointer presses row 12 Title, selects I40 Title, and successfully focuses `GridCell12_0`.
2. 482/488: removing the pending context expands the viewport to about 164.8 DIP and clamps the vertical offset to about 230.4 DIP.
3. 490-495: release-time hit testing at `(139.2, 246.4)` maps to row 11 I30 Title and extends the selection there without focus.
4. 497-507: the release handler selects row 11 again with focus, restores its pending context, and focuses `GridCell11_0`.

The subsequent native Tab is then consistent with `TitleCell.OnPreviewKeyDown` (`EditingGrid.cs:1338-1343`): it commits that editor's own unfinished text and navigates from its own row. This is not evidence that the commit implementation should be redirected to a different selected row.

The initial source hypotheses of a failed first Focus call, a recycled-cell GettingFocus cancellation, or a missing release handler are not supported by this reproduction: the trace shows the first focus succeeding and both release-time selections executing.

## Smallest repair boundary and regression requirement

Keep the change in the selection gesture. Record the press origin and whether a real drag has begun; before that point, neither release hit testing nor edge autoscroll should replace the identity selected by `BeginRange`. Movement detection should consider release coordinates as well as move events, and remain true after a drag starts even if the pointer returns to its origin. A small movement tolerance distinguishes pointer jitter from drag; document that local reason. Preserve existing fill behavior and Shift-click anchoring. No change is needed to composition guards, owned-editor retention, native focus ownership, or commit semantics for this demonstrated mechanism.

The necessary lower boundary is a real mounted EditingGrid with its pending-context layout, native pointer press/release, and a following native key. Direct UIA SetFocus bypasses `BeginRange`/`StartDrag`/`DragReleased` and cannot replace this case. Assert the selected and actually focused target, then verify that the old pending buffer and confirmed value remain unchanged after Tab, the target advances normally, and no Apply work is dispatched. Preserve relevant ordinary range-drag, Shift-click, and edge-autoscroll checks; the guard must not disable real range gestures. These are UI collaboration checks, not an assertion of whole-application or human acceptance.

The repair implementation and its Green runs were not reviewed or executed in this initial addendum. A fresh informed ordinary journey on a preserved copy is still needed after a corrected candidate is selected. The previous ordinary failure and its original checkpoint must remain retained.

## Final bounded source review

After the author froze the final source, all six working files and retained source copies matched `source-ready.json`, SHA256 `D22EF78C9F057DB65262EE7715D877C0344A95500E87509E9DEA4283BDBAFA92`. The production repair is confined to `SheetInteraction.cs`, SHA256 `7409D37C908B2B7E433D1BE52BF70A85041544C87C8491546282FCBBC3D4EEC2`. No concrete High or Medium issue was found in that bounded change. The common hit-test guard covers move and release events, keeps movement sticky, suppresses selection autoscroll before a drag starts, bypasses the guard for fill, and preserves the anchor established by the initial selection. Composition, editor retention, focus and commit code remain unchanged.

This reviewer read the other agent's retained `green-selected/results.xml`: 13 executed / 13 passed / 0 failed / 0 skipped. The final test source uses real pointer/key input and checks the complete saved workspace snapshot in addition to focus, selection, pending values and confirmed values; the added body-drag case checks returning to the pressed cell without changing work. These are inspected execution records, not independently executed tests. `regression-first/results.xml` separately retains 16 executed / 12 passed / 4 failed / 0 skipped, including four fill-related timeouts. The representative frozen-App fill control also timed out (1 executed / 0 passed / 1 failed / 0 skipped). Those unresolved product/fixture outcomes are not erased by the selected Green run, and the one control does not prove that all four failures predate the repair. Ordinary application behavior, unfamiliar evaluation and human acceptance remain outside this source review.

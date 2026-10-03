> Historical report, retained with its original candidate, failures and preparation-time status. This derivative changes relative links only; selected small attachments are copied, while other targets are explicitly local archive paths. It is not a new execution or the current task-status authority.
> Original: `tests/regression-evidence/issue73-workflows/blind-review/checks/range-input-repair/independent-source-review.md`
> Original SHA256: `8DBEE2AE608F05F5837786BBD6F73738B6554D77D28D9EF177098DA1E5642892`

# Independent range-input repair review

Reviewer: `v_creation_prepare`, 2026-10-03 JST. Boundary: source inspection and read-only verification of retained artifacts. This reviewer did not build, launch the application, operate UI, rerun tests or execute either readback helper.

## Result and identity

No concrete high- or medium-severity defect was found in the bounded production repair. This does not establish ordinary application usability, physical-key IME behavior, unfamiliar evaluation or human acceptance.

`source-ready.json` SHA256 `5599255DF745767F0974E5DB9C6732E1E81518B9A3B374DBEBD61C26572E2986` matched all nine listed live files and retained copies. It is a selected-source manifest, not a complete build-input manifest. Production changes were compared with `before/source`; existing unrelated changes were preserved.

- EditingGrid.RangeCommands.cs: `AAD7AB0E63D8CD0338C5C365B42ADA153D3AB752E1C5BC50E3B55678D9CB22E1`
- EditingGrid.Recycling.cs: `1F05DE6B15A9121B8246EA6B03BC93E233458B786734F18FC551967614FB17B2`
- EditingGrid.cs remains `0D818889AEF45DE877B932BEA3EE9480333C22226D0480A70A623C5EE348B6F1`.
- SheetInteraction.cs remains `7409D37C908B2B7E433D1BE52BF70A85041544C87C8491546282FCBBC3D4EEC2`.

The review applies the behavior map in `requirements-review.md`, the existing specification's range/Undo contract, DESIGN.md's task and native-input principles, and the repository test policy.

## Production behavior

The range button's own KeyDown handler processes Ctrl+Z only while that exact button has actual native focus and CanUseRangeCommands remains true. It reuses Run(Undo), without Select, forced cell focus, buffer commit or a view-wide accelerator. Existing selected-cell navigation and native TextBox Undo paths remain unchanged. The availability guard still excludes active-cell editing/buffers and blocked refresh/composition. Core target-buffer and Undo validation remain authoritative.

InstallOwnedSlots removes the old selection frame and fill handle from their existing Panel parents, then clears both cached slots only when the presentation host changes. The existing UpdateCell/PaintCellState path creates adornments in the new owned host. Native editors, their parents, identity, caret and input are retained. Reinstalling the same host preserves the live handle and its pointer capture.

The neighboring lifecycle was inspected: presenter unbind removes only recycled-host adornments; clean-editor release removes the owned host and clears its slots; RebuildRows cancels drag, calls ResetRecycling to remove owned adornments, clears arrays, then reindexes surviving editors. These paths do not leave the old recycled adornment as the active cached handle. The context-reveal ordering and sticky-pointer repair were not changed.

## Retained execution evidence

Read-only inspection of NUnit XML confirmed the following author-executed counts. None are new executions by this reviewer.

| Retained folder | Executed | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| fixture-first | 4 | 0 | 4 | 0 |
| red-built | 4 | 1 | 3 | 0 |
| red-frozen | 4 | 1 | 3 | 0 |
| green-first | 4 | 4 | 0 | 0 |
| regression-first | 28 | 25 | 3 | 0 |
| green-affected | 7 | 7 | 0 | 0 |
| green-selected | 28 | 28 | 0 | 0 |

Red interpretation is case-specific: red-built reached both menu operations before timing out at keyboard Undo. In red-frozen, Paste reached the Undo check and native fill acquisition failed, but FillDown timed out before the operation completed (line 69). Therefore red-frozen is not evidence that both menu Undo routes reproduced the defect. Fixture-first failures remain retained and are not product acceptance evidence.

The final menu tests use actual native menu keyboard selection, allow natural closure, and send Ctrl+Z without refocusing. Both final logs report `Button#GridRangeCommands` after closure. Assertions preserve range identities and unrelated pending text, restore the complete prior snapshot except its revision, then undo the independent source operation separately. Quick-filter native Undo leaves sheet work and buffers unchanged; the existing confirmed-estimate native text Undo case also passed in the selected run.

The final fill log identifies the pressed handle under `Canvas#SheetNativeEditors`, direct native press and capture, and a ten-row preview while held. Snapshot assertions prohibit changes before release; release checks exact item IDs/values and atomic Undo. The selected run also retains cancellation, upward fill, middle-target rejection, viewport-edge scrolling to row 100, body drag and context-reveal coverage.

Test-only corrections preserve the behavioral oracle: menu navigation waits for actual MenuFlyoutItem focus before sending the next key; the edge test reads the rendered target cell subtree rather than an implementation-specific ChoiceCell child ID, while retaining exact 100-item workspace identities and Undo assertions. The earlier 28/25/3 record remains a failed attempt.

These are scoped UI integration checks with real EditingGrid/DraftSession collaboration, native input and a substituted clipboard boundary. Ordinary window readability, physical-key IME, real clipboard contents and human acceptance remain separate.

## New ordinary-readback helper source check

No concrete high- or medium-severity defect was found in the bounded delta of `../u-range-input-ordinary/Read-ClosedUiCopy.ps1`, SHA256 `AC351FB39F9657C6354672A73A9326BF76DB2FE5FB70F1FD688B764C8F80BC14`. PowerShell parsing returned zero errors. The earlier executed `../u-input-ordinary/Read-ClosedCopy.ps1` remains SHA256 `4AE7657BB38A9A047AF908EA8E1587411CC81481D84E388367ACA0370F1AF1B9`.

The new helper records hashes for Read-UProduction.ps1 and Compare-U.py, copies both into the new after-close directory, executes those copies, and rejects changed source/copy hashes before writing its summary. Neither dependency relies on its original PSScriptRoot. Full Drafts inventory preservation, source rehashes, original U checks, and the existing result-comparison logic remain intact. Current dependency hashes are `3E95F04777D5775F9B40DD989880DC3F7BC4537FAC91DC59006D57C37EFAC119` and `6D530B7B8534E365D5A32A98FC6207AB67DC1277D987A69BD3B11AF6AAF124AE` respectively.

This is prepared-source review only. It does not retroactively establish dependency identity for earlier readbacks, bind an arbitrary supplied PID to a launch, or prove visible interaction. Root's new pre-action process/window observation and retained native UI evidence remain necessary alongside any subsequent helper result.

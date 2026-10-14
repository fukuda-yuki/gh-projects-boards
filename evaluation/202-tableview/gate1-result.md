# Gate 1 result — Issue #202 (2026-10-14)

**Gate 1 fails. Gates 2–4 were not run** (owner's fail-fast rule: stop when input or row structure does not hold).

Environment: Windows 11 Pro 10.0.26300, .NET SDK 10.0.401, Release, Windows App SDK `2.5.4-experimental` (Microsoft.UI.Xaml.Controls.Tabular 3.2.3.2609), physical Microsoft Japanese IME on the PMO desktop, ordinary executable, real Core and local storage, isolated fake gh (three rows).

## Runs

| Run | Command | Result |
| --- | --- | --- |
| Control arm: current renderer on the same Experimental SDK | `Test-E2E.ps1 -NoBuild -Filter TestCategory=GridIme` | **8/8 passed** (6 sheet + 2 settings cases) |
| TableView, original fixture (UIA cell lookup) | `-Filter TestCategory=GridImeTableView` | 0/6: `PlanTableDisplay1_Title` never exists in UIA |
| TableView, pointer-driven fixture, adapter as authored | same | 0/6 effective: F2 realizes the editor but keyboard focus stays on the row; typed text never reaches the editor |
| TableView, adapter focuses its editor on `Loaded` (public `Focus`) | same | F2 path: IME boundary holds, commit once; then fails "second Enter must move to the next row". Direct paths fail "typing must reach an editor". 1/6 passed (cancel-f2) |

Artifacts: `TestResults/e2e/20261014-233008-…` (control), `…-233104-…`, `…-233711-…`, `…-234049-…`, `…-234339-…` (TableView; each case has `steps.txt` and per-step screenshots). No case sent a GitHub mutation (`calls.jsonl`: 0 mutations in every case).

## Observed behavior (TableView 2.5.4-experimental)

Held, after the one-line focus fix:

- F2 opens the owned editor with the text selected; physical IME composition, conversion and IME confirmation stay inside the editor; IME confirmation does not commit, save or move.
- Esc during conversion cancels only the conversion; the next Esc cancels the edit with no operation and no save.
- The second Enter commits exactly once through the existing PlanSession path (`1 未発行のタスク`, saved `日本語`).

Failed:

1. **Direct entry is not available.** Typing on a selected cell never starts editing — Japanese IME or ASCII, owned template column or built-in `TableViewTextColumn` (temporary probe column, removed). TableView has no public `BeginEdit` or current-cell API, so the product cannot start an edit from typing without key replay or internal parts. Only F2 and double-click start editing.
2. **Enter does not continue to the next row.** After the commit the editor stays open on the same row and focus does not advance; Enter on a non-editing row does not move either (Down does). There is no public API to move the current cell, so the existing two-Enter contract cannot be restored by the adapter. Root cause inside TableView not isolated.
3. **No cell-level UI Automation.** Rows are exposed (`DataItem`, SelectionItem), but cell peers expose no control type, name or value, `Grid.GetItem` returns an empty element, and the owned editor is absent from the UIA tree. Screen readers cannot read cell values, and tests must drive cells by screen coordinates. The current renderer exposes named cell peers. This matches the known accessibility limitations in WinUI Gallery PR #2250.

The control arm passing on the same SDK attributes these failures to TableView, not to the Experimental SDK update.

## Code mapping at the stopping point

- (a) Current code deleted by relying on TableView: **0 lines**. Nothing becomes removable while direct entry, next-row Enter, range selection, paste and the Gantt still need the current renderer.
- (b) Product logic that stays: all Core, PlanSession, pending/problem handling, parsing, history, range/clipboard commands, hierarchy, Gantt geometry and wheel/divider behavior (≈3,086 inventoried view lines plus Core).
- (c) New adapter code for a three-column, F2-only probe: **252 net production lines** (`PlanSheetTableView.cs` 222, `App.xaml` +21, `App.xaml.cs` +1, `PlanSheetView.cs` +9/−1), plus a 177-line E2E fixture. The missing behaviors above have no public-API route, so the adapter cannot be completed at any size.

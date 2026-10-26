# Owner review 2026-10-26 — ordinary-app evidence

PR #299 head `40471c0d` (the later CI-timing test fix does not touch UI code).
Ordinary app, Release, offline evaluation data: 第2027.04版, 40 requirement Issues + 1,000 tasks + 20 people, 基準日 2027-01-13, `GHPB_TODAY=2027-01-13`. Window maximized on the owner's desktop (effective 1920×1032, 2400×1290 at 125% scale).

Host caveat: this session's file sandbox denies `File.Replace`, so the app binary used for this visual run was built with a local-only, uncommitted copy-and-move fallback for the plan save. The committed product code is unchanged, and the save mechanism is not visible in these screens; everything shown is the committed UI.

## 03-gantt — item 3 (Gantt show/hide)

- `01-gantt-visible.png`: default plan with the Gantt, its milestone ◆ strip and the status-bar legend.
- `02-gantt-hidden.png`: ガント unchecked in the sheet control row. The chart column, header, background, scrollbar, divider, scale controls and legend are gone; the sheet takes the width; counts and row content are unchanged.
- `03-settings-gantt-switch.png`: この PC（すべてのプロジェクト）› 表示 with ガントを表示 checked. The sheet toggle and this switch read the same stored preference (state shown at the same time).

## 04-dates — item 4 (calendar date cells)

- `01-date-calendar-open.png`: row #2 selected, its 開始日 cell shows the calendar affordance; the calendar button opened October 2026 with the cell's 13 selected. Typed ISO input, Alt+↓ from a selected date cell and the same calendar flyout are covered by the hosted cases.

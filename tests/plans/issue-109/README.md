# #109 main screen: conformance test plan

This plan checks the ordinary app against the agreed main-screen design after the #109 re-fix. The executor (Codex) runs it, records evidence and proposes bug candidates. **It does not fix product code.** The PMO triages the candidates and decides what becomes work.

Run it only on a build that contains the completed re-fix. Running it on `526601a` or earlier only reproduces the known gaps.

## Sources of truth

Judge each item by these sources, in this order:

1. [docs/spec.md](../../../docs/spec.md): *Current workspace and Project settings (#81 / #109)* and *Current plan sheet and row-aligned Gantt (#78 / #109)*.
2. [DESIGN.md › Visual language](../../../DESIGN.md#visual-language).
3. The reference images in [reference/](reference/). They are renderings of the [design canvas](https://claude.ai/artifact/HnHTkkX8RPA4qtRaV347Ca):

| File | Artboard | Effective size |
| --- | --- | --- |
| `final-1440x900.png` | 最終案 | 1440 × 900 |
| `addendum1-1920x1080-lateness.png` | 追補1: the PMO's display, one overdue task and its successors | 1920 × 1080 |
| `addendum2-1440x900-full-period-folded.png` | 追補2: 全期間, all folded | 1440 × 900 |
| `addendum3-1280x800-no-selection.png` | 追補3: no selection, narrower window | 1280 × 800 |
| `addendum4-parts-and-states.png` | 追補4: pickers, menus, tooltips, unpublished and lateness markers, progress, failure, conflict, names | 1440 × 900 |

The mockups use invented data (佐藤, 11/11, IDs 106–130). The app uses the offline evaluation version (person-U1…U20, status date 2026-10-05, different IDs). Compare structure, styling and rules, not the data. Where the 最終案 and an addendum or the specification differ, the addendum and the specification win. For example, a later-than-published task's end date is no longer red, only an overdue task's.

## Rules for the executor

- Use the ordinary Release executable with the offline evaluation data. Do not use live GitHub or the network.
- Produce every state through the app's own UI: clicks, typing, paste and the status-date picker. Do not edit the data root, settings files or fake-gh state by hand.
- Do not change product code, tests, docs or machine settings. Display scaling, theme and resolution are machine settings. Helper scripts may go under the run's `TestResults` folder only.
- An item that you could not observe is **unverified**, with the reason. Never report it as a pass. Report a blocked desktop session or tool as blocked.
- Look at every screenshot you cite. A capture that exists but was not inspected is not evidence.

## Environment and viewports

Build and launch as in [README › Offline evaluation](../../../README.md#offline-evaluation):

```powershell
dotnet build .\GhProjectsBoards.sln -c Release --no-restore
.\scripts\Start-Evaluation.ps1 -NoBuild
```

Select **接続**, then **第2027.04版**. Use a fresh data root for each run, and `-Resume` with the printed root to continue it. `scripts/Invoke-EvaluationUi.ps1` drives and captures the window (`-Action tree|shot|click|type|key|…`). Size the window with `SetWindowPos` from a helper script, not through system settings.

Viewport sizes below are **effective pixels**. Record the display scale. On the PMO desktop (125%), a window's physical size is the effective size × 1.25.

| ID | Effective window | Why | Physical at 125% |
| --- | --- | --- | --- |
| V1 | 1920 × 1032 | **Primary.** A maximized window on the PMO's 1920 × 1080 display at 100%, with a 48 px taskbar | 2400 × 1290 |
| V2 | 1440 × 900 | Same size as the 最終案, for side-by-side comparison | 1800 × 1125 |
| V3 | 1280 × 800 | The narrowest width that must show all ten default columns | 1600 × 1000 |
| V4 | 1000 × 720 | Narrow: the Gantt keeps at least 320 px and the sheet scrolls | 1250 × 900 |

V1 at an equivalent effective size verifies layout but not 100% pixel rendering (text sharpness, 1 px strokes). Mark 100% rendering **unverified** unless the run happens on a display already set to 100% by its owner. The owner excluded Windows dark mode and physical IME from this plan.

## States

Build these states in order in one data root. Note the plan IDs you used, because the fixture's IDs differ from the mockups.

| ID | State | How to produce it |
| --- | --- | --- |
| S1 | Fresh open, no selection | Open the version. 状況日 2026-10-05, 週. |
| S2 | One overdue finish and one overdue start, with successors later than published | Set 状況日 to a working day after several wave-1 tasks were due, so their published end or start is before it. Mark every due task complete except two. Use the title filter for the flat list of matching tasks, then paste 実績 h and 残 h (Actual = Estimate, Remaining = 0). Leave one in-progress task with Remaining > 0 and a published end before the 状況日: the overdue finish. Leave one task with Actual 0 and a published start before the 状況日: the overdue start. Clear the filter. |
| S3 | Entered value next to recalculated dates | In S2, type a new 見積 h or 担当者 on one task. |
| S4 | Folded overview | In S3, **すべて折りたたむ**, then view at 週 and 全期間. |
| S5 | Scale change with a selection | Select a task whose start is far from the 状況日. Switch 週 → 日 → 月 → 全期間 → 週. |
| S6 | 状況日 before every task | Set 状況日 2026-10-05 (before the first task) and view 全期間. |
| S7 | Parts | Open the Project picker and the「…」menu. Hover an indicator icon and a header. Tab to a command to show keyboard focus. Type a title longer than the column. Give one task two assignees if the sheet's assignee input allows it, otherwise record it as unverified. |
| S8 | Progress, failure, conflict | Capture 最新の情報に更新 while it runs. Induce a refresh failure and a conflict only through mechanisms the offline fixture already offers; check the fake gh and fixture options first. If you find none, mark the item unverified and cite the hosted UI tests that cover it. |
| S9 | Other views | Open 担当者, 設定 and the publication review (**発行…**, then cancel without publishing). |

## Checklist

Give each item one verdict: **pass**, **partial**, **fail** or **unverified**. Attach the screenshot path and the state/viewport. Where an item names a viewport, check it there; otherwise check V1, then V2 against the 最終案.

### A–K: the 最終案 (re-checked from the 2026-10-09 conformance report)

| ID | Expected | State |
| --- | --- | --- |
| A1 | Gray window ground; white card, 8 px radius, light border, 12 px side margins | S1 |
| A2 | Light theme (dark mode excluded from this run) | S1 |
| A3 | Segoe UI Variable / Yu Gothic UI; body 13 px; hierarchy by weight | S1 |
| B1 | App icon is the three-bar Gantt glyph, not a calendar | S1 |
| B2 | 「計画エディタ」 small and secondary | S1 |
| B3 | Borderless Project picker: version name bold, then `owner · Project N` and ▾ | S1 |
| B4 | Settings gear immediately left of the minimize button | S1 V1–V4 |
| B5 | Caption buttons are legible in the active window, with the system caption colors | S1 |
| C1 | 計画 / 担当者 tabs; the current tab is bold with a short accent underline | S1 |
| C2 | Right side order: 状況日, Undo/Redo, unpublished count, 最新の情報に更新, 発行… | S1 |
| C3 | 「状況日」 label and a date picker showing the weekday | S1 |
| C4 | 元に戻す / やり直し are borderless, with hover fill | S1 |
| C5 | Thin vertical separators: 状況日 \| Undo/Redo \| unpublished + GitHub commands | S1 |
| C6 | Amber count badge + 未発行のタスク; 未発行 0 タスク at zero | S1, S3 |
| C7 | 発行… is the only accent-filled button | S1 |
| D1 | Left-aligned labelled commands in the agreed groups and order | S1 |
| D2 | Separator between the task and overview groups | S1 |
| D3 | 「…」 holds コピー Ctrl+C, 貼り付け Ctrl+V, 下へコピー Ctrl+D, クリア Delete, CSV から追加…, 表示列 | S7 |
| D4 | 「尺度」 and the scale combo at the right end; 全期間 fits without wrapping | S1, S4 |
| D5 | Search icon inside the title filter | S1 |
| D6 | Stroke below the command bar | S1 |
| E1 | Selection line: very light ground, strokes | S2 |
| E2 | Plan ID (secondary) and title (bold) | S2 |
| E3 | Start reason 「開始: N の終了後」 | S2 |
| E4 | End reason: 「終了: 状況日 M/d から残り Nh」, 「終了: 開始から Nh」 or 「終了: 指定」; omitted for complete tasks and summaries | S2 |
| E5 | Lateness pill per N3 | S2 |
| E6 | Right-aligned Issue reference link. If the fixture lacks references, say so. | S2 |
| F1 | Headers ID, indicator icon, タスク名, 担当者, 見積 h, 残 h, 実績 h, 開始日, 終了日, 先行 | S1 |
| F2 | Bottom-aligned 12 px semibold secondary headers, 1 px stroke, no header boxes | S1 |
| F3 | Column widths close to ID 52, indicator 28, name 272, assignee 80, numbers 56, dates 92, 先行 64 | S1 |
| F4 | Header tooltip names the GitHub field | S7 |
| G1 | 28 px rows, light horizontal separators, no vertical grid | S1 |
| G2 | Requirement rows semibold with a fold chevron | S1 |
| G3 | A child's name starts to the right of its parent's name text, never left of it | S1 |
| G4 | Numbers right-aligned with tabular figures | S1 |
| G5 | Dates shown as `10/14 (水)` | S1 |
| G6 | Calculated dates secondary; typed and complete dates primary | S2 |
| G7 | Unpublished cell markers per N4 | S3 |
| G8 | Selected row: light accent ground and a short accent bar left of the ID | S2 |
| G9 | Current cell: 2 px accent outline | S2 |
| G10 | Red end date only on overdue rows whose end moved later (N2); not on rows that are only later than published | S2 |
| H1 | One indicator icon per row by priority; the tooltip lists all applicable states | S2, S7 |
| I1 | Upper tier months; year on the first label and in January | S1 |
| I2 | Lower tier Monday dates `10/19` at 週 | S1 |
| I3 | Accent pill 「状況日 M/d」 | S1 |
| I4 | Thin month separators | S1 |
| J1 | Non-working days shaded at 日 and 週 | S1 |
| J2 | Row separators in the Gantt at the sheet's row positions | S1, S2 |
| J3 | 2 px accent 状況日 line | S1 |
| J4 | Task bars 14 px, radius 3, tinted with outline, completed share solid | S2 |
| J5 | Summary bracket bars with end caps | S1 |
| J6 | Lateness segment per N2 | S2 |
| J7 | Arrow leaves the predecessor end horizontally, turns down and enters the successor bar from above with a filled triangle | S2 |
| J8 | Selected-row band continues across the Gantt | S2 |
| J9 | Opens at 週 with the 状況日 about a quarter from the left | S1 |
| K1 | Status bar 「要求事項 40 · タスク 1,000」, then nonzero overdue / later counts | S1, S2 |
| K2 | Legend: 実績, 残り, 発行済みからの遅れ, 非稼働日 | S1 |
| K3 | No Gantt legend on 担当者, 設定 or the publication review | S9 |

### N: the addendum (追補1–4)

| ID | Expected | State |
| --- | --- | --- |
| N1 | **Overdue rule.** Filled critical warning only for: an incomplete task with a published end before the 状況日, or a task with Actual 0 and a published start before the 状況日. A task due on the 状況日 itself is not overdue. Check both S2 tasks, a task due exactly on the 状況日 and a completed task. | S2 |
| N2 | Overdue row with a later end: red end date; Gantt segment tinted, dashed, labelled `+N日` at 日/週 | S2 |
| N3 | Later-than-published only: outlined warning, ordinary end-date color, dashed outline without tint or label. Selection line: outlined pill 「発行済み M/d から +N 日」, then 「先行の遅れによる」 when its start also moved later after a predecessor. Overdue row: tinted pill 「完了予定 M/d を過ぎて未完了」 (· +N 日) or 「開始予定 M/d を過ぎて未着手」 | S2 |
| N4 | Unpublished: the entered value has tint + corner; dates changed only by recalculation have the corner without tint | S3 |
| N5 | Folded requirement: filled warning when it contains an overdue task, including when its own end did not move; outlined when it contains only later-than-published work. The tooltip and selection line read 「配下: 期限超過 N · 予定より遅れ M」 | S4 |
| N6 | Scale change keeps the selected task's start about a quarter from the left; with no selection, the 状況日 | S5 |
| N7 | No `+N日` text at 月 or 全期間; the number stays in the tooltip, accessible name and selection line | S4, S5 |
| N8 | Empty selection line: 「タスクを選ぶと、開始と終了の理由がここに出ます」, and nonzero counts at the right | S1, S2 |
| N9 | V1: all ten default columns without horizontal scrolling; Gantt about 1,040 px (about 18 weeks at 週); about 29 rows visible. Measure and report the actual numbers. | S2 V1 |
| N10 | V3: all ten columns visible. V4: Gantt at least 320 px, the sheet scrolls horizontally, commands reachable through overflow | S1 V3, V4 |
| N11 | 全期間 includes the 状況日 when it is before every task: line and pill visible | S6 |
| N12 | Project picker: two-line items, current checked, separator, Project を開く…, 接続… | S7 |
| N13 | Standard tooltips and a visible keyboard focus ring | S7 |
| N14 | Long title ends with an ellipsis, with the full title in the tooltip. Several assignees show `first +N`, none shows blank, and a missing display name falls back to the login. | S7 |
| N15 | During refresh: thin progress bar under the command row; the command reads 更新中…; the plan is read-only | S8 |
| N16 | Failure: InfoBar at the top of the card with the affected work and 再試行 (保存を再試行 for saves) | S8 |
| N17 | Conflict cell: critical outline and corner, no tint; the selection line names the GitHub value | S8 |
| N18 | Overall: does the V1 screen read as the 追補1 design, and would the PMO choose it for the weekly update? Give a short judgement with reasons. | S2 V1 |

## Automated regression

After the visual run, run the routine suites in [tests/README › Routine verification](../../README.md#routine-verification): Core, the default hosted UI integration suite and the three E2E journeys. Report the exact commands and executed/passed/failed/skipped counts. A failure is a bug candidate unless it is a known environment issue; give the evidence either way.

## Output

Write everything under `TestResults/issue109-conformance/<yyyyMMdd-HHmm>/`:

- `screens/<state>-<viewport>[-detail].png` for each capture you cite.
- `results.md`, with these sections:
  - Environment: commit, build, data root, display scale, viewport method.
  - The checklist with verdicts and evidence paths.
  - Automated regression results.
  - Unverified items, with reasons.
- `bug-candidates.md`: one entry per candidate:
  - ID (BC-n) and a one-line title.
  - Checklist item.
  - Observed vs expected, with the screenshot.
  - Reproduction steps from a fresh data root.
  - Severity: **blocks weekly update**, **visible defect** or **polish**.
  - Suspected area, if known.
  - The lowest test boundary that could hold a regression test (logic, UI integration or E2E, per [tests/README](../../README.md)).

Do not open GitHub Issues. The PMO files the accepted candidates.

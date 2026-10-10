# Requirements

Source of truth: [Epic #88](https://github.com/fukuda-yuki/gh-projects-boards/issues/88) and its child Issues. [Epic #76](https://github.com/fukuda-yuki/gh-projects-boards/issues/76) defines the implemented planning-editor foundation. This is a navigation outline, not a replacement for Issue acceptance criteria.

## Product goal

A Windows planning editor for GitHub Projects. It replaces the planning work previously done with TFS 2017 + MS Project + the Excel add-in. The PMO takes in Issues already registered on GitHub and their weekly updates, then maintains effort in person-hours, assignments and predecessors while seeing the whole schedule. Start and end dates follow automatically, and adopted changes are published to GitHub in one step. Creating Issues in the app is occasional. GitHub's table and roadmap remain the place for general Issue work.

One PMO maintains one version, named 第YYYY.MM版 by its shipping date, in one Project. Normal scale is about 40 要求事項, each with 25 tasks across SA, UI, SS, PS, PG, PT, IT, ST and OT, numbered within each phase (for example SA-001), and about 20 people. The phase split is an evaluation placeholder until a real template is available. Versions are developed waterfall-style, not agile, between a project start and end date; some run six months to a year. The PMO reads their status by phase. Initial planning is followed by a weekly refresh, plan correction and publish cycle. Assignment and overload resolution remain human decisions; the app never levels resources automatically.

## Agreed boundaries

- Launch from a Windows executable; no browser extension or Excel dependency.
- Register Projects explicitly and switch between them. Each scoped Project is one workspace. Only Issues are planned; draft items and pull requests are not.
- Edit locally and publish explicitly, as with MS Project and the TFS Office integration. No request is sent per edit. The PMO can reset the plan to GitHub's current state, overwriting every local value with GitHub's data after a confirmation; afterwards the plan shows GitHub's values, dates included, and nothing is unpublished ([#123](https://github.com/fukuda-yuki/gh-projects-boards/issues/123)).
- The published plan is reproducible from GitHub plus the exported settings file: Issue title and assignees, blocked-by predecessors, sub-issue hierarchy, Estimate (見積), Actual (実績) and Remaining (残) number fields, a 工程 single-select field, Start/Target date fields, and two added Project fields, **開始日指定** (date) and **日程固定** (single select). Calendar, rates and allowances are local settings.
- Columns use standard Japanese headers and name the mapped GitHub field in their tooltip (#109 replaced showing the raw GitHub field name). In settings the PMO chooses the visible columns, including other Project fields and the Issue's labels, milestone and repository, and gives each a display name, as GitHub Projects allows; the chosen fields are edited and published like the planning fields, and #125 settles which field types are editable ([#125](https://github.com/fukuda-yuki/gh-projects-boards/issues/125)).
- Delegate authentication and API access to GitHub CLI (`gh api`); do not require manually issued PATs or a custom GitHub App.
- Start with local development and the designated sandbox. Company GHEC + EMU behavior remains unverified until tested there.

## Planning rules

- Effort is entered and shown in person-hours (人時). One working day is eight hours: weekdays 09:00–13:00 and 14:00–18:00, excluding Japanese public holidays and explicit company days off. Personal days off are not maintained: versions run for months and the PMO cannot keep them current ([#128](https://github.com/fukuda-yuki/gh-projects-boards/issues/128)).
- Effort follows the fields of a TFS task, which MS Project shares ([#131](https://github.com/fukuda-yuki/gh-projects-boards/issues/131)). Estimate (見積) is the original estimate (TFS Original Estimate, Project baseline work). Actual (実績) is the cumulative work done (Completed Work) and counts for the task's current assignee. Remaining (残) is the current estimate of the work left (Remaining Work) and drives scheduling, so an open task's forecast finish is worked back from it. The forecast (見込) is Actual + Remaining.
- A task is complete when its Issue is closed, and it ends on its close date.
- Scheduling is a bounded subset of MS Project auto-scheduling: finish-to-start predecessors, effort, the assignee's rate for the Project, the calendar, 開始日指定 (start no earlier than) and 日程固定 (keep typed dates). No lag, other link or constraint types, critical path, leveling or cost.
- Open tasks with work that are not 日程固定 are calculated; 日程固定 tasks and tasks without effort keep their GitHub dates. A calculated date that differs from GitHub is an unpublished change. The [plan sheet Issue](https://github.com/fukuda-yuki/gh-projects-boards/issues/78) lists the exact rules.
- The schedule is shown by day. Dates are recomputed from the inputs; minute endpoints are not stored.
- One status date (基準日) anchors actual and remaining entry. No remaining work is scheduled before it. It is the day of the latest GitHub read (the first take-in, 最新の情報に更新 or a reset to GitHub) unless the PMO picks another date, which holds until the next read; opening the app or a new day does not move it. After the first take-in and after a reset the plan shows GitHub's dates as they are and recalculates at the PMO's next planning action ([#130](https://github.com/fukuda-yuki/gh-projects-boards/issues/130)).
- A summary view shows the version against its start and end dates: elapsed and remaining working days, progress, forecast finish and remaining work against remaining capacity, by phase (a 工程 single-select Project field) and by person × phase, with each person's allowance, forecast (Actual + Remaining) and daily load ([#127](https://github.com/fukuda-yuki/gh-projects-boards/issues/127)). Overloads are shown, never levelled automatically.
- GitHub milestones appear on the Gantt and in the summary at their due dates, so the forecast finish can be checked against them. Their due dates are kept on GitHub ([#133](https://github.com/fukuda-yuki/gh-projects-boards/issues/133)).

## Requirement map

| Area | Owning Issue |
| --- | --- |
| Version-shaped evaluation data, reusing existing sandbox Issues | [#89](https://github.com/fukuda-yuki/gh-projects-boards/issues/89) |
| Initial planning and weekly maintenance in the task sheet at version scale | [#91](https://github.com/fukuda-yuki/gh-projects-boards/issues/91) |
| Internal distribution without a development environment | [#92](https://github.com/fukuda-yuki/gh-projects-boards/issues/92) |
| Backlog: mass Issue creation within GitHub limits | [#90](https://github.com/fukuda-yuki/gh-projects-boards/issues/90) |
| Owner review 2026-10-10: busy state, read-only summary rows, Japanese terms, discarding local changes, summary view, flicker, configurable columns, no personal days off | [#118](https://github.com/fukuda-yuki/gh-projects-boards/issues/118) |
| Backlog: optional comparison against an agreed plan | [#93](https://github.com/fukuda-yuki/gh-projects-boards/issues/93) |

## Explicit exclusions

Generic Issue administration, Kanban, Gantt drag editing, critical path, MS Project import, task creation by pasting from Excel, automatic resource leveling, personal days off, minute-precision schedules, realtime synchronization, cross-Project publish and organization-wide load. Mass creation and agreed-plan comparison are unscheduled conveniences. CSV import remains available but is not required for the ordinary planning job. Project-wide totals belong to the summary view; weekly reports, additional backup and logging are not planned unless version-scale use demonstrates a need. GHEC + EMU uses the appropriate gh authentication; it is not a separate product track.

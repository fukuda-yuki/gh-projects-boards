# Requirements

Source of truth: [Epic #76](https://github.com/fukuda-yuki/gh-projects-boards/issues/76) and its child Issues. This is a navigation outline, not a replacement for their acceptance criteria.

## Product goal

A Windows planning editor for GitHub Projects. It replaces the planning work previously done with TFS 2017 + MS Project + the Excel add-in. The PMO plans while seeing the whole schedule: creates tasks, enters effort in person-hours, assigns people and sets predecessors. Start and end dates follow automatically, and adopted changes are published to GitHub in one step. GitHub Projects cannot do this; its table and roadmap remain the place for general Issue work.

One PMO maintains the plan. Normal scale is about 1,000 tasks, fewer than 20 people and a weekly review.

## Agreed boundaries

- Launch from a Windows executable; no browser extension or Excel dependency.
- Register Projects explicitly and switch between them. Each scoped Project is one workspace. Only Issues are planned; draft items and pull requests are not.
- Edit locally and publish explicitly, as with MS Project and the TFS Office integration. No request is sent per edit.
- The published plan is reproducible from GitHub plus the exported settings file: Issue title and assignees, blocked-by predecessors, sub-issue hierarchy, Estimate/Remaining/Actual number fields, Start/Target date fields, and two added Project fields, **開始日指定** (date) and **日程固定** (single select). Calendar, rates and allowances are local settings.
- Columns backed by a GitHub field show the GitHub field name as the header.
- Delegate authentication and API access to GitHub CLI (`gh api`); do not require manually issued PATs or a custom GitHub App.
- Start with local development and the designated sandbox. Company GHEC + EMU behavior remains unverified until tested there.

## Planning rules

- Effort is entered and shown in person-hours (人時). One working day is eight hours: weekdays 09:00–13:00 and 14:00–18:00, excluding Japanese public holidays and explicit company or personal days off.
- Work fields follow TFS: Estimate is the original estimate, Remaining is what is left and drives scheduling, Actual is cumulative and counts for the task's current assignee.
- Scheduling is a bounded subset of MS Project auto-scheduling: finish-to-start predecessors, effort, the assignee's rate for the Project, the calendar, 開始日指定 (start no earlier than) and 日程固定 (keep typed dates). No lag, other link or constraint types, critical path, leveling or cost.
- Open tasks with work that are not 日程固定 are calculated; completed tasks, 日程固定 tasks and tasks without effort keep their GitHub dates. A calculated date that differs from GitHub is an unpublished change. The [plan sheet Issue](https://github.com/fukuda-yuki/gh-projects-boards/issues/78) lists the exact rules.
- The schedule is shown by day. Dates are recomputed from the inputs; minute endpoints are not stored.
- One status date (状況日), today by default, anchors actual and remaining entry. No remaining work is scheduled before it.
- For each person in the selected Project: allowance, Estimate total, Actual, Remaining, forecast (Actual + Remaining) and daily load. Overloads are shown, never levelled automatically.

## Requirement map

| Area | Owning Issue |
| --- | --- |
| Rendering choice for plan sheet + Gantt; refresh and batched publish measurement | [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77) |
| Workspace shell and settings: open and switch Projects, column mapping, calendar, people | [#81](https://github.com/fukuda-yuki/gh-projects-boards/issues/81) |
| Plan sheet with Gantt: tasks, effort, assignees, predecessors, automatic dates | [#78](https://github.com/fukuda-yuki/gh-projects-boards/issues/78) |
| Refresh and publish, including assignees and new Issues | [#79](https://github.com/fukuda-yuki/gh-projects-boards/issues/79) |
| People view: daily load, allowance, forecast | [#80](https://github.com/fukuda-yuki/gh-projects-boards/issues/80) |
| CSV import of new tasks with predecessors and parents (after #78 and #79) | [#82](https://github.com/fukuda-yuki/gh-projects-boards/issues/82) |

## Explicit exclusions

Generic Issue administration, Kanban, Gantt drag editing, critical path, MS Project import, task creation by pasting from Excel, automatic resource leveling, minute-precision schedules, realtime synchronization, cross-Project publish and organization-wide load. Baseline comparison, weekly report copy and distribution/company-environment validation are deferred until the child Issues above are accepted. CSV import of new tasks is required and follows #78 and #79.

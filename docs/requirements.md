# Requirements

Source of truth: [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and its linked Issues. This is a navigation outline, not a replacement for their acceptance criteria.

## Product goal

A native C#/.NET 10/WinUI 3 Windows workspace for PMO to plan and replan GitHub Project work. Boards is the editable table; Gantt and Summary consume the same plan as those surfaces are delivered. Normal weekly review is approximately 1,000 tasks and fewer than 20 people.

## Agreed boundaries

- Launch from a Windows executable; no browser extension or Excel dependency.
- Explicitly register Projects and switch between them through Repository-oriented navigation. The navigation hierarchy does not redefine Project ownership or filter out Issues from other repositories.
- GitHub-native data uses scoped observations and local drafts; exact intraday planning metadata has explicit checkpoint authority. Publish only through reviewed Apply. See the [planning contract](planning.md).
- Delegate authentication and initial API access to GitHub CLI (`gh api`); do not require manually issued PATs or a custom GitHub App.
- Measure the selected feature at 1,000 tasks/20 fixture people; this is not a retrieval cap. Keep inherited latency failures under #65 separate.
- Start with local development and designated GitHub test data. Company GHEC + EMU behavior remains unverified until tested there.

## Requirement map

| Area | Owning Issues |
| --- | --- |
| Data/ownership, fidelity, storage and compatibility | [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2) |
| Weighted Auto/Manual Boards, actual/remaining replan and complete data lifecycle | [#61](https://github.com/fukuda-yuki/gh-projects-boards/issues/61) |
| Same-plan Gantt inspection and manual editing | [#15](https://github.com/fukuda-yuki/gh-projects-boards/issues/15) |
| Summary, allowance and protected baseline | [#64](https://github.com/fukuda-yuki/gh-projects-boards/issues/64) |
| Overload visibility without adjustment | [#62](https://github.com/fukuda-yuki/gh-projects-boards/issues/62) |
| New-task/dependency CSV import | [#63](https://github.com/fukuda-yuki/gh-projects-boards/issues/63) |
| Integrated P1/P2, human recovery and inherited local latency | [#65](https://github.com/fukuda-yuki/gh-projects-boards/issues/65) |
| Existing Apply performance/recovery; coverage publication | [#51](https://github.com/fukuda-yuki/gh-projects-boards/issues/51), [#56](https://github.com/fukuda-yuki/gh-projects-boards/issues/56) |
| Distribution, notices and enterprise environment | [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13) |

## Explicit exclusions

Kanban, full Excel/Gantt exchange, broader fields/sets/long text and richer scheduling are named in #1's parked scope. Gantt is selected P1; table-only success is not the combined P1 gate. Cross-Project bulk Apply, continuous synchronization, automatic resource leveling and complete rollback after publication are excluded.

## Existing foundations

Preserve the existing connection, registration, native table, drafts, reconciliation and creation/Apply engines. Former layer Issues are historical evidence, not instructions to recreate them. One person-day is eight raw hours; weight applies once. Full estimate, cumulative actual and independent remaining work are separate. Durable Manual endpoints prevail until explicitly released. The [planning contract](planning.md), [specification](spec.md), [design criteria](../DESIGN.md), [architecture](architecture.md) and [decisions](decisions.md) define the adopted behavior.

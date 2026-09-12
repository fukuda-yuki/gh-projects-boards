# Requirements

Source of truth: [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and its linked Issues. This is a navigation outline, not a replacement for their acceptance criteria.

## Product goal

A C# Windows desktop application for editing GitHub Issues in a Project-scoped table: one row per Issue, one column per field. Users enter different values across rows and prepare new Issues alongside existing updates in the same workspace.

## Agreed boundaries

- Use C# + .NET 10 + WinUI 3 / Windows App SDK as the target platform. [#22](https://github.com/fukuda-yuki/gh-projects-boards/issues/22) owns migration validation; the currently integrated WPF executable does not establish WinUI acceptance.
- Launch from a Windows executable; no browser extension or Excel dependency.
- Explicitly register Projects and switch between them through Repository-oriented navigation. The navigation hierarchy does not redefine Project ownership or filter out Issues from other repositories.
- GitHub is the authoritative data source. Keep local drafts and publish changes only through an explicit manual apply operation.
- Delegate authentication and initial API access to GitHub CLI (`gh api`); do not require manually issued PATs or a custom GitHub App.
- Begin performance validation with 100 items; this is not a silent retrieval limit.
- Start with local development and designated GitHub test data. Company GHEC + EMU behavior remains unverified until tested there.

## Requirement map

| Area | Owning Issues |
| --- | --- |
| Technology, editable fields, and item types | [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2) |
| Authentication, connection, and API access | [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3) |
| Project registration and navigation | [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4), [#5](https://github.com/fukuda-yuki/gh-projects-boards/issues/5) |
| Data acquisition, table editing, and local drafts | [#6](https://github.com/fukuda-yuki/gh-projects-boards/issues/6), [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7), [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8) |
| Refresh, conflicts, manual apply, and new Issue creation | [#9](https://github.com/fukuda-yuki/gh-projects-boards/issues/9), [#10](https://github.com/fukuda-yuki/gh-projects-boards/issues/10), [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11) |
| Performance, recovery, distribution, and environment validation | [#12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12), [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13) |

## Outside the initial release

Kanban ([#14](https://github.com/fukuda-yuki/gh-projects-boards/issues/14)), Gantt ([#15](https://github.com/fukuda-yuki/gh-projects-boards/issues/15)), and Excel exchange ([#16](https://github.com/fukuda-yuki/gh-projects-boards/issues/16)) are future work. Cross-Project bulk apply, continuous synchronization, and complete rollback after publishing are not initial-release requirements.

## Open scope

Connection diagnostics and the guarded API adapter implement the initial [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3) slice. Editable fields, supported item types, grid selection, persistence, and distribution conditions remain in their owning Issues. Resolve technology and distribution choices through [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2) and [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13); see [decisions](decisions.md).

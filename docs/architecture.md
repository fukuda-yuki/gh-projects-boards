# Architecture

Source: [#1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2). This is a responsibility outline, not a commitment to separate assemblies or a completed design.

## Current structure

`GhProjectsBoards.sln` contains one WPF application: `src/GhProjectsBoards.App`. It targets .NET 10 and opens an empty window. No grid, domain model, persistence implementation, or GitHub adapter has been added.

## Responsibility boundaries

| Responsibility | Purpose | Owning Issues |
| --- | --- | --- |
| Windows UI | Project navigation, table editing, and review of changes | [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4), [#5](https://github.com/fukuda-yuki/gh-projects-boards/issues/5), [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7) |
| Application logic | Identity, validation, field differences, conflicts, and operation state | [#6](https://github.com/fukuda-yuki/gh-projects-boards/issues/6), [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8), [#9](https://github.com/fukuda-yuki/gh-projects-boards/issues/9), [#10](https://github.com/fukuda-yuki/gh-projects-boards/issues/10), [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11) |
| Local storage | Registered Projects, baselines, drafts, and recoverable operation history | [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4), [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8) |
| GitHub CLI adapter | Authentication diagnostics and structured `gh api` calls | [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3) |

Introduce code boundaries when an implementing Issue needs them. Do not create empty layer projects or speculative interfaces for this table.

## Data and process design

Pending: data contracts, storage schema, adapter interface, and operation lifecycle. Preserve the distinctions in [specification](spec.md); record chosen technologies in [decisions](decisions.md).

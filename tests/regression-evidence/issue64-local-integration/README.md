# Local integration of the preserved Summary and planning repair

The user requested local integration into the original checkout. The checkout
is on `codex/issue-64-summary`. Merge commit
`19ccc4d828acc50886b05b3e7ae9904578561e91` has both original parents:

- Preserved Summary: `671011ad04beb7b7cce8df8328808d7de1aaf13a`.
- Contextual planning/input repair: `2072e3d6737c5cbfb76892ef3b6d5642796a9c4a`.

The repair actually branched from main
`3dcb3364d6c65f07f04b600e3b65f5ad53403557`, not from the Summary tip. Both branches
modified shared files after that common ancestor; the local merge therefore
had 12 conflicting files. Both checkouts were clean before integration. The
original Summary tip also remains named by
`codex/issue-64-before-local-integration-20260920`; the repair branch/worktree
remains unchanged. No history was rewritten and no user data was opened or
converted. Main and remote refs were not changed.

## Resolutions

- Retain Summary labor classification, allowances and protected baseline along
  with native-assignment provenance and contextual Actual/date editing.
- Write checkpoint v12; read original v10 Summary and v11 assignment checkpoints
  without rewriting on read. Combined planning metadata uses v4. Preserve
  earlier plan versions in Undo history and retain the original file as backup
  on save. Old branch readers refuse v12 instead of dropping unfamiliar data.
- Extend the repair's detached save snapshot to Summary/baseline arrays,
  including historical planning operations and adopted calendar contents.
- Compare protected values by content across detached preview snapshots;
  reference identity is not evidence that the user replaced a baseline.
- Keep Summary refresh behind the repair's native-input refresh guard. Preserve
  both fixture entry points and prevent assignment upgrades or batch setup from
  dropping the Summary version.

These are integration corrections. Further dependent Summary UI expansion,
the unresolved Summary review findings, the prior negative human input
evaluation and the remaining measured scroll stalls are not closed here.
See the preserved [Summary continuation](../issue64-summary/CONTINUATION.md)
and [repair evidence](../issue61-65/README.md).

## Executed validation

Windows 11 build 26200, x64, .NET SDK 10.0.401/runtime 10.0.12,
Windows App SDK 1.8.260804001. Runs were serialized. Storage and external-endpoint
fixtures were isolated and synthetic. [validation.json](validation.json)
contains individual results, failed messages/stacks, original artifact hashes
and UI binary hashes. Local raw artifacts remain under `TestResults/` at the
recorded relative paths. User-profile paths and machine names are sanitized in
the portable receipt; original files remain untouched.

| Run | Result | Interpretation |
| --- | --- | --- |
| focused-01 | 139 passed, 1 failed, 0 skipped | The new test supplied a start after the retained finish. Corrected the test date; the product properly rejected the invalid pair. |
| integration-red-02 | 4 passed, 1 failed, 0 skipped | Reproduced unchanged protected metadata being rejected in a detached preview. |
| integration-green-03 | 5 passed, 0 failed/skipped | Migration, detached ownership, contextual edits, protection and Undo after the fix. |
| core-04 | 643 passed, 0 failed/skipped | Entire non-live Core suite, including real isolated storage/adapter collaborators. |
| build-05 | Passed, 0 errors, 2 warnings | Existing hosted Windows App SDK CS0436 initializer warnings. Later incremental solution build had 0 warnings/errors. |
| ui-06 | 42 passed, 1 failed, 0 skipped | All 37 Planning/Gantt cases passed; the additional Summary case invoked its button before Loaded. |
| ui-07 | 5 passed, 1 failed, 0 skipped | Checking enabled alone still did not establish Loaded. Retained this unsuccessful driver correction. |
| ui-08 | 6 passed, 0 failed/skipped | Summary controls, including protected baseline to Gantt date editing, return and Undo, passed after observing Loaded and enabled. Production code was unchanged between UI attempts. |
| ordinary-09 | 1 passed, 0 failed/skipped | Ordinary executable, 1000-task Gantt edit, Project switching and normal process restart; isolated fake gh. |

Commands from the repository root:

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter 'TestCategory!=LiveGitHub&(FullyQualifiedName~Planning|FullyQualifiedName~Summary|FullyQualifiedName~DraftLifetime)' --logger 'trx;LogFileName=focused-01.trx' --results-directory TestResults/local-integration
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter 'FullyQualifiedName~SummaryPlanningIntegrationTests' --logger 'trx;LogFileName=integration-red-02.trx' --results-directory TestResults/local-integration
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter 'FullyQualifiedName~SummaryPlanningIntegrationTests' --logger 'trx;LogFileName=integration-green-03.trx' --results-directory TestResults/local-integration
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter 'TestCategory!=LiveGitHub' --logger 'trx;LogFileName=core-04.trx' --results-directory TestResults/local-integration
dotnet build GhProjectsBoards.sln -c Release
./scripts/Test-UiIntegration.ps1 -NoBuild -Where '(class =~ "Planning.*HostedTests" or class =~ "GanttHostedTests" or class =~ "SummaryHostedTests") and cat != PlanningPerformance and cat != ReviewRetention' -TimeoutSeconds 300
# Used for both ui-07 and ui-08, with the intervening driver correction:
./scripts/Test-UiIntegration.ps1 -Where 'class =~ "SummaryHostedTests"' -TimeoutSeconds 180
./scripts/Test-E2E.ps1 -Filter 'TestCategory=E2E&FullyQualifiedName~OrdinaryGanttThousandTaskEditProjectSwitchAndRestartRetainOnePlan'
```

Use new results filenames/directories when reproducing; retain these originals.
The final source tree `18c8ce2ac7a67a95d024af2e661c73f13c6fbea1` was committed
unchanged after validation. Tests ran while HEAD was the first parent and the
merge was staged. Core source did not change after core-04; subsequent changes
were the UI test's Loaded wait. The later handoff commit changes evidence only.

Focused local code review covered version transitions, detached arrays,
protected comparisons, assignment provenance, refresh gating and verified
identity promotion. No new independent review of the combined source is
claimed. Source/test/product-document whitespace checks passed; the whole
merge diff also reports pre-existing trailing whitespace in preserved review
and PR evidence, which was not rewritten.

Live GitHub, new performance measurements, physical IME reruns, other display
environments, assistive technology, human reevaluation and CI were not run.
This is local branch integration, not remote publication, main integration,
release or complete Unit D/Q1 acceptance.

## Continue in the original checkout

`git log` now includes both histories on `codex/issue-64-summary`; its upstream
is `origin/codex/issue-64-summary`. No push was attempted during this local task.
The existing `scripts/Start-GanttCheck.ps1` entry point now uses the repaired
isolated launcher. `scripts/Start-PlanningCheck.ps1 -Scenario Weekly` is also
available here. Both use new synthetic data roots by default. Human acceptance
remains a separate decision; further dependent Summary work stays held.

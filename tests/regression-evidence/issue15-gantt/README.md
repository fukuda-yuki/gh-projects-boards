# Unit C — Issue #15 evidence index

Ready for source review and user evaluation. GANTT-01–05 have the engineering
evidence below; GANTT-06 supplies the bounded #65 handoff. Human acceptance is
**not run**. Publication, PR creation, merge, Issue closure and release remain
user-owned. No P2 feature is included.

## Source and execution checkpoints

Branch: `codex/issue-15-gantt`, no upstream or remote publication.
Base: freshly fetched main `40489a2d6eb3edfa4ed4a15775bbbbc9b9052acd`.
PR #66 was verified **MERGED** at 2026-09-20 00:50:15 UTC. Its head
`409737272ca50828143e400b15f4aa4573abe826` is an ancestor of main and has the same
tree as the merge. Both foundation build checks succeeded; coverage-pages was
skipped. Final fetch still reports the same main. These are foundation results,
not Unit C results.

Product implementation: `27b94874affb607606c31a9839908c888c287475`.
Final validated source/tests: `226220233ccdb3e0961d3671b7b89c9b71e600b4`.
The latter corrects observation timing only; production is identical to `27b9487`.
The following handoff commit changes this evidence directory only. No dependency,
schema or package change was needed.

Before editing, the execution note selected the integrated foundation and named
`RegistrationPanel` / `EditingGrid`, `EditingWorkspace.PlanFor`, the existing
`PlanningDialogAsync`, coherent Undo and `DraftSession` as the production paths.
The continuous checkpoints were app-connected rendering and pixel inspection →
editing/lifetime → scale/review → this handoff. [CONTINUATION.md](CONTINUATION.md)
is the compact continuation record; this is the sole acceptance map.

## GANTT acceptance map

Pass applies to the stated boundary and environment, not human or whole-#65 acceptance.

| Group | Implementation entry | Verification and independently specified outcomes | Result / remaining limitation |
| --- | --- | --- | --- |
| GANTT-01 | `EditingGrid.Gantt`, `RegistrationPanel.projectViewPositions`, existing editor/session | Actual pending-title/edit/Undo and filtered/search-excluded selection roundtrips; ordinary 1,000-task P1/P2 switching, same-task edit and restart. Direct/F2 physical Japanese composition blocks view switching until natural confirmation, retaining an uncommitted buffer. | Pass. No remote requests in the cached journey. Per-Project view/selection is in-session; work persists without a new persisted-viewport contract. |
| GANTT-02 | `GanttProjection`, `GanttAxis`, `GanttView`, shared calendar | E16/80% ends Wed Oct 7 13:00; A 09:00–13:00, B 14:00–18:00; axis x=36/52/56/72 at 96 DIPs/day; real bar/link geometry within one device pixel; one-minute width 1/15 DIP. Adopted Oct 12 holiday and personal Oct 13 10:00–12:00 exception agree across Gantt details and Boards editor. | Pass. Exact local endpoints, not DATE reconstruction. Light/Dark inspected; other DPI and OS High Contrast not run. |
| GANTT-03 | Shared planning commit/history and Gantt refresh | Real controls: Auto→Manual 12:07–13:00 with predecessor conflict; weight→50%, calendar choice and cutoff/replan retain it; successor starts 14:00 from adopted finish. Explicit Auto ends Fri Oct 9 13:00; one Undo restores the interval/mode while retaining settings. Ordinary weekly actual=5/remaining=3, replan and restart retain Manual before reviewed Apply. | Pass. #61 owns existing formula/permission/reconciliation rules. No second scheduler or resource leveling. |
| GANTT-04 | Projection states, selected context, visible operation InfoBar | Empty/legacy/local/duplicate/hidden rows; stale/unresolved/partial states and maximum date; conflicting Manual retained with advice; invalid earlier finish rejected; missing owner/weight shown unknown; locked-store failure and retry verified by independent checkpoint reload. | Pass. No fabricated complete bars or omitted rows. #61 remote failure rules are preserved; the entire remote-failure matrix was not repeated. |
| GANTT-05 | Native virtualized list, frozen identity, axis and relationship reveal | 1,000 tasks: both axes, day/week, tiny Manual interval, 120-node chain, twelve-way fan-in, unfiltered distant edit retaining its visible row, narrow-window reveal and return to Boards row 990/1000. Actual controls and ordinary normal/narrow/restart workflow; timings below. | Pass at declared engineering boundaries. Dense incoming lines can overlap; full relationships remain inspectable/navigable. Human readability and physical presentation latency remain separate. |
| GANTT-06 | This index, validation receipt, PR handoff and Issue updates | Exact running source, independent review, retained failures, synthetic evaluation and bounded #65 contribution. | Handoff complete. #65 P1/P2, inherited failures, full-job value and human acceptance remain open. |

Entry points: [projection](../../../src/GhProjectsBoards.Core/Projects/GanttProjection.cs),
[view host](../../../src/GhProjectsBoards.App/EditingGrid.Gantt.cs),
[timeline](../../../src/GhProjectsBoards.App/GanttView.cs),
[logic cases](../../GhProjectsBoards.Tests/GanttProjectionTests.cs),
[hosted cases](../../GhProjectsBoards.UiIntegration.Tests/GanttHostedTests.cs),
[ordinary/IME cases](../../GhProjectsBoards.E2E.Tests/GanttJourneyTests.cs),
[weekly journey](../../GhProjectsBoards.E2E.Tests/PlanningJourneyTests.cs).

## Final execution receipt

Final selections ran against `2262202`; UI/ordinary runners recorded a clean
source tree. [validation.json](validation.json) contains case-level names/results,
commands, source blob identities, binary hashes and raw measurements. Counts
belong to these executions; no historical totals are combined.

| Boundary | Passed / failed / skipped | Mechanism and local result |
| --- | --- | --- |
| Logic | 10 / 0 / 0 | NUnit / dotnet; `TestResults/issue15/final-core/gantt.trx` |
| UI integration, including selected existing planning/row/focus/theme/navigation regressions | 32 / 0 / 0 | Real WinUI controls/events and isolated real storage; `TestResults/ui-integration/run-20260920-105340-713-02ef9c72` |
| Ordinary app and native boundary | 4 / 0 / 0 | Two whole-app workflows plus direct/F2 physical Japanese IME cases; public FlaUI UIA3/input, real checkpoint/restart; `TestResults/e2e/20260920-105510-1d542e78d7104e44b78087efdc896891/e2e.trx` |

Four ordinary/native cases includes two IME parameter cases, not four whole-app
journeys. The weekly journey substitutes external fake gh, checks nine reviewed
mutation payloads and no mutation before approval. The 1,000-task cached journey
makes no GitHub requests. Neither is live-GitHub evidence.

Environment: Windows 10.0.26200 x64, Ryzen 7 9700X / 16 logical processors,
61.6 GiB RAM, Radeon Graphics + RTX 5070 Ti, SDK 10.0.401, runtime 10.0.12,
Windows App SDK 1.8.260804001. DPI 125%, High Contrast off. Hosted windows:
1400×1000 and 1200×750 physical pixels; ordinary windows: 1600×1000 and
1200×750. Client area excludes chrome. Host build succeeded with two inherited
auto-initializer CS0436 warnings; final solution/E2E build had zero warnings/errors.

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter FullyQualifiedName~GanttProjectionTests --logger 'trx;LogFileName=gantt.trx' --results-directory TestResults/issue15/final-core
./scripts/Test-UiIntegration.ps1 -Where 'class == GhProjectsBoards.UiIntegration.Tests.GanttHostedTests or test =~ GanttSelectionOutsideBoards or class =~ PlanningHostedTests or class =~ RowViewHostedTests or class =~ SettingsFocusHostedTests or class =~ ThemeHostedTests or test =~ InvokingCachedProjectRevealsWorkspace or test =~ DraftSavePreservesCollapsedNavigation' -TimeoutSeconds 240
./scripts/Test-E2E.ps1 -Filter 'FullyQualifiedName~Gantt'
```

## Measurements

[GanttWorkload](../../GhProjectsBoards.Tests/GanttWorkload.cs) extends the #61
normal fixture: 1,000 tasks, 20 people, six Project fields, mixed modes/progress,
100/80/50 weights, adopted holidays, personal exception, initial 120-node chain,
other ten-node chains, twelve incoming links at task 990, duplicate titles,
one-minute weekend Manual work and three incomplete date states. Known endpoints
span 2026-10-05 through 2027-03-15, with trailing axis space; no task truncation.

Two warmups, ten sequential samples per series. Task 1 estimate alternates 4↔8
hours; 29 effective task endpoint pairs change each time. Undo history grows in
the continuing session. This is not a paired speedup experiment.

| Boundary (ms) | Min | Median | Max |
| --- | ---: | ---: | ---: |
| Scheduler only, prepared inputs | 0.760 | 0.879 | 1.045 |
| Planning Save invoke → expected text, dialog close and two render events | 568.111 | 615.586 | 660.349 |
| Separate full-snapshot save to a new checkpoint directory | 229.668 | 323.882 | 475.559 |
| First/last selection + reveal → onscreen row and two render events | 57.489 | 69.298 | 125.559 |

The <=1,000 ms Gantt recalculation engineering check passed at the declared
render-event boundary. Expected values, actual bar geometry and captured renders
are checked independently, but the timing does not measure physical scanout or
independently timestamp presented pixels. Snapshot creation is outside isolated
save timing. Product saving can overlap publication; numbers are not additive
stage costs. #65's separate 100 ms table selection/menu failure remains open.

## Review and retained failures

Independent review covered changed source/tests/docs and selected images, with
re-review after fixes: no remaining must-fix source finding. The reviewer did not
run the app or provide human acceptance. Parent-agent review also inspected
ordinary app pixels and executed the workflows above.

Consumer corrections: connect the existing Boards scroll template when opening
a Project initially in Gantt; expose save/Undo failures in the visible view;
retain filtered/search-excluded selection and distant-row viewport; keep missing
owner/weight unknown; bound maximum dates; retain visible F6 regions and themed
selector labels. No upstream scheduler defect or unrelated improvement was
absorbed into Unit C.

Failures remain in their original directories. [hosted-attempts.json](hosted-attempts.json)
indexes hosted attempts with their Git HEAD and working-tree changes; an early
dirty run does not establish behavior of its base commit alone. The material
failures below are not rewritten as passes.

| Attempt under TestResults | Outcome / disposition |
| --- | --- |
| `issue15/core-red/gantt-red.trx` | 1 pass / 1 fail with projection stub; implementation then passed 2, expanded 9, and final 10 cases. |
| `issue15/build-early.log`, `issue15/ordinary-first.log` | Early compile errors, including the new test's missing resize helper; no runtime acceptance. |
| UI `100224-219-74fa0d1c`, `100323-700-47b5961c` | Focus/dialog observation timing failed. Early pixel inspection also exposed record-text rendering; the native DataTemplate fixed it before broader integration. |
| E2E `20260920-101242-26a76ab7881d4ac29527ae221e556a0a` | 0/1: return to Boards could not reach row 1000 after initially opening in Gantt; scroll-template integration repaired. |
| UI `103029-008-b5aaba9f` | Ambiguous Path import caused test build failure; no runtime cases. |
| UI `103205-313-9569148f` | 6/3: F6/selection observation, subpixel tolerance at 125% DPI and unrealized retry control; corrected visible-region handling and assertions. |
| UI `103405-087-7a34122c`, `103615-284-0f87603b` | 8/1 and 29/1: retry used a command-bar helper for an InfoBar button, then read storage during saving. Correct public invocation/durable readback passed at `103741-152-bfcd94bd`. |
| UI `104121-415-3950352b` | 2/1: unfiltered row 1000 disappeared after edit. Offset restoration passed the unchanged regression at `104240-424-969ebeb6`. |
| E2E `20260920-104734-fc2a7fb9a54446c2b2255fab8786d1ad` | 3/1: test toggled the responsive pane during closing and reopened it. Public closed-state wait passed at `105213-cbca55e14dff43ffa3c135e1fec6525e`, then final 4/0. Product code unchanged. |

UI IDs use prefix `TestResults/ui-integration/run-20260920-`. Some early captures
preceded scrolling and left selection offscreen; they are excluded from the
final set. Final scale evidence requires an onscreen selected row and reveal
after narrowing. Old partial passes are not accumulated into final counts.

## Artifacts and evaluation

Tracked images are unmodified captures of the real app-connected EditingGrid
and Gantt controls with synthetic data. [images.json](images.json) records their
source/run/hash. They are UI-host captures, not ordinary-window captures; relative
links work here and from the branch after publication.

- [Light](assets/gantt-geometry-Light.png), [Dark](assets/gantt-geometry-Dark.png).
- [1,000-task replan](assets/gantt-1000-replanned.png), [far exact interval](assets/gantt-1000-last-day.png).
- [Selected fan-in](assets/gantt-1000-fan-in-week.png), [narrow selected row](assets/gantt-1000-narrow.png).

Raw TRX/XML/logs/checkpoints, binary/environment receipts, ordinary-window images
and old failures stay under local TestResults. Hosted raw captures also remain
at temporary paths in stdout.log. Ordinary images were inspected locally; DWM
borders can include desktop edge pixels, so they are excluded from publication.
A local C: path is not a remote attachment. The tracked sanitized receipt and
six images are the portable review set; raw evidence is local to this workspace.

```powershell
dotnet build GhProjectsBoards.sln -c Release
./scripts/Start-GanttCheck.ps1
# After normal close, reopen the printed directory:
./scripts/Start-GanttCheck.ps1 -DataRoot 'C:\absolute\printed-directory' -Resume
```

The launcher was exercised with `-PrepareOnly`, including checkpoint readback,
then `-Resume -PrepareOnly` with the checkpoint hash unchanged. It creates new
isolated data and refuses overwrite. Open saved `github.com / ID
42`, then **P1**; no connection check needed. Prepared local data is at
`TestResults/gantt-evaluation-29c79749ea95487e8fcb883e91f962bf`.

1. Leave a title/estimate unfinished on Boards, switch Gantt and back. Task 1
   runs Oct 5 09:00–13:00; task 2 runs 14:00–Oct 6 10:00 at 80%. Duplicate titles
   have distinct Issue numbers.
2. Search `#50`, select and **選択へ移動**: Manual Oct 10 12:07–12:08 on a weekend.
   Inspect exact details; its bar is not expanded into a day.
3. Inspect `#990` and all predecessor identities; reveal a related task, clear
   search, navigate both axes, change day/week and narrow the window. Use
   **選択へ移動** to bring the selected row/date into view.
4. Inspect 997 (Unplanned), 998 (only Nov 2 12:07 start), 999 (unresolved external
   prerequisite), 1000 (Manual Mar 15 2027 12:07–13:00). Edit 1000 finish to 16:19,
   switch P2/P1, **表で開く**, inspect retained `24未確定`, then close/reopen.
5. Evaluate wording, readability and the actual planning job yourself. #65's
   human comparison/investment envelope is not fulfilled by synthetic checks.

## Handoff and ownership

- **Source review ready:** complete local changeset, scoped regressions and
  independent review; no known implementation blocker.
- **User evaluation ready:** ordinary/native evidence, runnable synthetic data,
  fixed examples and inspected normal/narrow output.
- **Not accepted / still owned elsewhere:** human natural-input/IA/full-job value,
  High Contrast/other DPI/accessibility environments, #65's inherited 100 ms
  selection/menu failure, #51's 50-field UNPROCESSABLE/throughput disposition,
  P2 and release. New live validation was unnecessary for this local consumer;
  #61 evidence is reused only for its original source/behavior. This is not all-P1 acceptance.

Use [pr-title.txt](pr-title.txt) and [pr-body.md](pr-body.md) for one Unit C PR
against main after user publication. A/B is already in main; do not republish it.
Local configuration: `branch.autoSetupMerge=simple`, `push.default=simple`,
`push.autoSetupRemote=true`; no upstream. User-owned next actions: branch
publication, PR/source review, evaluation and later merge/closure decision.
This assignment ends here; no next P2 work is started or scheduled.

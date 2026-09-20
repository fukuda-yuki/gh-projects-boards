# Independent review and unresolved work

Oracle 0.20.1 browser review completed in 22m16s. Requested target:
`gpt-5.6-sol`, browser log target GPT-5.6 Sol and effort label `Pro`.
These are tool/picker observations, not a verified server identity. One request,
14 attached source/contract files, reported input 45.04k/output 2.64k tokens.
The reviewer performed static analysis, not runtime execution or human review.
It reviewed an earlier uncommitted bundle; its local complete response is
`TestResults/issue64/independent-review.md`. No credentials or user task data
were submitted. The original response is retained, not rewritten after fixes.

Exact command (the sanitized prompt is copied to [review-prompt.txt](review-prompt.txt);
the command originally read that same prompt from the local TestResults path):

```powershell
oracle --engine browser --model gpt-5.6-sol --browser-thinking-time heavy --browser-hide-window --timeout 10m --slug issue64-summary-review --write-output TestResults/issue64/independent-review.md --prompt (Get-Content TestResults/issue64/review-prompt.txt -Raw) --file AGENTS.md DESIGN.md docs/planning.md src/GhProjectsBoards.Core/Projects/SummaryContract.cs src/GhProjectsBoards.Core/Projects/SummaryProjection.cs src/GhProjectsBoards.Core/Projects/PlanningWorkspace.cs src/GhProjectsBoards.Core/Projects/PlanningContract.cs src/GhProjectsBoards.Core/Projects/CreationPlanning.cs src/GhProjectsBoards.Core/Projects/DraftStore.cs src/GhProjectsBoards.Core/Projects/EditingWorkspace.cs src/GhProjectsBoards.App/SummaryView.cs src/GhProjectsBoards.App/EditingGrid.Summary.cs src/GhProjectsBoards.App/EditingGrid.Gantt.cs tests/GhProjectsBoards.Tests/SummaryTests.cs
```

The browser runtime continued beyond the supplied timeout. It was reattached
with `oracle session issue64-summary-review --render`; no duplicate model
request was submitted. That reattachment later reported "Chrome is no longer
reachable" after the original browser session ended. The original invocation
exited 0 and saved the complete response; the reattachment failure is retained
separately and is not a second review result. The final answer had nine numbered
finding groups.

| Review group | Parent-agent verification and disposition |
| --- | --- |
| 1. Missing Summary/LaborKind properties in current checkpoint silently default | Addressed before review returned. v10 presence is checked in current plans and history after record validation; v9 may omit the added properties. Two new missing-property cases and the existing null-task corruption case pass; original bytes remain. |
| 2. Missing joint-worker actual disappears from Project completeness | Addressed before review returned. Observed Red retained; missing actual now participates in completeness while known hours remain 104h in the independent two-task case. Final Core passes. |
| 3. Native assignee with no legacy independent owner | The review applied the previous mandatory OwnerId contract. Its proposed unresolved-owner policy must **not** be adopted as the new product decision. Revised #61 requires a complete unique native assignee and a deliberate legacy-owner migration. The current Summary fallback is not final against that requirement. Coordinate with #61. |
| 4. Failed first save after temp readback cannot retry because no main exists | Source-supported inherited DraftStore concern: LoadAsync refuses orphan temp, while SaveAsync retains a temp after late failure. Current Summary test covers writer-lock failure with an existing checkpoint, not a post-temp initial failure. No runtime reproduction or fix claimed. Coordinate the shared persistence owner; reproduce at its real boundary before changing recovery. |
| 5. Protected-baseline promotion can collide with a preexisting remote baseline identity | Source supports a gap: promotion substitutes the local task ID without checking baseline target uniqueness; the normal lineage test has no coexistence collision. Verified-row filtering narrows but does not establish safety for a baseline captured before verification. Reproduce uncertain-creation/refresh coexistence and preserve the original baseline on rejection. Conflicting duplicate canonical item values also need explicit incomplete/reconciliation behavior instead of silent first-wins selection. |
| 6. Rollup details show zero instead of retained raw values | Confirmed by source: the contribution values are replaced by known zero before the detail view sees them. Project exclusion is intended; raw-value display is not. Keep raw detail values and an independent aggregation-inclusion flag, with logic and formatting checks on resume. |
| 7. Cold projection may initialize workspace fields | Confirmed inherited call path: GanttProjection/PlanFor calls Open, which can add fields/increment revision. The passing read-only test uses an already initialized workspace. Cold projection/preview is unverified; fix at the shared snapshot/query boundary with the repair owner rather than adding another workspace. No remote write was observed or claimed here. |
| 8. Boards/Gantt selected task is not passed into Summary | Confirmed by source: ShowProjectView computes id but Summary branch only passes person; Present retains the previous/first Summary task. The hosted passing roundtrip starts at the first task and does not cover this defect. Add a non-first-task actual-control regression and route the existing stable identity after the input repair. |
| 9a. Entirely unknown detail appends 0 hours | Confirmed in SummaryText.Exact. Preserve unknown in the hours form; label mixed known/unknown as a subtotal. Await the resumed UI correction; do not claim the current detail is accepted. |
| 9b. Past-cutoff actual has a fixed today header | Confirmed fixed header despite an earlier reporting cutoff. The stale-date logic is now conservative, but the header needs the applicable reporting-date context. No presentation fix or new UI acceptance run after the hold. |

The earlier UI failures mentioned by the reviewer were later resolved within
their narrow cases, with five hosted cases passing. That does not answer the
additional selected-task defect above. Review remains **open**, with no re-review
or all-clear. The revised prerequisite is the reason to preserve and coordinate
these changes instead of continuing shared UI/lifetime work concurrently.

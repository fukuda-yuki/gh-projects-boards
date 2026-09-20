# Unit C — Issue #15 evidence index

## Execution checkpoint

Base: freshly fetched `origin/main` at `40489a2d6eb3edfa4ed4a15775bbbbc9b9052acd`.
PR #66 merged at 2026-09-20 00:50:15 UTC. Its head
`409737272ca50828143e400b15f4aa4573abe826` is an ancestor and has the same tree as
the merge. Both reported build checks passed; coverage-pages was skipped.
These are foundation results, not Unit C validation.

Branch: `codex/issue-15-gantt`, initially clean, no upstream. Local publication
configuration uses same-name branches. Remote publication, PR, merge and closure
remain user-owned.

Production entry points: `EditingGrid` within `RegistrationPanel`,
`EditingWorkspace.PlanFor`, `PlanningDialogAsync`, existing coherent planning
Undo, `DraftSession` and refresh/restart lifecycle. The consumer will use exact
adopted endpoints and `WorkingCalendar`; it will not infer times from DATE cells.

Continuous checkpoints: (1) app-connected timeline and pixel inspection;
(2) editing/replan/lifetime integration; (3) normal-scale visual/performance
verification and independent review; (4) evidence and publication handoff.
Current: app-connected and editing/lifetime checkpoints exercised. The ordinary
1,000-task journey found a lazy Boards-scroll initialization problem when a
Project initially reopened in Gantt; the consumer integration now initializes
the existing scroll connection on return. Ordinary Gantt/restart and the linked
weekly-replan/Apply journey passed on that correction. Final native-input,
theme and assembled-source review remain in progress; this is not final delivery.

## Presentation and verification plan

Use a compact common Boards/Gantt selector (Summary unavailable until #64).
Keep native Boards editors mounted so switching does not commit their text.
Use a virtualized native task list with frozen readable identity, a horizontal
day/week axis in Japan wall-clock minutes, and selected-task directed links
instead of an unreadable all-edge overlay. Retain every task, including
Unplanned/partial/unresolved work. Exact endpoints, input/owner/weight/calendar,
advice and predecessor navigation are available in selected-task context.
The linked existing planning editor commits Manual overrides and explicit Auto.

Projection rules use hand-specified expected coordinates/times/statuses in logic
tests. Actual controls verify switching, selection/reveal, editing and rendering.
An ordinary executable journey verifies Project/restart/pending work and the
integrated planning workflow with an explicitly substituted GitHub endpoint.
Normal-scale measurements declare graph, date horizon, warmups and sample counts;
calculation, visible publication and durable save are separate boundaries.

## Acceptance map (maintained in place)

| Group | Implementation entry | Verification | Result / limitation |
| --- | --- | --- | --- |
| GANTT-01 | EditingGrid view host; existing plan editor/session | Actual view/edit and filtered Project roundtrip; ordinary Project/restart journey | Observed pass; final physical IME checks pending |
| GANTT-02 | Adopted-plan projection; WorkingCalendar | Independent E16/80, lunch, exception and DATE-fidelity expectations; rendered bar/link geometry | Observed pass; Light selector capture correction pending |
| GANTT-03 | Existing plan/Undo plus Gantt refresh | Override, weight/predecessor/calendar/replan, explicit Auto and Undo through controls | Observed pass; ordinary final-source rerun pending |
| GANTT-04 | Projection states and selected context | Partial/Unplanned/unresolved/stale/conflicting Manual, invalid input; save failure/retry | Core and hosted pass; save test corrected its early durability readback |
| GANTT-05 | Virtualized rows/axis/reveal | 1,000-task controls and ordinary normal/narrow app; timing and pixels | Observed pass at stated render-event boundary; final-source capture pending |
| GANTT-06 | This index and #65 contribution | Source/command/result map and independent assembled review | In progress; human acceptance remains separate |

Existing #65 selection/menu latency, human IA/native acceptance, #51's 50-field
UNPROCESSABLE/throughput disposition and P2/release gates remain owned elsewhere.
No foundation passing total will be relabeled as Gantt acceptance.

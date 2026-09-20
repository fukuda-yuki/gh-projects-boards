# Focused isolated workflow reevaluation

**Ready for the focused synthetic workflow reevaluation described in #61; human evaluation has not run.** Known scroll freezes and the retained Gantt/retention timing failures are engineering residuals, not something the user is asked to approve. This is not broad product, performance or Summary acceptance.

Use the retained `scripts/Start-PlanningCheck.ps1` from this corrective branch. It rebuilds the ordinary app and fixture from the current checkout, records source/content/binary hashes in the root's `diagnostics/launch-*.json`, rejects unmarked or mismatched roots, and never replaces an existing root without the explicit Resume path. It uses an empty isolated gh configuration and removes four token environment variables. No connection check is needed for the cached synthetic data.

## Prepared roots on the implementation host

Workspace: `C:\Users\mwam0\.codex\worktrees\issue65-stabilize\gh-projects-boards`

| Scenario | Absolute data root | Preparation |
| --- | --- | --- |
| Fresh | `C:\Users\mwam0\.codex\worktrees\issue65-stabilize\gh-projects-boards\TestResults\post68\handoff\fresh` | Prepared with validated readback at 948dd53; initial Project setup remains available. |
| Weekly | `C:\Users\mwam0\.codex\worktrees\issue65-stabilize\gh-projects-boards\TestResults\post68\handoff\weekly` | Prepared with validated readback at 948dd53; 20 tasks, two Projects, 20 people, explicit native assignment and weights. Actual 5 / Remaining 4 are retained for the first task. |

Weekly was actually launched through this launcher on 948dd53. Using the normal controls, the agent selected saved `viewer / github.com / ID 42` and P1, inspected readable Boards pixels, Actual 5 / Remaining 4 and disabled `Summary（準備中）`, then closed normally. No workflow edit was made to the handoff root. The separate ordinary E2E already exercises the requested editing/restart journey; this initial-screen observation is not counted as another E2E or human acceptance.

From this workspace:

```powershell
./scripts/Start-PlanningCheck.ps1 -Resume -DataRoot 'C:\Users\mwam0\.codex\worktrees\issue65-stabilize\gh-projects-boards\TestResults\post68\handoff\weekly'
```

Use the same command with the `fresh` root for initial setup. On another checkout/machine, prepare new roots with `./scripts/Start-PlanningCheck.ps1 -Scenario Fresh` or `-Scenario Weekly`; keep the printed root and use `-Resume -DataRoot '<that absolute root>'` to reopen. Do not import the evidence archive as application data.

The measured binaries remain frozen at 948dd53 under `TestResults/post68/final/app`. Later receipt-only commits have identical product source but can change assembly provenance metadata on rebuild. A future launcher run records its own actual source/hash; it must not borrow the frozen performance receipt's hash.

## Ordinary job to reevaluate

1. Open the saved synthetic profile and P1. In Fresh, set field mappings and the Project calendar/weight once; use the existing native assignee and enter Estimate. A second independent planning-owner selection is not required.
2. In Weekly, select the first task by its Issue identity. Update Actual 5 to 7 using its contextual action and one explicit reporting cutoff; keep Remaining at 4. Move to the next row and reuse the confirmed cutoff. Historical reports/worker attribution must stay visible and intact.
3. Change Remaining 4 to 3 independently; Actual must stay 7. Undo must affect only the chosen operation.
4. Enter a start or finish with explicit minute precision using the date editor. The selected endpoint becomes Manual; the other endpoint and confirmed actual history remain intact.
5. Switch Boards/Gantt and return to the same task, close normally, then Resume the same root. Confirm the edits and chosen task context are coherent after reopening.

The evaluation question is whether this small ordinary job is understandable and usable. The user is not asked to run tests, gather a profile, approve known freezes, or evaluate the incomplete Summary consumer. Stop dependent acceptance if a new blocker is observed and report its task/field/action/current state without replacing the original data. P1/P2, broader environments/recovery, #51, release, live GitHub and complete #64 acceptance retain their owners.

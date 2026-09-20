# Source review of `ab292d50f02d4e373871c48f408ce1151bf7a169`

This is a static review of the supplied combined source. I did not execute tests or the application, and I am not treating the parent tests, merged source, or retained regression evidence as runtime or human-acceptance certification.

I found **eight surviving issues: three High, four Medium, and one Low**. The previously reported cold-projection mutation appears fixed in the supplied core path.

## Findings

### 1. High — Confirmed source defect: a failed initial save after temp readback prevents every subsequent retry

**Location:** `src/GhProjectsBoards.Core/Projects/DraftStore.cs:64-70, 99-126`

`SaveAsync` first calls the public `LoadAsync`. When there is no canonical checkpoint, `LoadAsync` refuses to continue if any matching `.tmp` exists. The save then writes and validates a temp file, but a failure after readback—most directly `canCommit() == false`, but also a failing initial `File.Move`—leaves that temp behind. The next retry calls `LoadAsync`, sees the orphan temp, and fails before it can create or commit a replacement checkpoint.  

**Reachable reproduction:** start with no `.json`; call `SaveAsync(record, 0, () => false)` so serialization, durable flush, and readback succeed; then call `SaveAsync` again with the retained or newer record. The second call fails at the current-load step because the first temp remains.

**Current observable result:** the in-memory work is retained, but no canonical checkpoint can be established without external recovery-file intervention. Restart correctly reports an interrupted checkpoint, but an explicit in-process retry cannot recover.

The existing lifetime test does not cover this boundary: it holds the profile writer lock, so the first failure occurs before temp creation and readback. 

**Narrow fix:** separate “read the committed canonical revision for compare-and-swap” from the public recovery-sensitive `LoadAsync`. While holding the existing writer locks, `SaveAsync` should compare only against the canonical `.json`; an orphan temp must remain preserved as recovery evidence but must not prevent a new explicit save attempt. It must not silently adopt, overwrite, or delete that temp.

**Lowest reliable regression boundary:** isolated `DraftStore` filesystem integration. Inject failure through `canCommit` after readback, verify the temp bytes remain unchanged, retry with a newer snapshot, and verify that the canonical file contains the latest snapshot while the rejected candidate was never promoted.

---

### 2. High — Confirmed source defect: verified creation promotion can collide inside a protected baseline

**Location:** `src/GhProjectsBoards.Core/Projects/CreationPlanning.cs:19-81, 84-90`; `SummaryContract.cs:42-50`

A valid protected baseline can contain both:

* a local task such as `local-A`, and
* an already observed remote task such as `I1`.

This can arise during uncertain creation followed by refresh: the baseline records every projected task, while `ProjectPlanning.Tasks` need not contain metadata for every remote row.

When the creation journal later verifies `local-A` as `I1`, `PromotePlanning` blindly substitutes `local-A → I1` in the protected baseline. It does not check whether another baseline task already has `TaskId == I1`. The shown occupancy helper only checks current planning tasks and active field work; it does not examine protected-baseline identities. 

The resulting baseline contains duplicate `TaskId` values and cannot pass the existing protected-baseline validation. That validation is an important correctness guard, but it detects the problem only after promotion has constructed an invalid candidate. 

The same blind identity substitution also affects `LocalLinks`: if a task already contains both a link to `local-A` and a link to `I1` with the same relation kind, promotion creates duplicate `(PredecessorId, Kind)` entries, which planning validation rejects.  

**Reachable reproduction:** capture a baseline containing `local-A` and `I1`, keep only `local-A` in planning metadata, then verify the local creation as `I1`. The occupancy check does not reject this state; promotion creates two baseline tasks keyed by `I1`.

**Current observable result:** the verified remote creation exists, but local lineage promotion cannot produce a valid v12 checkpoint. Depending on the omitted caller boundary, the operation either fails during validation/save or exposes an invalid in-memory candidate. Durable corruption should be prevented by validation, but the verified creation remains locally unreconciled.

**Narrow fix:** perform a complete identity-substitution preflight at the beginning of `PromotePlanning`, before transferring fields, clearing the retained local key, or invalidating Undo:

* A protected-baseline target collision must reject; two protected snapshots must not be silently merged or selected first-wins.
* An exactly equivalent duplicate dependency edge may be deliberately coalesced only when all edge payload is identical; conflicting relation metadata must reject.
* Current task-ID collisions must likewise reject before mutation.

On rejection, preserve the original protected baseline, local task and fields, pending buffers, history, creation journal, and verified lineage evidence.

**Lowest reliable regression boundary:** core creation/planning integration through the real verification/promotion entry point. Snapshot the complete workspace before promotion; reproduce baseline and link collisions; assert rejection and byte-equivalent retained baseline, local planning data, fields, history, and creation journal.

---

### 3. High — Confirmed source defect: duplicate canonical Issue rows are silently first-wins and can receive fan-out writes

**Location:** `PlanningWorkspace.cs:21-49, 151-182`; `SummaryProjection.cs:33-35`; `SummaryContract.cs:80-91`

`PlanFor` collapses rows with the same canonical Issue ID through `DistinctBy`, without comparing their NUMBER/DATE values, draft state, availability, pending input, or conflicts. The chosen input is therefore determined by snapshot row order. 

The consequences extend beyond read-only display:

* `GanttProjection` displays every row but supplies each duplicate with the one calculation/input selected by `PlanFor`.
* `SummaryProjection` collapses the Gantt rows by `TaskId` again.
* Protected-baseline capture collapses them again and records whichever row survived.
* `ProjectPlan` iterates all rows and applies the one calculated task projection to every duplicate item row. Thus an operation based on one row can create Start, Finish, or Actual drafts on another duplicate row as well.   

**Reachable reproduction:** provide two Project item rows with distinct item IDs but the same Issue node ID. Give their mapped Estimate fields values such as 8 and 16 hours, or give one a retained local draft. Reverse their snapshot order between otherwise identical projections.

**Current observable result:** the schedule, Summary contribution, and captured baseline change with row order. The second row’s conflicting value is neither reported as incomplete nor reconciled. A planning commit can also stage generated projection changes against both item IDs.

**Narrow fix:** canonicalize by Issue ID through an explicit grouping step rather than `DistinctBy`. Compare every planning-relevant value and state. When duplicates disagree:

* produce an incomplete/unresolved planning input;
* skip generated projection writes for all members of the ambiguous group;
* refuse protected-baseline capture with a specific reconciliation error;
* retain both rows, their fields, pending buffers, conflicts, and history unchanged.

No duplicate should be deleted or implicitly overwritten. Identical duplicates may use a deterministic representative only after equivalence has been established.

**Lowest reliable regression boundary:** core planning integration. Cover conflicting duplicate Estimate, Remaining, Actual, and DATE values; reverse row order; verify order-independent unresolved output, no generated changes on either item, unchanged revision/history after rejected baseline capture, and preservation of both rows’ work.

---

### 4. Medium — Confirmed scope regression: Summary is still exposed through normal application navigation

**Location:** `src/GhProjectsBoards.App/EditingGrid.Gantt.cs:42-53, 59-74`

The normal project selector creates an enabled `Summary` item, adds it beside Boards and Gantt, maps its selection to `ProjectView.Summary`, and constructs/presents the full Summary view. There is no preparing/disabled gate in the supplied source.  

**Reachable reproduction:** open an ordinary editing workspace and select `Summary` in the normal `SelectorBar`.

**Current observable result:** normal users can enter the held Summary surface and invoke allowance, baseline, Undo, settings, and editing commands, despite the current instruction that normal Summary access remain temporarily unavailable while isolated test access continues.

**Narrow fix:** gate only the production navigation route—remove or disable the normal selector item and present the preparing state required by the owning Issue. Retain `SummaryView`, v12 metadata, internal routing needed by isolated tests, and existing checkpoints. Do not delete or migrate Summary data.

**Lowest reliable regression boundary:** actual WinUI control integration. Verify the ordinary selector has no enabled Summary route, while the isolated Summary host can still construct and exercise the view.

---

### 5. Medium — Confirmed source defect: rollup details replace retained raw effort with zero

**Location:** `src/GhProjectsBoards.Core/Projects/SummaryProjection.cs:47-82, 91-95`

The projection correctly decides that rollup tasks must be excluded from aggregate totals, but it implements that decision by replacing the contribution’s Estimate, Actual, and Remaining values with known zero before storing the detail row. Person totals are then calculated from those altered contributions.  

**Reachable reproduction:** use a Rollup task with retained raw Estimate 16, Actual 4, and Remaining 12 hours.

**Current observable result:** project and person totals avoid double-counting, but the task detail says 0/0/0 rather than showing the retained raw values alongside “excluded from aggregation.” This erases evidence needed to understand the exclusion.

**Narrow fix:** retain the raw contribution values and add an independent aggregation-inclusion state, or use a separate aggregate input collection. Project/person aggregation must ignore rollup contributions without modifying their display values.

**Lowest reliable regression boundary:** core `SummaryProjection` logic. Assert that raw values remain in `SummaryContribution`, while Project and person totals exclude them exactly once.

---

### 6. Medium — Confirmed source defect: the selected non-first Boards/Gantt task is lost on Summary entry

**Location:** `EditingGrid.Gantt.cs:59-74`; `SummaryView.cs:125-149`

`ShowProjectView` calculates the incoming stable row ID in `id`, but the Summary branch calls `UpdateSummary(true, personId)` and discards `id`. `SummaryView.Present` can preserve only its prior Summary selection; on first entry it selects the first person and first contribution, and on later entry it may retain an older Summary row instead of the newly selected Boards/Gantt row.  

**Reachable reproduction:** select the second task on Boards or Gantt, then enter Summary through the internal/isolated route. Repeat after previously selecting a different Summary task.

**Current observable result:** Summary highlights the first or previously selected task. Subsequent “Boardsで開く,” “Ganttで開く,” or “工数を編集” therefore targets that visible replacement, not the task from which the user entered.

**Narrow fix:** pass the incoming stable row ID through `UpdateSummary` into `Present`. Select a person contribution that contains that row—retaining the current Summary person when it contains the row, otherwise choosing a deterministic matching contribution—and then select the row. This must be UI selection only; it must not call `Open`, initialize fields, commit cells, or flush a checkpoint.

**Lowest reliable regression boundary:** actual WinUI control integration, covering entry from both Boards and Gantt with a non-first task and verifying the selected `SummaryTasks` item and command target.

Although normal Summary access should currently be blocked by finding 4, this defect remains in the isolated route and would survive a later re-enable.

---

### 7. Medium — Confirmed source defect: exact rendering invents “0 hours” for wholly unknown effort

**Location:** `src/GhProjectsBoards.App/SummaryView.cs:13-16, 140, 161`

`SummaryText.Value` correctly renders a wholly unknown `EffortValue` as `不明`, but `SummaryText.Exact` always appends `v.Hours`. `EffortValue.Missing` stores zero in the numeric accumulator, so the resulting text is effectively “unknown person-days / 0 person-hours.” The same formatter is used by person details and full task details.  

**Reachable reproduction:** display a contribution whose value is `EffortValue.Missing`.

**Current observable result:** absence or unverified effort is presented together with a false known zero. Mixed known/unknown values also present their known hours as though they were a complete total rather than a subtotal.

**Narrow fix:** when there are no known operands and at least one unknown operand, omit the numeric hours and state that hours are unknown. For mixed values, label `Hours` explicitly as the known subtotal and retain the unknown/stale counts.

**Lowest reliable regression boundary:** a formatter-level App test for wholly unknown, known zero, mixed known/unknown, and stale-known values. A whole-application journey is unnecessary for this rule.

---

### 8. Low — Confirmed source defect: the Actual column header says “today” when the projection uses a past cutoff

**Location:** `SummaryProjection.cs:33-36`; `SummaryView.cs:97, 125-132`

The projection selects the earlier of today and the configured cutoff. Thus a past cutoff excludes later reports. The header is nevertheless fixed as `実績（本日時点）`, even though the context line separately shows the earlier reporting basis.   

**Reachable reproduction:** configure a cutoff before today and include reports both before and after it.

**Current observable result:** the figures reflect the cutoff, while the column claims they reflect today.

**Narrow fix:** make the header use the projection cutoff, or use an unambiguous static label such as “reporting-cutoff basis” while retaining the exact date in context. Do not change the existing conservative stale-report rule as part of this wording correction.

**Lowest reliable regression boundary:** actual Summary control integration with a past cutoff, asserting the rendered header and context use the same reporting basis.

## Historical review ledger disposition

The retained ledger had nine numbered groups, with 9 divided into two display findings. 

| Historical group                                            | Current disposition                                                                                                                                                                                                                             |
| ----------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1. Missing Summary/LaborKind properties silently default    | **Fixed in the supplied source.** Current v10/v12 JSON is checked for explicit `Summary` and `LaborKind` properties; planning and history are then validated.                                                                                   |
| 2. Missing joint-worker Actual disappears from completeness | **Fixed for non-rollup tasks.** The union of estimate, remaining, and report workers creates a missing Actual operand for a worker without a report, while retaining known reported hours. Rollup zeroing remains separately open as finding 5. |
| 3. Native assignee without legacy independent owner         | **Previous recommendation not applicable.** Do not reinstate a mandatory second owner. The current assignment contract ties nonlegacy `OwnerId` to one complete unique adopted assignee and permits explicitly retained legacy semantics.       |
| 4. Initial post-temp failure cannot retry                   | **Survives as finding 1.**                                                                                                                                                                                                                      |
| 5. Baseline promotion collision / duplicate canonical rows  | **Both survive, split as findings 2 and 3.**                                                                                                                                                                                                    |
| 6. Rollup detail shows zero                                 | **Survives as finding 5.**                                                                                                                                                                                                                      |
| 7. Cold projection initializes workspace fields             | **Fixed in the supplied core projection path.** See below.                                                                                                                                                                                      |
| 8. Boards/Gantt selection not passed to Summary             | **Survives as finding 6.**                                                                                                                                                                                                                      |
| 9a. Unknown detail appends zero hours                       | **Survives as finding 7.**                                                                                                                                                                                                                      |
| 9b. Actual header fixed to today                            | **Survives as finding 8.**                                                                                                                                                                                                                      |

## Combined v12/planning v4 audit

### Detached arrays and protected comparisons

`SummaryContract.SameSettings` now compares allowances, baseline people, baseline tasks, holiday dates, exceptions, and working intervals by value rather than relying on record equality of detached array references. That directly addresses the merge risk where a valid detached candidate was rejected merely because its arrays were separately allocated. 

`CommitPlanning` performs projection acceptance and the complete commit on a restored staged workspace, then replaces live fields, planning, history, and revision only after the staged work succeeds. `DraftSession.CommitAsync` similarly prepares a restored candidate and swaps `Workspace` only after durable save succeeds. Those are appropriate rejection-preservation boundaries.  

I found no additional source-supported detached-array comparison regression in these files. The implementation of `DraftSnapshot.Copy` itself is not included in the bundle, so I cannot independently inspect every nested copy branch; the supplied tests exercise representative Summary, baseline, registration, field, and planning-history collections, but remain test source rather than executed evidence.  

### Planning history and version migration

The envelope accepts versions 1–12, requires the appropriate planning metadata version, validates both current plans and before/after history plans, and writes new snapshots as v12. The v10/v11 compatibility test source also preserves the original file on read and expects it as `.bak` after the next save. I found no additional migration data-loss path in the supplied files.   

This does not make finding 1 safe: migration and schema validation occur before the initial orphan-temp retry deadlock.

### Assignment and owner compatibility

The current model retains `PlanningAssignment`, allows explicitly marked legacy assignment, and validates that a nonlegacy `OwnerId` equals the sole assignee only when the observed assignment is complete and unique. Multiple, empty, or incomplete assignment remains unresolved rather than creating another planning owner.  

I found no justification for reinstating the superseded mandatory second-owner contract.

### Independent Actual and Remaining

Summary forecast remains `Actual + Remaining`, and the projection treats them as distinct operands. No supplied path resynchronizes Remaining from Actual or estimate changes. The surviving Summary defects alter presentation or attribution completeness, not that independence.

### Cold projection and prohibited side effects

The historical cold-mutation call path is no longer present in the supplied core projection:

* `Open` remains the initializing operation.
* `ReadRows` calls the same row builder with `initializeFields: false`.
* `PlanFor` now uses `ReadRows`.
* `GanttProjection.Create` also uses `ReadRows`.

Consequently, cold Gantt/Summary projection does not add draft fields, increment `Revision`, create history, save a checkpoint, or perform a remote operation in these paths. The `calculatedPlans` cache is transient and is not included in the checkpoint.   

There is a separate call to mutating `Open` when `SelectGanttRow` must reveal a row absent from the current Boards projection. The surrounding initial-grid construction is not supplied, so I cannot establish that this is reachable with a genuinely cold workspace; I therefore do **not** classify it as a confirmed residual of historical group 7. It is an appropriate targeted runtime check before claiming all navigation side effects closed. 

## Rejection and preservation requirements

The narrow fixes should preserve these invariants:

* **Initial-save rejection:** retain every orphan temp byte and the live workspace; permit explicit retry without silently promoting the orphan.
* **Creation-lineage collision:** reject before altering fields, buffers, history, local task IDs, dependency identities, or protected-baseline arrays.
* **Duplicate canonical rows:** detection must be read-only. It must neither choose a conflicting value nor write generated projections to either row.
* **Summary access hold:** disable only normal navigation. Do not delete Summary metadata, baselines, allowances, Undo, or isolated test access.
* **Display and selection fixes:** must not commit cells, initialize fields, alter planning history, save, or access GitHub.

## Sustained-scroll investigation: source hypotheses only

I found no source basis for naming a causal performance defect.

Three bounded costs are visible:

1. On a cold cache, `GanttProjection.Create` calls `PlanFor`, which builds `ReadRows`, and then builds `ReadRows` again for presentation. That can increase entry/projection cost, but it does not establish sustained scrolling stalls.  
2. Recycled Summary person presenters allocate a new Grid and six TextBlocks on every `DataContextChanged`. Person selection also scans all contributions, allocates a new task-line array, and replaces the task list source. These are plausible isolated-Summary realization or keyboard-selection costs, not measured causes.  
3. Returning to Boards forces a grid `UpdateLayout`, followed in the active case by `ScrollIntoView` and a second `UpdateLayout`. This can explain transition jank, not sustained scrolling. The attached source wires `ScrollChanged` but does not include that handler, so its per-scroll work cannot be reviewed here. 

Runtime reproduction must distinguish continuous wheel/touch scrolling, keyboard selection movement, view-entry projection, and return-to-Boards layout. None of these hypotheses justifies lowering correctness guards or changing persistence behavior.

## Review limits

The bundle does not include the implementation of `DraftSnapshot.Copy`, the caller that sequences `PlanningIdentityOccupied` and `PromotePlanning`, `PlanningEngine`, `GanttView`, or the `ScrollChanged`/`ScrollSizeChanged` handlers. Those omissions prevent certification of their internal behavior. They do not weaken the confirmed findings above, which are reachable directly from the supplied methods and valid input states.

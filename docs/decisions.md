# Decisions

Record accepted choices and their rationale. Keep task progress and experimental findings in the owning Issues.

## Planning editor for GitHub Projects

Accepted 2026-10-05 under [#76](https://github.com/fukuda-yuki/gh-projects-boards/issues/76). The product replaces the planning work previously done with TFS 2017 + MS Project + the Excel add-in. GitHub Projects has no way to build a schedule from effort, assignees and predecessors, so the app provides that job and leaves general Issue work to GitHub.

- **Local editing with explicit publish.** The PMO edits a local copy and publishes adopted changes, as with MS Project and the TFS Office integration. This avoids a request per edit during weekly adjustment. Ordinary screens show changed-cell markers and the unpublished count; conflicts are resolved before publishing.
- **One central screen.** A plan sheet with a row-aligned Gantt, modeled on the MS Project Gantt Chart view, plus a people view modeled on Resource Usage and a single settings page. Project commands are **最新の情報に更新** and **発行**, the names used by the TFS Office integration. Columns backed by a GitHub field show the GitHub field name as the header, because the PMO recognizes fields by those names.
- **Person-hours and TFS work fields.** Effort is entered and shown in person-hours (人時); one working day is eight hours. Estimate is the original estimate, Remaining is what is left and drives scheduling, Actual is cumulative and counts for the task's current assignee.
- **A bounded subset of MS Project auto-scheduling.** Effort, the assignee's Project rate, the calendar and finish-to-start predecessors determine dates. A typed start date is kept as **開始日指定** (start no earlier than); **日程固定** keeps typed start and end. No per-task mode selection, lag, other link or constraint types, critical path, leveling or cost. Parity with MS Project is not a goal.
- **Inputs are persisted, dates are derived.** Open tasks with work that are not 日程固定 are calculated from their inputs; completed tasks, 日程固定 tasks and tasks without effort keep their GitHub dates. A calculated date that differs from GitHub is an unpublished change. The schedule is shown by day, so minute endpoints are not stored. Exact rules: [#78](https://github.com/fukuda-yuki/gh-projects-boards/issues/78).
- **Reproducible from GitHub plus settings.** Title and assignees (Issue), predecessors (`blockedBy`), hierarchy (sub-issues), Estimate/Remaining/Actual (Project number fields), Start/Target (Project date fields), and the added 開始日指定 (date) and 日程固定 (single select) fields hold the published plan. Calendar, rates and allowances are local settings that can be exported and imported as a file; unpublished edits and Undo stay local. Another data root with the same settings reproduces the plan by refreshing.
- **One status date.** 状況日 replaces the separate report date and replanning cutoff.
- **No MS Project import and no task creation by pasting from Excel.** Existing plans are not migrated by file. New tasks are imported from CSV into the local plan and published explicitly ([#82](https://github.com/fukuda-yuki/gh-projects-boards/issues/82)).
- **Local data starts fresh.** The new local format does not read previous checkpoints and leaves old files untouched, following the internal-tool policy in AGENTS.md.

Sources: [MS Project views](https://support.microsoft.com/en-gb/office/overview-of-project-views-6cb1dbcd-5cd5-4cc2-a878-aa365564266d), [Azure Boards capacity](https://learn.microsoft.com/en-us/azure/devops/boards/sprints/set-capacity?view=azure-devops), [Issue schema](https://docs.github.com/en/graphql/reference/issues), [Project schema](https://docs.github.com/en/graphql/reference/projects), [official holidays](https://www8.cao.go.jp/chosei/shukujitsu/gaiyou.html).

## Publishing

Publishing reads the Project once to detect fields changed both locally and on GitHub since the last refresh, checks the bound gh identity once, sends several updates per GraphQL request one request at a time, and verifies with one read afterwards. Use up to 50 aliases only for NUMBER/DATE-only field batches. Mixed fields/assignees and relationship additions/removals start at 10 aliases, a conservative default rather than a demonstrated safe GitHub capacity. Creation and Project addition remain at 10 aliases. Send requests strictly serially. On RESOURCE_LIMITS_EXCEEDED, halve the failed request down to one alias and retry only unsuccessful aliases; retain successful outcomes, apply the reduced ceiling to later requests in the same phase/attempt, and record actual request sizes. Project item position changes and sub-issue reprioritization use one alias per request because each move changes the ordered collection used by subsequent moves; field-update batch capacity is not evidence for position-update capacity. Retain partial/uncertain outcomes and verify them before another explicit publish. These are operation-specific sizes, not a general mutation limit.

Field value updates set an absolute value, so a failed or uncertain update is sent again by the next explicit publish. Issue creation is not idempotent: a new task keeps its local identity until a verified Issue exists, and an uncertain creation is checked against GitHub before any new attempt. Honor Retry-After and rate-limit reset headers; never send requests in parallel. Use `updateIssue`, assignee mutations, `updateProjectV2ItemFieldValue` / `clearProjectV2ItemFieldValue`, `addBlockedBy` / `removeBlockedBy`, sub-issue mutations, `updateProjectV2ItemPosition`, `createIssue` and `addProjectV2ItemById`; `createProjectV2Field` only when the PMO explicitly adds 開始日指定 or 日程固定 from settings. Undo after publishing creates new unpublished changes and never writes to GitHub by itself. Source: [GitHub API guidance](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).

### Throughput targets for #79

Use these engineering acceptance ceilings on a responsive github.com connection with the PMO's gh process-per-request transport, no rate-limit response, no conflict and no unrelated concurrent edits. Measure the product publisher; the baseline observations in #77 do not establish product acceptance. Retain complete pagination. The live fixture creates at most 100 marked seed Issues and 50 new Issues in each of three runs (250 total); refuse more than 300 planned creations. GitHub content-creation limits bound the live fixture size. Measure the resulting Project (normally about 105–255 items), retaining item/page counts and time per item/page; label the linear 1,000-task extrapolation explicitly. UI acceptance with 20 people and all configured fields remains separate.

| Operation | Product elapsed-time ceiling | Included boundary / diagnostic budget |
| --- | --- | --- |
| Refresh the complete sandbox Project | **60 s extrapolated to 1,000 tasks** | Record actual full-fetch/local-adoption time and item/page counts separately from the linear extrapolation. Per-item/page read rates use only that refresh operation's item-page request durations; publish samples do not report refresh rates. Visible dates/bars require ordinary-app validation. |
| Publish 300 field updates | **180 s** | Publish command accepted through one identity check, one full conflict read, all writes, one complete verification read, durable local adoption and updated UI; exclude only human review dwell. The write-request phase must be **45 s or less**. |
| Create 50 Issues and add them to the Project | **200 s** | Publish command accepted through the same preflight/readback/local-adoption boundary; exclude only human review dwell. Creation + membership reconciliation/add phase must be **65 s or less**. |

The full publish ceilings reserve two 60 s reads plus the write budget and 15 s for remaining orchestration. They are targets, not demonstrated 1,000-task results. Test 300 distinct cells per run: 100 owned Issues times three different existing NUMBER fields, with different values each run; do not satisfy the target by deduplicating the baseline's 100 cells written three times. Creation excludes optional assignee/dependency/hierarchy writes; measure those separately. Record failures and rate-limited runs separately rather than treating them as latency passes. For product acceptance retain three complete runs per workload and require each to meet its ceiling; report all timings and request counts.

Creation pacing for #79 spaces create-batch starts by one second per Issue in the preceding batch, so request time overlaps the interval and the first batch needs no artificial delay. Persist dispatch reservations and allow at most 60 Issue creation attempts in any rolling 60-second window, including across restart, batch-size changes and consecutive publishes on the same Project. Count uncertain attempts conservatively. Wait until both the start-to-start interval and rolling-window capacity permit dispatch; honor server backoff as well. This is a per-Project publisher bound, not an account-wide guarantee. The live setup uses the same pacing policy and hands its recent reservations to the product workload. Include product pacing in measured publish/write time and record setup pacing separately. Print the creation/request budget and estimated duration before any mutation. Cleanup remains one Issue per request with at least two seconds between deletes and independent baseline verification.

Basis: PMO Project 3 measurement with gh 2.100, 54–55 items: refresh 2.03–2.72 s; linear 1,000-item estimate 37–49 s includes fixed process costs and is not a measurement. For 300 writes, batches 1/10/25/50 took 216.1/43.7/34.6/30.2 s; 50 creates + adds at 10 aliases took 51.0 s including observation waits; about 458/5,000 hourly points for 446 requests, no primary/secondary throttle. Evidence belongs to [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77).

Built-in **Auto-add to project** may make an Issue a member before explicit add. Preserve successful aliases from partial responses; for the exact already-present add outcome, resolve the item by content identity in the bound Project. Every created Issue receives an explicit add; unresolved additions do not suppress later add batches. Confirm membership with at most four complete reads separated by one-second cancellable delays. If it remains absent, retain the created identity for the next publish; never recreate the Issue or discard successful siblings. Record visibility/read waits separately; the measurement tool's fixed observation waits are not a required product delay. Uncertain creation still follows the creation guard. Failed operations block only dependent operations; unrelated fields, relationships and positions continue when their Issue identities and memberships exist. Never-dispatched operations retain pending state and a readable reason. Relationship retries reconcile observed existing links before treating an already-existing response as success. `RESOURCE_LIMITS_EXCEEDED` is distinct from rate throttling. Ten-alias `deleteIssue` hit this limit while create/add at 10 and measured NUMBER-only field updates up to 50 did not; cleanup uses one Issue per request and rediscovers survivors. Do not infer higher safe sizes or general GraphQL capacity from these observations.

## Desktop platform

Use **C# + .NET 10** with a **WinUI 3 / Windows App SDK** window as the shell. Application rules, GitHub access and scheduling remain in one UI-independent Core library. Render the plan sheet and Gantt with native WinUI controls, as selected below under [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77).

Windows App SDK is pinned to `1.8.260804001` in the app project. The development target is `net10.0-windows10.0.26100.0`, x64, with minimum platform 19041. The development executable is unpackaged and self-contained to make ordinary-executable checks explicit. These are build settings, not a final supported-device or distribution promise.

Select dependencies only for demonstrated requirements and acceptable unconditional commercial terms.

## Plan sheet and Gantt rendering (#77)

Choose **native WinUI** for the PMO's initial planning and weekly task maintenance with the whole schedule in view. Keep one virtualized vertical viewport for sheet cells, bars and predecessor arrows. Native cell automation and a usable local UI-test runtime outweigh the web candidate's better initial density; repair density in the selected implementation rather than maintaining two renderers. Comparison against an agreed plan is a separate, optional backlog feature under #93.

| #77 criterion | Decision basis and remaining contract |
| --- | --- |
| Edit to dates/bars within 0.2 s; scrolling | PMO's 1,000-row native samples were 23.4–45.6 ms to Rendered versus web 10.8–69.0 ms to the second animation frame including the bridge. Both fit the prototype budget; their boundaries differ and neither proves physical presentation or the real scheduler's speed. Both showed no blanks in six fast-wheel captures. Long sessions and broader scroll workloads remain unverified. |
| Japanese IME | Both prototypes separate composition confirmation from cell commit in code; physical-key confirmation, cancellation and reconversion remain mandatory #78 checks. Native TextBox keeps composition in the existing native input path. No physical IME pass is claimed. |
| Range selection, rectangular copy/paste, fill, Ctrl+D | Neither prototype supplies these. Adapt the existing WinUI selection/editor and transaction behavior verified through PlanSheetEditingTests / PlanSheetHostedTests to the new plan identities; do not import the legacy workspace or Apply journal. Web would need new JS selection/drag/clipboard behavior plus a C# bridge. |
| Arrows, row alignment and presentation | Both prototypes draw FS arrows and align sheet/chart rows through one scroll surface. Native still needs a fixed header, unclipped ID and compact readable rows; web had a sticky header and denser rows. #78 also owns day/week/month scales. Gantt drag editing is excluded. |
| UI test approach | Native cells expose AutomationIds and work in the mounted WinUI host and PMO ordinary executable. Web exposed zero external UIA Edit controls and relied on ExecuteScriptAsync; renderer/GPU failures also prevented local web UI execution. Select native event/control tests plus public UIA for ordinary-app journeys. |
| Effort for #78 | Native reuses one C# input/test stack and the existing range-operation contracts. Both still require model binding, columns, selected/editing state, operation Undo and zoom. Native must repair layout and validate recycling/focus; web would additionally require bridge ordering, accessible cells and a second input implementation. This is an integration-effort judgment, not a delivery-time promise. |
| License terms | Use the pinned Windows App SDK 1.8.260804001 / resolved WinUI 1.8.260803003 and original rendering code; no paid grid or company-size/revenue condition is introduced. Cached SDK/WinUI license.txt permits Windows development/testing and bundled binary redistribution subject to its terms/notices; distribution review remains separate. The alternative used original JS and WebView2 SDK 1.0.3179.45 with permissive attribution terms, but Evergreen runtime redistribution terms were not verified offline. |

The web review also found invalid text/error loss across other edits or scrolling, and stale typed Start values against calculated dates/bars. Those are additional reasons not to retain the web input path; deleting it does not establish that those behaviors were fixed.

The product plan sheet adopts native virtualized rows, TextBox editors, aligned arrows and frame instrumentation, connected to the real Core plan scheduler and operation history. No prototype folder, preview launch route or disposable scheduler remains. Use the product publisher live proof for throughput verification; its execution contract is in [tests/README.md](../tests/README.md#planning-editor-publication-79).

For #78, put scheduling/validation/Undo combinations in Core tests, then mount real native views and drive focus, text and commands to assert pending input, committed values, dates and bar geometry. Keep row/field AutomationIds for external UIA/FlaUI and use the ordinary executable with isolated fake gh for representative publish journeys. Physical-key Japanese IME, actual clipboard transport, focus under scrolling, Light/Dark/High Contrast, readable density and visible-frame performance require their own native/desktop checks. DOM injection is not a second test path. Commands and fixture boundaries are in [tests/README.md](../tests/README.md#plan-sheet-and-gantt-78).

## Testing

Adopt **logic-layer unit tests > UI-layer integration tests > E2E tests** as the testing priority. Put rule combinations and failure branches in the lowest reliable boundary, then verify presentation wiring with real UI integration. Keep E2E for representative whole-application workflows. This makes lower-layer behavior the primary proof rather than relying on a whole-app journey to diagnose every rule or state. It is not a numerical quota or a prohibition on E2E, physical IME, full regression or live validation.

Classify by tested scope rather than automation technology or process layout. UI integration checks a bounded collaboration of real UI components, events/commands, presentation state and rendered results; E2E checks a representative workflow through the application's principal layers to an explicit endpoint. Neither UI Automation nor fake gh decides that classification. Record actual collaborators, substitutions and assertions; do not call a broad workflow UI integration merely because it ends at a fake service.

Use NUnit for the existing logic, adapter integration and desktop suites, and FlaUI UIA3 for desktop automation. Exact versions live in the project files. A dedicated UI test host and a direct or external driver are execution choices, not test levels. Inspect existing cases and reuse adequate mechanisms before introducing a host or another project for a concrete coverage gap. This avoids unnecessary infrastructure without treating project names as evidence of coverage or its absence.

Core tests exercise real application collaborators. A unit of behavior need not mean one class surrounded by mocks. The fake gh executable controls the external process boundary and has no network fallback; actual adapter/process and isolated-storage integration remain covered. UI integration keeps the asserted view/event/presentation/binding collaboration real, using controlled dependencies outside that scope. ViewModel-only assertions do not establish control wiring. Whole-application desktop journeys retain the ordinary executable and public UI Automation contract.

Prefer behavior and state assertions to interaction assertions. Assert the observable result, state change or error of a real collaboration rather than call counts, call order or private state; interaction assertions add observation points that break on behavior-preserving refactoring. Cover each behavior at one boundary: a collaborator is tested for its own contract, not for its caller's behavior, because repeating the caller's viewpoint produces false failures when the collaborator changes internally. Thin CRUD is covered once at the integration boundary, while logic-unit cases exist for real branching, validation, ordering, identity, conflict or failure rules. Case names state the condition and expected outcome, case bodies follow arrange/act/assert, and matrices use table-driven cases. No coverage percentage or suite-size target applies; the user reviews the proposed behavior list for necessity, duplication and layer assignment. A diagnostic line/branch HTML report for `GhProjectsBoards.Core` is produced from `GhProjectsBoards.Tests` with Microsoft Code Coverage and ReportGenerator and published to GitHub Pages on `main`. It is not an acceptance gate.

Keep test scope, driver/runtime mechanics and environment/evidence requirements distinct. Physical IME, native focus, clipboard/picker, process lifetime and live GitHub may require real facilities but do not automatically make a test E2E. App-level E2E with fake gh is not real-GitHub acceptance, and focused live adapter verification is not necessarily application E2E. Preserve performance and human acceptance separately.

Select execution for a stated risk or acceptance need; permission and historical suite inventories are not blanket execution requirements. Reclassification requires case-level scope evidence. Discovery and compilation do not establish runtime acceptance. The [test policy](../tests/README.md) owns selection, reporting and migration rules.

## Native table-input lifecycle

This applies to the current WinUI grid and the native plan renderer selected in #77.

Use an input-ready native WinUI TextBox for the cell editor, preparing focus and replacement selection during cell selection. Keep application Selected/Editing state and committed values separate from native editability. Start application editing on actual composition/text changes or F2, rather than changing read-only state on the first character. This lets the native IME own composition without replaying input or using private APIs.

Verify the method in the ordinary plan sheet. Its behavioral contract includes direct and F2 input, range selection, separate draft/committed values, IME confirmation versus cell commit, cancellation and reconversion. Full grid layout, virtualization, paste/Undo and keyboard edge policies still require their own implementation and validation. The selected method does not introduce a TableView dependency.

## Authentication and process ownership

Use stored gh authentication. Exclude all four gh token environment overrides from children, expose only safe authentication metadata and require recognized keyring storage before writes. Plaintext and unknown storage remain diagnosable without permitting writes.

Use `ArgumentList`, UTF-8 JSON stdin, explicit hostname/target, asynchronous execution, a 30-second process timeout, cancellation and structured results. Bind stable viewer identity and recheck it before each publish. Never resend a write without an explicit publish; Issue creation follows the duplicate guard above.

These rules avoid a second credential owner and keep issue content as data. See [gh environment variables](https://cli.github.com/manual/gh_help_environment), [login](https://cli.github.com/manual/gh_auth_login), [auth status](https://cli.github.com/manual/gh_auth_status) and [specification](spec.md).

Preflight cannot atomically prevent another process from switching gh authentication. Connection state is in memory. Persistent workspaces and company-environment verification have their own acceptance criteria.

## Decision ownership

Use one versioned JSON planning checkpoint per host/stable viewer/Project with existing .NET libraries. Reuse checked temporary writes, write-through flush, same-directory replacement and locking, with session save serialization and optimistic durable-revision checks. This is a local store, not multi-device synchronization. Its contents are unencrypted private local work; preserve last-good files and diagnose corruption instead of resetting them.

| Topic | Owner |
| --- | --- |
| Native rendering choice and initial publish targets | #77; adopted above |
| Workspace shell, Project switching, settings and column mapping | #81 |
| Plan sheet, scheduling rules and status date | #78 |
| Refresh, publish, conflicts and creation guard | #79 |
| People view and allowance | #80 |
| CSV import of new tasks | #82 |
| Distribution, notices, signing and company-environment validation | Deferred under #76 (previous findings in closed #13) |

Required dependencies must not require paid licensing or company-size/revenue eligibility. Local development permission is distinct from binary redistribution permission. Resolve the actual output-to-license/notice manifest before a release; build output alone is not approval to distribute it.

The parent Windows App SDK and its DWrite/Widgets packages have different redistribution wording. Their applicability to the app-local payloads remains unresolved and blocks distribution. Preserve the exact [terms and package provenance](dependencies.md).

### Core scheduling representation (#78)

Use the UI-independent `PlanEditor` namespace. Keep nullable day inputs and baseline dates separate from derived date cells; working-hour endpoints exist only inside recalculation. Reuse the bundled holiday reader and validated holiday CSV parser without extending legacy planning metadata.

The [current scheduling contract](spec.md#current-planning-editor-scheduling-contract-78) keeps ordinary zero-effort entry reachable as a milestone: an open task with no planned/performed positive work and at least one zero effort value is a milestone, including Estimate entry that fills Remaining with zero. Closed work, or zero Remaining after positive planned/performed work, is complete. Fixed dates still take precedence over calculating a milestone.

Isolate bad refreshed effort/dates to row warnings and retained GitHub dates, excluding their dependency endpoints. Propagate isolation to summaries containing invalid children so incomplete roll-ups cannot be published as calculated values. Pure edit transformations refuse negative effort and inverted typed pairs whose dates are kept; moving an automatic start past an old end remains valid because its end recalculates; structural errors, invalid settings and cycles still reject the calculation. This preserves the rest of the plan without silently repairing another person's data.

Use exact rational working hours internally, constructed from decimal inputs with BigInteger arithmetic from the standard library. Integer-minute rounding would change subminute effort; tolerances would risk changing true near-boundary work. Exact arithmetic preserves working-interval endpoints across chains and rate changes without either policy or a new dependency. The optional explicit status date overrides a caller-supplied today on each calculation; the local document persists that optional choice.

Preserve missing effort in summaries instead of inventing zero totals. Summary predecessors propagate to descendants. Rates are (0, 100] percent; zero capacity is rejected rather than creating a schedule that cannot finish. Retained in-progress starts are historical, while remaining work starts no earlier than the effective 状況日. Deterministic reason tie-breaking and warning strings are owned by the scheduling contract.

### Local document and durability (#78 / #79 / #81)

Use an immutable baseline/current document and bounded before/after operation patches (200 steps) in a fresh `PlanningEditor/v1` folder. Reuse existing host/viewer/Project identity and root resolution, but never read the old checkpoint format. Patch history keeps ordinary 1,000-task edits from multiplying the entire plan by the history depth. The [local document contract](spec.md#current-local-plan-document-operations-and-storage-78--79--81) defines commands, markers and settings import/export.

Autosave runs in one background loop and coalesces pending revisions while retaining their operation history. Memory becomes current before disk completion; the save task and explicit flush own durability. Atomic replacement follows the existing stores' mechanics, with a per-Project lock and expected file fingerprint to refuse competing or corrupt source replacement. Keep failed state in memory and surface only actionable save failures to the UI.

Settings exports are portable and account-independent. Retain unfamiliar people/field identities with warnings instead of silently changing imported intent. Do not persist UI layout. Row order and hierarchy are independent: moving selected rows preserves parent identities; indent/outdent changes parent identities without an implicit reorder. Core commands apply prepared inputs; CSV parsing, network refresh/publish and UI orchestration remain at their respective boundaries.

## CSV import identity and validation (#82)

Use Japanese headers with optional empty values and file-local keys; reference loaded Issues by repository and number. Resolve repositories and assignable logins through scoped read-only API calls before accepting any row. Reuse the scheduler's cycle validation and expose its involved task identities to map errors to file lines; do not maintain a second scheduling graph in the importer.

Keep the original-byte SHA-256 on imported rows instead of introducing a separate import ledger. Any remaining row from the same bytes requires repeat confirmation. This gives persistence, publish promotion and Undo the same ownership as the rows, while avoiding semantic matching of task titles. File keys are transient. Add newly resolved people with the existing default 100% rate in the same operation. A changed or re-encoded file is intentionally not considered an exact repeat.

CSV repository values require multi-repository creation within the selected Project. Preserve #79 ordering, batching and pacing; map each creation placeholder and uncertain-creation lookup to its own preflighted destination. This does not enable moving existing Issues or cross-Project publication.

## People load allocation (#80)

Automatic tasks use the scheduler's remaining-work allocation, including partial working days. Fixed tasks keep their specified dates and distribute Remaining evenly across available working days in that interval: the fixed interval is authoritative, so this view does not front-load or reschedule it. If no working day exists, the work remains explicitly unallocated. Completed tasks contribute no future load. Unassigned and multiply assigned tasks stay in separate groups with unknown capacity rather than duplicating hours across people. Weeks run Monday–Sunday and months use full calendar boundaries; aggregate hours and capacity before computing a percentage.

## Planning editor publish recovery (#79)

- Removed Project members remain unavailable conflicts rather than silently discarding local work or recreating Issues. Explicit discard or copy-to-new resolves their disposition.
- Persist a creation marker in the Issue body and reconcile against a complete repository Issue traversal, including closed Issues; search-index absence is not sufficient evidence to recreate an uncertain Issue.
- Keep new-pipeline progress inside the scoped planning checkpoint. Do not read or migrate the legacy Apply journal.
- Share one authenticated connection lease across the serial publish operation; retain host/viewer checks on contributing reads.

Project item order remains the displayed row order. Native sub-issue sibling order is separately retained for B/L/R comparison and review. A remote-only sibling reorder reorders those sibling slots locally; concurrent divergent sibling orders require resolution. Publishing aligns sibling order with the reviewed planning order without introducing an independent editable hierarchy-order column.

## Publish review lifetime (#79)

Use an in-workspace review instead of a modal confirmation wizard. A PMO can close it during a long publish and inspect the read-only sheet; the workspace owns the operation and keeps stage text visible. Switching Project or changing inputs waits until publication finishes. Window close cancels and awaits the operation, letting the existing Core checkpoint retain uncertainty. Restart requires an explicit refresh or publish; it never silently resumes network writes. This gives a single confirmation point and preserves the existing creation guard without introducing a second UI journal.

## Offline evaluation dataset

Generate synthetic remote dates with the same Core scheduler and settings used by the app, then seed both the fake endpoint and isolated local document. This avoids presenting automatic date corrections as evaluator edits. Fix the sample status date to 2026-10-05 for repeatable restarts; Resume preserves the local document and fake remote state. The existing test executable supplies fake gh through the child-only PATH, so the product needs no evaluation mode or alternate renderer.

Sub-issue reprioritization uses one move per request, like Project item positioning. Its moves are order-dependent; batching with partial-alias retries can produce a different final order.

## Version-shaped evaluation workload (#89)

One shared WBS defines 40 requirements with 25 executable V-model tasks each; the 40 summary Issues are additional rows, not part of the 1,000-task count. The live registered state begins with R01–R39 (1,014 Issues). R40's 26 Issues arrive in week three, giving 1,040 Issues. Reuse existing sandbox Issues and create only confirmed missing WBS identities. Registration establishes titles, hierarchy and order, leaving effort, assignees, predecessors and phase-start inputs for the PMO in the ordinary app. A fresh intake starts with zero unpublished tasks. Never publish the abandoned local creation checkpoint.

The offline planned state uses the same WBS, 20 synthetic people and demand near available capacity across the working plan, with specific overloads and an allowance overrun that the PMO can identify and correct. Capacity and load are measured from the generated schedule rather than inferred from fixture size. The fixed status date makes restarts reproducible. Native sibling order and calculated dates are seeded on both sides of the offline boundary; initial publication differences are zero. Debug evaluation uses Debug binaries for both the app and fake gh. Real-GitHub write evidence uses only assignable sandbox users; it does not establish 20-person live assignment behavior.

## Inaccessible Project membership (#79 / #81)

Project item totalCount can include items that are not returned to the viewer. A valid cursor chain ending at hasNextPage false completes item traversal; the nonnegative difference from delivered unique items counts as inaccessible, together with returned REDACTED items. Do not create placeholder tasks or infer clears from these observations. Other connection completeness checks and item identity/cursor/error validation remain strict. Settings and live-proof read evidence expose the inaccessible count.

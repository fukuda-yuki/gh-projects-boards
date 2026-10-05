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
- **No MS Project import and no task creation by pasting from Excel.** Existing plans are not migrated by file. New tasks can be imported from a CSV file once the plan sheet and publishing exist ([#82](https://github.com/fukuda-yuki/gh-projects-boards/issues/82)).
- **Local data starts fresh.** The new local format does not read previous checkpoints and leaves old files untouched, following the internal-tool policy in AGENTS.md.

Sources: [MS Project views](https://support.microsoft.com/en-gb/office/overview-of-project-views-6cb1dbcd-5cd5-4cc2-a878-aa365564266d), [Azure Boards capacity](https://learn.microsoft.com/en-us/azure/devops/boards/sprints/set-capacity?view=azure-devops), [Issue schema](https://docs.github.com/en/graphql/reference/issues), [Project schema](https://docs.github.com/en/graphql/reference/projects), [official holidays](https://www8.cao.go.jp/chosei/shukujitsu/gaiyou.html).

## Publishing

Publishing reads the Project once to detect fields changed both locally and on GitHub since the last refresh, checks the bound gh identity once, sends several updates per GraphQL request one request at a time, and verifies with one read afterwards. The current per-field dispatch with repeated identity checks costs about two seconds per field, which makes weekly publishing impractical. Use up to 50 aliases per field-update request, 10 per createIssue request and 10 per addProjectV2ItemById request; send requests serially. These are operation-specific sizes, not a general mutation limit.

Field value updates set an absolute value, so a failed or uncertain update is sent again by the next explicit publish. Issue creation is not idempotent: a new task keeps its local identity until a verified Issue exists, and an uncertain creation is checked against GitHub before any new attempt. Honor Retry-After and rate-limit reset headers; never send requests in parallel. Use `updateIssue`, assignee mutations, `updateProjectV2ItemFieldValue` / `clearProjectV2ItemFieldValue`, `addBlockedBy` / `removeBlockedBy`, sub-issue mutations, `updateProjectV2ItemPosition`, `createIssue` and `addProjectV2ItemById`; `createProjectV2Field` only when the PMO explicitly adds 開始日指定 or 日程固定 from settings. Undo after publishing creates new unpublished changes and never writes to GitHub by itself. Source: [GitHub API guidance](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).

### Throughput targets for #79

Use these engineering acceptance ceilings on a responsive github.com connection with the PMO's gh process-per-request transport, no rate-limit response, no conflict and no unrelated concurrent edits. Measure the ordinary product; the standalone tool is the baseline, not acceptance of the new publish pipeline. Use 1,000 Issue tasks, 20 people, all configured plan fields and predecessor/parent data; retain complete pagination. The creation case starts with 950 existing tasks and ends with 1,000.

| Operation | Product elapsed-time ceiling | Included boundary / diagnostic budget |
| --- | --- | --- |
| Refresh 1,000 tasks | **60 s** | Command accepted through complete fetch, local reconciliation/recalculation and updated visible dates/bars. |
| Publish 300 field updates | **180 s** | Publish command accepted through one identity check, one full conflict read, all writes, one complete verification read, durable local adoption and updated UI; exclude only human review dwell. The write-request phase must be **45 s or less**. |
| Create 50 Issues and add them to the Project | **200 s** | Publish command accepted through the same preflight/readback/local-adoption boundary; exclude only human review dwell. Creation + membership reconciliation/add phase must be **65 s or less**. |

The full publish ceilings reserve two 60 s reads plus the write budget and 15 s for remaining orchestration. They are targets, not demonstrated 1,000-task results. Test 300 actual number/date field changes; do not satisfy the target by deduplicating the baseline's 100 cells written three times. Creation excludes optional assignee/dependency/hierarchy writes; measure those separately. Record failures and rate-limited runs separately rather than treating them as latency passes. For product acceptance retain three complete runs per workload and require each to meet its ceiling; report all timings and request counts.

Basis: PMO Project 3 measurement with gh 2.100, 54–55 items: refresh 2.03–2.72 s; linear 1,000-item estimate 37–49 s includes fixed process costs and is not a measurement. For 300 writes, batches 1/10/25/50 took 216.1/43.7/34.6/30.2 s; 50 creates + adds at 10 aliases took 51.0 s including observation waits; about 458/5,000 hourly points for 446 requests, no primary/secondary throttle. Evidence belongs to [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77).

Built-in **Auto-add to project** may make an Issue a member before explicit add. Preserve successful aliases from partial responses; for the exact already-present add outcome, resolve the item by content identity in the bound Project. Never recreate the Issue or discard successful siblings. Record visibility/read waits separately; the measurement tool's fixed observation waits are not a required product delay. Uncertain creation still follows the creation guard; other errors stop and preserve evidence. `RESOURCE_LIMITS_EXCEEDED` is distinct from rate throttling. Ten-alias `deleteIssue` hit this limit while create/add at 10 and field updates up to 50 did not; cleanup uses one Issue per request and rediscovers survivors. Do not infer higher safe sizes or general GraphQL capacity from these observations.

## Desktop platform

Use **C# + .NET 10** with a **WinUI 3 / Windows App SDK** window as the shell. Application rules, GitHub access and scheduling remain in one UI-independent Core library. Render the plan sheet and Gantt with native WinUI controls, as selected below under [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77).

Windows App SDK is pinned to `1.8.260804001` in the app project. The development target is `net10.0-windows10.0.26100.0`, x64, with minimum platform 19041. The development executable is unpackaged and self-contained to make ordinary-executable checks explicit. These are build settings, not a final supported-device or distribution promise.

Select dependencies only for demonstrated requirements and acceptable unconditional commercial terms.

## Plan sheet and Gantt rendering (#77)

Choose **native WinUI** for the PMO's weekly task editing and schedule comparison. Keep one virtualized vertical viewport for sheet cells, bars and predecessor arrows. Native cell automation and a usable local UI-test runtime outweigh the web candidate's better initial density; repair density in the selected implementation rather than maintaining two renderers.

| #77 criterion | Decision basis and remaining contract |
| --- | --- |
| Edit to dates/bars within 0.2 s; scrolling | PMO's 1,000-row native samples were 23.4–45.6 ms to Rendered versus web 10.8–69.0 ms to the second animation frame including the bridge. Both fit the prototype budget; their boundaries differ and neither proves physical presentation or the real scheduler's speed. Both showed no blanks in six fast-wheel captures. Long sessions and broader scroll workloads remain unverified. |
| Japanese IME | Both prototypes separate composition confirmation from cell commit in code; physical-key confirmation, cancellation and reconversion remain mandatory #78 checks. Native TextBox keeps composition in the existing native input path. No physical IME pass is claimed. |
| Range selection, rectangular copy/paste, fill, Ctrl+D | Neither prototype supplies these. Adapt the existing WinUI selection/editor and transaction behavior evidenced by BulkEditingTests / BulkEditingHostedTests to the new plan identities; do not import the legacy workspace or Apply journal. Web would need new JS selection/drag/clipboard behavior plus a C# bridge. |
| Arrows, row alignment and presentation | Both prototypes draw FS arrows and align sheet/chart rows through one scroll surface. Native still needs a fixed header, unclipped ID and compact readable rows; web had a sticky header and denser rows. #78 also owns day/week/month scales. Gantt drag editing is excluded. |
| UI test approach | Native cells expose AutomationIds and work in the mounted WinUI host and PMO ordinary executable. Web exposed zero external UIA Edit controls and relied on ExecuteScriptAsync; renderer/GPU failures also prevented local web UI execution. Select native event/control tests plus public UIA for ordinary-app journeys. |
| Effort for #78 | Native reuses one C# input/test stack and the existing range-operation contracts. Both still require model binding, columns, selected/editing state, operation Undo and zoom. Native must repair layout and validate recycling/focus; web would additionally require bridge ordering, accessible cells and a second input implementation. This is an integration-effort judgment, not a delivery-time promise. |
| License terms | Use the pinned Windows App SDK 1.8.260804001 / resolved WinUI 1.8.260803003 and original rendering code; no paid grid or company-size/revenue condition is introduced. Cached SDK/WinUI license.txt permits Windows development/testing and bundled binary redistribution subject to its terms/notices; distribution review remains separate. The alternative used original JS and WebView2 SDK 1.0.3179.45 with permissive attribution terms, but Evergreen runtime redistribution terms were not verified offline. |

The web review also found invalid text/error loss across other edits or scrolling, and stale typed Start values against calculated dates/bars. Those are additional reasons not to retain the web input path; deleting it does not establish that those behaviors were fixed.

Retain `Prototypes/WinUi/NativePrototype.cs` as the #78 rendering base: virtualized rows, native editors with cell-owned invalid input, date/bar refresh and aligned arrows. The native-only preview window, frame metrics, 1,000-row `PrototypePlan` and their tests remain solely as its executable rendering fixture; the 8-hour/calendar-day calculation is disposable and must be replaced when #78 connects the real Core plan. They are not a product scheduler or another app. No web renderer, HTML asset, web launch route or web-only tests remain. Keep the standalone sandbox measurement tool until #79 is measured through the ordinary product; its execution contract is in [tests/README.md](../tests/README.md#sandbox-throughput-measurement).

For #78, put scheduling/validation/Undo combinations in Core tests, then mount real native views and drive focus, text and commands to assert pending input, committed values, dates and bar geometry. Keep row/field AutomationIds for external UIA/FlaUI and use the ordinary executable with isolated fake gh for representative publish journeys. Physical-key Japanese IME, actual clipboard transport, focus under scrolling, Light/Dark/High Contrast, readable density and visible-frame performance require their own native/desktop checks. DOM injection is not a second test path. Commands and fixture boundaries are in [tests/README.md](../tests/README.md#native-plan-rendering-base).

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

Retain the method in the ordinary app's explicit input-check window. Its behavioral contract includes direct and F2 input, range selection, separate draft/committed values, IME confirmation versus cell commit, cancellation and reconversion. Full grid layout, virtualization, paste/Undo and keyboard edge policies still require their own implementation and validation. The selected method does not introduce a TableView dependency.

## Authentication and process ownership

Use stored gh authentication. Exclude all four gh token environment overrides from children, expose only safe authentication metadata and require recognized keyring storage before writes. Plaintext and unknown storage remain diagnosable without permitting writes.

Use `ArgumentList`, UTF-8 JSON stdin, explicit hostname/target, asynchronous execution, a 30-second process timeout, cancellation and structured results. Bind stable viewer identity and recheck it before each publish. Never resend a write without an explicit publish; Issue creation follows the duplicate guard above.

These rules avoid a second credential owner and keep issue content as data. See [gh environment variables](https://cli.github.com/manual/gh_help_environment), [login](https://cli.github.com/manual/gh_auth_login), [auth status](https://cli.github.com/manual/gh_auth_status) and [specification](spec.md).

Preflight cannot atomically prevent another process from switching gh authentication. Connection state is in memory. Persistent workspaces and company-environment verification have their own acceptance criteria.

## Issue #4 registration storage

Use existing .NET `System.Text.Json` and filesystem APIs, without a new dependency, for a versioned per-user registration/settings and currently supported read-cache format. One atomic record per normalized host/viewer/Project contains settings and snapshot together. A root writer lock, flushed and verified temporary write, and same-directory replacement protect the last good record. Backups are explicit recovery material, never authenticated sessions or automatically selected snapshots.

This is the retained legacy registration format. Migrated profiles use the authoritative checkpoint for registrations, drafts, Undo, shared Issue work, planning and Apply history. The unencrypted location, sensitive-data implications and local deletion/recovery rules are documented in [README](../README.md#local-registration-storage).

Discovery uses the current official [Repository.projectsV2](https://docs.github.com/en/graphql/reference/repos#repository) linked-Project relationship, [ProjectV2.repositories and owner connections](https://docs.github.com/en/graphql/reference/projects) and [Project API guidance](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects). Repository association does not change Project ownership or item scope.

## Decision ownership

Use one versioned JSON draft/checkpoint record per host/stable viewer with existing .NET libraries. Reuse checked temporary writes, write-through flush, same-directory replacement and locking, with session save serialization and optimistic durable-revision checks. This is a local store, not multi-device synchronization. Its contents are unencrypted private local work; preserve last-good files and diagnose corruption instead of resetting them.

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

Use the independent `PlanEditor` namespace alongside legacy planning until its screens are replaced. Keep nullable day inputs and baseline dates separate from derived date cells; working-hour endpoints exist only inside recalculation. Reuse the bundled holiday reader and validated holiday CSV parser without extending legacy planning metadata. Their neutral holiday contracts can move when legacy planning is removed.

The [current scheduling contract](spec.md#current-planning-editor-scheduling-contract-78) keeps ordinary zero-effort entry reachable as a milestone: an open task with no planned/performed positive work and at least one zero effort value is a milestone, including Estimate entry that fills Remaining with zero. Closed work, or zero Remaining after positive planned/performed work, is complete. Fixed dates still take precedence over calculating a milestone.

Isolate bad refreshed effort/dates to row warnings and retained GitHub dates, excluding their dependency endpoints. Propagate isolation to summaries containing invalid children so incomplete roll-ups cannot be published as calculated values. Pure edit transformations refuse negative effort and inverted typed pairs whose dates are kept; moving an automatic start past an old end remains valid because its end recalculates; structural errors, invalid settings and cycles still reject the calculation. This preserves the rest of the plan without silently repairing another person's data.

Use exact rational working hours internally, constructed from decimal inputs with BigInteger arithmetic from the standard library. Integer-minute rounding would change subminute effort; tolerances would risk changing true near-boundary work. Exact arithmetic preserves working-interval endpoints across chains and rate changes without either policy or a new dependency. The optional explicit status date overrides a caller-supplied today on each calculation; persistence of the optional choice belongs to Phase 3.

Preserve missing effort in summaries instead of inventing zero totals. Summary predecessors propagate to descendants. Rates are (0, 100] percent; zero capacity is rejected rather than creating a schedule that cannot finish. Retained in-progress starts are historical, while remaining work starts no earlier than the effective 状況日. Deterministic reason tie-breaking and warning strings are owned by the scheduling contract.
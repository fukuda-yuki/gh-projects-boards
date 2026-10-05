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

Publishing reads the Project once to detect fields changed both locally and on GitHub since the last refresh, checks the bound gh identity once, sends several updates per GraphQL request one request at a time, and verifies with one read afterwards. The current per-field dispatch with repeated identity checks costs about two seconds per field, which makes weekly publishing impractical. The batch size is set from the measurement in [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77).

Field value updates set an absolute value, so a failed or uncertain update is sent again by the next explicit publish. Issue creation is not idempotent: a new task keeps its local identity until a verified Issue exists, and an uncertain creation is checked against GitHub before any new attempt. Honor Retry-After and rate-limit reset headers; never send requests in parallel. Use `updateIssue`, assignee mutations, `updateProjectV2ItemFieldValue` / `clearProjectV2ItemFieldValue`, `addBlockedBy` / `removeBlockedBy`, sub-issue mutations, `updateProjectV2ItemPosition`, `createIssue` and `addProjectV2ItemById`; `createProjectV2Field` only when the PMO explicitly adds 開始日指定 or 日程固定 from settings. Undo after publishing creates new unpublished changes and never writes to GitHub by itself. Source: [GitHub API guidance](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).

## Desktop platform

Use **C# + .NET 10** with a **WinUI 3 / Windows App SDK** window as the shell. Application rules, GitHub access and scheduling remain in one UI-independent Core library. The plan sheet and Gantt are rendered either with WinUI controls or with web components hosted in WebView2; [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77) decides by comparing input (including Japanese IME), scale, paste/fill, Gantt interaction, build effort and license terms.

Windows App SDK is pinned to `1.8.260804001` in the app project. The development target is `net10.0-windows10.0.26100.0`, x64, with minimum platform 19041. The development executable is unpackaged and self-contained to make ordinary-executable checks explicit. These are build settings, not a final supported-device or distribution promise.

Select dependencies only for demonstrated requirements and acceptable unconditional commercial terms.

## Testing

Adopt **logic-layer unit tests > UI-layer integration tests > E2E tests** as the testing priority. Put rule combinations and failure branches in the lowest reliable boundary, then verify presentation wiring with real UI integration. Keep E2E for representative whole-application workflows. This makes lower-layer behavior the primary proof rather than relying on a whole-app journey to diagnose every rule or state. It is not a numerical quota or a prohibition on E2E, physical IME, full regression or live validation.

Classify by tested scope rather than automation technology or process layout. UI integration checks a bounded collaboration of real UI components, events/commands, presentation state and rendered results; E2E checks a representative workflow through the application's principal layers to an explicit endpoint. Neither UI Automation nor fake gh decides that classification. Record actual collaborators, substitutions and assertions; do not call a broad workflow UI integration merely because it ends at a fake service.

Use NUnit for the existing logic, adapter integration and desktop suites, and FlaUI UIA3 for desktop automation. Exact versions live in the project files. A dedicated UI test host and a direct or external driver are execution choices, not test levels. Inspect existing cases and reuse adequate mechanisms before introducing a host or another project for a concrete coverage gap. This avoids unnecessary infrastructure without treating project names as evidence of coverage or its absence.

Core tests exercise real application collaborators. A unit of behavior need not mean one class surrounded by mocks. The fake gh executable controls the external process boundary and has no network fallback; actual adapter/process and isolated-storage integration remain covered. UI integration keeps the asserted view/event/presentation/binding collaboration real, using controlled dependencies outside that scope. ViewModel-only assertions do not establish control wiring. Whole-application desktop journeys retain the ordinary executable and public UI Automation contract.

Prefer behavior and state assertions to interaction assertions. Assert the observable result, state change or error of a real collaboration rather than call counts, call order or private state; interaction assertions add observation points that break on behavior-preserving refactoring. Cover each behavior at one boundary: a collaborator is tested for its own contract, not for its caller's behavior, because repeating the caller's viewpoint produces false failures when the collaborator changes internally. Thin CRUD is covered once at the integration boundary, while logic-unit cases exist for real branching, validation, ordering, identity, conflict or failure rules. Case names state the condition and expected outcome, case bodies follow arrange/act/assert, and matrices use table-driven cases. No coverage percentage or suite-size target applies; the user reviews the proposed behavior list for necessity, duplication and layer assignment. A diagnostic line/branch HTML report for `GhProjectsBoards.Core` is produced from `GhProjectsBoards.Tests` with Microsoft Code Coverage and ReportGenerator and published to GitHub Pages on `main`. It is not an acceptance gate.

Keep test scope, driver/runtime mechanics and environment/evidence requirements distinct. Physical IME, native focus, clipboard/picker, process lifetime and live GitHub may require real facilities but do not automatically make a test E2E. App-level E2E with fake gh is not real-GitHub acceptance, and focused live adapter verification is not necessarily application E2E. Preserve performance and human acceptance separately.

Select execution for a stated risk or acceptance need; permission and historical suite inventories are not blanket execution requirements. Reclassification requires case-level scope evidence. Discovery and compilation do not establish runtime acceptance. The [test policy](../tests/README.md) owns selection, reporting and migration rules.

## Native table-input lifecycle

This applies to the current WinUI grid and to any WinUI rendering chosen in #77; a web rendering must meet the same input contract.

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
| Rendering choice and publish batch size | #77 |
| Workspace shell, Project switching, settings and column mapping | #81 |
| Plan sheet, scheduling rules and status date | #78 |
| Refresh, publish, conflicts and creation guard | #79 |
| People view and allowance | #80 |
| CSV import of new tasks | #82 |
| Distribution, notices, signing and company-environment validation | Deferred under #76 (previous findings in closed #13) |

Required dependencies must not require paid licensing or company-size/revenue eligibility. Local development permission is distinct from binary redistribution permission. Resolve the actual output-to-license/notice manifest before a release; build output alone is not approval to distribute it.

The parent Windows App SDK and its DWrite/Widgets packages have different redistribution wording. Their applicability to the app-local payloads remains unresolved and blocks distribution. Preserve the exact [terms and package provenance](dependencies.md).

# Decisions

Record accepted choices and their rationale. Keep task progress and experimental findings in the owning Issues.

## Planning fidelity and portability

Adopt the [planning ownership matrix](planning.md) and checkpoint v8. GitHub NUMBER supplies selected work scalars; DATE is only a day projection. Exact intraday Manual/Auto provenance, per-person reports, weights and adopted calendar belong in typed local metadata, never Issue prose. Existing scalar drafts/reconciliation/Apply retain their authority; no second store or queue. Copy adopted holidays into the Project checkpoint so a bundle update cannot rewrite the plan.

Use validated full-checkpoint backup and same-host/viewer restore into an empty root. Preserve unfinished/unknown dispatch evidence; independent copies are not concurrent synchronization. Unknown versions reject rather than reset. Native `blockedBy` means directed FS predecessor, distinct from hierarchy; unsupported richer links remain explicit. External contracts: [Issue schema](https://docs.github.com/en/graphql/reference/issues), [Project schema](https://docs.github.com/en/graphql/reference/projects), [official holidays](https://www8.cao.go.jp/chosei/shukujitsu/gaiyou.html). #2 owns bounded proofs, #61 production integration, #13 distribution notices.

## Table-centered workspace

Microsoft Project's [sheet-oriented views](https://support.microsoft.com/en-gb/office/overview-of-project-views-6cb1dbcd-5cd5-4cc2-a878-aa365564266d) and [optional lower detail view](https://support.microsoft.com/en-gb/project/split-a-view-in-project-desktop) inform the chosen table-centered workspace. [DESIGN.md](../DESIGN.md) owns the general UI/UX/IA criteria; the [workspace contract](spec.md#workspace-presentation) owns concrete layout and interaction behavior.

The compact account/connection strip, collapsible repository navigation and optional bottom details preserve space for fields. The current command set does not justify the height of a large ribbon, and permanent side details would compete with the widest columns. Cache time stays in the Project header; the default new-row destination belongs in Project settings under the workspace contract, separating an infrequent default from routine editing and Apply commands.

Native command overflow and responsive Project commands provide compact command access. General reachability and context criteria live in [Interaction and context](../DESIGN.md#interaction-and-context); the workspace contract specifies window sizing, status routes and focus restoration. Native input controls and public focus APIs remain the implementation choice.

Keep row/column definitions in the existing scoped profile checkpoint and preserve canonical row/field identities beneath presentation changes. Transient pane visibility, selection and scrolling do not acquire a separate persistence owner. Reuse the established draft, conflict and Apply engines; this presentation decision adds no dependency or storage schema. Human visual/usability acceptance remains owned by #65.

Frozen row identity and table-local header commands support comparison across columns. Native choice buttons open their option controls on request to reduce idle per-cell templates, while retained active/pending editors protect native text/IME input. The [input contract](spec.md#selection-input-and-rectangular-operations) owns that behavior. One viewport and nonanimated wheel/scrollbar updates keep row identity and fields aligned; touch/precision-touchpad behavior requires its own native acceptance evidence. The workspace and [column](spec.md#project-specific-columns) contracts specify the corresponding controls and operations.

## Existing-field mutation and journal

Use `updateIssue` with only `id/title`, `updateProjectV2ItemFieldValue` with Project/item/field IDs and `singleSelectOptionId`, and `clearProjectV2ItemFieldValue` for explicit clear. Their documented inputs expose no expected-value/revision conditional update. `clientMutationId` is not a lock or a proven idempotency key. Verify the returned identity (and title) plus a separate authoritative field read; unknown or mismatching outcomes remain unapplied. Sources: [Issue schema](https://docs.github.com/en/graphql/reference/issues), [Project schema](https://docs.github.com/en/graphql/reference/projects).

Use sequential dispatch with at least one second between mutation starts, revalidating after the wait. Honor Retry-After and primary reset headers; absent timing defaults to one minute with exponential increases. Rescheduling is bounded to three waits per execution and always revalidates. Only a known rate-limit rejection permits automatic mutation rescheduling. Ambiguous writes require explicit reconciliation. Transport never retries mutations. Source: [GitHub API guidance](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).

Keep execution records in the existing version 8 profile checkpoint, preserving older records and recovery files. A separate OS file lease prevents concurrent executors of the same data root/profile; it is not server-wide exclusion. Baseline acknowledgement and journal outcomes commit together. This cannot remove remote read/write races or provide remote atomicity. The complete Project reader is reused conservatively for identity/structure checks; existing Apply performance belongs to #51 and integrated local latency to #65.

## Local preparation identity and recovery

Keep local new rows separate from fetched Issue/item records so incomplete preparation never needs a fictitious remote baseline or update capability. Capture destination per row; defaults only seed subsequent work. Store local lifetime changes in the existing guarded transaction history and version 8 profile checkpoint to make values, removal and Undo recover together. Preserve selected IDs and saved labels when definitions disappear rather than remapping by name. Remote Apply can invalidate its own old baseline history while retaining a mixed operation's local Undo remainder. Sources: #7/#8/#9; remote creation handoff: #11.

## Native platform

Use **C# + .NET 10 + WinUI 3 / Windows App SDK** for the native Windows desktop app. Native controls and public Windows APIs provide the UI boundary; application rules remain in one UI-independent Core library. Implement screens from their behavioral contracts, not from another framework's visual tree.

Windows App SDK is pinned to `1.8.260804001` in the app project. The development target is `net10.0-windows10.0.26100.0`, x64, with minimum platform 19041. The development executable is unpackaged and self-contained to make ordinary-executable checks explicit. These are build settings, not a final supported-device or distribution promise. See [Microsoft WinUI 3 documentation](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) and [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

No grid library, MVVM framework or persistence technology is selected by the platform decision. Select dependencies only for demonstrated requirements and acceptable unconditional commercial terms.

## Testing

Adopt **logic-layer unit tests > UI-layer integration tests > E2E tests** as the testing priority. Put rule combinations and failure branches in the lowest reliable boundary, then verify presentation wiring with real UI integration. Keep E2E for representative whole-application workflows. This makes lower-layer behavior the primary proof rather than relying on a whole-app journey to diagnose every rule or state. It is not a numerical quota or a prohibition on E2E, physical IME, full regression or live validation.

Classify by tested scope rather than automation technology or process layout. UI integration checks a bounded collaboration of real UI components, events/commands, presentation state and rendered results; E2E checks a representative workflow through the application's principal layers to an explicit endpoint. Neither UI Automation nor fake gh decides that classification. Record actual collaborators, substitutions and assertions; do not call a broad workflow UI integration merely because it ends at a fake service.

Use NUnit for the existing logic, adapter integration and desktop suites, and FlaUI UIA3 for desktop automation. Exact versions live in the project files. A dedicated UI test host and a direct or external driver are execution choices, not test levels. Inspect existing cases and reuse adequate mechanisms before introducing a host or another project for a concrete coverage gap. This avoids unnecessary infrastructure without treating project names as evidence of coverage or its absence.

Core tests exercise real application collaborators. A unit of behavior need not mean one class surrounded by mocks. The fake gh executable controls the external process boundary and has no network fallback; actual adapter/process and isolated-storage integration remain covered. UI integration keeps the asserted view/event/presentation/binding collaboration real, using controlled dependencies outside that scope. ViewModel-only assertions do not establish control wiring. Whole-application desktop journeys retain the ordinary executable and public UI Automation contract.

Prefer behavior and state assertions to interaction assertions. Assert the observable result, state change or error of a real collaboration rather than call counts, call order or private state; interaction assertions add observation points that break on behavior-preserving refactoring. Cover each behavior at one boundary: a collaborator is tested for its own contract, not for its caller's behavior, because repeating the caller's viewpoint produces false failures when the collaborator changes internally. Thin CRUD is covered once at the integration boundary, while logic-unit cases exist for real branching, validation, ordering, identity, conflict or failure rules. Case names state the condition and expected outcome, case bodies follow arrange/act/assert, and matrices use table-driven cases. No coverage percentage or suite-size target applies; the user reviews the proposed behavior list for necessity, duplication and layer assignment. A diagnostic line/branch HTML report for `GhProjectsBoards.Core` is produced from `GhProjectsBoards.Tests` with Microsoft Code Coverage and ReportGenerator and published to GitHub Pages on `main`. It is not an acceptance gate.

Keep test scope, driver/runtime mechanics and environment/evidence requirements distinct. Physical IME, native focus, clipboard/picker, process lifetime and live GitHub may require real facilities but do not automatically make a test E2E. App-level E2E with fake gh is not real-GitHub acceptance, and focused live adapter verification is not necessarily application E2E. Preserve performance and human acceptance separately.

Select execution for a stated risk or acceptance need; permission and historical suite inventories are not blanket execution requirements. Reclassification requires case-level scope evidence; moving coverage down requires observed replacement assertions before retiring redundant checks. Discovery and compilation do not establish runtime acceptance. The [test policy](../tests/README.md) owns selection, reporting and migration rules.

## Native table-input lifecycle

Use an input-ready native WinUI TextBox for the cell editor, preparing focus and replacement selection during cell selection. Keep application Selected/Editing state and committed values separate from native editability. Start application editing on actual composition/text changes or F2, rather than changing read-only state on the first character. This lets the native IME own composition without replaying input or using private APIs.

Retain the method in the ordinary app's explicit input-check window. Its behavioral contract includes direct and F2 input, range selection, separate draft/committed values, IME confirmation versus cell commit, cancellation and reconversion. Full grid layout, virtualization, paste/Undo and keyboard edge policies still require their own implementation and validation. The selected method does not introduce a TableView dependency.

## Authentication and process ownership

Use stored gh authentication. Exclude all four gh token environment overrides from children, expose only safe authentication metadata and require recognized keyring storage before writes. Plaintext and unknown storage remain diagnosable without permitting writes.

Use `ArgumentList`, UTF-8 JSON stdin, explicit hostname/target, asynchronous execution, a 30-second process timeout, cancellation and structured results. Bind stable viewer identity and recheck before dispatch. Never automatically resend a failed or uncertain write.

These rules avoid a second credential owner and keep issue content as data. See [gh environment variables](https://cli.github.com/manual/gh_help_environment), [login](https://cli.github.com/manual/gh_auth_login), [auth status](https://cli.github.com/manual/gh_auth_status) and [specification](spec.md).

Preflight cannot atomically prevent another process from switching gh authentication. Connection state is in memory. Persistent workspaces and company-environment verification have their own acceptance criteria.

## Issue #4 registration storage

Use existing .NET `System.Text.Json` and filesystem APIs, without a new dependency, for a versioned per-user registration/settings and currently supported read-cache format. One atomic record per normalized host/viewer/Project contains settings and snapshot together. A root writer lock, flushed and verified temporary write, and same-directory replacement protect the last good record. Backups are explicit recovery material, never authenticated sessions or automatically selected snapshots.

This is the retained legacy registration format. Migrated profiles use the authoritative checkpoint for registrations, drafts, Undo, shared Issue work, planning and Apply history. The unencrypted location, sensitive-data implications and local deletion/recovery rules are documented in [README](../README.md#local-registration-storage).

Discovery uses the current official [Repository.projectsV2](https://docs.github.com/en/graphql/reference/repos#repository) linked-Project relationship, [ProjectV2.repositories and owner connections](https://docs.github.com/en/graphql/reference/projects) and [Project API guidance](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects). Repository association does not change Project ownership or item scope.

## Decision ownership

Use one versioned JSON draft/checkpoint record per host/stable viewer with existing .NET libraries. Cross-field operations, shared Issue-title work, local rows, Project preferences and Apply history commit coherently without coordinating per-Project draft files. Before checkpoint migration, registration caches are independent records; after migration, retained legacy files are recovery material and the checkpoint is authoritative. Reuse checked temporary writes, write-through flush, same-directory replacement and locking, with session save serialization and optimistic durable-revision checks. This is a local store, not multi-device synchronization. Its contents are unencrypted private local work; preserve last-good files and diagnose corruption instead of resetting them.

| Topic | Owner |
| --- | --- |
| Supported field/item matrix and component suitability | #2 |
| Few-row Japanese input contract | #24 |
| Boards planning and its data lifecycle | #61 |
| Feature persistence and recovery | #61 / #64 / #63 |
| Cross-feature validation and inherited performance under the layered test policy | #65 |
| Distribution, component notices, signing, update/rollback and enterprise validation | #13 |

Required dependencies must not require paid licensing or company-size/revenue eligibility. Local development permission is distinct from binary redistribution permission. Resolve the actual output-to-license/notice manifest before a release; build output alone is not approval to distribute it.

The parent Windows App SDK and its DWrite/Widgets packages have different redistribution wording. Their applicability to the app-local payloads remains unresolved and blocks distribution under #13. The approved transfer from #22 is a scope decision, not license clearance. Preserve the exact [terms and package provenance](dependencies.md).

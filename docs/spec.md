# Specification

This document records agreed behavior from [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). Connection diagnostics, bounded retrieval and Project registration/cache are implemented; bounded existing-Issue editing and local draft recovery are implemented. Apply and the broader editing/storage requirements remain in their owning Issues.

## Connection and API access

Source: [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3).

- Startup is local. Manual checking detects or uses the selected gh executable, reads its version and active authentication metadata, and obtains the stable numeric viewer ID from `/user` on the explicit hostname. Authentication JSON content determines validity even when `auth status` exits with zero.
- A connection binds hostname, stable viewer ID, and gh executable. Rechecking does not adopt a different binding. Reads and writes recheck authentication and identity immediately before dispatch; mismatches permanently invalidate that context. An explicit new connection creates a new binding. This is in-memory groundwork for future workspaces, not persistence.
- Issue and Project URLs must be HTTPS on the selected host, with explicit repository/owner and number. Each diagnostic independently reports read access, `viewerCanUpdate`, and the relevant OAuth update scope. Absent metadata stays unknown. Actual permissions and scope presence are separate; neither alone proves a later mutation will succeed.
- Only stored gh authentication is used by child processes. The four token environment variables are excluded without altering global settings. The UI reports variable names, never values. Authentication metadata is projected without the token field. Plaintext storage exposes the `hosts.yml` path and blocks writes; unknown storage blocks writes too. Reads remain available for diagnosis.
- The app never retrieves, stores, or displays credentials. Raw stdout/stderr and payloads are transient parser inputs, not logs or diagnostic output. Safe diagnostics expose outcome, HTTP status, exit code, classified error codes, and timing; remote error messages are omitted.
- REST and GraphQL use explicit host and target, `ArgumentList`, and UTF-8 JSON stdin. No shell interprets the payload. Child prompts, debug output, telemetry, and implicit gh host/repository overrides are disabled.
- The default timeout is 30 seconds per gh process, including stdin transfer. Cancelling or closing the window terminates the owned child process. Multiple commands can make the overall connection check longer than one process timeout.
- Results distinguish success, failure, cancellation, timeout, and unknown outcome, retaining available data and classified GraphQL errors. Partial GraphQL data is not complete success. Interrupted dispatched writes and potentially applied server failures are unknown; preflight failures establish that the target write was not dispatched. No automatic retry is performed.

Preflight cannot atomically lock gh authentication against changes made by another process between the identity check and API dispatch. Do not switch external gh authentication during an operation. Eliminating that race would require a different credential/session mechanism; token extraction is outside this design.

GHEC + EMU IdP/browser authentication, enterprise host behavior, organization policy, required scopes, proxy/TLS connectivity, credential-store availability, and executable restrictions remain unverified until tested in the company environment under [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

## Project registration and cache

Source: [#4](https://github.com/fukuda-yuki/gh-projects-boards/issues/4), with bounded navigation from [#5](https://github.com/fukuda-yuki/gh-projects-boards/issues/5).

- The ordinary window exposes owner/repository-linked discovery, owner search and direct user/organization Project URLs after normal connection checking. Discovery queries use READ permission, existing URL validation and guarded preflight; they never require update permission or keyring storage merely to read. All discovery connections traverse to a terminal cursor; errors prevent a complete-list result.
- Repository association is read from GitHub's repository/Project connections, never inferred from contained Issues. Initial retrieval uses the production ProjectReader for the whole Project. Identity confirmation displays title, owner, host, bound viewer and duplicate-registration status before retrieval.
- Registration identity is normalized host + stable viewer ID + Project node ID. Multiple repository navigation entries select the same saved record. Left navigation groups the explicitly selected profile by owner and actual repository links, with a separate unlinked group.
- Only a Complete reader result plus successful durable local save publishes registration success. Unsupported field types and unavailable content do not negate a completed traversal. Partial first attempts are not registrations. Cancelled/failed refreshes preserve the previous saved snapshot. Stage/count progress has no invented percentage.
- Startup and navigation are local. Saved profiles require explicit selection and are marked cached/unverified; they do not restore authentication. A checked matching connection is required for server operations. Switching profile/context cancels and settles owned work. Obsolete results cannot publish into another profile or save after cancellation.
- The preview displays all retrieved items, Issue repository/number/title/Open-Closed and single-select values. Other item kinds, archived items and unsupported/unavailable/empty/not-loaded values remain distinct. The registered workspace supports the bounded editing contract below; partial retrieval remains a read-only preview.
- Default repository is an optional local `owner/repo` setting for future Issue creation and does not filter retrieval. Unregistration confirms removal of only the selected scoped registration and its owned cache/backup/temp data, after settling retrieval. No GitHub data is mutated. When drafts or operation history exist, cancellation is the default; the user explicitly chooses retention or discard of exclusively owned work. Shared Issue drafts and their baseline provenance survive while another registration references them.
- Version 1 registration JSON is an atomic settings/snapshot record with schema and nested identity validation. Saves flush and verify temporary data before replacement, retain the previous file as a backup and reject concurrent writers. Corruption, unsupported schema, access/save failures and interrupted files are diagnosed without automatic data reset. See [storage and recovery instructions](../README.md#local-registration-storage).

## Editing and drafts

Editing, Project switching, and local saving do not write to GitHub. Preserve drafts across refresh and restart. Existing-cell blank paste means no change by default; clearing a value is explicit. Local new rows are distinct from GitHub Draft items. See [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7) and [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).

### Bounded registered-Project field matrix

This production slice does not finalize or reduce #2's broader MVP matrix.

| Field/item | Read | Local edit | Explicit clear | Validation and identity |
| --- | --- | --- | --- | --- |
| Existing Issue title | Loaded Issue title | Yes, with observed Issue update capability | Rejected | Nonblank, no tabs/newlines; scoped Issue node ID |
| Project-owned single-select, including Status | Option ID and label | Yes, with observed Project update capability | Yes | Existing option ID; Project/item/field IDs; labels are display/input metadata |
| Repository/number and Issue Open/Closed | Read-only reference column | No | No | Native Issue identity, never the Status label |
| Other fields | Explicit supported-read/unsupported classification in reference column | No | No | Preserve fetched ownership and field IDs |
| PR / GitHub Draft / unavailable / unknown item | Explicit classification retained as a row | No | No | Never convert to an Issue or local new row |
| Unsupported/unavailable/not-loaded value | Distinct classification | No | No | Never interpret as editable empty |

The reader obtains `Issue.viewerCanUpdate` and `ProjectV2.viewerCanUpdate` as nullable, dated observations. Only explicit true with an observation timestamp permits the corresponding local editor. Old registration records remain readable with unknown capability. Cached capability does not authenticate a profile or promise a future mutation will succeed. No keystroke makes a permission/network request. See the official [Issue schema](https://docs.github.com/en/graphql/reference/issues) and [Project schema](https://docs.github.com/en/graphql/reference/projects).

### Selection, input and rectangular operations

Columns are title, Project-owned single-select fields in retrieved order, then read-only reference metadata. Scrolling does not change a control's stable row/field keys. This bounded ListView retains native editors per row rather than recycling a live editor into another row; very large workloads remain a separate performance boundary.

Selection prepares native TextBox focus and replacement selection before direct typing. Actual native text/composition or F2 starts editing. Arrows while selected move one cell; Shift+arrows extend a rectangle. Arrows while editing retain native caret/candidate behavior. Enter during composition confirms the IME only; the later Enter validates/commits the cell and moves down one row. Tab/Shift+Tab commit an active editor then move in row-major order. Enter/arrows clamp at an edge; Tab clamps at the first/last cell. No navigation creates a row. Escape outside composition cancels the cell buffer; native composition/candidate cancellation remains separate. Navigating to another cell preserves incomplete buffers instead of committing or discarding them.

The native choice editor commits an explicitly selected option. Delete or **値をクリア** is an explicit clear operation for selected cells. A required title or read-only target rejects the whole clear. During text editing/composition Ctrl+Z belongs to native text; while selected Ctrl+Z belongs to grid-operation Undo. Ctrl+C/V while selected and the toolbar commands operate on the rectangle. A pending clipboard read is invalidated on workspace transition/close and cannot retarget another profile.

TSV uses literal tabs and LF/CRLF row separators, without quoted-cell escaping. One final row separator is accepted; internal/trailing empty cells are preserved. Rows must have equal widths and fit entirely in existing rows/columns. Empty pasted cells mean no change, including empty trailing cells; they never mean clear. Every destination must be editable, even for a blank cell. A nonempty single-select label must match exactly one option; duplicate labels are rejected, while the choice editor selects by ID. Invalid shape, bounds, permission or values cause no partial operation and report a position/reason where a cell is identifiable. Unknown/unavailable values cannot be copied as empty cells.

Each successful paste/clear is one transaction. Undo restores the exact preceding field draft state, explicit clear intent and recoverable buffer. It verifies every affected field's expected state/version before modifying any field; later shared-Issue edits or an active buffer reject the entire Undo. Project histories cannot undo another Project's independent work.

### Local durability and lifecycle

Fetched observations, editor buffers and committed local differences remain separate. Committing/saving never modifies the baseline; returning to baseline removes the effective difference. Titles share a host/viewer/Issue identity across Projects; selects remain scoped to Project/item/field IDs. Opening another cached observation never replaces an active draft's pinned baseline. The UI distinguishes per-Project and profile-wide changed-field counts and local saving/saved/failed states from GitHub synchronization.

One versioned JSON draft record per host/stable viewer under `Drafts/` holds baseline value/source Project/retrieval time, clear/value intent, recoverable text, field stamps and transaction history. Version 2 adds observations, conflicts and the authoritative registration checkpoint described below; version 1 is still readable. It contains neither credentials, raw API payloads nor private IME state. Startup/profile selection restore it locally. A restored buffer is pending text, not a committed cell.

Draft saves are serialized. Under a filesystem writer lock, the expected durable revision is checked, a temporary record is flushed and validated, and the complete profile record is atomically replaced with a last-good backup. A cross-field operation is recovered wholly or not at all. The session advances its acknowledged revision only after success and drains later changes before reporting a successful flush. Competing-process/stale saves fail instead of overwriting. Corruption, incompatible schema, mismatched scope and interrupted files are diagnosed without resetting source data. Intermediate edits after the last successful save may be lost on forced termination; normal transitions/close wait for a successful flush. Save failure keeps in-memory text and cancels navigation/close.

Refresh reconciles existing titles and Project-owned single-select fields using the contract below. The final checkpoint replacement rechecks local revision, operation generation, connection identity and active composition synchronously immediately before replacement. Unregistration settles owned work and defaults to cancellation, offering explicit retention or discard. Discard never removes Issue work shared by another registration; its pinned source provenance remains in the surviving draft record. Discard plus registration removal is one durable checkpoint decision; legacy files remain recovery material after migration.

## Data identity and availability

Keep Issue fields, Project item fields, and application work state distinct. Share the same Issue's body fields across Projects while retaining each Project item's own values. Missing, unreadable, unsupported, and explicitly empty values are different states. See [#6](https://github.com/fukuda-yuki/gh-projects-boards/issues/6).

### Bounded Project read contract

The Core Project reader accepts an explicitly selected Project node ID bound to the existing connection's host and stable viewer ID. It uses the guarded connection service for every page. It performs queries only, with the same identity preflight, credential isolation, per-process timeout and cancellation as other reads. Project registration/navigation UI is separate work.

Each returned Project has a stable owner ID, node ID, number and URL. Repository and Issue identities also include the connection scope. Items retain their own node ID, content identity, archived flag and field values. Issues are indexed separately from items: two Project results refer to the same logical Issue key, while their item values remain independent. These are immutable read observations; there is no cross-result cache, automatic synchronization or draft state.

| Data | Read behavior |
| --- | --- |
| Issue title and Open/Closed state | Loaded directly from the Issue, never through a field display name |
| Project single-select, including Status | Field ID, Project ownership, option IDs/names and selected option ID |
| Issue-derived or organization Issue fields exposed by a Project | Ownership and Project field identity/type retained; their projected values are unsupported in this slice |
| Other Project fields | Identity, type and known ownership retained as unsupported; unknown ownership stays unknown |
| PR / GitHub Draft | Explicit item kinds with content IDs; no ordinary Issue or local-new-row conversion |
| Redacted/null content | Unavailable content with the Project item identity retained |
| Unknown item/value types | Explicitly unsupported, retaining encountered type and available IDs |

Field names and option names are display metadata, never identity or destination selectors. Native Issue properties use their Issue identity and typed title/state properties; a same-named Project field has its own field ID. The bounded editor additionally reads dated Issue and Project viewerCanUpdate observations. Missing/denied observations do not enable editing; creation and remote application remain outside this contract.

The reader traverses Project field definitions, items and every implemented item-value connection to the terminal page, including more than 100 entries. It checks node/ownership identities, duplicate fields/options/items/Issues/value IDs, duplicate field assignments, repeated cursors, missing paging metadata and inconsistent total counts. On API or structural failure it stops further requests, retains already observed data and reports a classified problem. No automatic retry occurs.

Result outcomes are Complete, Partial, Failed, Cancelled and TimedOut. Cancellation/timeout can retain a partial Project. Per-connection completion flags describe traversal, not universal support or readability. A Complete result means the requested traversal completed without detected problems; it can contain explicitly unsupported fields and known unavailable items. It does not establish a transactionally consistent snapshot: GitHub does not pin successive queries to one revision, and equal-count concurrent replacements can escape count/cursor checks.

Values distinguish Present, Empty, Unsupported, Unavailable and NotLoaded. An explicit null option, or an absent supported field after a complete, error-free traversal, is Empty. On a partial read, nulls become Unavailable and absent supported fields stay NotLoaded; redacted content never implies an empty Issue or field. Unknown option IDs retain the observed ID as Unavailable. Partial GraphQL data is retained but never treated as complete. Missing or unsupported data cannot authorize clearing or deletion; this slice has no writes or apply implementation.

The broader MVP field/item editing matrix remains under [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2). Body, assignees, labels, milestone, text/number/date/iteration/multi-select, Issue fields and relationships remain candidates requiring separate read/edit/clear decisions.

Schema references: [GitHub Project types](https://docs.github.com/en/graphql/reference/projects), [Project API queries and redacted items](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects), and [cursor pagination](https://docs.github.com/en/graphql/guides/using-pagination-in-the-graphql-api).

## Refresh and apply

The bounded #9 workflow refreshes registered Projects with existing local work. It covers Issue titles and Project-owned single-select option IDs only. B is the pinned baseline, L the committed local value (never unfinished editor text), and R the fetched observation. For known valid values: L=B adopts R cleanly; R=B retains L; L=R adopts R cleanly; otherwise keep B/L/R as an unresolved conflict. Explicit select clear is distinct from unavailable, unsupported or not-loaded values. Apply these rules independently per field. Repeated refresh retains original B/L until explicit resolution or valid convergence.

Title observations and drafts are shared by scoped Issue ID; Project/item/field select keys remain independent. Opening another cached Project only initializes missing keys and never rebases an accepted observation. Fetched timestamps order accepted observations within the profile; they are client observation times, not GitHub locks or revision tokens. Structural changes report added/no-longer-observed items, archive state, missing/type-changed fields and renamed/deleted option IDs. Absence from a complete Project traversal is not proof of Issue deletion. Unknown capabilities and detached values stay blocked and retain identifiers, drafts and buffers.

Persist local work before retrieval. Reconcile against current local state after I/O; a revision or connection-generation change before durable replacement rejects that candidate. Active composition defers refresh with an actionable message. Inactive buffers remain pending, never auto-committed. Failed/cancelled/timed-out/partial reads do not commit a reconciliation or replace the complete cache. Staged observations and unknown ranges are separate from the accepted workspace.

The comparison UI shows B/L/R, observation time, ownership, IDs and current blocking reason. Resolvable conflicts offer fetched GitHub, local, or another valid value. A decision is bound to the displayed local revision and remote observation ID; stale decisions require a new comparison. On resolution R becomes the baseline and the chosen value becomes local state; choosing R is clean. Remote observations never create user Undo units. Preserve unaffected history; explicitly invalidate affected stale operations and deleted-option entries. Resolution Undo is subject to the same state checks and cannot overwrite a subsequent observation.

Version 2 profile checkpoints atomically contain registrations/caches, baselines, observations, drafts, buffers, conflicts and history. A checked, flushed temporary file and one atomic replacement form the commit; there is no second cache write. Root/profile locks and expected durable revision reject competing writers; first migration also compares all legacy registrations under the lock. Restore registrations and drafts from the same checkpoint object. Broken profiles do not prevent independent valid profiles from restoring. Version 1 records remain readable and previous files/backups are retained. Publish success only after durable replacement; failures leave the original in-memory work and last coherent checkpoint intact. A crash after replacement but before UI notification recovers the committed checkpoint on restart.

Apply remains unimplemented. #10 must revalidate target identity, value and capability immediately before dispatch, including queued operations whose observations can become stale while waiting. Refresh is an observation, not a server-side lock or a transactionally consistent multi-page snapshot, and cannot eliminate the read/write race. New rows and broader fields remain outside this slice. See [#9](https://github.com/fukuda-yuki/gh-projects-boards/issues/9), [#10](https://github.com/fukuda-yuki/gh-projects-boards/issues/10), [GitHub Project schema](https://docs.github.com/en/graphql/reference/projects) and [pagination](https://docs.github.com/en/graphql/guides/using-pagination-in-the-graphql-api).

## New Issue creation

Track Issue creation, returned IDs, Project addition, field assignment, and result verification separately. An uncertain creation result must not trigger automatic recreation. See [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11).

## Contracts to define

- Supported field and item-type matrix: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2).
- Persistence format, location, and recovery: [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).
- Distribution and company environment checks: [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

# Specification

This document records agreed behavior from [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). Connection diagnostics and the internal API boundary are implemented; the editing, persistence, and apply sections describe future work in their owning Issues.

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

## Editing and drafts

Editing, Project switching, and local saving do not write to GitHub. Preserve drafts across refresh and restart. Existing-cell blank paste means no change by default; clearing a value is explicit. Local new rows are distinct from GitHub Draft items. See [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7) and [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).

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

Field names and option names are display metadata, never identity or destination selectors. Native Issue properties use their Issue identity and typed title/state properties; a same-named Project field has its own field ID. No capability to edit, create or clear is granted by this read contract.

The reader traverses Project field definitions, items and every implemented item-value connection to the terminal page, including more than 100 entries. It checks node/ownership identities, duplicate fields/options/items/Issues/value IDs, duplicate field assignments, repeated cursors, missing paging metadata and inconsistent total counts. On API or structural failure it stops further requests, retains already observed data and reports a classified problem. No automatic retry occurs.

Result outcomes are Complete, Partial, Failed, Cancelled and TimedOut. Cancellation/timeout can retain a partial Project. Per-connection completion flags describe traversal, not universal support or readability. A Complete result means the requested traversal completed without detected problems; it can contain explicitly unsupported fields and known unavailable items. It does not establish a transactionally consistent snapshot: GitHub does not pin successive queries to one revision, and equal-count concurrent replacements can escape count/cursor checks.

Values distinguish Present, Empty, Unsupported, Unavailable and NotLoaded. An explicit null option, or an absent supported field after a complete, error-free traversal, is Empty. On a partial read, nulls become Unavailable and absent supported fields stay NotLoaded; redacted content never implies an empty Issue or field. Unknown option IDs retain the observed ID as Unavailable. Partial GraphQL data is retained but never treated as complete. Missing or unsupported data cannot authorize clearing or deletion; this slice has no writes or apply implementation.

The broader MVP field/item editing matrix remains under [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2). Body, assignees, labels, milestone, text/number/date/iteration/multi-select, Issue fields and relationships remain candidates requiring separate read/edit/clear decisions.

Schema references: [GitHub Project types](https://docs.github.com/en/graphql/reference/projects), [Project API queries and redacted items](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects), and [cursor pagination](https://docs.github.com/en/graphql/guides/using-pagination-in-the-graphql-api).

## Refresh and apply

Compare the fetched baseline, local draft, and current GitHub value per field. Retain drafts and surface conflicts. Manual apply sends only changed fields after review; success, failure, and unknown results remain distinct. Resume only after reconciliation, without blindly resending successful or uncertain writes. See [#9](https://github.com/fukuda-yuki/gh-projects-boards/issues/9) and [#10](https://github.com/fukuda-yuki/gh-projects-boards/issues/10).

## New Issue creation

Track Issue creation, returned IDs, Project addition, field assignment, and result verification separately. An uncertain creation result must not trigger automatic recreation. See [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11).

## Contracts to define

- Supported field and item-type matrix: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2).
- Persistence format, location, and recovery: [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).
- Distribution and company environment checks: [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

# Specification

This document records agreed behavior from [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). Connection diagnostics, the internal API boundary, and an offline editing prototype are implemented; production editing, persistence, and apply remain future work in their owning Issues.

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

The [#19 prototype](https://github.com/fukuda-yuki/gh-projects-boards/issues/19) is an independent, discard-on-close window containing 100 deterministic rows and five fields defined for the prototype: required single-line Title, required Issue state Open/Closed, nullable decimal Number, nullable `yyyy-MM-dd` Date, and nullable Choice High/Medium/Low. Issue state is distinct from Project Status. These fields do not decide the product field matrix.

Rectangular CRLF/LF TSV applies at the selection's top-left; trailing empty cells survive parsing and mean no change. Invalid values, ragged rows, and out-of-bounds destinations reject the whole operation. A dedicated clear/Delete outside editing explicitly empties optional cells; required fields make the whole clear invalid. Adding a row defaults state to Open, flags its empty Title, focuses that cell, and supports row 101 without paste-driven auto-append.

Each committed cell edit, range paste, clear, and row addition has one Undo entry; rejected and unchanged operations have none. Stable local row IDs preserve operation targets through virtualization. Editor text, committed values, and errors remain separate; editor cancellation cannot roll back earlier committed operations. Tab/Shift+Tab navigate, Enter commits and moves down, and editor keys/IME confirmation belong to the editor. The current implementation fails the direct-start Japanese IME requirement and is not an adopted grid component; see [the decision](decisions.md#editing-grid-component) and #19 evidence.

Editing, Project switching, and local saving do not write to GitHub. Preserve drafts across refresh and restart. Existing-cell blank paste means no change by default; clearing a value is explicit. Local new rows are distinct from GitHub Draft items. See [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7) and [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).

## Data identity and availability

Keep Issue fields, Project item fields, and application work state distinct. Share the same Issue's body fields across Projects while retaining each Project item's own values. Missing, unreadable, unsupported, and explicitly empty values are different states. See [#6](https://github.com/fukuda-yuki/gh-projects-boards/issues/6).

## Refresh and apply

Compare the fetched baseline, local draft, and current GitHub value per field. Retain drafts and surface conflicts. Manual apply sends only changed fields after review; success, failure, and unknown results remain distinct. Resume only after reconciliation, without blindly resending successful or uncertain writes. See [#9](https://github.com/fukuda-yuki/gh-projects-boards/issues/9) and [#10](https://github.com/fukuda-yuki/gh-projects-boards/issues/10).

## New Issue creation

Track Issue creation, returned IDs, Project addition, field assignment, and result verification separately. An uncertain creation result must not trigger automatic recreation. See [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11).

## Contracts to define

- Supported field and item-type matrix: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2).
- Persistence format, location, and recovery: [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).
- Distribution and company environment checks: [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

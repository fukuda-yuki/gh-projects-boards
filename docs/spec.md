# Specification

This outline records agreed behavioral boundaries from [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). Detailed contracts belong here as their owning Issues are resolved. None of the behavior below is implemented by the skeleton.

## Editing and drafts

Editing, Project switching, and local saving do not write to GitHub. Preserve drafts across refresh and restart. Existing-cell blank paste means no change by default; clearing a value is explicit. Local new rows are distinct from GitHub Draft items. See [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7) and [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).

## Data identity and availability

Keep Issue fields, Project item fields, and application work state distinct. Share the same Issue's body fields across Projects while retaining each Project item's own values. Missing, unreadable, unsupported, and explicitly empty values are different states. See [#6](https://github.com/fukuda-yuki/gh-projects-boards/issues/6).

## Refresh and apply

Compare the fetched baseline, local draft, and current GitHub value per field. Retain drafts and surface conflicts. Manual apply sends only changed fields after review; success, failure, and unknown results remain distinct. Resume only after reconciliation, without blindly resending successful or uncertain writes. See [#9](https://github.com/fukuda-yuki/gh-projects-boards/issues/9) and [#10](https://github.com/fukuda-yuki/gh-projects-boards/issues/10).

## New Issue creation

Track Issue creation, returned IDs, Project addition, field assignment, and result verification separately. An uncertain creation result must not trigger automatic recreation. See [#11](https://github.com/fukuda-yuki/gh-projects-boards/issues/11).

## Contracts to define

- Supported field and item-type matrix: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2).
- Connection/account identity and diagnostics: [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3).
- Persistence format, location, and recovery: [#8](https://github.com/fukuda-yuki/gh-projects-boards/issues/8).
- Distribution and company environment checks: [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

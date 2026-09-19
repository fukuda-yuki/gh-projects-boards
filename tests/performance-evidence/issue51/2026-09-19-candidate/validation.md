# Validation ledger and limits

Release, .NET SDK 10.0.401, Windows 10.0.26200. Exact retained TRX hashes/counts are in [test-ledger.json](test-ledger.json); commands are in [commands.md](commands.md). Counts are executed/passed/failed/skipped and are not summed across overlapping selections.

| Run | Counts | What it establishes |
| --- | --- | --- |
| a-red | 5/2/3/0 | Combined-response expectations failed before A. |
| a-green | 127/127/0/0 | A with scoped observation, reader, Apply and creation regression. |
| b-red | 7/0/7/0 | Missing/switched response principal could produce valid evidence before B's provenance checks. |
| b-green | 35/34/1/0 | One malformed continuation test fixture stopped before the intended second page; retained as a failed test attempt. |
| b-green-corrected | 35/35/0/0 | Realistic complete first page and continuation exercise viewer provenance. |
| current-regression | 195/195/0/0 | Apply, creation, connection, reader/scoped observation, new identity checks and then-current stage guards. |
| harness-second | 35/34/1/0 | Diagnostic sanitizer initially removed useful punctuation-adjacent reason words; corrected before live work. |
| stages-first | 7/5/2/0 | Reloaded lifecycle stage had not restored its run-owned identity allowlist; corrected before live work. |
| harness-reviewed | 44/44/0/0 | Stage ledger containment, protocol framing, cooldown and durable restoration corrections. |
| harness-final | 64/64/0/0 | Identity plus then-current lifecycle/privacy guards. |
| harness-final-v2 | 69/69/0/0 | Uncertain cleanup no-replay, HTTP-date cooldown and malformed GraphQL data. |
| harness-final-v3 | 71/71/0/0 | Final C# harness source: identity 17, live guards 35, stage collaboration 19; includes mixed-quote sentinels and cross-stage pacing. |
| UI host initial selection | 0/0/0/0 | Incorrect class selector; runner rejected zero execution. Not acceptance. |
| UI host corrected selection | 8/8/0/0 | Real view/event paths: success return, retained history, mixed/uncertain outcomes, hidden/deleted targets, obsolete-tooltip withdrawal and late Project result. |
| Ordinary app | 3/3/0/0 | Success/review/history/restart at two sizes and a mixed-result journey at 960x600 through real WinUI and an isolated external fake gh. |

Build failures while introducing the harness (`Selected.Id`, then `Selected.Project`) are recorded in the session output; the second also survives in `TestResults/issue51-candidate/harness-first.log`. They are compilation failures, not behavioral Red. The subsequent current-main overlay build succeeded with zero warnings/errors. The ordinary candidate solution build succeeded with two existing SDK-generated CS0436 warnings in the UI integration host and zero errors. No SDK/dependency changes were made.

The 195-case selection includes 157 Core observation/connection/Apply/creation cases and 38 earlier harness cases. The final 71-case selection replaces the evolving harness evidence and repeats the 17 identity cases. These are scoped executions, not a claim that every repository test ran. C# changes stopped before freezing commit `21a9884`; the later comparison-script correction only labels the predeclared one-sample controls and does not change either measured executable.

## Ordinary application and design review

[ui-evidence.json](ui-evidence.json) records clean source commit, ordinary executable/test/fake-gh hashes, verified loaded WinUI module records, screenshot hashes and actual selected methods. The application references the production Core assembly, whose `ApplyRemote` uses the combined/viewer-bound reader without an experiment flag. Both successful durable acknowledgement and failed-field return were exercised through the ordinary confirmation path. This is product-path integration with a substituted external endpoint; it is not a benchmark-only implementation.

The affected user decisions remain whether to confirm changes, return to editing after completion, inspect retained history or correct an exact failed cell. Visual inspection against DESIGN.md confirmed readable short warning/action text, the nearby failed-cell explanation and retained pending input, plus accessible history controls at the captured widths. Existing horizontal table scrolling and lower-priority status truncation remain; this change does not redesign the workspace. Screenshots are retained privately, with hashes here; no raw desktop or Project content is committed.

No physical-IME rerun was selected because the change is in observation/connection behavior and preserves UI input code. No human UX acceptance, screen-reader listening, additional OS text-scale/high-contrast configurations, GHEC/EMU acceptance, main integration or release acceptance is claimed. PR #57's separate physical-IME and identity evidence is linked only in its prepared Testing replacement.

## Provenance and privacy

Old experiment commit `d25ddd16123fef91cff8204890c8d334bc513aec` and its worktree were preserved. Nine frozen archives were opened, their relevant source inspected, and hashes matched the retained closeout index; see [recovered-archives.json](recovered-archives.json). Original old and current run directories remain under ignored `TestResults`. Deleted fixture manifests are not reused.

The committed extract contains selected numeric spans/counters, source/binary/archive hashes, protocol metadata and bounded sanitized diagnostics. It excludes original Project/title content, full fixture/checkpoint records, viewer/account metadata, absolute user-profile paths, auth output, credentials and raw server responses. SHA-256 links the extract to retained originals; it does not turn an unmeasured or failed run into accepted evidence. The minimal live packet must be separately reviewed before any external support submission; none was submitted.

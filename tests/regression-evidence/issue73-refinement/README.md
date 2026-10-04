# Issue #73 workspace refinement

This review unit addresses the user's three corrections: reduce instructional UI, improve ordinary table performance, and connect effort correction with Summary. [Issue #73](https://github.com/fukuda-yuki/gh-projects-boards/issues/73) owns status and acceptance. The implementation is on `codex/issue-73-workspace-refinement`, based on `bb2c8bc5d821e0b5518ce8d1336f1081c2fc6eaa`. This record distinguishes executed engineering checks from comparative product value and human acceptance.

## Decisions

| Priority | Finding and adopted correction | Preserved boundary |
| --- | --- | --- |
| High | First use exposed a three-step explanation, idle commands and settings. Show the current next action, open the native account selector for saved work, use a native Project CommandBar with overflow, and link the existing manual. | Explicit account/Project selection, connection recovery, cached work, meaningful errors and durable Apply history remain available. |
| High | Starting/changing pending input rebuilt all fetched row/cell data for the unpublished-work status. Resolve the few candidate item identities before constructing their cells. | Read-only status, complete related fields, shared Issue appearances, Project scope, local new rows, orphan recovery, ordering and the initializing Apply-review path are retained. |
| High | Summary's Remaining text was transient, and ordinary effort correction opened full task planning. Share the existing field buffer, validate with Enter, cancel with Escape, and open the focused daily-progress editor. | Stable task identity, saved unfinished text, operation Undo, Manual dates and explicit GitHub publication are unchanged. Hidden Boards columns remain usable from Summary. |
| High | In the ordinary 20-person view, scrolling removed every numerical column label. Keep the labels above the vertical viewport and horizontally aligned with the native list. | Native virtualization, keyboard navigation, complete first/last rows and compact-height correction controls remain required. |

GitHub Projects and Azure Boards already offer bulk editing; this alone does not establish a reason to build another product. The differentiated work remains local planning, independent Actual/Remaining correction, person-level comparison, explicit publication and durable resumption. [Design references](../../../DESIGN.md#reference-scope) record the primary sources and the concrete patterns adopted. No comparative speed or usability advantage over those products has been demonstrated by this change.

The default sheet already uses WinUI `ListView` with recycling `ItemsStackPanel`. Replacing it solely because `ItemsPanel` is set would not follow the observed cause. No new grid dependency or alternate input framework was adopted. The selected change removes measured work from the input path while retaining the existing native input/selection contract.

## Independent review loop

Three agents reviewed IA, workflow continuity, and grid/performance independently. The root reviewed their findings against the PMO job and inspected ordinary application pixels. Only consequential in-scope findings were implemented. The second pass found hidden effort columns, the saved-work CTA's invisible response, and the disappearing Summary labels. Root independently reviewed the narrow Core optimization; the workflow reviewer independently reviewed the registration/CommandBar changes.

The first fixed-header implementation moved the native vertical scrollbar outside a narrow viewport. An independent reviewer raised the risk; the ordinary app showed the inaccessible edge, and a hosted geometry check reproduced it (bar right 974.4 DIP, viewport width 760 DIP). The final implementation keeps both native ListView scrollbars and synchronizes only the separate column header through public scroll events. The final focused check exercises vertical scrolling, horizontal round trips, native Home/End, complete first/last rows and compact correction controls.

The final independent IA and workflow source reviews found no additional consequential in-scope defects. They reviewed each other's implementation, including account selection, explicit actions, hidden effort columns, buffer/Undo continuity, read-only candidate semantics and scroll subscription lifetime. This is a bounded review result, not a claim that the whole product is defect-free or accepted by its user.

## Core work reduction

The fixed allocation workload has 1,000 items and 12 select fields in each of two Projects, with one pending Title and one committed select value. Setup and assertions are outside the seven measured synchronous reads, following one warmup.

| Measurement per read | Before | After |
| --- | ---: | ---: |
| Managed allocation range | 13,867,800–13,868,536 bytes | 2,491,528–2,491,880 bytes |
| Elapsed min / median / max | 27.64 / 29.99 / 47.49 ms | 7.11 / 7.21 / 7.83 ms |
| Explicit 8 MiB allocation budget | Failed | Passed |

Allocation fell about 82%. Timing was collected on a shared machine; it is not an input-to-display deadline or a user-visible speed ratio. Candidate semantics passed 7/7 before and after. The final selected Apply, creation, local planning and weekly recovery checks passed 64/64 across logic and adapter/storage boundaries. The earlier two fixture errors are preserved separately from the causal allocation failure.

Reproduction commands (PowerShell, repository root):

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter 'FullyQualifiedName~ReadApplyCandidatesTests'
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName=GhProjectsBoards.Tests.ApplyCandidatePerformanceTests.ThousandRowsTwelveFieldsKeepSparseCandidatesWithinTheAllocationBudget'
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ApplyConfirmationTests|FullyQualifiedName~PlanningLocalRowTests|FullyQualifiedName~PlanningCreationTests|FullyQualifiedName~WeeklyApplyTests|FullyQualifiedName~WeeklyApplyRecoveryTests|FullyQualifiedName~CreationTests'
```

## Evidence boundaries

The [execution ledger](execution-ledger.json) retains 27 execution records, including failed attempts and the final targeted replacements. Result files and source/environment receipts are copied alongside it. Counts from overlapping targeted runs are not added as unique coverage. The [performance report](performance.md) separately records the four before/after ordinary diagnostics, sample limits and remaining synchronous stalls.

- Hosted checks exercise real WinUI views, controls, events, focus and isolated persistence. Synthetic external boundaries are declared by their fixtures.
- Ordinary journeys use the standard executable, UI Automation/native input, isolated stores and fake `gh`; they do not establish live GitHub or GHEC + EMU acceptance.
- Core timing measures synchronous projection, excluding fixture setup and assertions. Ordinary driver timing includes UIA/polling overhead; app spans exclude native allocations and composited presentation. Neither is an input-to-pixel deadline.
- Sampled screenshots can show readable attained viewports and retained content. They cannot prove uninterrupted frame delivery, absence of every stall, or human usability acceptance.
- The pre-existing report about slow GitHub updates concerns a separate external operation. Local scroll/input improvement is not evidence that remote Apply became faster.
- The manual and this bounded change do not close Issue #73 or the combined product gate #65. Human acceptance and comparison against the user's existing workflow remain unverified.

## Final selected verification

| Boundary | Executed result | Artifact |
| --- | --- | --- |
| Core candidate semantics / allocation / affected collaborators | 7/7, 1/1, 64/64 passed; no skips | [Core evidence](performance.md#causal-core-check) |
| Broad relevant hosted UI selection, including real Actual input, workspace, planning and Summary | 116 executed, 113 passed, 3 failed, 0 skipped | [Broad run](results/run-20261004-233811-536-4de06ed4.xml) |
| The three failed hosted cases, in a fresh process | 3/3 passed, 0 skipped | [Targeted rerun](results/run-20261004-234428-326-0daf306f.xml) |
| Physical Japanese IME in Summary Remaining | 1/1 passed; composition started/ended, confirmation Enter did not commit or numerically validate | [Native IME](results/run-20261004-232508-665-37501855.xml) |
| First ordinary journey selection | Onboarding passed; planning and Summary failed (3 executed, 1 passed) | [First ordinary run](results/ordinary-20261004-234144-112e3283b7bc4b29a7f681d31877361f.trx) |
| Ordinary Summary/planning rerun | Summary passed; planning reached its legacy English-label assertion and failed (2 executed, 1 passed) | [Second ordinary run](results/ordinary-20261004-234616-f4cfba54c28b4ea4b305a3fddcc499de.trx) |
| Ordinary planning, weekly correction, reviewed publication and restart | 1/1 passed, 0 skipped; fake gh endpoint, exact payload assertions retained | [Final planning run](results/ordinary-20261004-234751-f66e511027b945fc93dae37ec4bc9f67.trx) |
| Ordinary 50/1,000-row diagnostics | Each final run 1/1 passed with exact bulk/Undo checkpoints and normal exit 0 | [Runtime evidence](performance.md#values-navigation-and-lifetime) |

All 116 selected hosted cases have a passing result across the broad run and its focused rerun; this is not a claim that a later single 116-case run was green. Two failed tests waited for a popup parent but clicked an unready child. They now wait for the actual button to be loaded and enabled, preserving all behavior assertions and `Ui.Click` guards. The native retention check was unchanged; its focused rerun released all 160 sampled editors and the old view after focus transfer. Earlier clean-source records also contain both failure and success for this check. Prior assertion contamination is a hypothesis, not an established cause of the initial retention failure.

The ordinary driver previously toggled an already-open navigation pane while cached accounts were still loading. It now determines whether to toggle from the visible toggle state, then waits for the accounts. The planning journey also retained routes and an English `Manual` label that were obsolete on the starting HEAD: it now opens the current settings page, disclosed date text inputs and Actual details, and checks the current `日時を指定` label. Its semantic dates, Manual state, dependency, no-write, restart and exact publication-payload assertions remain intact. These are driver repairs, not relaxations of product acceptance.

The last ordinary solution build passed with zero warnings/errors. Earlier clean builds of the hosted project emitted the two SDK auto-initializer type-conflict warnings recorded in their logs. The new Summary driver nullable warning was subsequently corrected. Final production files match the frozen app's input hashes; see [source check](final-source-check.json) and the [frozen receipt](performance/frozen-candidate-final-v2-receipt.json).

## Ordinary visual review

The root operated the final frozen executable against isolated synthetic storage. The retained screenshots are unedited captures, not generated mockups. [Manual image mapping](manual-image-sources.json) identifies the four refreshed manual images.

- [First start](observations/final-empty-0.png) exposes the current connection action; invoking it reaches [connection settings](observations/final-connection-0.png).
- Saved work opens the native account picker. Selecting its account and P1 reaches the [ordinary Boards](observations/final-board-0.png).
- [Summary](observations/final-summary-0.png) shows B with allowance 10, estimate 8, Actual 7 and forecast 12 person-days, with excess 2. Selecting its task and entering Remaining 8 hours changes forecast to 8 days and [headroom to 2](observations/final-summary-corrected-0.png).
- [Daily correction](observations/final-summary-daily-0.png) opens the selected task with Actual 56 and Remaining 8 hours. Cancel returns to the same person/task. [One Undo](observations/final-summary-undone-0.png) restores Remaining 40 hours and excess 2 days.
- At a 998x794-pixel window with the sidebar open, the native [vertical scrollbar reaches the last person](observations/final-summary-narrow-vertical-0.png) while the labels remain in place. The [right endpoint](observations/final-summary-right-settled-0.png) and [left return](observations/final-summary-left-settled-0.png) retain column alignment after a 600 ms settling interval. One earlier immediate return capture is retained with labels between paint states; it is not used as stable endpoint evidence or proof of uninterrupted frame delivery.
- The app was reopened against the same store; the undone values remained. All three final Computer Use app processes were closed normally with Alt+F4 and confirmed absent afterward. Their exit codes were not captured; exit-0 claims above apply only to the instrumented diagnostics.

The intermediate `candidate-*` captures were rejected because the recording helper reused a baseline state. They remain in ignored local originals, and are not in this evidence package. Every final capture was passed explicitly to the corrected helper. Earlier defect images and failed tests remain available rather than being replaced by later success.

## Delivery and remaining decisions

This is one Issue-linked branch review unit; no main integration or user acceptance is claimed. The implementation addresses the three requested corrections without introducing a new grid dependency, changing remote mutation pacing, or adding later product features. Independent final reviews found no additional consequential defect within this bounded change.

The engineering results support continuing work on the PMO planning/review workflow. They do not establish that this product is superior to GitHub Projects, Azure Boards or the user's current process. The next product decision is a same-task comparison and human acceptance of the weekly workflow; GHEC/EMU and live remote-update throughput also remain distinct unverified gates. The retained 243 ms construction and 104 ms flush observations are not hidden, and no delay-free claim is made.

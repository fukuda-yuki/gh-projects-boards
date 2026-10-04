# Project replacement save notification

Moving the opening save from `EditingGrid` construction to its `Loaded` handler removes a redundant update of the table being replaced. The source change preserves the real draft session, writer lock, durable acknowledgement and explicit retry. This is a measured reduction in duplicate synchronous work, **not** a demonstrated improvement in total Project-switch or initial-display latency.

## Source and evidence boundary

The previous candidate is the frozen final-v2 executable documented in [the preceding performance record](../performance.md). The new executable is `TestResults/planning-build-dda0b6e2a63b478b9233a0eae3b8b8c3/app/GhProjectsBoards.App.exe`, built from `e20d9b5562ec2e7097e4581eb21f22ec2e229560` plus the recorded working-tree changes. Comparing the two receipts' production source hashes finds only `src/GhProjectsBoards.App/EditingGrid.cs` changed. The new [frozen receipt](performance/frozen-after-receipt.json) retains all build inputs and binary hashes. Its App DLL SHA256 is `BF16CA30A98645C13EA6E207DA948F1C5EBB53DD8DEB5E95737D520328D3AF34`; Core DLL SHA256 is `C1DBEB500A9C8D0E5F98CCB0D216AB6A89FE5C01B2E6FA319E7DAB351D73930A`.

The completed comparison uses the same `paced-selection-v4-horizontal-bulk` driver, 1,000 cached rows in each of two synthetic Projects, twelve single-select fields plus Title and Repository, ordinary/wide window sizes, 125% DPI, trace, timed frames and bulk-correctness phases. Each source has one completed run. Windows is `10.0.26200.0`, .NET runtime `10.0.12`; the ordinary app uses isolated real checkpoint storage with no GitHub connection or mutation. Fresh fixture timestamps and some focus-dependent local commit transitions differ, so the saved records are not byte-identical initial workloads.

[Comparison JSON](performance/comparison.json) retains the individual spans, nested updates, checkpoint traces and lifetime events. Complete [before](performance/before/app-trace.jsonl) and [after](performance/after/app-trace.jsonl) traces are copied here, alongside the [copy/hash manifest](performance/manifest.json). Inclusive nested spans must not be added together. Managed bytes exclude native XAML allocation. App callbacks and driver/GDI capture intervals are not physical presentation deadlines.

## Removed work and remaining time

Before the change, P2 construction starts a save while P1 is still loaded. Its saving notification runs P1's `session-inline` update, including a full status re-evaluation after P2 field initialization. The new table already calculated its status during construction. Starting the save once it is mounted lets its unchanged-revision presentation path handle the notification.

| P2 opening-save observation | Before | After |
| --- | ---: | ---: |
| Synchronous flush call | 103.8670 ms / 8,576,440 bytes | 1.0537 ms / 404,360 bytes |
| Saving-notification synchronous span | 103.5380 ms | 0.3123 ms |
| Update nested within that call | Old P1: 103.5210 ms / 8,173,840 bytes | Current P2: 0.2684 ms / 1,904 bytes |
| Old P1 aggregate nested in that update | 96.0484 ms / 7,651,880 bytes, generation-2 collection | Absent |
| Whole P2 constructor | 179.4060 ms / 46,805,344 bytes | 179.9048 ms / 36,919,472 bytes |
| Initial P1 constructor | 243.0461 ms | 342.7994 ms |

This is not the same expensive update deferred into the new table: the old aggregate is absent and the new nested update allocates only 1,904 bytes. The old handler can still receive a notification before `Unloaded`, but its existing `IsLoaded` guard prevents presentation work. The runtime trace shows P2 `grid-loaded` before P1 `grid-unloaded`; no assumed event ordering or new retirement state is required.

The total switch is not proven faster. In the new run a generation-2 collection coincides with P2's **constructor** aggregate (104.5990 ms), and its total constructor time stays near 180 ms. The initial constructor also takes longer in this single observation. Across the whole mixed-action run, aggregate count is 16 → 15 and aggregate managed bytes total 107,720,592 → 101,090,832; aggregate elapsed time is 347.1713 → 384.7405 ms. These mixed totals are context, not repeated identical latency samples. Full-row construction and horizontal presentation remain outside this repair; there is no 100 ms interaction guarantee.

## Save, navigation and lifetime validation

The new bounded UI integration case uses two actual `EditingGrid` controls, a real `DraftSession`/`DraftStore`, native child replacement and the real retry button. Its observed Red changes the old table's text from saved to saving merely by constructing an unmounted replacement. Green retains the old presentation, shows failure/retry only in the mounted replacement when the writer lock is held, and independently reloads both Projects' unchanged pending buffers after retry. Journal remains empty. Both `Loaded`-first and `Unloaded`-first native event orders occurred across successful executions.

Six selected final UI cases passed, with zero failures or skips, in `TestResults/ui-integration/run-20261005-003937-650-06df570a`. They cover the new lifetime case, invalid pending title retained when navigation cannot save, native CommandBar access after panel remount, overlay/inline cached-Project navigation, and Gantt failed-save retry. The previous broad 116-case selection did not include the two `HostedTests` navigation/remount cases; these are additional selected evidence, not inherited acceptance.

The attempts are retained rather than overwritten:

| Attempt | Executed / passed / failed / skipped | Interpretation |
| --- | --- | --- |
| `run-20261005-003449-882-baa03271` | 1 / 0 / 1 / 0 | Expected behavioral Red before production change |
| `run-20261005-003534-353-ac40d2eb` | 1 / 1 / 0 / 0 | New lifetime case Green |
| `run-20261005-003629-255-1324305e` | 5 / 3 / 2 / 0 | Existing valid-title pending expectation and hidden-command lookup failed |
| `run-20261005-003742-282-207d078c` | 2 / 1 / 1 / 0 | Old constructor-flush comparison: hidden command also failed; valid title depends on focus leaving |
| `run-20261005-003850-008-0185f574` | 6 / 5 / 1 / 0 | Invalid-title correction passed; command-helper edit initially hit the wrong similar test and was corrected |
| `run-20261005-003937-650-06df570a` | 6 / 6 / 0 / 0 | Final selected source |

The title test now uses invalid whitespace because the specification explicitly commits a valid ordinary title when focus leaves. It retains visible text, pending buffer, original confirmed value, selected Project, open navigation and no-write assertions. The remount case uses the existing native overflow helper; its row-creation assertion is unchanged. No contractual assertion was deleted to obtain Green.

The completed [ordinary-app run](performance/after/run.json) passed its one selected case with no skipped phase: 100 exact paste targets and 24,900 non-target fields were preserved, one Undo restored the prior field state, and Journal remained empty. PID 33196 closed normally with exit 0. The preceding [foreground-guard failure](performance/failed-foreground/run.json) stopped before Project switching and is not a performance result; its owned PID 37408 also closed normally with exit 0. An unrelated app at a different path was not touched.

## Pixel findings

The [bottom image](performance/after/05-bottom-after-wheel.png) shows rows through 1,000, and [paste](performance/after/19-bulk-pasted.png) / [Undo](performance/after/20-bulk-undone.png) show the expected Done/Todo transition. However, direct review of the immediate Project-return image exposes a remaining blank table: only the native title editor is visible. The same state exists in the previous candidate's image, which was not among the earlier report's inspected images.

- [Before, immediate Project return](performance/before/16-returned-project.png)
- [After, immediate Project return](performance/after/16-returned-project.png)
- [After, subsequent native input](performance/after/17-rapid-navigation-input.png)

The next image, after native navigation/input, shows rows again. Its capture begins about 1.07 seconds after the immediate image in both runs, but input and UI Automation inspection occurred in between. These images alone cannot establish whether the blank viewport recovers without action or how long it lasts. The driver has therefore gained a separate sparse GDI-only observation at 100/500/1,000 ms after the original immediate image, **before** that snapshot's UI Automation reads. It keeps the immediate image and existing sequence numbers. The follow-up observation is separate from the timing comparison above. UI Automation success and the passing journey do not make the blank pixels acceptable; human acceptance remains unrun.

The bounded [passive follow-up](performance/idle-return/run.json) completed its one selected case with no failures/skips on the same frozen product executable. The [original immediate image](performance/idle-return/16-returned-project.png) again shows the blank table. Rows are visible in the [first passive capture](performance/idle-return/16-returned-project-idle-100ms.png), and remain visible in the [500 ms](performance/idle-return/16-returned-project-idle-500ms.png) and [1,000 ms](performance/idle-return/16-returned-project-idle-1000ms.png) target images. No input, focus change, scrolling or UI Automation query occurs between the immediate image and all three captures.

| Target after immediate PNG completion | Actual capture interval after that boundary | Pixels |
| --- | --- | --- |
| 100 ms | 114.2967–153.5706 ms | Rows 1–12 visible |
| 500 ms | 507.7935–519.8395 ms | Rows visible |
| 1,000 ms | 1,006.1810–1,018.6600 ms | Rows visible |

[Raw timestamps](performance/idle-return/16-returned-project-idle.json) retain the immediate image's capture start as well. The first extra image spans 152.5710–191.8449 ms after that start. This run establishes spontaneous recovery in the sampled interval; it does not establish a persistent blank requiring user action. The sparse captures do not establish the exact first painted frame, a 100 ms deadline, continuous smoothness or absence of other blank episodes. No additional product change was made for this observation.

## Commands

The new lifetime Red/Green used `./scripts/Test-UiIntegration.ps1 -Where 'cat == GridLifetime'`. Final scoped validation used the same runner with the new category and the five navigation/remount/retry cases selected by their full test names/patterns; each metadata file retains the exact command and binary hashes.

The frozen app/seed were built with `./scripts/Start-PlanningCheck.ps1 -Scenario Load -DataRoot <absolute flush-loaded-load root> -PrepareOnly`. Both ordinary attempts used:

```powershell
./scripts/Test-SheetDiagnostic.ps1 -RunId <unique run id> -ItemCount 1000 -SelectFieldCount 12 -NoBuild -Executable <frozen executable> -SourceRevision e20d9b5562ec2e7097e4581eb21f22ec2e229560 -Trace -Frames -BulkCorrectness
```

For the passive-return observation, only the E2E driver was rebuilt using `dotnet build tests/GhProjectsBoards.E2E.Tests/GhProjectsBoards.E2E.Tests.csproj -c Release -p:BuildProjectReferences=false` (zero warnings/errors). The product executable and Core source were held fixed.

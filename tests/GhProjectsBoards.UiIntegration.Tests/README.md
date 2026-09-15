# Hosted WinUI integration

Run on Windows x64 with the repository's .NET/Windows App SDK prerequisites and an unlocked interactive desktop. No driver server, developer credentials, network fallback, machine installation or security-policy change is needed. This remains a native XAML application, not a headless test suite.

```powershell
./scripts/Test-UiIntegration.ps1
./scripts/Test-UiIntegration.ps1 -Where 'name =~ ApplyReview'
./scripts/Test-UiIntegration.ps1 -Discover
```

The runner builds Release by default, accepts NUnit selection expressions through `-Where`, uses unique `TestResults/ui-integration/run-*` directories, and rejects empty, failed, skipped or incomplete selections. `-NoBuild` requires an existing Release build. Source state/diff, source and binary hashes, OS/SDK, build/total elapsed time, process identity, stdout/stderr and NUnit XML results identify each execution. NUnit full test names are the stable identities. Discovery never counts as hosted execution.

## Mechanism and isolation

The executable references **the existing App assembly**, with narrow friend access to its internal controls and to Core/test collaborators. `HostedApplication` inherits the production App's compiled XAML resources/metadata and changes only startup. It mounts the real `RegistrationPanel`, `EditingGrid`, native editors, and production Apply selection/review/history dialogs. Neither copied views nor a second view implementation is compiled. The normal App has no reference to this executable, NUnitLite or other host dependencies.

Microsoft's [WinUI testing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/testing/) requires a XAML application/UI thread for XAML types and suggests library extraction. Direct App reuse works here, so no extraction is necessary. The host uses the existing .NET 10 / Windows App SDK 1.8.260804001 x64 self-contained targets. [NUnitLite 4.6.1](https://www.nuget.org/packages/NUnitLite/4.6.1) is pinned to match the existing NUnit 4.6.1, with its package's MIT license permitting commercial use without eligibility restrictions. NUnitLite runs the selected assembly and emits native NUnit XML; Core and FlaUI suites retain their existing framework/adapter.

Tests run serially within one process. Each case has a fresh workspace/session, synthetic gh boundary and real unique temporary store. Reused `CreationHarness`/`ApplyTests.Harness` supply actual orchestration, readers, journals and filesystem storage; only the gh process boundary is synthetic. Setup data is distinguished from actions under test. Actual button automation peers invoke production Click handlers; public native text/selector changes drive real events and selection. Assertions never call private handlers to stand in for these actions.

Mount/unmount awaits the **actual Loaded/Unloaded events**, then checks the visual root. A tracked UI synchronization context counts async-void events and posted continuations. Teardown releases controlled external work, hides dialogs, removes views, stops workspace operations and flushes real storage before checking quiescence. Asynchronous failure is sticky: it fails the owning case and prevents further successful cases in that process. Missing dispatcher, bounded condition/event timeout, incomplete async teardown or process hang is a failure. The process watchdog terminates only its owned host. No settling sleeps replace completion conditions. Stores and failure evidence remain local for inspection; they contain synthetic work only.

## Boundary map

This map defines where the behavior is maintained. Execution evidence and historical attempts live in the owning Issues/PRs. The identified Apply presentation assertions have a lower-layer replacement; other route adaptations do not retire their contractual coverage.

| Contract / prior E2E assertion | Logic / adapter / storage | Hosted primary coverage | Retained E2E / remaining risk |
| --- | --- | --- | --- |
| `LocalRowsSurviveOrdinaryExistingApplyAndExplicitSelection`: selection's incomplete-row explanation, candidate count, review's incomplete-row explanation (three assertions) | `CreationTests.MixedSelectionCreatesDistinctIdenticalTitlesAndPreservesUnselectedIncompleteRow`; `SelectedIncompleteLocalRowIsReportedInsteadOfSilentlyExcluded`; `LocalRowTests.ExistingApplyPreservesLocalRowsAndOnlySendsRemoteTargets`; actual sessions/stores | `ApplyReviewShowsSelectedUpdatesAndCreationsAndExcludesIncompleteRows(False/True)` verifies actual target count/IDs, destination/intents, pending exclusion, cancel/explicit dispatch | Same named E2E retains ordinary selection→review→Apply, exact request ID/count, pending-row retention, normal close and restart recovery. Required-case manifest unchanged |
| Loaded add, choice/clear, remove/Undo, rejected existing-row removal | Core `LocalRowTests` identity/atomicity/Undo and storage matrix | `LoadedGridAddChoiceClearRemoveUndoPreservesStableIdentity`; `RejectedExistingRowRemovalShowsErrorAndPreservesWork` | `LocalRowsContinuousEditingDuplicateRemoveUndoSwitchRestartAndRefresh` retains native focus/Tab, scrolling and restart. No wholesale case removal |
| Native TextChanging → pending buffer; later presentation cannot replace it | Core editing/buffer and real persistence tests | `NativeTextChangedPendingBufferSurvivesPresentationUpdate` | Physical `GridIme`, OS clipboard, candidate/composition and human natural input remain separate; assigning Text is not IME evidence |
| Cached/connected and busy/cancel/fail/success presentation; off-thread late result | Core `RegistrationWorkflowTests.LateResultAfterProfileSwitchCannotPublishOrSave` and `RegistrationStoreTests` | `CachedAndConnectedPanelReflectReadAvailability`; `ControlledRetrievalShowsBusyAndSettledStates`; `OffThreadChangeAndLateTransitionResultKeepCurrentWorkspace` | Connection/registration ordinary process journeys and owned-child cancellation/close remain |
| Loaded lifecycle, delayed command/discovery/review and workspace replacement | Real sessions/stores; controlled completion at external response or UI scheduling boundary | `DelayedGridCommandCannotCrossUnloadRemount`; `PanelUnloadReleasesWorkspaceRefreshGuardAndRemounts`; `LateDiscoveryCannotPopulateReinitializedPanel`; `DelayedReviewCannotOpenDialogAfterRemount` | Native focus/composition timing and process shutdown remain E2E/IME risks |
| History disabled throughout dialog continuation, one resume, recovery without duplicate dispatch | Core Apply executor/journal/state regressions | `HistoryRemainsDisabledUntilOutstandingContinuationSettles`; `SelectionCancellationDoesNotDispatch` | `OrdinaryApplyReviewsTitleThenVerifiesAndRestoresHistoryWithoutReplay`, `InterruptedApplyRetainsJournalAndNeverRestartsWrites` retain process/restart risks |

CI builds this executable and discovers its cases without starting XAML. UI execution on `windows-latest` has not been established and is not enabled. Local hosted execution is required for delivery. The moved group measures development feedback/setup work, not product latency; #12's performance attribution, field breadth and human acceptance remain open.

## Project columns

`ColumnHostedTests` mounts the same production grid with three same-named single-select fields, two scoped Projects, local rows and isolated real checkpoints. It exercises actual settings buttons/checkboxes/NumberBox events for Save/Cancel/Reset, invalid width retry, stale definitions, identity-preserving selection, hidden selection clearing, original-key Undo, retained native pending text and ignored late text from a replaced editor. Alignment assertions compare actual transformed X positions as well as widths. `HostedTests.ColumnHiddenDifferencesRemainInExistingAndCreationReview` uses the ordinary settings and Apply dialogs to verify hidden IDs and intents remain reviewable.

Core `ColumnTests` owns canonical/visible mapping, mixed-row atomic transactions, hidden-field append/duplication, malformed schema, v1–5 migration and real-storage failure/restart/history preservation. Ordinary `ProjectColumnsClipboardOriginalUndoAndActualRestart` covers native clipboard, range retention on width change and two-Project process restart. `ProjectColumnsPhysicalDirectAndF2PendingTransition` separately covers physical direct/F2 Japanese input and natural composition/focus boundaries. Public Text assignments in this host are never IME evidence.

## Workspace presentation and view collaboration

These cases use the real production grid/panel/dialogs and their public control/event paths. `ColumnTests.Project`, `EditingTests.Registration` and the existing creation harness provide scoped synthetic setup; sessions and checkpoint stores are real. The history case prepares uncertainty through the real executor with the external response controlled, then verifies the presentation and keep-on-hold action. It does not claim to reproduce a whole user session.

| Observable behavior | Hosted cases / assertions | Other boundary retained |
| --- | --- | --- |
| Scroll without losing column or selected-work context | `SheetHostedTests.ScrollingKeepsColumnContextAndSelectedPendingDetailsWithoutChangingWork`: real vertical/horizontal ScrollViewer movement, header/row transformed alignment, visible selected pending text/identity, unchanged committed differences | Ordinary `WorkspaceReconstructionSession` checks public UIA scrolling and captures the actual window at representative sizes; visual density is evaluated from the running app |
| Return from settings to replacement input; resume pending input at its caret | `SettingsFocusHostedTests.SettingsSaveRestoresCommittedTitleReplacementAndCancelPreservesPendingCaret(columns/rows)`: actual settings Save/Cancel, retained identity, native selection, SelectedText→TextChanging and unchanged committed value | Physical keys, F6 rectangle retention and real clipboard remain in the ordinary session; Japanese composition remains separate `GridIme` evidence |
| Theme changes retain active pending work | `ThemeHostedTests.ChangingWorkspaceThemeKeepsFocusedSelectedPendingTitle(Light/Dark)`: actual requested-theme transition, editor/focus/selection/buffer retention and rendered PNG attachment | Render capture does not itself establish color contrast or visual acceptance; ordinary full-window themes, OS High Contrast and other DPI settings require their own inspection |
| Navigation/settings/status remain usable around local saves | `WorkspaceShellHostedTests.cs` extends `HostedTests`: `DraftSavePreservesCollapsedNavigationActiveProjectAndKeyboardFocus`, `ProjectSettingsFlyoutSavesDefaultDestinationWithoutChangingExistingRows`, `StatusDetailsRemainReadableWhenNoProjectIsSelected` | Ordinary startup, connection→workspace navigation, collapsed-pane public UIA routes and actual process restart belong to the ordinary-app journeys |
| Readable view candidates preserve saved rules and work | `ColumnCandidatePreviewShowsVisibleOrderAndDuplicateIdentityBeforeSave`, `CandidateSummaryExplainsCombinedFiltersAndResetWithoutChangingSavedView`, plus existing column/row Save/Cancel/Reset, zero-match, stale-candidate and hidden-buffer cases | Core `ColumnTests`/`RowViewTests` own ID mapping, predicates, migration and atomicity; ordinary sessions retain native clipboard and restart |
| Row visibility does not silently approve work or retarget delayed actions | `FilteredApplyCandidatesRequireExplicitHiddenInclusion`, `PromotionNotificationKeepsDisplayedPositionAndActiveIdentity`, `LateClipboardRejectsChangedDestinationMapping`; the clipboard task is controlled before completion | Core owns approved-payload immutability, fresh-membership and promotion rules; ordinary Apply/creation/restart journeys retain external process integration |
| Recovery history exposes destination, stage and uncertainty without another creation | `WorkspaceApplyPresentationTests.cs` extends `HostedTests`: `UncertainCreationHistoryShowsDestinationStageAndContextualResolutionWithoutRetrying`; existing mixed selection/review cases retain exact intended identities and values | `CreationTests` owns uncertainty/lineage rules; ordinary mixed creation and URL-binding/restart journeys retain full application coverage |
| B/L/R distinguishes local clear, known empty and unavailable remote values | `ComparisonPresentationTests.ComparisonDistinguishesLocalClearKnownEmptyAndUnconfirmedRemoteWithoutChangingDraft(present/empty/unavailable)`: rendered labelled values, accessible comparison group, resolution availability and unchanged local intent on close | Core reconciliation owns resolution rules; native ordinary comparison and human choice/readability checks remain separate |

Command-bar cases wait for the actual overflow button to load before invoking its automation peer. Settings, discovery and unregister cases follow the corresponding flyout/expander before operating controls. Dialog footer lookup excludes content buttons. These routes retain existing assertions about values, identities, lifecycle and prohibited writes; a different screen arrangement is not a reason to duplicate every branch through E2E. The public UIA tree can differ from the in-process XAML tree, so the ordinary runner follows its exposed navigation surface independently.

```powershell
./scripts/Test-UiIntegration.ps1 -Where 'class =~ SettingsFocusHostedTests'
./scripts/Test-UiIntegration.ps1 -Where 'class =~ SheetHostedTests or class =~ ThemeHostedTests or class =~ ComparisonPresentationTests'
```

Use [the ordinary workspace walkthrough](../../docs/workspace.md) for complete sessions. A focused hosted pass establishes only its selected collaboration; it does not establish whole-application, physical-IME, live-service or human acceptance.

## Runner fault verification

These intentionally failing infrastructure probes are excluded from the normal product suite. Run each in a separate process; an expected failure must have a nonzero runner exit, not just matching text in a log.

```powershell
./scripts/Test-UiIntegration.ps1 -Where 'name == MissingCase' -NoBuild
./scripts/Test-UiIntegration.ps1 -Where 'name == AssertionFailure' -NoBuild
./scripts/Test-UiIntegration.ps1 -Where 'name == AsyncFailureAfterAssertion' -NoBuild
./scripts/Test-UiIntegration.ps1 -Where 'name == IncompleteTeardown' -NoBuild
./scripts/Test-UiIntegration.ps1 -Where 'name == HungHost' -NoBuild -TimeoutSeconds 3
```

Keep these separate from product pass counts. `AsyncFailureAfterAssertion` releases its failing event from teardown, after the assertion; `IncompleteTeardown` deliberately never completes an event. Failure-path root removal is recorded. The ordinary suite must still pass after each isolated fault process.

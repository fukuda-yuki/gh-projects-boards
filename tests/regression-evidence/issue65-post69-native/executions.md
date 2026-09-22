# Execution ledger

All new executions below ran on Windows 10.0.26200 x64, .NET SDK 10.0.401, Windows App SDK package 1.8.260804001, at 125% display scale. The product and drivers were non-elevated. Only the user-approved, bounded CPU/XAMLActivity WPR controller was elevated. No live GitHub mutations, machine configuration changes or package changes occurred.

## Source and binaries

| Population | Product source | App DLL SHA256 |
| --- | --- | --- |
| Candidate native cold/warm and short before | `94922ffd17489b464bf8f23376e702ad1ef8600f` | `DEEB7F5387AD67C84E6476890738597740269E6CD5384B03201A3FB012AC7470` |
| Frozen correction: short after, final six, thumb journey | `b66d8f9d4099d320f32465d7b6b183fb6efcc586` | `5D5243363C0A96D4193D35F17DD0A8DF4FA24DE946D5CFE9277FDD193EA3DDF6` |
| Rebuilt ordinary guards and final additional fill host | Same corrective source | `B837EEF4E01975877BD50EC898BD6030CC31D0E87B42888FD5FB485CB95A6ECC` |

The frozen correction was built before the product commit; its exact product changes were then committed. First UI-host build source metadata is main `1854d4b...` plus the retained two-file source patch. Later source manifests name `b66d8f9...`. A rebuilt ordinary test executable is identified separately, not substituted for the frozen performance binary. Full executable/Core/PRI/driver hashes are in the archive. Driver SHA256 is `9035A4E6F8CCC458D093265618D652871338FA3F4574B69F698A64AC1222620D`; its source is unchanged between the retained driver checkout and this branch.

Before/after frozen local XAML DLLs have identical SHA256 `95F5F5B7CEC1CA2FA30580121F5298122E32DB96ED5F04D9D1A74224511E5F0C`, PE CodeView GUID `72f0bfb0-e2d3-cb2c-5f24-fe090eae2608` / age 2, and embedded resource string `3.1.8.2608`. Trace module metadata agrees with that identity. Win32 FileVersionInfo/ordinary loaded-module receipts display `3.1.8.2503`; that display string is preserved, not used to infer a dependency change. The binary hash and CodeView match establish the comparison identity.

## Executed checks

| Execution / artifact directory | Executed / passed / failed / skipped | Scope and result |
| --- | --- | --- |
| `run-20260922-171437-910-639b2ef2` | 5 / 5 / 0 / 0 | Initial actual-control viewport, long-title, focused/pending and keyboard range collaboration. Build: 0 errors, 2 existing CS0436 warnings. |
| `post69-native-candidate-cold-01`, `post69-native-candidate-warm-01` | 1 / 1 / 0 / 0 each | Profiled ordinary-app state/persistence diagnostics. Attribution samples, not performance acceptance. Named WPR session finalized successfully; 0 lost events. |
| `post69-viewport-before-short-01`, `post69-viewport-after-short-01` | 1 / 1 / 0 / 0 each | Unprofiled warm before/after discrimination; 2 -> 0 corroborated delayed commands, but 4 slow callbacks in both. |
| `post69-viewport-final-{cold,warm}-{01,02,03}` | 1 / 1 / 0 / 0 each | Six unprofiled state/persistence passes; scroll gate FAIL. Preserve all 17 slow callbacks, 3 corroborated delays and 4 visible delays with incomplete attribution. |
| `run-20260922-172806-143-4e70b9f4` | 13 / 13 / 0 / 0 | Final bounded real-control collaboration; 56.296 s. Gantt/lifetime guards included. |
| `20260922-172923-a6c85b5904604b9999aa7d62c874378e` | 3 / 3 / 0 / 0 | Ordinary Gantt edit/Project switch/restart and direct/F2 physical IME; 26 s. Release solution build: 0 errors, 2 existing CS0436 warnings. |
| `post69-viewport-thumb-01` | 1 / 1 / 0 / 0 | Ordinary native-bar drag to row 1,000, immediate input, horizontal/vertical return, durable pending identity. Functional/native evidence, not a timed performance campaign. |
| `run-20260922-173916-151-87e0e0c4` | 1 / 1 / 0 / 0 | Actual pointer fill at viewport edge reaches row 100, commits on release, and operation Undo preserves the source edit; 8.183 s. |

The 5 initial and 13 final cases overlap; do not sum them into unique behavioral coverage. Hosted cases use the real EditingGrid/workspace and native controls. The ordinary diagnostic uses the principal application layers through persisted synthetic output; its process/native mechanism does not replace the lower-layer coverage. The two IME cases prove the selected physical composition/focus boundary only, not full IME acceptance. Test XML/TRX and original commands are retained in `raw-evidence.zip` and indexed in `executions.json`.

## Commands and workload

The retained `i65-residual/scripts/Test-SustainedInput.ps1` producer was invoked with an absolute frozen `-Executable`, exact `-SourceRevision`, unique `-RunId`, `-Condition cold|warm`, `-Mode standard` and `-TraceDetail light`. The thumb journey uses `-Mode scroll`. Standard workload: 1,000 tasks, 20 fixture people, six fixture fields (eight visible columns), 1080x760 window, 20 s Title, 20 s NUMBER and 20 s alternating scrolling; warm adds the existing 10 s input prehistory. Pending input, Undo and durable readback are retained. Fixture preparation and test commands, exact hashes and source manifests are recorded per run.

The final UI command is recorded verbatim in its `metadata.json`; it selects SheetFocusedScrollHostedTests, SheetViewportHostedTests, ThemeHostedTests, the pending Gantt Save and 1,000-task Gantt measurement cases, ReviewRetention, body-drag cancellation and Shift-arrow range fill/Undo. The final addition is:

```powershell
./scripts/Test-UiIntegration.ps1 -NoBuild -Where 'name == FillAtViewportEdgeScrollsToHundredthRowAndCommitsOnlyOnRelease' -TimeoutSeconds 120
```

The ordinary command is:

```powershell
./scripts/Test-E2E.ps1 -Filter 'FullyQualifiedName~OrdinaryGanttThousandTaskEditProjectSwitchAndRestartRetainOnePlan|(FullyQualifiedName~ProjectViewSwitchDoesNotEndNativeCompositionOrCommitTheCell&Name~Gantt)'
```

The retained event decoder and details extractor were built locally against existing TraceEvent 3.1.27, with zero errors and respectively 1 / 5 CS0618 QPC warnings. Original source and extracts remain unchanged; the new decoder refuses an existing output directory. A details-project relative copy path and a package-script syntax typo were corrected before their successful builds/use; neither is a product behavioral Red. Native symbol/conversion logs remain private. No executed behavioral test failed or was skipped; performance observations still fail their separate gate.

## Omitted and limited coverage

Core logic/GitHub/storage contracts did not change; no full Core or live campaign was rerun. No new human, High Contrast, alternate DPI, Narrator, full P1/P2, Summary or release acceptance was performed. No heavy native trace of the viewport correction was requested after the completed post-69 comparison. Work reduction on that correction is app row-realization evidence and unprofiled replay, not a second native attribution claim. Gantt guards are not a controlled speedup comparison; report postprocessing was active for the beginning of that hosted run. Build/discovery is never counted as behavioral execution.

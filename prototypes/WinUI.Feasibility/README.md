# WinUI input feasibility probe

Scope and acceptance: [Issue #22](https://github.com/fukuda-yuki/gh-projects-boards/issues/22). This is a three-row, two-column input probe, not a product UI or the 100-row acceptance spike. Results and migration plans belong to the owning Issue.

## Build and run

On Windows with .NET 10 SDK and Windows SDK 26100:

```powershell
dotnet build prototypes/WinUI.Feasibility/WinUI.Feasibility.csproj -c Release -p:RestoreLockedMode=true
./prototypes/WinUI.Feasibility/bin/Release/net10.0-windows10.0.26100.0/win-x64/WinUI.Feasibility.exe
./scripts/Test-WinUiFeasibility.ps1
```

The probe intentionally evaluates unpackaged x64 activation with application-local Windows App SDK and .NET runtimes. It installs no runtime globally and performs no package registration or certificate trust changes. This is an evaluation of one distribution candidate; it does not establish clean-machine or enterprise distribution acceptance. The output directory must travel together; copying only the exe is insufficient.

The probe contains no gh integration, storage, network calls, clipboard paste, Undo, or custom input hooks. Select the second Title cell, then type physical N/I/H/O/N/G/O with Japanese IME enabled. Compare direct selection with F2 and the standard TextBox. Selection must remain distinct from editing. Do not replace this test with Unicode insertion. When the entry gate fails, retain the failure before implementing the wider five-column/100-row or connection scenarios.

## Automation

`WinUiFeasibilityTests` uses the existing NUnit/FlaUI UIA3 test project with a separate opt-in category. Test assemblies currently remain WPF-targeted; the launched prototype has no WPF reference. This proves only the scenarios actually executed against WinUI, not removal of WPF from the harness.

The script saves TRX, per-key JSON, UIA text, and window screenshots in a unique `TestResults/issue22/probe-*` directory. Runs are serial. Keep other input away from the desktop. The script fails on missing results, zero execution, any failed case, or any skipped case. Screenshots can contain pixels outside the client area or overlapping windows; review before sharing. Human composition/candidate selection/confirmation/cancellation/reconversion, rectangular selection, and operation history remain distinct acceptance gates.

The physical-key stimulus follows `ImeComparisonTests.cs` in PR #21 at `73e4cbd1d0ef142895477b308d070222570a2e65`. No WPF input repair code was transferred.

## Follow-up input design comparison

[Issue #24](https://github.com/fukuda-yuki/gh-projects-boards/issues/24) owns the separate read-only-to-editable experiment. Its baseline is the unmerged PR #23 head `cb65fa826453f5b91e7aa3812c9b34cd25d9d2d9`; the follow-up branch and PR must show only the incremental diff against that branch until integration is reviewed.

Run `./scripts/Test-DirectImeDesign.ps1` on the same controlled Japanese IME desktop. It runs six cases: direct/F2 entry in a TableView custom column and in a standard-control three-row panel, plus direct entry with IME verified active in a reference TextBox before selecting each design. The script requires all six to pass; a failed design is not adopted. It preserves source/test/script hashes, commit/dirty state, environment, TRX, per-event logs and scoped screenshots in a unique `TestResults/ime-followup/run-*` directory.

For manual inspection, set process-local `GHPB_IME_MODE` to `column` or `standard` before launching the same ordinary exe; leave it unset for the unchanged #23 control. Optional `GHPB_IME_TRACE` specifies a local synthetic event-log file written on close. These are probe controls, not product configuration. `InputProbeWindow` uses a native read-only TextBox while selected, toggles the same element's public `IsReadOnly` property in `OnPreviewKeyDown`, and records composition/focus/commit events. No key or text is replayed. Committed row values remain separate from editor text; only the explicit cell commit writes them.

The standard-control comparison is a small input experiment, not a replacement for the product grid. Range selection, full candidate-selection/cancellation/reconversion and human usability still require their own evidence; passing F2/Enter checks cannot waive direct-input failure. The original four-case probe and its failure predicate remain unchanged. No dependencies or permanent UI driver were added.

Versioned API sources: [TableView v1.4.1 cell lifecycle](https://github.com/w-ahmad/WinUI.TableView/blob/v1.4.1/src/TableViewCell.cs), [public column element creation](https://github.com/w-ahmad/WinUI.TableView/blob/v1.4.1/src/Columns/TableViewColumn.cs), [TextBox.IsReadOnly, App SDK 1.8](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.isreadonly?view=windows-app-sdk-1.8), and [TextCompositionStarted](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textcompositionstarted?view=windows-app-sdk-1.8). A public API is a feasibility candidate, not evidence of correct IME timing.

## Components and maintenance

Exact direct/transitive versions and content hashes are in `packages.lock.json`. Direct probe pins are Windows App SDK `1.8.260804001` and WinUI.TableView `1.4.1`. See [third-party notices](THIRD-PARTY-NOTICES.md). No MVVM library or second UI driver is introduced. The application maintainer must review upstream servicing and licenses, refresh the lockfile explicitly, and rerun the entry gate for every candidate update. Never update the installed Windows runtime to make this probe pass.

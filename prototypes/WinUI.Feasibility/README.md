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

## Components and maintenance

Exact direct/transitive versions and content hashes are in `packages.lock.json`. Direct probe pins are Windows App SDK `1.8.260804001` and WinUI.TableView `1.4.1`. See [third-party notices](THIRD-PARTY-NOTICES.md). No MVVM library or second UI driver is introduced. The application maintainer must review upstream servicing and licenses, refresh the lockfile explicitly, and rerun the entry gate for every candidate update. Never update the installed Windows runtime to make this probe pass.

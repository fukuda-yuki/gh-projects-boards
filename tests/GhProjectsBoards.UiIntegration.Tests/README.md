# Hosted WinUI integration

The host references the production App assembly and mounts real WinUI views with the production resources. It runs NUnitLite serially on an interactive Windows desktop. It is not a headless substitute or an ordinary-app E2E runner. Dependencies remain pinned in the project; no new driver, service or machine configuration is required.

```powershell
Set-Location C:\w\g76
dotnet build C:\w\g76\tests\GhProjectsBoards.UiIntegration.Tests\GhProjectsBoards.UiIntegration.Tests.csproj -c Release --no-restore
C:\w\g76\scripts\Test-UiIntegration.ps1 -NoBuild -Where 'cat == PlanWorkspace'
```

`PlanWorkspaceHostedTests` mounts `PlanWorkspaceView` with real document/session/store, scheduler, reader and publisher collaborators. A separate fake-gh process supplies external responses and isolated files hold real persisted work. Public control events/automation peers drive connection, Project selection, settings and explicit field setup. The file-picker seam substitutes only the native picker; CSV/JSON parsing and persistence remain real.

The cases cover mapped task presentation, per-Project switching and restart restoration, explicit missing fields, rate/company/personal-day recalculation with one Undo, holiday and settings-file roundtrip, draft/PR counts, invalid settings and failed reconnection. Real file-lock cases verify save recovery and failed Project-switch isolation. Controlled fake-gh responses verify that close cancels and waits for the owned subprocess during connection, opening and refresh. A forty-Project fixture verifies scrolling and command reachability; native calendar date lists cover selection without an edit, addition, duplicate no-op, removal and Undo. Native TextBox assignment establishes event wiring, not physical IME behavior. Ordinary process restart, native picker, real GitHub and human usability require separate checks.

`EditingGrid`, `BulkEditingHostedTests` and other directly hosted grid collaborators remain for Phase 6 adaptation. Their existing native-input and clipboard cases require the relevant desktop facilities. They are not current workspace acceptance. RegistrationPanel/ConnectionPanel/Apply navigation tests were retired with those views.

The runner writes unique `TestResults/ui-integration/run-*` directories with source/diff, hashes, OS/SDK, elapsed time, process identity, logs and NUnit XML. `-NoBuild` requires matching binaries. Empty, skipped, failed or incomplete selections fail. Discovery never counts as execution. Mount/unmount waits for actual lifecycle events; asynchronous errors remain failures, and teardown flushes owned work. The watchdog only terminates its owned host.

Infrastructure fault probes remain opt-in: `AssertionFailure`, `AsyncFailureAfterAssertion`, `IncompleteTeardown`, and `HungHost`. Run each separately; an expected failure requires a nonzero runner exit. Keep these apart from product counts. See [test policy](../README.md) for boundary selection and evidence rules.

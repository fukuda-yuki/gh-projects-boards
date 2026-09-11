# Decisions

Sources: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2) and [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13). Record each resolved choice with its reason and source. A proposed technology is not an accepted decision, and a skeleton does not complete either Issue.

## UI and runtime

**Decision:** Use WPF with .NET 10 (`net10.0-windows`) for the minimal application skeleton. The user selected this option on 2026-09-10 while resolving the open UI/runtime choice in #2.

**Reason:** WPF provides a Windows desktop window in the agreed C# environment, and .NET 10 is an LTS release. See the official [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) and [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

**Limit:** This chooses the application shell, not a grid component or storage technology. The grid spike has a separate decision below; other #2 choices remain open.

## Editing-grid component

**Decision:** Do not adopt the evaluated WPF DataGrid prototype for production editing. Keep it reachable in the ordinary executable as an offline evaluation artifact. [#19](https://github.com/fukuda-yuki/gh-projects-boards/issues/19) owns the evidence and measurements.

**Follow-up boundary:** Retain WPF DataGrid as a candidate during [#20](https://github.com/fukuda-yuki/gh-projects-boards/issues/20). A standard DataGridTextColumn with writable TwoWay binding reproduces the same direct-start failure in the evaluated environment, while its F2 path and a standalone TextBox retain the first syllable. This localizes the observed failure to the transition into editing, without establishing its internal WPF/IME cause or proving that all small repairs are impossible. No demonstrated repair is adopted. Cell selection remains separate from editing; the unsuccessful timing/state experiments are retained in #20 rather than production code.

**Reason:** Direct Japanese composition from a selected cell drops the first key in the evaluated Microsoft Japanese IME environment. The loss was reproduced with FlaUI physical keys and separate, slowly paced native UI operations. F2 before composition avoids that path, but requiring users to remember a workaround is insufficient for the intended editing experience. Cell/range editing, atomic validation and Undo can be supplied with app-local code; that does not compensate for silent text loss.

**Implementation burden:** Standard DataGrid provides navigation, selection, virtualization, and native TextBox/ComboBox editing. Operation-level Undo, atomic TSV interpretation/validation, blank-versus-clear behavior, stable row identity, and explicit new-row handling are supplemental implementation. A small text-column subclass keeps one-way initialization editable while retaining native input handling; reading the editor document at commit handles observed reconversion/Text-property divergence. Direct IME startup remains unresolved.

**License and limit:** [WPF is MIT-licensed](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT); this prototype adds no component fee, package, or assembly. This does not choose the application license. The rejection applies to the evaluated prototype/environment, not every possible WPF customization or IME. Further lifecycle work needs a demonstrated cause; alternative-component evaluation and changes to selection behavior require a separate scope decision. Any external adoption requires cost/license review and authority for dependency changes. Persistence, real GitHub field mapping, apply, and broader #2/#7/#12 acceptance remain separate.

## Automated testing

**Decision:** Use NUnit across unit, integration, and desktop E2E tests; use FlaUI with UIA3 for native WPF E2E. The user delegated E2E tooling selection and introduction on 2026-09-10. Interpret controller-level integration in WPF as ViewModel/command plus real application-logic collaboration. See [test boundaries and execution](../tests/README.md).

**Reason:** [FlaUI](https://github.com/FlaUI/FlaUI) automates WPF using Windows UI Automation without a separate driver server. [NUnit apartment control](https://docs.nunit.org/articles/nunit/writing-tests/attributes/apartment.html) and [nonparallel execution](https://docs.nunit.org/articles/nunit/writing-tests/attributes/nonparallelizable.html) support desktop-test requirements while allowing one assertion framework across levels. [FlaUI UIA3](https://www.nuget.org/packages/FlaUI.UIA3/5.0.0) and [NUnit 4](https://www.nuget.org/packages/NUnit/4.6.1) are MIT-licensed; these are test-only dependencies, not a choice of application license. Exact direct dependency versions live in the test project file.

**Limit:** CI executes deterministic unit/integration tests and discovers desktop tests. Desktop E2E runs locally on an interactive Windows desktop. Live GitHub tests are separate, opt-in, and limited to the designated sandbox; they do not establish GHEC + EMU compatibility. NuGet compatibility and compilation do not establish runtime reliability.

## GitHub authentication and process boundary

**Decision:** Use stored gh authentication and internal app-local connection/API classes for [#3](https://github.com/fukuda-yuki/gh-projects-boards/issues/3). Remove all four gh token environment overrides from child processes. Project only non-token authentication metadata; require recognized keyring storage for writes. Keep plaintext and unknown storage diagnosable without permitting writes.

**Reason:** [gh environment variables](https://cli.github.com/manual/gh_help_environment) can override stored accounts. [gh login](https://cli.github.com/manual/gh_auth_login) can fall back to plaintext storage, and [JSON auth status](https://cli.github.com/manual/gh_auth_status) can exit zero despite an authentication problem. These behaviors require explicit diagnostics and child-process policy; extracting tokens would create a second credential owner.

**Decision:** Use `ArgumentList` and UTF-8 JSON stdin with explicit hostname/target, asynchronous execution, a 30-second process timeout, cancellation, and structured outcomes. Bind to stable viewer ID and recheck before dispatch. A failed or uncertain write is never automatically resent.

**Reason:** Issue text must remain data, and incomplete responses must not become duplicate writes. Keeping the adapter in the application and adding only the authorized NUnit project supplies the current behavior without committing to later workspace/storage abstractions.

**Limit:** Preflight does not atomically prevent another process from switching gh authentication between commands. The adapter relies on the selected gh executable and operating-system credential store. Current connection state is in memory; persistence and company-environment validation remain separate work.

## Remaining choices

| Topic | Status | Required follow-up |
| --- | --- | --- |
| Grid component | Prototype not adopted; WPF DataGrid retained as a candidate in #20 | Resolve the demonstrated direct-start failure before adoption; standard comparison and failed repairs are recorded in #20 |
| Local persistence | Pending; SQLite is a candidate in #2 | Decide from draft and recovery requirements before #8 implementation |
| Editable fields and item types | Pending | Define supported, read-only, and excluded cases in #2 |
| External components and application license | Pending | Record component licenses and the repository license policy in #2 |
| Distribution | Pending | Decide Windows/CPU support, runtime packaging, gh installation, signing, and delivery in #13 |

## Validation limits

Grid usability, storage recovery, and GHEC + EMU compatibility require their own evidence. Live sandbox verification establishes only the exercised operations. Do not infer broader product acceptance from compilation, deterministic tests, or connection diagnostics.

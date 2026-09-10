# Decisions

Sources: [#2](https://github.com/fukuda-yuki/gh-projects-boards/issues/2) and [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13). Record each resolved choice with its reason and source. A proposed technology is not an accepted decision, and a skeleton does not complete either Issue.

## UI and runtime

**Decision:** Use WPF with .NET 10 (`net10.0-windows`) for the minimal application skeleton. The user selected this option on 2026-09-10 while resolving the open UI/runtime choice in #2.

**Reason:** WPF provides a Windows desktop window in the agreed C# environment, and .NET 10 is an LTS release. See the official [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) and [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

**Limit:** This chooses the application shell, not a grid component or storage technology. Grid suitability still requires the prototype and license checks in #2.

## Remaining choices

| Topic | Status | Required follow-up |
| --- | --- | --- |
| Grid component | Pending | Prototype paste, IME, keyboard editing, Undo, and 100-row responsiveness in #2 |
| Local persistence | Pending; SQLite is a candidate in #2 | Decide from draft and recovery requirements before #8 implementation |
| Editable fields and item types | Pending | Define supported, read-only, and excluded cases in #2 |
| External components and application license | Pending | Record component licenses and the repository license policy in #2 |
| Distribution | Pending | Decide Windows/CPU support, runtime packaging, gh installation, signing, and delivery in #13 |

## Validation limits

Grid usability, storage recovery, live GitHub integration, and GHEC + EMU compatibility require their own evidence. Do not infer them from compilation or an empty application window.

# Decisions

Record accepted choices and their rationale. Keep task progress and experimental findings in the owning Issues.

## Platform

Use **C# + .NET 10 + WinUI 3 / Windows App SDK** for the native Windows desktop app. Native controls and public Windows APIs provide the UI boundary; application rules remain in one UI-independent Core library. Implement screens from their behavioral contracts, not from another framework's visual tree.

Windows App SDK is pinned to `1.8.260804001` in the app project. The development target is `net10.0-windows10.0.26100.0`, x64, with minimum platform 19041. The development executable is unpackaged and self-contained to make ordinary-executable checks explicit. These are build settings, not a final supported-device or distribution promise. See [Microsoft WinUI 3 documentation](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) and [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13).

No grid library, MVVM framework or persistence technology is selected by the platform decision. Select dependencies only for demonstrated requirements and acceptable unconditional commercial terms.

## Testing

Use NUnit for logic, integration and desktop tests, and FlaUI UIA3 for the ordinary WinUI executable. No separate driver server or second permanent UI driver is required by this design. Exact test dependency versions live in the project files.

Core tests exercise real application collaborators. The fake gh executable controls the external process boundary and has no network fallback. E2E uses stable AutomationIds, user operations and condition-based waits rather than private application calls or an assumed grid peer shape.

Keep deterministic CI, interactive desktop E2E, physical-key IME automation, human IME acceptance, performance and live GitHub validation separate. Discovery and compilation do not establish runtime acceptance. See [test policy](../tests/README.md).

## Authentication and process ownership

Use stored gh authentication. Exclude all four gh token environment overrides from children, expose only safe authentication metadata and require recognized keyring storage before writes. Plaintext and unknown storage remain diagnosable without permitting writes.

Use `ArgumentList`, UTF-8 JSON stdin, explicit hostname/target, asynchronous execution, a 30-second process timeout, cancellation and structured results. Bind stable viewer identity and recheck before dispatch. Never automatically resend a failed or uncertain write.

These rules avoid a second credential owner and keep issue content as data. See [gh environment variables](https://cli.github.com/manual/gh_help_environment), [login](https://cli.github.com/manual/gh_auth_login), [auth status](https://cli.github.com/manual/gh_auth_status) and [specification](spec.md).

Preflight cannot atomically prevent another process from switching gh authentication. Connection state is in memory. Persistent workspaces and company-environment verification have their own acceptance criteria.

## Decision ownership

| Topic | Owner |
| --- | --- |
| Supported field/item matrix and component suitability | #2 |
| Few-row Japanese input contract | #24 |
| Table editing, selection, paste and Undo | #7 |
| Local persistence and recovery | #8 |
| Cross-feature E2E and performance | #12 |
| Distribution, component notices, signing, update/rollback and enterprise validation | #13 |

Required dependencies must not require paid licensing or company-size/revenue eligibility. Local development permission is distinct from binary redistribution permission. Resolve the actual output-to-license/notice manifest before a release; build output alone is not approval to distribute it.

The parent Windows App SDK and its DWrite/Widgets packages have different redistribution wording. Their applicability to the app-local payloads remains unresolved and blocks distribution under #13. The approved transfer from #22 is a scope decision, not license clearance. Preserve the exact [terms and package provenance](dependencies.md).

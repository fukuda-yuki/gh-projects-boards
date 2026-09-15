# gh-projects-boards

A native Windows desktop application built with **C#, .NET 10 and WinUI 3 / Windows App SDK** for preparing GitHub Issue and Project changes in a table.

GitHub remains the source of truth. Editing is local; only an explicit apply operation publishes changes. Product requirements and acceptance belong to [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) and its linked Issues.

## Build and run

Use Windows x64, the .NET 10 SDK and Windows SDK 10.0.26100.0. Run from the repository root:

```powershell
dotnet build GhProjectsBoards.sln --configuration Release
.\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe
```

The development executable is unpackaged with app-local .NET and Windows App SDK runtimes. Launching the connection screen requires no GitHub login and performs no network request. End-user packaging, signing, notice manifests and clean-machine acceptance are owned by [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13); a development build is not a distributable release.

The [dependency terms and source inventory](docs/dependencies.md) distinguishes Windows development use from binary redistribution. DWrite/Widgets redistribution applicability remains an unresolved #13 distribution blocker.

## Check a connection

Install [GitHub CLI](https://cli.github.com/). Use automatic detection, browse, or enter the path to `gh.exe`. Enter the GitHub hostname and optionally an Issue URL and a user/organization Project URL on that host.

Select **接続を確認 / 再確認** to inspect the CLI version, account and stable ID, credential storage, scopes, and separate Issue/Project permissions. `不明` means unverified; successful login does not by itself establish write access.

The **ログイン・権限追加の手順** section supplies PowerShell commands to copy and run in your own terminal. Complete browser authentication there, then recheck. The app does not initiate login or alter gh configuration.

After an intentional account, host or executable change, review the destination and select **新しい接続として確認**. Rechecking never silently adopts another identity. Cancel stops the check; closing the window waits for the owned gh operation to stop. Connection inputs and state are in memory.

## Register and reopen a Project

After checking a connection, open **登録済みProject / Projectを追加**, then **Projectを追加**. Select or enter an owner, optionally select one of its repositories, and search Projects. Repository results are actual GitHub Project links. Alternatively enter a same-host user/organization Project URL, including a Project without repository links. Discovery traverses all pages before presenting a complete list.

Review the identity, account and supported retrieval scope, then choose **取得してローカル登録**. Only a completed traversal durably saved locally becomes a registration. Duplicate URLs/routes open the same host/viewer/Project workspace. **最新を取得** explicitly refreshes it. The registered workspace edits existing Issue titles and Project single-select values locally, while retaining item classifications and read-only metadata.

On restart, open the Project page and explicitly choose a saved profile to view its cached Projects without network access. Cached account metadata is not authentication. Check the connection before new server operations. Changing connection host/executable clears the active workspace binding and discovery results.

The per-Project default repository is only a local setting for future Issue creation. **ローカル登録を解除…** confirms local registration/cache removal; it does not modify GitHub. When local work exists, cancellation is the default; explicitly retain it or discard only work not shared by another registration.

### Local registration storage

The default directory is `%LOCALAPPDATA%\GhProjectsBoards\Registrations`. Version 1 JSON contains Project names, repository names, Issue titles/states, supported field values, stable identities and retrieval timestamps. **It is not encrypted** and may contain private work data. Protect the Windows user profile and backups according to your organization policy. Tokens, authenticated connection objects and raw API/process streams are never persisted.

Use an absolute process-local override for isolated verification; invalid overrides fail without falling back to real data:

```powershell
$env:GHPB_DATA_ROOT = 'C:\Temp\ghpb-registration-check'
.\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe
```

Before checkpoint migration, each scoped Project has one hash-named JSON file containing both settings and cache. Migrated profiles use the authoritative checkpoint described below. Saves flush and validate a new temporary file, then atomically move/replace it; replacement keeps the preceding `.bak`. A competing writer is rejected. Corrupt/new-schema files are diagnosed and not overwritten or reset. Interrupted `.tmp`/`.removed` files and orphaned backups are reported, never automatically restored. To recover, close the app, preserve copies of the affected files, and restore a verified same-key/version backup under its original `.json` name; do not copy another profile's file over it. Local unregistration removes that key's JSON, backup and temporary data. Uninstall behavior is not yet defined by a distribution package; manually removing the data directory after closing all app instances removes these caches. Drafts use the separate versioned store described below; Apply history uses the authoritative version 7 checkpoint.

## Project column settings

In an ordinary registered Project, open **列の設定**. Toggle single-select columns, use **上へ / 下へ**, and enter widths from 80 to 1200 logical units. Title stays first and the reference/new-row destination stays last. **ローカル保存** accepts the change only after checkpoint saving succeeds. **キャンセル** discards the candidate; **既定値に戻す** changes the candidate and requires Save. Defaults are 320 for Title and 200 for other columns.

Copy, paste and clear follow visible order. For example, with Title/C/A/Reference visible, a two-cell paste into C/A never targets hidden B. Reordering clears the old rectangle but retains a visible active cell by identity; hiding it clears selection. Width changes retain selection and native editors. **操作を元に戻す** continues to address the original fields after a view change. If IME composition is active, finish or cancel it naturally and retry settings; pending text is preserved, not committed.

**新規行として貼り付け** first shows the TSV input columns: Title followed by visible single-select columns. Review the IDs/order and captured destination, then add or cancel. Hidden fields start as Unspecified. Whole-row duplication still copies supported committed hidden values. Apply review includes hidden differences/setup and labels them **グリッドでは非表示**; hiding a field never withdraws a planned mutation.

Preferences are separate for each host/account/Project and restore offline after switching/restart. Missing/type-changed field IDs remain diagnosed in settings/comparison; replacements with the same name receive defaults. Save failure retains the candidate for retry. See the [column contract](docs/spec.md#project-specific-columns) and [storage recovery](#local-registration-storage). Overall design acceptance remains under #31.

## Project row sorting and filtering

Open **行の表示設定** in a registered Project. Choose source order, Title, or a single-select field and direction. Enter a literal Title substring; select option IDs or Empty/Unspecified/Unknown choices per field. Choices within a field are alternatives, and different fields are combined. Hidden columns can still have active filters. **ローカル保存・適用** saves and evaluates; **キャンセル** discards the candidate; **行設定をリセット** clears row conditions without changing columns and requires Save.

Edit or paste while the rows remain in their current arrangement. Committed changes show **再適用が必要**; unfinished text does not drive the view. **行表示を再適用** evaluates committed values explicitly. Add/duplicate/append keeps new rows temporarily visible at the end. Native composition defers view changes; finish or cancel it naturally. Hiding an active row clears data selection but retains its buffer. Undo can affect hidden rows without removing the filter.

The status shows active criteria, total/displayed rows, hidden work and temporary inclusion. **Apply…** starts with displayed candidates and requires row selection. **非表示行も候補に含める** is off by default. A changed visibility set after pre-Apply refresh requires selection again. Review includes hidden column differences of selected rows; **実行履歴…** remains available for hidden rows. Approved operations keep their identities across later filtering.

Switch Projects or close/restart normally to restore saved definitions. A missing field/option reports its ID and shows no data rows until you repair the condition or explicitly reset. All underlying rows and recovery work remain stored. Save failure keeps the dialog candidate for retry. These controls are a functional adapter; #31 owns the replacement design and human usability acceptance.
## Credentials and failures

### Local editing and recovery

Use **新規行を追加** to prepare an incomplete local row below the fetched Issues. Enter its title, choose supported single-select values, and edit the last column's actual destination (`owner/repository`; horizontal scrolling or Tab reaches it). Select a cell and use Shift+up/down for multiple rows, then **選択行を複製** to copy committed values or **新規行を削除** to remove local rows. Mixed existing/local removal is rejected. **新規行として貼り付け** appends TSV in title/single-select order, validates the entire batch and creates one Undo unit. Empty titles are allowed during manual preparation but rejected by append. Clearing values never deletes a row.

Preparation works in an explicitly selected saved profile without authentication. New-row validation is separate from existing-field differences. In **Apply…**, select existing updates and/or local creation rows; only selected rows are validated. Review each resolved Repository ID, committed title and single-select intent, then choose **明示的にApply**. Unspecified preserves initial server values; Set and ExplicitClear request initial Project setup. Pending text is never implicitly committed. Save, switch Projects, refresh and restart retain work in the authoritative version 7 checkpoint; versions 1–6 remain readable.

**実行履歴…** distinguishes received identity, verified Issue existence, pending Project setup, verified completion and earlier uncertain attempts. Resume observes exact identities and never resends uncertain Issue creation. For an unknown creation, **作成の不確定結果を解決** offers hold, independently verified Issue URL binding, or a separately reviewed new attempt with explicit duplicate-risk acknowledgement. Binding performs no GitHub mutation. Successful later work does not prove an earlier attempt created nothing. Use the per-batch Resume buttons for older work. For a known Issue, **既知Issueの設定を再比較** reviews current local select choices and fresh observations; it also explicitly identifies withdrawal of retired fields. Complete identity evidence remains in history after promotion to an existing row. Creation-related removal, destination changes and Undo are guarded; unrelated mixed Undo remains available.

For the ordinary-app creation walkthrough and opt-in live validation, see [creation workflow](docs/creation-workflow.md). The live runner uses the exact authorized sandbox, verifies three run-owned Issues including one intercepted response, and independently checks cleanup and preservation of pre-existing data.

Existing Issue titles and Project single-select fields can be edited in registered Projects when their dated update capability was retrieved. Older caches show unknown permission and remain read-only until an explicit successful refresh. F2 and direct Japanese input are separate entry paths; IME Enter confirms composition, and the next Enter commits locally. Shift+arrows select rectangles. Toolbar copy/paste/clear/Undo and selected-cell Ctrl+C/V/Z operate on the grid; editing Ctrl+Z stays native. Empty pasted cells mean no change. No operation here applies changes to GitHub.

`Drafts/` below the registration root stores one versioned JSON per host/viewer. Version 1 remains readable. Version 2 retains private titles, buffers, baselines, observations, conflicts and Undo history. On the first successful refresh (or discard/unregister), this file becomes the authoritative profile checkpoint containing all its registrations as well. Subsequent registration/settings/removal and draft saves update that single file; the old per-Project files remain recovery material and must not be independently restored into an active checkpoint. It is unencrypted and inherits the current user's directory protection; protect backups too. No credentials or raw API payloads are stored. Normal switching/close waits for durable saving; a failed save keeps the editor open and shows a retry action. Forced termination guarantees the last durable checkpoint, not subsequent typing. `.tmp` files are unacknowledged attempts; `.bak` is the previous revision. Stop all app instances and preserve all files before recovery. Restore a validated complete same-profile checkpoint, never just one Project from a different revision. A missing/corrupt checkpoint blocks that profile instead of reviving stale legacy caches.

`最新を取得` compares the pinned baseline (B), committed local value (L) and fetched GitHub observation (R) per field. Independent changes coexist; identical changes become clean. Conflicting changes remain local and appear in `競合・未確認を比較`, including B/L/R, observation time, stable IDs, ownership and blocking reasons. Choose GitHub, local, or another valid value; this saves locally and never applies to GitHub. Older caches cannot overwrite shared Issue-title work. Incomplete retrieval preserves the last complete checkpoint and displays staged observations separately. Unknown/deleted fields, options, removed items and lost capability retain their local recovery data. Unfinished text remains pending; finish or cancel it naturally and refresh again. Active IME composition defers refresh without synthesizing input.

Unaffected operation Undo remains usable; affected obsolete operations are explicitly invalidated with a reason. Resolution Undo restores the unresolved comparison only while its state is still safe. Unregistration defaults to cancellation and offers explicit retain/discard; shared Issue work survives. Discard and registration removal share one checkpoint commit. Retained work can be reopened when the same Project is registered again. Removing the entire data directory deletes recovery data and is not an application command.

`scripts/Test-RefreshLive.ps1` is an explicit automated sandbox test with separate disposable fixture writes and owned cleanup. For a human live check use [the manual workflow](docs/refresh-manual-check.md); it creates its fixture only when you invoke setup. No live fixture is created by ordinary startup or synthetic checks.

For an isolated manual check, build Release and run `scripts/Start-EditingCheck.ps1`. It creates a new synthetic data directory and launches the ordinary executable with process-local `GHPB_DATA_ROOT`. Open the Project page, select `github.com / ID 42`, then `P1`/`P2`; no connection check is needed. Each contains 101 shared Issues with independent single-select values. Reuse that exact directory with `-DataRoot <absolute-path> -Resume` to check process restart; the default always creates new data. `-PrepareOnly` creates the setup without launching. Existing directories are never overwritten.

Child gh processes use stored authentication. The app removes `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN` and `GITHUB_ENTERPRISE_TOKEN` from those children without changing the parent environment. It reports variable names, never their values, and never extracts, displays or stores a token.

Recognized Windows credential-store (`keyring`) authentication permits guarded adapter writes. Plaintext or unknown credential storage blocks writes but remains diagnosable. Commands copied into your terminal use that terminal's environment; remove token overrides there when updating stored authentication.

Each gh process has a default 30-second timeout. Authentication failures, resource permissions, rate limits, network errors, cancellation and uncertain write outcomes are distinct. An uncertain write is never automatically resent. Diagnostics omit request bodies and raw process streams.

## Check native table input

The ordinary app includes a bounded, three-row/two-column input check using synthetic values:

```powershell
.\src\GhProjectsBoards.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\GhProjectsBoards.App.exe --input-check
```

Click a cell once and type Japanese directly, or use F2. The line below each editor shows the committed value: IME confirmation must leave it unchanged, and the following Enter commits the cell and moves down once. Arrow and Shift navigation select cells/ranges without changing values. The standard TextBox is a reference control. Data is discarded on exit; this screen does not connect to GitHub.

This keeps the native input method available for verification while full table editing, paste, Undo and 100-row behavior are implemented under [#7](https://github.com/fukuda-yuki/gh-projects-boards/issues/7). Launch without the option for the connection screen. See [tests](tests/README.md) for automated and human input checks.

## Test

**Testing priority: logic-layer unit tests > UI-layer integration tests > E2E tests.** Put most behavioral coverage in lower layers; E2E and live validation supplement them for relevant integration/native/external risks. This does not ban those executions or impose a numerical ratio. The [test policy](tests/README.md) defines boundary selection, behavior-over-interaction test design, evidence and coverage migration.

### Apply existing fields

In a connected registered Project, choose **Apply…**, select rows, and review field differences. Resolve any displayed conflicts locally before reviewing again. **明示的にApply** sends existing title and single-select changes only. Pending text is excluded. **実行履歴…** shows field outcomes and provides explicit revalidation/resume; reopening never resumes writes. Uncertain work may require withdrawing the old approval and preparing a fresh review. Already verified success is not resent. Cancellation does not roll back completed changes.

Execution history is stored in version 6 of the authoritative profile checkpoint alongside remaining drafts and cache observations. Preserve the entire profile and backups for recovery. Versions 1–5 remain readable. Live product validation is opt-in via `scripts/Test-ApplyLive.ps1`; it creates only a disposable sandbox fixture and independently verifies cleanup.

### Routine logic and adapter checks

```powershell
# Core logic and real adapter integration with synthetic external boundaries; no live GitHub:
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj --configuration Release --filter 'TestCategory!=LiveGitHub'
```

Use relevant focused logic and adapter tests during implementation.

### UI integration

Verify bounded collaboration of actual UI components, event/command wiring, presentation state and displayed results, with dependencies outside that scope controlled. Read the [UI integration policy](tests/README.md#ui-integration) for examples and case-level classification. Either an existing runtime with UI Automation or a dedicated test host may be suitable; neither defines the test level. Inspect existing cases before declaring missing coverage or introducing new infrastructure. ViewModel-only assertions do not establish view/control wiring.

```powershell
# Hosted UI integration: real WinUI views/controls with isolated collaborators
.\scripts\Test-UiIntegration.ps1
```

The current mechanism is the [production-sharing host](tests/GhProjectsBoards.UiIntegration.Tests/README.md), which is a mechanism rather than the definition of this level.

### Supplementary boundary checks

Select these for the changed boundary or an explicit acceptance need; this is a command reference, not a checklist to run in full for every change. Runner/project names do not classify every contained case. Full regression remains available when warranted. Use the documented desktop filters for a scoped run and report the collaboration, real/replaced dependencies and entry/result boundary actually exercised.

```powershell
# Full ordinary-executable desktop regression with isolated fake gh, when selected:
.\scripts\Test-E2E.ps1

# Physical-key Japanese IME for relevant native-input changes or acceptance:
.\scripts\Test-ReadyInput.ps1

# Real-GitHub validation for relevant external-contract risks or acceptance:
.\scripts\Test-LiveGitHub.ps1
```

Read the [test policy](tests/README.md) before running. Desktop execution requires an unlocked controlled session. Live tests target only [the designated sandbox repository](https://github.com/fukuda-yuki/codex-sandbox) and [user Project 3](https://github.com/users/fukuda-yuki/projects/3). They create and clean up disposable data. Inspect retained failures and uncertain outcomes before another live run. Sandbox permission does not require live execution for every task.

CI runs deterministic Core tests and discovers desktop tests without launching UI. Those results do not establish view/control interaction, physical IME, real-GitHub or company GHEC + EMU acceptance. Current execution evidence and incomplete feature scope belong to the owning Issues, not this README.

## Structure

- `src/GhProjectsBoards.Core/`: UI-independent connection orchestration and guarded GitHub CLI/API logic.
- `src/GhProjectsBoards.App/`: WinUI 3 presentation, window lifetime, native dialogs and clipboard.
- `tests/`: NUnit logic/integration tests, isolated fake gh, and FlaUI UIA3 desktop journeys.
- `scripts/`: deterministic desktop and live sandbox execution entry points.

[Requirements](docs/requirements.md) · [Specification](docs/spec.md) · [Architecture](docs/architecture.md) · [Decisions](docs/decisions.md) · [AGENTS.md](AGENTS.md)

# Working in the workspace

Use the workspace to prepare Issue and Project changes locally, review the intended results, and explicitly apply selected work to GitHub. A complete session includes finding a Project, editing several rows, arranging the view, reviewing changes and reopening the saved work. Control labels below match the Japanese application.

## Start with isolated local work

From the repository root, build Release and launch the existing synthetic editing check:

```powershell
dotnet build GhProjectsBoards.sln --configuration Release
./scripts/Start-EditingCheck.ps1
```

The script prints an isolated data directory and opens the ordinary application with two cached Projects containing 101 Issues. Open the Project-list button if the navigation pane is closed, select the saved `github.com / ID 42` account, and open **P1**. **P2** shares Issue titles and has independent Project select values. No connection check is needed for this local session.

Keep the printed path. After closing the app normally, reopen the same work with:

```powershell
./scripts/Start-EditingCheck.ps1 -DataRoot 'C:\absolute\printed-data-directory' -Resume
```

This setup supports local editing, views, new rows, switching and restart. It does not supply a connected GitHub service for refresh or Apply. Use a separately registered real Project for the connected sections below; use the existing [refresh check](refresh-manual-check.md) when an isolated, controlled conflict example is needed.

## Open a connected Project

1. Open **接続設定** from the account strip. Detect or choose `gh.exe`, enter the hostname, and select **接続を確認 / 再確認**. Check the account and stable ID. Permission shown as unknown remains unverified. The connection page's **ワークスペース** button returns to the editor.
2. Choose **Projectを追加…**. Enter a same-host Project URL, select **URLを確認**, review the target, then choose **取得してローカル登録**. Alternatively expand **所有者・Repositoryから探す** to discover linked Projects.
3. Select the saved Project in the navigation tree. In a smaller window, successful selection closes the overlaid navigation pane and reveals the sheet; use the Project-list button to reopen it. In a wider window, navigation stays beside the sheet. The parent Repository organizes navigation; it does not filter the Issues contained in a multi-repository Project. The heading identifies the Project, cached retrieval time and default destination. A cached account can be opened offline and is distinct from an authenticated connection.
4. Open **Projectの情報と設定** to inspect exact identities and retrieval details. Set **新しいローカル行の既定Repository** to the intended `owner/repository` and choose **設定を保存**. This seeds later rows; it does not retarget existing rows.

Use Issues and destinations you intend to change for connected editing and Apply. Connection/login details are in [README](../README.md#check-a-connection).

## Edit a block of work

Select an Issue title once, then type to replace it, or press F2 to edit it. Try an ordinary title such as `Plan review`, commit it, and change a single-select value in another row. Use **選択内容の詳細** to inspect the active cell's effective value, pending text, validation and identity without leaving the sheet.

| Input | Result |
| --- | --- |
| Type after selecting an unedited title | Begin native replacement input |
| F2 | Begin editing the selected title |
| Enter during IME composition | Confirm the native composition; the cell is still uncommitted |
| Enter after composition ends | Commit the cell and move down one row |
| Tab / Shift+Tab | Commit an active editor and move to the next/previous cell |
| Arrows while selected; Shift+arrows | Move the active cell; extend a rectangle |
| Arrows while editing | Move the native caret or composition/candidate selection |
| Escape outside composition | Cancel the active cell's pending buffer |
| F6 / Shift+F6 | Cycle table → **再適用** → details command, or reverse, retaining the active rectangle and pending text |

F6 waits while IME composition is active. Returning from view settings to an unedited title prepares replacement input; returning to pending input preserves editing. If a settings change hides the active cell, focus returns to **再適用**. Selection and editing remain separate; moving to another cell does not implicitly commit its pending buffer.

Prepare two rows of tab-separated text, select a title cell, and use **貼り付け**. For a view whose next column offers `Done` and `Todo`, this example fills a two-by-two block:

```text
Plan A	Done
Plan B	Todo
```

Paste follows visible column order and must fit existing rows. Empty cells mean no change. Invalid shapes, read-only destinations or ambiguous option labels reject the whole operation. Use **値をクリア** for an explicit clear where supported; required existing titles cannot be cleared. **コピー** copies the selected rectangle's committed effective values.

Choose **元に戻す** once to restore the preceding block operation. Undo restores the prior draft state, including any earlier pending buffer. For example, undoing a committed title can remove its committed difference while its earlier typed buffer remains visible; Escape explicitly cancels that buffer. During native text editing, Ctrl+Z belongs to the text editor; use the grid command for operation-level Undo. Undo can reject an operation whose fields have since changed elsewhere, and it does not roll back GitHub.

Scroll to the last rows and horizontally across the fields. Column headings stay above the sheet and follow horizontal movement. Use row numbers, references and selected details to locate work again. The **…** menu exposes commands moved out of the command bar at narrower widths.

## Arrange the view without losing work

1. Open **列**. Change the title width, reorder supported single-select columns with **↑ / ↓**, or hide one. Check the preview and choose **ローカル保存**. Title remains first; reference/new-row destination remains last. **キャンセル** leaves saved settings unchanged, and **既定値に戻す** changes the candidate until saved.
2. Open **並べ替え・フィルター**. Choose Title or a single-select order and direction. Enter a literal title substring, or expand a field's options and **空値 / 新規行の未指定 / 不明・未取得** states. Options within one field are alternatives; separate fields and the title condition combine. Read identifying details when field/option names repeat.
3. Choose **ローカル保存・適用**. Edit a matching row so its committed value would no longer match. The row stays in place until **変更した値で再適用**. Pending text does not determine sorting/filtering. New rows remain temporarily visible until explicit reapplication.
4. Inspect total/displayed rows, hidden work and temporary rows in the view summary. Hidden columns can still filter rows. Undo preserves the settings and can affect work hidden by the current view. **並べ替え・絞り込みをリセット** clears row conditions after Save while preserving columns.

Width changes retain the rectangle. Reordering or changing visibility retains a visible active cell by identity and clears the former rectangle. Hidden active cells lose data selection, while their drafts and pending text remain stored. Missing or incompatible saved criteria show a repair/reset explanation rather than silently broadening the view or deleting work. Finish native composition naturally before retrying a deferred view change.

## Prepare local rows

Choose **新規行を追加**, enter a title, set supported Project values, and inspect the row's actual destination. A blank or incomplete row remains local work. The default destination only provides its initial value. A new row is not a GitHub Issue until explicitly created and independently verified.

Use **選択行を複製** to copy committed values into independent local rows. **新規行として貼り付け** reviews the input mapping—Title followed by visible single-select fields—and the captured destination before appending. Hidden fields begin unspecified. Normal paste does not append rows. **新規行を削除** only accepts a wholly local selection; mixed existing/local removal is rejected. Row creation/removal and field edits participate in their operation-level Undo contracts.

Use **元に戻す** after removing a local row, including after closing and reopening the saved work. The row returns to its previous view position when this view still remembers it. If the row was absent when the view opened, the restored row and its pending text appear temporarily at the end, even if the current filter excludes it. Other hidden rows stay hidden, existing displayed rows keep their order, and saved view settings remain unchanged. **変更した値で再適用** removes the temporary exception and applies the saved criteria again; the row can become hidden while its restored work remains stored.

## Refresh and compare B / L / R

With a checked connection, choose **最新を取得**. The app saves local work and reconciles a complete fresh observation. Pending input remains pending; partial or failed retrieval does not replace the accepted complete cache. Open **状況の詳細…** for the full selectable explanation, including when no Project is selected.

If GitHub and local work have diverged, open **… → 競合・未確認を比較** and select the affected field:

- **B 基準** is the baseline from which local work started.
- **L ローカル** is the committed local intent, including explicit clear.
- **R GitHub** is the newly observed value. Known empty and unconfirmed/unavailable are different states.

Inspect the side-by-side values and expand **所有範囲・識別情報・構成変更** as needed. Choose **GitHub値を採用**, **ローカル値を保持**, or a valid alternative. These choices update local reconciliation state; they do not publish changes. Unavailable observations can prevent resolution until a reliable observation is available. The [refresh walkthrough](refresh-manual-check.md) provides a controlled conflict setup and independent readback.

## Review and Apply selected work

1. Choose **GitHubへ反映…**. Select the existing updates and complete local rows you intend to send. Leave unrelated preparation rows unselected. **非表示行も候補に含める** starts off; enable it explicitly to inspect hidden rows.
2. Choose **選択行を照合**. The app checks fresh data. If visibility membership changes, select targets again. Review the exact Project, Repository destinations, existing changes versus new creation, current/intended values, and hidden-column differences. Pending text is retained and excluded from the payload.
3. Cancel once if the selection is not ready; the local work remains. Prepare a new review when local work or remote observations change. **明示的にApply** approves the reviewed payload; later editing does not retarget that approval.
4. Open **実行履歴…** to inspect field outcomes and creation stages: Issue creation/verification, Project membership, initial values, setup and readback. A later local edit or pending buffer survives acknowledgement of an earlier approved value. Unselected rows remain local work.

Refresh, editing, local saving, view changes and Project switching do not themselves Apply. The supported creation/recovery details are in [Creation and recovery](creation-workflow.md).

## Recover uncertainty and resume a session

An interrupted or uncertain request is not proof that nothing happened. The history keeps its identities, stages and attempts. Startup does not resume writes automatically.

For an uncertain creation, open **作成の不確定結果を解決**. **保留を続ける** retains it. If the Issue can be independently identified, enter its same-host URL, choose **URLを独立確認**, review its identity/destination and explicitly bind it. Binding is a local reconciliation decision. A separate new-attempt review requires a duplicate-risk acknowledgement; it does not erase the earlier uncertainty. For a known Issue, **この実行を照合・再開** checks remaining work; stale setup choices require their explicit comparison/review.

Switch to another cached Project, then return. Shared Issue titles remain shared, Project select values remain independent, and each Project restores its own column and row definitions. Leave a pending title and an incomplete local row, close normally, and reopen the same data directory. Select the saved account and Project; inspect pending text, local destinations, view definitions and execution history before checking a connection. Opening saved work or history does not replay requests.

Normal navigation and close wait for local saving. If saving fails, keep the app open, inspect the status, repair the destination problem and use **ローカル保存を再試行**. When that failure blocks a Project selection, the current work and open navigation pane remain available. A forced termination can recover only the last acknowledged checkpoint. Pane visibility, scroll position and active selection are transient; they are not saved Project preferences. [Storage and backup handling](../README.md#local-registration-storage) describes the recovery files.

For a complete visual evaluation, repeat the same session with a wide and a smaller window, including command overflow, navigation, details, view settings and history. Record the actual window size, DPI and theme used. Evaluate natural Japanese typing separately from automated ASCII/selection checks; the [test policy](../tests/README.md) distinguishes those evidence boundaries.

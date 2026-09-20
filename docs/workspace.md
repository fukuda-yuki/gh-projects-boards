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

1. Open **接続設定** from the account strip. Enter the hostname and select **接続を確認 / 再確認**. Check the account and connection state. **CLIと接続の詳細** contains the explicit executable path, version, storage and scopes. **元の作業へ戻る** returns to the editor or the calling registration/confirmation workflow. This screen does not contain Project registration or target URLs; connection success alone does not establish target permissions.
2. Choose **Projectを追加…**. Enter a same-host Project URL, select **URLを確認**, review the target, then choose **取得してローカル登録**. Alternatively expand **所有者・Repositoryから探す** to discover linked Projects.
3. Select the saved Project in the navigation tree. In a smaller window, successful selection closes the overlaid navigation pane and reveals the sheet; use the Project-list button to reopen it. In a wider window, navigation stays beside the sheet. The parent Repository organizes navigation; it does not filter the Issues contained in a multi-repository Project. The heading identifies the Project and cached retrieval time; Project settings contain the default destination. A cached account can be opened offline and is distinct from an authenticated connection.
4. Open **Projectの情報と設定** to inspect exact identities and retrieval details. Set **新しいローカル行の既定Repository** to the intended `owner/repository` and choose **設定を保存**. This seeds later rows; it does not retarget existing rows.

Use Issues and destinations you intend to change for connected editing and Apply. Connection/login details are in [README](../README.md#check-a-connection).

## Edit a block of work

Select an Issue title once, then type to replace it, or press F2 to edit it. Try an ordinary title such as `Plan review`, commit it, and change a single-select value in another row. Use **選択内容の詳細** to inspect the active cell's effective value, pending text, validation and identity without leaving the sheet.

| Input | Result |
| --- | --- |
| Type after selecting an unedited title | Begin native replacement input |
| F2 | Edit the selected title, or open a single-select menu |
| Cell body click; body drag / Shift+click | Select a cell; extend a contiguous range |
| Choice arrow / F4 / Space | Open the selected single-select cell's options |
| Ctrl+D / **下へコピー** | Copy the top value down a selected vertical range |
| Enter during IME composition | Confirm the native composition; the cell is still uncommitted |
| Enter after composition ends | Commit the cell and move down one row |
| Tab / Shift+Tab | Commit an active editor and move to the next/previous cell |
| Arrows while selected; Shift+arrows | Move the active cell; extend a rectangle |
| Arrows while editing | Move the native caret or composition/candidate selection |
| Escape outside composition | Cancel the active cell's pending buffer |
| F6 / Shift+F6 | Cycle table → visible **再適用** (when needed) → details command, or reverse, retaining the active rectangle and pending text |

F6 waits while IME composition is active. Returning from view settings to an unedited title prepares replacement input; returning to pending input preserves editing. If a settings change hides the active cell, focus returns to the available view/details command. Selection and editing remain separate; moving to another cell does not implicitly commit its pending buffer.

Prepare two rows of tab-separated text, select a title cell, and use **貼り付け**. For a view whose next column offers `Done` and `Todo`, this example fills a two-by-two block:

```text
Plan A	Done
Plan B	Todo
```

Paste follows visible column order and must fit existing rows. With one selected cell, a rectangular TSV starts at that cell. With a larger selection, a multi-cell TSV must have exactly the same shape. Empty cells mean no change. Invalid shapes, read-only destinations or ambiguous external option labels reject the whole operation. Use **値をクリア** for explicit clear; required existing titles cannot be cleared. **コピー** uses committed effective values and preserves field/option identities for internal paste.

To repeat one value over 10 or 100 rows, use any of these routes:

1. Change and commit the source cell. Drag its lower-right handle upward or downward; the highlighted preview shows the intended range, and holding at the top/bottom edge scrolls to more rows. Release to copy. Escape or lost capture cancels without changing destinations.
2. Select the source, **コピー**, select a vertical range in the same column, then **貼り付け**. One nonempty value fills the selected cells; selecting different columns is rejected.
3. Select a vertical range with the source at the top and use **下へコピー (Ctrl+D)**.

All targets are checked before any change. A conflicting, read-only or pending target rejects the whole batch and identifies the reason. Finish or cancel pending source/destination input yourself. Empty-source fill is unavailable; use explicit clear instead. Filtered-out rows are excluded, while matching rows beyond the viewport participate. Undo once restores the copied destinations and preserves the prior source edit; Undo again restores the source. These operations never communicate with GitHub or append rows.

The upper-right diamond marks a committed local value that is still unreflected on GitHub. It stays visible alongside the selection border, without moving the value or arrow. The editing frame/caret identifies unfinished native input. The footer separates **ローカル保存済み** from **GitHub未反映**; use **選択内容の詳細** for baseline/local/fetched GitHub values and reasons.

Choose **元に戻す** once to restore the preceding block operation. Undo restores the prior draft state, including any earlier pending buffer. For example, undoing a committed title can remove its committed difference while its earlier typed buffer remains visible; Escape explicitly cancels that buffer. During native text editing, Ctrl+Z belongs to the text editor; use the grid command for operation-level Undo. Undo can reject an operation whose fields have since changed elsewhere, and it does not roll back GitHub.

Scroll to the last rows and horizontally across the fields. Row numbers, titles and repository/Issue numbers stay visible beside later fields. Column headings stay above the sheet and follow horizontal movement. The visible vertical scrollbar and mouse wheel move the same viewport while retaining pending input. Shift+wheel moves horizontally. The **…** menu exposes commands moved out of the command bar at narrower widths.

## Arrange the view without losing work

For common changes, click a column heading to sort, filter, move or hide that column. Drag its right boundary to resize; the accepted width is saved when released. Type a title substring in the inline filter and choose **絞り込み** or Enter; **解除** clears only the title condition. Full column and compound filter settings remain available below.

1. Open **列**. Change the title width, reorder supported single-select columns with **↑ / ↓**, or hide one. Check the preview and choose **ローカル保存**. Title remains first; Repository remains last. **キャンセル** leaves saved settings unchanged, and **既定値に戻す** changes the candidate until saved.
2. Open **並べ替え・フィルター**. Choose Title or a single-select order and direction. Enter a literal title substring, or expand a field's options and **空値 / 新規行の未指定 / 不明・未取得** states. Options within one field are alternatives; separate fields and the title condition combine. Read identifying details when field/option names repeat.
3. Choose **ローカル保存・適用**. Edit a matching row so its committed value would no longer match. The row stays in place until **変更した値で再適用**. Pending text does not determine sorting/filtering. New rows remain temporarily visible until explicit reapplication.
4. Inspect displayed/total rows and hidden work in the compact footer. Temporary rows or a required reapplication reveal the view notice; full criteria remain reachable in **選択内容の詳細** and view settings. Hidden columns can still filter rows. Undo preserves settings and can restore work hidden by the current view. **並べ替え・絞り込みをリセット** clears row conditions after Save while preserving columns.

Width changes retain the rectangle. Reordering or changing visibility retains a visible active cell by identity and clears the former rectangle. Hidden active cells lose data selection, while their drafts and pending text remain stored. Missing or incompatible saved criteria show a repair/reset explanation rather than silently broadening the view or deleting work. Finish native composition naturally before retrying a deferred view change.

## Inspect and edit the adopted Gantt plan

Choose **Gantt** beside **Boards** in the Project selector. Select a row to see its mode and exact start/finish in Japan time. Use **日 / 週**, the bottom horizontal scrollbar and the task list's vertical scrollbar to inspect the horizon. Repository/Issue identity stays beside the timeline; a row with missing or unresolved dates remains visible without a complete bar. A thin short bar represents its exact duration. Shading shows the Project's working calendar; **詳細** shows the selected person's endpoint-day exceptions and the adopted holiday revision.

Search a title or Issue number to locate work. **選択へ移動** brings the selected row and its date into view. The **先行 → 選択 → 後続** selector lists complete relationships; selecting an available task reveals it even outside the current search. Arrows show only the selected task's incident FS relationships. Use **詳細** for full identity, exact inputs, ownership/weight, reason, automatic suggestion and warnings.

Choose **日程を編集**, change the adopted start or finish, and **保存**. The task becomes Manual and retains those dates through subsequent replan. Explicitly choose **Auto** in that editor to release the override. **元に戻す** reverses the coherent local operation. **表で開く** returns to the same row, temporarily revealing a row excluded by the Boards filter. Unfinished cell text is retained across view changes; active IME conversion must be confirmed or canceled naturally before switching. F6 / Shift+F6 moves among the Gantt task list, edit and details commands.

For an isolated 1,000-task evaluation, run `./scripts/Start-GanttCheck.ps1`, open the printed saved profile and **P1**, then choose **Gantt**. This uses the planning evaluation's Load scenario: it rebuilds the ordinary app, records source/binary hashes, isolates credentials, and keeps fixture metadata outside registered data. Keep the printed data directory and use `-DataRoot 'C:\absolute\printed-directory' -Resume` to reopen this evaluation. Resume requires that launcher's root-specific synthetic marker; an older or arbitrary data directory is rejected without modification. **P2** provides a second cached Project for roundtrips. The mapped Finish role is displayed with the fixture's observed name **EndDate**. See the [Gantt evaluation and evidence index](../tests/regression-evidence/issue15-gantt/README.md) for fixed expected examples and the normal/narrow workflow.

## Prepare local rows

Choose **新規行を追加**, enter a title, set supported Project values, and inspect the row's actual destination. A blank or incomplete row remains local work. The default destination only provides its initial value. A new row is not a GitHub Issue until explicitly created and independently verified.

Use **選択行を複製** to copy committed values into independent local rows. **新規行として貼り付け** reviews the input mapping—Title followed by visible single-select fields—and the captured destination before appending. Hidden fields begin unspecified. Normal paste does not append rows. **新規行を削除** only accepts a wholly local selection; mixed existing/local removal is rejected. Row creation/removal and field edits participate in their operation-level Undo contracts.

Use **元に戻す** after removing a local row, including after closing and reopening the saved work. The row returns to its previous view position when this view still remembers it. If the row was absent when the view opened, the restored row and its pending text appear temporarily at the end, even if the current filter excludes it. Other hidden rows stay hidden, existing displayed rows keep their order, and saved view settings remain unchanged. **変更した値で再適用** removes the temporary exception and applies the saved criteria again; the row can become hidden while its restored work remains stored.

## Refresh and compare B / L / R

With a checked connection, choose **最新を取得**. The app saves local work and reconciles a complete fresh observation. Pending input remains pending; partial or failed retrieval does not replace the accepted complete cache. Open **Projectの情報と設定 → 処理状況・診断…** for the full selectable explanation. When no Project is selected, use **状況の詳細…** in the status area.

If GitHub and local work have diverged, open **… → 競合・未確認を比較** and select the affected field:

- **B 基準** is the baseline from which local work started.
- **L ローカル** is the committed local intent, including explicit clear.
- **R GitHub** is the newly observed value. Known empty and unconfirmed/unavailable are different states.

Inspect the side-by-side values and expand **所有範囲・識別情報・構成変更** as needed. Choose **GitHub値を採用**, **ローカル値を保持**, or a valid alternative. These choices update local reconciliation state; they do not publish changes. Unavailable observations can prevent resolution until a reliable observation is available. The [refresh walkthrough](refresh-manual-check.md) provides a controlled conflict setup and independent readback.

## Review and Apply selected work

1. Choose **GitHubに反映…** from the Project header. **反映内容の確認** lists changed existing rows and local creation rows, initially unselected. Unchanged Issues are omitted. Select individual rows or use **表示中の変更をすべて選択**. Incomplete rows display their preparation problems and block final approval if selected. **非表示行も候補に追加する** starts off; adding candidates does not select them.
2. The app checks the latest state automatically in this same screen. Review the Project, host/account, Repository/Issue identities and **GitHubの値 → 反映する値**, including changes in hidden columns. Counts distinguish updated Issues, new Issues and field changes. Pending strings are shown separately as unsent input. Resolve conflicts using **GitHubの値を使う** or **自分の変更を使う** beside the affected field, or explicitly deselect the row. Failed/incomplete checking shows the saved-data time and a retry/connection recovery route. Nothing is written yet.
3. Choose **GitHubに反映（N件）** for final approval. Zero effective changes, unresolved selected work or stale/incomplete checks prevent sending with a visible reason. A changed visibility set requires explained reselection without expanding the scope. **編集へ戻る** keeps all local work. Same-account connection recovery returns here for rechecking without sending; a different host/account cannot inherit the selection. Approved payloads remain frozen, with durable saving, pre-dispatch checks and independent readback.
4. Successful execution keeps the editing table open. If work remains incomplete, dismiss the brief warning to reach the first affected cell, then use **次の問題へ**. The cell marker and nearby reason distinguish failure from an unverified result. Hidden targets appear temporarily; **再適用** or switching Projects restores the saved view. A missing target explains its absence and offers **反映結果** without moving to a different cell. Active IME composition retains focus until confirmation or cancellation; switching Project/account cancels deferred navigation. A later edit or pending buffer survives acknowledgement of an earlier approved value. Unselected rows remain local work. Cancellation stops unsent operations and does not undo completed GitHub updates.
5. Open **反映結果・履歴** explicitly to inspect remaining work. **完了分を含む保存履歴** shows all saved outcomes. Each execution has its own **確認して再開** and **承認を撤回** actions when applicable. The information icon opens supplementary help by hover, click or keyboard. Opening results or navigating between problem cells sends nothing and retains the full execution evidence.

Refresh, editing, local saving, view changes and Project switching do not themselves Apply. The supported creation/recovery details are in [Creation and recovery](creation-workflow.md).

## Recover uncertainty and resume a session

An interrupted or uncertain request is not proof that nothing happened. The history keeps its identities, stages and attempts. Startup does not resume writes automatically.

For an uncertain creation, open **作成の不確定結果を解決**. **保留を続ける** retains it. If the Issue can be independently identified, enter its same-host URL, choose **URLを独立確認**, review its identity/destination and explicitly bind it. Binding is a local reconciliation decision. A separate new-attempt review requires a duplicate-risk acknowledgement; it does not erase the earlier uncertainty. For a known Issue, **この実行の未完了を確認して再開** checks remaining work; stale setup choices require their explicit comparison/review.

Switch to another cached Project, then return. Shared Issue titles remain shared, Project select values remain independent, and each Project restores its own column and row definitions. Leave a pending title and an incomplete local row, close normally, and reopen the same data directory. Select the saved account and Project; inspect pending text, local destinations, view definitions and execution history before checking a connection. Opening saved work or history does not replay requests.

Normal navigation and close wait for local saving. If saving fails, keep the app open, inspect the status, repair the destination problem and use **ローカル保存を再試行**. When that failure blocks a Project selection, the current work and open navigation pane remain available. A forced termination can recover only the last acknowledged checkpoint. Pane visibility, scroll position and active selection are transient; they are not saved Project preferences. [Storage and backup handling](../README.md#local-registration-storage) describes the recovery files.

For a complete visual evaluation, repeat the same session with a wide and a smaller window, including command overflow, navigation, details, view settings and history. Record the actual window size, DPI and theme used. Evaluate natural Japanese typing separately from automated ASCII/selection checks; the [test policy](../tests/README.md) distinguishes those evidence boundaries.

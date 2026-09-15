using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid : Grid
{
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock selection = new();
    private readonly TextBlock columnNotice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly List<FrameworkElement[]> controls = [];
    private readonly List<TextBlock[]> markers = [];
    private readonly DraftSession session;
    private EditRow[] rows;
    private EditRow[] canonicalRows;
    private ColumnLayout layout;
    private readonly ProjectRegistration registration;
    private readonly Func<Task<bool>> prepareLocalRows;
    private readonly Func<Task<string>> readClipboard;
    private readonly string projectId;
    private int currentRow, currentColumn, anchorRow, anchorColumn;
    private bool active;
    private bool selecting;
    private int generation;
    internal void CancelPending() => generation++;
    internal bool CanRefresh => !controls.SelectMany(r => r).OfType<TitleCell>().Any(t => t.Composing);
    internal (string Item, FieldKey? Field)? SelectionIdentity => active ? (rows[currentRow].ItemId, rows[currentRow].Cells[currentColumn].Key) : null;
    internal void RestoreSelection((string Item, FieldKey? Field)? identity)
    {
        if (identity is not { } target) return;
        var created = session.Workspace.Creations.LastOrDefault(c => c.LocalId == target.Item && c.Completed);
        if (created?.ItemId is { } item && created.Verified is { } issue && !session.Workspace.LocalRows.Any(r => r.Id == target.Item))
            target = (item, target.Field?.Kind switch { "LocalTitle" => new("Title", issue.Id), "LocalSelect" => new("Select", item, projectId, target.Field.FieldId), _ => null });
        var r = Array.FindIndex(rows, row => row.ItemId == target.Item);
        if (r < 0) { selection.Text = "選択していた項目はProjectで未観測です。別の行には移動していません。"; return; }
        var c = Array.FindIndex(rows[r].Cells, cell => cell.Key == target.Field);
        if (c >= 0) Select(r, c, false, false);
    }
    internal EditingGrid(ProjectRegistration registration, DraftSession session, Func<Task<bool>> prepareLocalRows, RowProjection? previousProjection = null, Func<Task<string>>? readClipboard = null)
    {
        this.session = session; this.registration = registration; this.prepareLocalRows = prepareLocalRows; projectId = registration.Snapshot.Id.NodeId;
        this.readClipboard = readClipboard ?? (async () => await Clipboard.GetContent().GetTextAsync());
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        layout = session.Workspace.Columns(registration);
        projection = previousProjection ?? new(registration.Snapshot.Id);
        if (previousProjection is null) projection.Reapply(session.Workspace, registration);
        else projection.Promote(session.Workspace);
        canonicalRows = session.Workspace.Open(registration); rows = layout.Resolve(projection.Resolve(canonicalRows));
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var toolbar = new StackPanel { Spacing = 8 };
        var editCommands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var recoveryCommands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(editCommands); toolbar.Children.Add(recoveryCommands);
        var rowCommands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(rowCommands);
        var columnSettings = new Button { Content = "列の設定" }; AutomationProperties.SetAutomationId(columnSettings, "GridColumns");
        columnSettings.Click += async (_, _) => await ConfigureColumnsAsync(columnSettings); recoveryCommands.Children.Add(columnSettings);
        var viewSettings = new Button { Content = "行の表示設定" }; AutomationProperties.SetAutomationId(viewSettings, "GridRowSettings");
        viewSettings.Click += async (_, _) => await ConfigureRowsAsync(viewSettings); recoveryCommands.Children.Add(viewSettings);
        var reapply = new Button { Content = "行表示を再適用" }; AutomationProperties.SetAutomationId(reapply, "GridReapply");
        reapply.Click += (_, _) => ReapplyRows(reapply); recoveryCommands.Children.Add(reapply);
        void RowCommand(string text, string id, Func<EditRow[], string[]?> action)
        {
            var button = new Button { Content = text }; AutomationProperties.SetAutomationId(button, id);
            button.Click += async (_, _) =>
            {
                var request = generation;
                var targets = active ? SelectedRows() : [];
                if (!CanRefresh || !await prepareLocalRows() || request != generation || !IsLoaded) return;
                Run(() => { var added = action(targets); projection.IncludeNew(session.Workspace.Open(registration), added ?? []); RebuildRows(); if (added is { Length: > 0 }) Select(Array.FindIndex(rows, r => r.ItemId == added[0]), 0, false); });
            };
            rowCommands.Children.Add(button);
        }
        RowCommand("新規行を追加", "GridAddRow", _ => [session.Workspace.AddRow(registration)]);
        RowCommand("選択行を複製", "GridDuplicateRows", targets => session.Workspace.DuplicateRows(registration, targets));
        RowCommand("新規行を削除", "GridRemoveRows", targets => { session.Workspace.RemoveRows(projectId, targets); return null; });
        var append = new Button { Content = "新規行として貼り付け" }; AutomationProperties.SetAutomationId(append, "GridAppendRows");
        append.Click += async (_, _) => await AppendAsync(); rowCommands.Children.Add(append);
        void Command(string text, string id, Action action)
        {
            var b = new Button { Content = text }; AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => Run(action); editCommands.Children.Add(b);
        }
        Command("コピー", "GridCopy", Copy);
        var paste = new Button { Content = "貼り付け" }; AutomationProperties.SetAutomationId(paste, "GridPaste");
        paste.Click += async (_, _) => await PasteAsync(); editCommands.Children.Add(paste);
        Command("値をクリア", "GridClear", ClearSelected);
        Command("操作を元に戻す", "GridUndo", Undo);
        var save = new Button { Content = "ローカル保存を再試行" }; AutomationProperties.SetAutomationId(save, "GridSave");
        save.Click += async (_, _) => { var request = generation; await session.FlushAsync(); if (IsLoaded && request == generation) Update(); }; recoveryCommands.Children.Add(save);
        var compare = new Button { Content = "競合・未確認を比較" }; AutomationProperties.SetAutomationId(compare, "GridConflicts");
        compare.Click += async (_, _) => await CompareAsync(); recoveryCommands.Children.Add(compare);
        Children.Add(toolbar);
        var info = new StackPanel { Spacing = 4 }; info.Children.Add(status); info.Children.Add(selection); info.Children.Add(columnNotice);
        info.Children.Add(new ScrollViewer { Content = viewNotice, MaxHeight = 40, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        AutomationProperties.SetAutomationId(viewNotice, "RowViewStatus");
        AutomationProperties.SetAutomationId(columnNotice, "ColumnTransitionStatus");
        AutomationProperties.SetAutomationId(status, "DraftStatus"); AutomationProperties.SetAutomationId(selection, "GridSelection");
        SetRow(info, 1); Children.Add(info);
        AutomationProperties.SetAutomationId(list, "ProjectItems"); SetRow(list, 2); Children.Add(list);
        BuildRows();
        Unloaded += (_, _) => { generation++; session.Changed -= SessionChanged; };
        Loaded += (_, _) => { session.Changed -= SessionChanged; session.Changed += SessionChanged; Update(); };
        Update(); _ = session.FlushAsync();
    }
    private EditRow[] SelectedRows()
    {
        if (!active) throw new InvalidOperationException("セルを選択してください。Shift＋上下で複数行を選択できます。");
        return rows[Math.Min(anchorRow, currentRow)..(Math.Max(anchorRow, currentRow) + 1)];
    }
    private void Undo()
    {
        if (!CanRefresh) throw new InvalidOperationException("IME変換中です。自然に確定・取消してからUndoしてください。");
        session.Workspace.Undo(projectId);
        // Undo restores original keys, including rows absent from the current projection.
        if (!canonicalRows.Select(r => r.ItemId).SequenceEqual(session.Workspace.Open(registration).Select(r => r.ItemId))) RebuildRows();
    }
    private void RebuildRows()
    {
        var identity = SelectionIdentity; generation++; active = false;
        projection.Promote(session.Workspace);
        canonicalRows = session.Workspace.Open(registration); rows = layout.Resolve(projection.Resolve(canonicalRows)); list.Items.Clear(); controls.Clear(); markers.Clear();
        BuildRows(); RestoreSelection(identity);
    }
    private void BuildRows()
    {
        var header = new Grid { ColumnSpacing = 8 };
        for (var c = 0; c < layout.Visible.Length; c++)
        {
            header.ColumnDefinitions.Add(new() { Width = new GridLength(layout.Visible[c].Preference.Width) });
            var label = new TextBlock { Text = layout.Visible[c].Name, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(label, $"GridHeader{c}"); SetColumn(label, c); header.Children.Add(label);
        }
        list.Header = header;
        for (var r = 0; r < rows.Length; r++)
        {
            var line = new Grid { ColumnSpacing = 8 }; var rowControls = new List<FrameworkElement>(); var rowMarkers = new List<TextBlock>();
            for (var c = 0; c < rows[r].Cells.Length; c++)
            {
                var rr = r; var cc = c; var cell = rows[r].Cells[c];
                line.ColumnDefinitions.Add(new() { Width = new GridLength(layout.Visible[c].Preference.Width) });
                var container = new StackPanel { Spacing = 4 }; var marker = new TextBlock { TextWrapping = TextWrapping.Wrap }; rowMarkers.Add(marker); container.Children.Add(marker);
                FrameworkElement editor;
                if (cell.Key?.Kind is "Select" or "LocalSelect" && cell.Editable)
                {
                    var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = cell.Options, DisplayMemberPath = "Name" };
                    combo.SelectedItem = cell.Options.SingleOrDefault(o => o.Id == session.Workspace.Value(cell));
                    combo.GotFocus += (_, _) => { if (CurrentEditor(rr, cc, combo)) FocusedCell(rr, cc); };
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (updating || !CurrentEditor(rr, cc, combo) || combo.SelectedItem is not SelectOption option) return;
                        Run(() => session.Workspace.Commit(projectId, cell, option.Id, true));
                    };
                    combo.PreviewKeyDown += (_, e) => { if (CurrentEditor(rr, cc, combo) && !combo.IsDropDownOpen) NavigateKey(rr, cc, e); };
                    editor = combo;
                }
                else
                {
                    var text = new TitleCell(this, rr, cc, cell);
                    editor = text;
                }
                AutomationProperties.SetAutomationId(editor, $"GridCell{r}_{c}");
                AutomationProperties.SetName(editor, $"行 {r + 1} 列 {c + 1} {cell.Display} {cell.Reason}");
                ToolTipService.SetToolTip(editor, cell.Reason ?? "取得時の権限観測に基づくローカル編集。GitHubへの反映はありません。");
                container.Children.Add(editor); rowControls.Add(editor); SetColumn(container, c); line.Children.Add(container);
            }
            controls.Add(rowControls.ToArray()); markers.Add(rowMarkers.ToArray());
            // Each container owns stable keys for its lifetime; scrolling never retargets a live editor.
            var item = new ListViewItem { Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(0), Margin = new(0), BorderThickness = new(0) };
            AutomationProperties.SetName(item, rows[r].Cells[^1].Display); list.Items.Add(item);
        }
    }
    private async Task CompareAsync()
    {
        if (!CanRefresh) { status.Text = "IME変換を自然な操作で確定・取消してから比較してください。"; return; }
        CancelPending();
        var fields = session.Workspace.Fields.Where(f => f.Conflict || f.Observation?.Reason is not null).ToArray();
        var diagnostics = string.Join("\n", session.Workspace.StructuralChanges.Concat(session.Workspace.UndoWarnings)
            .Concat(layout.Columns.Where(c => !c.Available).Select(c => $"未確認の列設定を保持: {c.Id.FieldId}")));
        if (fields.Length == 0) {
            var summary = new ContentDialog { XamlRoot = XamlRoot, Title = "構成変更とUndo", CloseButtonText = "閉じる",
                Content = new ScrollViewer { MaxHeight = 340, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "競合・未確認の保存フィールドはありません。\n" + diagnostics } } };
            await summary.ShowAsync(); return;
        }
        var picker = new ComboBox { Header = "保存フィールド（IDで識別）", ItemsSource = fields.Select(f => $"{(f.Key.Kind == "Title" ? "タイトル" : "単一選択")} / {f.Key.NodeId} / {f.Key.FieldId}").ToArray(), SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(picker, "ConflictField");
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(detail, "ConflictComparison");
        var text = new TextBox { Header = "別のタイトル" }; AutomationProperties.SetAutomationId(text, "ConflictAlternativeTitle");
        var options = new ComboBox { Header = "別の選択肢", DisplayMemberPath = "Name" }; AutomationProperties.SetAutomationId(options, "ConflictAlternativeOption");
        var clear = new CheckBox { Content = "明示的にクリア" }; AutomationProperties.SetAutomationId(clear, "ConflictAlternativeClear");
        var other = new Button { Content = "別の値をローカル採用" }; AutomationProperties.SetAutomationId(other, "ConflictUseAlternative");
        var content = new StackPanel { Spacing = 8 }; foreach (var element in new FrameworkElement[] { picker, new ScrollViewer { MaxHeight = 260, Content = detail }, text, options, clear, other }) content.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "競合・未確認の比較（ローカルのみ）", Content = content,
            PrimaryButtonText = "GitHub値を採用", SecondaryButtonText = "ローカル値を保持", CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "ConflictDialog");
        DraftField selected = fields[0]; ResolutionDecision? decision = null; LocalValue? alternative = null;
        void Show()
        {
            selected = session.Workspace.Fields.Single(f => f.Key == fields[picker.SelectedIndex].Key); var remote = selected.Observation!;
            string Display(string? v) => v is null ? "（明示的な空値）" : selected.Key.Kind == "Title" ? v : $"{remote.Options.SingleOrDefault(o => o.Id == v)?.Name ?? "選択肢不明"} [ID: {v}]";
            detail.Text = $"所有: {(selected.Key.Kind == "Title" ? "Issue（同じアカウント内で共有）" : "Project項目")}\nProject: {remote.Project.NodeId}\n観測: {remote.At.LocalDateTime:yyyy-MM-dd HH:mm:ss}\nB 基準: {Display(selected.Baseline)}\nL ローカル: {Display(selected.Change is { } local ? local.Value : selected.Baseline)}\nR GitHub: {(remote.Availability is ValueAvailability.Present or ValueAvailability.Empty ? Display(remote.Value) : EditingWorkspace.AvailabilityText(remote.Availability))}\n{remote.Reason ?? "有効な値競合。選択はローカル保存のみです。"}\n未確定文字: {selected.Buffer ?? "なし"}";
            decision = selected.Conflict && remote.Reason is null ? session.Workspace.Decision(selected.Key) : null;
            if (diagnostics.Length > 0) detail.Text += "\n\n構成変更・Undo:\n" + diagnostics;
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = other.IsEnabled = decision is not null;
            text.Visibility = selected.Key.Kind == "Title" ? Visibility.Visible : Visibility.Collapsed;
            options.Visibility = clear.Visibility = selected.Key.Kind == "Select" ? Visibility.Visible : Visibility.Collapsed;
            text.Text = selected.Change?.Value ?? selected.Baseline ?? ""; options.ItemsSource = remote.Options; options.SelectedIndex = -1; clear.IsChecked = false;
        }
        picker.SelectionChanged += (_, _) => Show();
        other.Click += (_, _) =>
        {
            alternative = selected.Key.Kind == "Title" ? new(text.Text) : clear.IsChecked == true ? new(null, true)
                : options.SelectedItem is SelectOption option ? new(option.Id) : null;
            if (alternative is not null) dialog.Hide();
        };
        Show(); var result = await dialog.ShowAsync();
        if (decision is null || result == ContentDialogResult.None && alternative is null) return;
        var value = alternative ?? (result == ContentDialogResult.Primary ? selected.Observation!.Value : selected.Change is { } local ? local.Value : selected.Baseline) switch
        { null => new LocalValue(null, true), var chosen => new LocalValue(chosen) };
        await session.CommitAsync(candidate => { candidate.Resolve(selected.Observation!.Project.NodeId, decision, value); return candidate; }, () => IsLoaded && CanRefresh);
        Update();
    }
    private bool updating;
    private bool CurrentEditor(int r, int c, FrameworkElement editor) => IsLoaded && r >= 0 && r < controls.Count
        && c >= 0 && c < controls[r].Length && ReferenceEquals(controls[r][c], editor);
    private void FocusedCell(int r, int c)
    {
        if (selecting || active && currentRow == r && currentColumn == c) return;
        Select(r, c, false, false);
        if (controls[r][c] is TitleCell text && !text.Editing) text.SelectAll();
    }
    private void SessionChanged()
    {
        var request = generation;
        if (DispatcherQueue.HasThreadAccess) { if (IsLoaded) Update(); }
        else if (!DispatcherQueue.TryEnqueue(() => { if (IsLoaded && request == generation) Update(); }))
            throw new InvalidOperationException("The editing UI dispatcher is unavailable.");
    }
    private void Update()
    {
        if (!CanRefresh) return;
        // The durable acknowledgement arrives before RegistrationWorkspace publishes the matching
        // complete snapshot. Keep the existing selection/editor identity until that panel handoff.
        if (canonicalRows.Any(row => row.IsLocal && !session.Workspace.LocalRows.Any(r => r.Id == row.ItemId)
            && session.Workspace.Creations.Any(c => c.LocalId == row.ItemId && c.Completed && c.ItemId is { } id
                && !registration.Snapshot.Items.Any(i => i.Id.NodeId == id)))) return;
        if (!canonicalRows.Where(r => r.IsLocal).Select(r => r.ItemId).SequenceEqual(session.Workspace.LocalRows.Where(r => r.ProjectId == projectId).Select(r => r.Id))) { projection.IncludeNew(session.Workspace.Open(registration), session.Workspace.LocalRows.Where(r => r.ProjectId == projectId && !canonicalRows.Any(old => old.ItemId == r.Id)).Select(r => r.Id)); RebuildRows(); }
        updating = true;
        try
        {
            var canonical = canonicalRows;
            var definition = session.Workspace.RowView(registration);
            viewNotice.Text = $"全行 {canonical.Length} / 表示 {rows.Length} / 非表示の作業 {canonical.Count(r => !DisplayedRowIds.Contains(r.ItemId) && session.Workspace.RowHasWork(r))} / 一時表示 {rows.Count(r => projection.Temporary.Contains(r.ItemId))}\n条件: {definition.Sort} {(definition.Descending ? "降順" : "昇順")} [{definition.FieldId}] / タイトル: {definition.Title} / " + string.Join("; ", (definition.Filters ?? []).Select(f => $"[{f.FieldId}] {string.Join(",", f.OptionIds.Concat(f.States))}")) + " / 行の表示設定で解除・リセット";
            if (projection.Problem is { } problem) viewNotice.Text += " / " + problem;
            if (projection.NeedsReapply(session.Workspace, registration)) viewNotice.Text += " / 値が変わりました。行表示の再適用が必要です（Undoは非表示行にも反映）。";
            var hidden = canonical.SelectMany(r => r.Cells).Where(c => layout.Hidden(c.Key?.FieldId)).ToArray();
            status.Text = $"このProject {canonical.SelectMany(r => r.Cells).Where(session.Workspace.Changed).Select(c => c.Key).Distinct().Count()} / プロフィール変更フィールド {session.Workspace.DifferenceCount} / {session.Status}";
            var hiddenChanges = hidden.Count(session.Workspace.Changed); var hiddenPending = hidden.Count(c => session.Workspace.Buffer(c) is not null);
            var hiddenConflicts = hidden.Count(c => session.Workspace.Field(c)?.Conflict == true);
            var hiddenLocal = session.Workspace.LocalRows.Where(r => r.ProjectId == projectId).SelectMany(r => r.Selects).Count(s => layout.Hidden(s.FieldId) && s.Intent != "Unspecified");
            if (hiddenChanges + hiddenPending + hiddenConflicts + hiddenLocal > 0)
                status.Text += $" / 非表示列: 変更 {hiddenChanges}・未確定 {hiddenPending}・競合 {hiddenConflicts}・新規設定 {hiddenLocal}";
            status.Text += $" / 競合 {session.Workspace.Fields.Count(f => f.Conflict)} / 未確認 {session.Workspace.Fields.Count(f => f.Observation?.Reason is not null)}";
            status.Text += $" / ローカル行 {rows.Count(r => r.IsLocal)}（選択・照合・明示的Applyで作成）";
            status.Text += $"\n構成変更 {session.Workspace.StructuralChanges.Count} / 無効化したUndo {session.Workspace.UndoWarnings.Count()}（比較画面に詳細）";
            selection.Text = active ? $"行 {anchorRow + 1} 列 {anchorColumn + 1} ～ 行 {currentRow + 1} 列 {currentColumn + 1}" : "セルを選択してください。Shift＋矢印で範囲選択。F2で編集。";
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Cells.Length; c++)
            {
                var cell = rows[r].Cells[c];
                var selected = active && r >= Math.Min(anchorRow, currentRow) && r <= Math.Max(anchorRow, currentRow) && c >= Math.Min(anchorColumn, currentColumn) && c <= Math.Max(anchorColumn, currentColumn);
                markers[r][c].Text = (selected ? "選択 " : "") + (session.Workspace.Changed(cell) ? "変更あり " : "") + (session.Workspace.Buffer(cell) is not null ? "編集中（未確定）" : cell.Reason ?? "");
                if (cell.Key?.Kind is "Select" or "LocalSelect" && session.Workspace.Buffer(cell) is { } pending)
                    markers[r][c].Text += " / 未確定文字: " + pending;
                if (rows[r].IsLocal && c == 0) markers[r][c].Text += " 新規 / " + string.Join(" / ", session.Workspace.LocalProblems(registration, rows[r].ItemId));
                if (rows[r].IsLocal && c == 0 && session.Workspace.Creations.LastOrDefault(x => x.LocalId == rows[r].ItemId) is { } creation)
                    markers[r][c].Text += " / " + creation.Reason + " " + creation.Verified?.Url;
                if (cell.Key?.Kind == "LocalSelect") markers[r][c].Text += " / " + session.Workspace.LocalRows.Single(x => x.Id == rows[r].ItemId).Selects.SingleOrDefault(s => s.FieldId == cell.Key.FieldId)?.Intent;
                if (cell.Key?.Kind == "LocalSelect" && session.Workspace.Value(cell) is { } savedId && !cell.Options.Any(o => o.Id == savedId))
                    markers[r][c].Text += " 保存値: " + session.Workspace.LocalRows.Single(row => row.Id == rows[r].ItemId).Selects.Single(s => s.FieldId == cell.Key.FieldId).OptionName + " [" + savedId + "]（要確認）";
                var field = session.Workspace.Field(cell);
                markers[r][c].Text += field?.Conflict == true ? " 競合（比較が必要）" : "";
                if (field?.Observation?.Reason is { } reason) markers[r][c].Text += " " + reason;
                if (controls[r][c] is TitleCell text) text.Refresh();
                if (controls[r][c] is ComboBox combo) { combo.IsEnabled = field?.Conflict != true && field?.Observation?.Reason is null; combo.SelectedItem = cell.Options.SingleOrDefault(o => o.Id == session.Workspace.Value(cell)); }
            }
        }
        finally { updating = false; }
    }
    private IEnumerable<EditCell> Range()
    {
        if (!active) throw new InvalidOperationException("セルを選択してください。");
        for (var r = Math.Min(anchorRow, currentRow); r <= Math.Max(anchorRow, currentRow); r++)
            for (var c = Math.Min(anchorColumn, currentColumn); c <= Math.Max(anchorColumn, currentColumn); c++) yield return rows[r].Cells[c];
    }
    private void Select(int r, int c, bool extend, bool focus = true)
    {
        currentRow = r; currentColumn = c; active = true;
        if (!extend) { anchorRow = r; anchorColumn = c; }
        if (focus)
        {
            selecting = true;
            list.ScrollIntoView(list.Items[r]);
            ((Control)controls[r][c]).Focus(FocusState.Keyboard);
            if (controls[r][c] is TitleCell text && !text.Editing) text.SelectAll();
            selecting = false;
        }
        Update();
    }
    private void ClearSelected()
    {
        var targets = Range().ToArray();
        for (var r = Math.Min(anchorRow, currentRow); r <= Math.Max(anchorRow, currentRow); r++)
            for (var c = Math.Min(anchorColumn, currentColumn); c <= Math.Max(anchorColumn, currentColumn); c++)
            {
                var cell = rows[r].Cells[c];
                if (!cell.Editable || cell.Key?.Kind == "Title") throw new InvalidOperationException($"行 {r + 1} 列 {c + 1}: {cell.Reason ?? "必須タイトルはクリアできません。"}");
            }
        session.Workspace.Clear(projectId, targets);
    }
    private static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    private void NavigateKey(int r, int c, KeyRoutedEventArgs e)
    {
        if (Down(VirtualKey.Control))
        {
            if (e.Key == VirtualKey.C) { Run(Copy); e.Handled = true; }
            if (e.Key == VirtualKey.V) { _ = PasteAsync(); e.Handled = true; }
            if (e.Key == VirtualKey.Z) { Run(Undo); e.Handled = true; }
            return;
        }
        var shift = Down(VirtualKey.Shift);
        if (e.Key == VirtualKey.Tab)
        {
            var index = Math.Clamp(r * rows[r].Cells.Length + c + (shift ? -1 : 1), 0, rows.Length * rows[r].Cells.Length - 1);
            Select(index / rows[r].Cells.Length, index % rows[r].Cells.Length, false); e.Handled = true;
        }
        else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Enter)
        {
            Select(Math.Clamp(r + (e.Key is VirtualKey.Down or VirtualKey.Enter ? 1 : e.Key == VirtualKey.Up ? -1 : 0), 0, rows.Length - 1),
                Math.Clamp(c + (e.Key == VirtualKey.Right ? 1 : e.Key == VirtualKey.Left ? -1 : 0), 0, rows[r].Cells.Length - 1), shift && e.Key != VirtualKey.Enter);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete) { Run(ClearSelected); e.Handled = true; }
    }
    private void Run(Action action)
    {
        try { action(); Update(); _ = session.FlushAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { status.Text = ex is InvalidOperationException ? ex.Message : "クリップボードを利用できません。"; }
    }
    private void Copy()
    {
        _ = Range().ToArray();
        var lines = new List<string>();
        for (var r = Math.Min(anchorRow, currentRow); r <= Math.Max(anchorRow, currentRow); r++)
        {
            var values = new List<string>();
            for (var c = Math.Min(anchorColumn, currentColumn); c <= Math.Max(anchorColumn, currentColumn); c++)
            {
                var cell = rows[r].Cells[c]; var value = session.Workspace.Value(cell);
                if (cell.Key?.Kind is "Select" or "LocalSelect" && value is not null && !cell.Options.Any(o => o.Id == value)) throw new InvalidOperationException("保存された選択肢IDを確認できません。コピーを中止しました。");
                if (cell.Key is not null && cell.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                    throw new InvalidOperationException("未取得・非対応の値を空欄としてコピーできません。");
                values.Add(cell.Key is null ? cell.Display : cell.Key.Kind is "Select" or "LocalSelect" ? cell.Options.SingleOrDefault(o => o.Id == value)?.Name ?? "" : value ?? "");
                if (values[^1].IndexOfAny(['\r','\n','\t']) >= 0) throw new InvalidOperationException($"行 {r + 1} 列 {c + 1}: タブ・改行を含む値はこのTSV形式でコピーできません。");
            }
            lines.Add(string.Join('\t', values));
        }
        var package = new DataPackage(); package.SetText(string.Join("\r\n", lines)); Clipboard.SetContent(package); Clipboard.Flush();
    }
    private async Task PasteAsync()
    {
        // Capture targets before awaiting the clipboard; navigation cannot redirect this operation.
        var r = Math.Min(anchorRow, currentRow); var c = Math.Min(anchorColumn, currentColumn); var selected = active;
        var requestGeneration = generation;
        var destinations = rows.Select(row => row with { Cells = row.Cells.ToArray() }).ToArray();
        try
        {
            var text = await readClipboard();
            if (generation != requestGeneration || !IsLoaded) { status.Text = "表示対象が変わったため、遅れて届いた貼り付けを中止しました。"; return; }
            Run(() => { if (!selected) throw new InvalidOperationException("セルを選択してください。"); session.Workspace.Paste(projectId, destinations, r, c, text); });
        }
        catch (Exception) { status.Text = "クリップボードを読み取れません。"; }
    }
    private async Task AppendAsync()
    {
        var request = generation; var revision = session.Workspace.Revision;
        var inputMapping = layout.Visible.Where(c => c.Id.Role != "Reference").ToArray();
        var destination = registration;
        try
        {
            var text = await readClipboard();
            if (request != generation || !IsLoaded || revision != session.Workspace.Revision || !CanRefresh) return;
            if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "新規行の入力列を確認", PrimaryButtonText = "行を追加", CloseButtonText = "キャンセル",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "TSV列順:\n" + string.Join("\n", inputMapping.Select((col, i) => $"{i + 1}: {col.Name} [{col.Id.FieldId ?? "Title"}]")) + $"\n宛先: {destination.DefaultRepository ?? "未指定"}\n非表示フィールドは未指定です。" } };
            AutomationProperties.SetAutomationId(dialog, "AppendColumnsDialog");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || request != generation || !IsLoaded || !CanRefresh) return;
            Run(() => { var added = session.Workspace.AppendRows(destination, text, inputMapping.Select(c => c.Id).ToArray()); projection.IncludeNew(session.Workspace.Open(registration), added); RebuildRows(); Select(Array.FindIndex(rows, r => r.ItemId == added[0]), 0, false); });
        }
        catch (Exception) { status.Text = "クリップボードを読み取れません。追加していません。"; }
    }
    private sealed class TitleCell : TextBox
    {
        private readonly EditingGrid owner; private readonly int row, column; private readonly EditCell cell;
        private bool restoring; private bool composing;
        public bool Composing => composing;
        public bool Editing { get; private set; }
        public TitleCell(EditingGrid owner, int row, int column, EditCell cell)
        {
            this.owner = owner; this.row = row; this.column = column; this.cell = cell;
            IsReadOnly = !cell.Editable; Refresh();
            GotFocus += (_, _) => { if (owner.CurrentEditor(row, column, this)) owner.FocusedCell(row, column); };
            TextCompositionStarted += (_, _) => { composing = true; Editing = true; };
            TextCompositionEnded += (_, _) => composing = false;
            TextChanging += (_, _) =>
            {
                if (restoring || !cell.Editable || !owner.CurrentEditor(row, column, this)) return;
                Editing = true; owner.session.Workspace.SetBuffer(cell, Text); _ = owner.session.FlushAsync();
            };
        }
        public void Refresh()
        {
            var field = owner.session.Workspace.Field(cell);
            IsReadOnly = !cell.Editable || field?.Conflict == true || field?.Observation?.Reason is { } reason && !reason.StartsWith("未確定文字");
            var buffer = owner.session.Workspace.Buffer(cell);
            var committed = owner.session.Workspace.Value(cell);
            var value = buffer ?? (cell.Key is null ? cell.Display : cell.Key.Kind is "Select" or "LocalSelect"
                ? cell.Options.SingleOrDefault(o => o.Id == committed)?.Name ?? (committed is not null ? "保存された選択肢IDを確認できません（要照合）" : EditingWorkspace.AvailabilityText(cell.Availability))
                : committed ?? cell.Reason ?? "閲覧不可");
            if (Text != value) { restoring = true; Text = value; restoring = false; }
            Editing = buffer is not null;
        }
        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (!owner.CurrentEditor(row, column, this)) return;
            if (!Editing) { owner.Select(row, column, (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0); e.Handled = true; return; }
            owner.Select(row, column, false, false); base.OnPointerPressed(e);
        }
        protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
        {
            if (!owner.CurrentEditor(row, column, this)) return;
            if (composing) { base.OnPreviewKeyDown(e); return; }
            if (!Editing && e.Key == VirtualKey.F2 && cell.Editable) { Editing = true; owner.session.Workspace.SetBuffer(cell, Text); _ = owner.session.FlushAsync(); e.Handled = true; }
            else if (Editing && e.Key == VirtualKey.Escape)
            { owner.session.Workspace.SetBuffer(cell, null); Refresh(); SelectAll(); _ = owner.session.FlushAsync(); e.Handled = true; }
            else if (Editing && e.Key is VirtualKey.Enter or VirtualKey.Tab)
            {
                try { owner.session.Workspace.Commit(owner.projectId, cell, Text); Editing = false; owner.NavigateKey(row, column, e); _ = owner.session.FlushAsync(); }
                catch (InvalidOperationException ex) { owner.status.Text = ex.Message; e.Handled = true; }
            }
            else if (!Editing) owner.NavigateKey(row, column, e);
            // Native text Undo stays inside the editing/composition path.
            base.OnPreviewKeyDown(e);
        }
    }
}

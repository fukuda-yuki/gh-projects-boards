using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed class EditingGrid : Grid
{
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock selection = new();
    private readonly List<FrameworkElement[]> controls = [];
    private readonly List<TextBlock[]> markers = [];
    private readonly DraftSession session;
    private readonly EditRow[] rows;
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
        var r = Array.FindIndex(rows, row => row.ItemId == target.Item);
        if (r < 0) { selection.Text = "選択していた項目はProjectで未観測です。別の行には移動していません。"; return; }
        var c = Array.FindIndex(rows[r].Cells, cell => cell.Key == target.Field);
        if (c >= 0) Select(r, c, false, false);
    }
    internal EditingGrid(ProjectRegistration registration, DraftSession session)
    {
        this.session = session; projectId = registration.Snapshot.Id.NodeId;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        rows = session.Workspace.Open(registration);
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var toolbar = new StackPanel { Spacing = 8 };
        var editCommands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var recoveryCommands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(editCommands); toolbar.Children.Add(recoveryCommands);
        void Command(string text, string id, Action action)
        {
            var b = new Button { Content = text }; AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => Run(action); editCommands.Children.Add(b);
        }
        Command("コピー", "GridCopy", Copy);
        var paste = new Button { Content = "貼り付け" }; AutomationProperties.SetAutomationId(paste, "GridPaste");
        paste.Click += async (_, _) => await PasteAsync(); editCommands.Children.Add(paste);
        Command("値をクリア", "GridClear", ClearSelected);
        Command("操作を元に戻す", "GridUndo", () => session.Workspace.Undo(projectId));
        var save = new Button { Content = "ローカル保存を再試行" }; AutomationProperties.SetAutomationId(save, "GridSave");
        save.Click += async (_, _) => { await session.FlushAsync(); Update(); }; recoveryCommands.Children.Add(save);
        var compare = new Button { Content = "競合・未確認を比較" }; AutomationProperties.SetAutomationId(compare, "GridConflicts");
        compare.Click += async (_, _) => await CompareAsync(); recoveryCommands.Children.Add(compare);
        Children.Add(toolbar);
        var info = new StackPanel { Spacing = 4 }; info.Children.Add(status); info.Children.Add(selection);
        AutomationProperties.SetAutomationId(status, "DraftStatus"); AutomationProperties.SetAutomationId(selection, "GridSelection");
        SetRow(info, 1); Children.Add(info);
        AutomationProperties.SetAutomationId(list, "ProjectItems"); SetRow(list, 2); Children.Add(list);
        var columns = registration.Snapshot.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem && f.DataType == "SINGLE_SELECT").Select(f => f.Name);
        list.Header = "タイトル | " + string.Join(" | ", columns) + " | Repository / 番号 / Open-Closed（参照専用）";
        for (var r = 0; r < rows.Length; r++)
        {
            var line = new Grid { ColumnSpacing = 8 }; var rowControls = new List<FrameworkElement>(); var rowMarkers = new List<TextBlock>();
            for (var c = 0; c < rows[r].Cells.Length; c++)
            {
                var rr = r; var cc = c; var cell = rows[r].Cells[c];
                line.ColumnDefinitions.Add(new() { Width = new GridLength(c == 0 ? 320 : 200) });
                var container = new StackPanel { Spacing = 4 }; var marker = new TextBlock(); rowMarkers.Add(marker); container.Children.Add(marker);
                FrameworkElement editor;
                if (cell.Key?.Kind == "Select" && cell.Editable)
                {
                    var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = cell.Options, DisplayMemberPath = "Name" };
                    combo.SelectedItem = cell.Options.SingleOrDefault(o => o.Id == session.Workspace.Value(cell));
                    combo.GotFocus += (_, _) => FocusedCell(rr, cc);
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (updating || combo.SelectedItem is not SelectOption option) return;
                        Run(() => session.Workspace.Commit(projectId, cell, option.Id, true));
                    };
                    combo.PreviewKeyDown += (_, e) => { if (!combo.IsDropDownOpen) NavigateKey(rr, cc, e); };
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
            var item = new ListViewItem { Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, rows[r].Cells[^1].Display); list.Items.Add(item);
        }
        session.Changed += SessionChanged;
        Unloaded += (_, _) => { generation++; session.Changed -= SessionChanged; };
        Loaded += (_, _) => { session.Changed -= SessionChanged; session.Changed += SessionChanged; Update(); };
        Update(); _ = session.FlushAsync();
    }
    private async Task CompareAsync()
    {
        if (!CanRefresh) { status.Text = "IME変換を自然な操作で確定・取消してから比較してください。"; return; }
        CancelPending();
        var fields = session.Workspace.Fields.Where(f => f.Conflict || f.Observation?.Reason is not null).ToArray();
        var diagnostics = string.Join("\n", session.Workspace.StructuralChanges.Concat(session.Workspace.UndoWarnings));
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
    private void FocusedCell(int r, int c)
    {
        if (selecting || active && currentRow == r && currentColumn == c) return;
        Select(r, c, false, false);
        if (controls[r][c] is TitleCell text && !text.Editing) text.SelectAll();
    }
    private void SessionChanged() { if (DispatcherQueue.HasThreadAccess) Update(); else DispatcherQueue.TryEnqueue(Update); }
    private void Update()
    {
        updating = true;
        try
        {
            status.Text = $"このProject {rows.SelectMany(r => r.Cells).Where(session.Workspace.Changed).Select(c => c.Key).Distinct().Count()} / プロフィール変更フィールド {session.Workspace.DifferenceCount} / {session.Status}";
            status.Text += $" / 競合 {session.Workspace.Fields.Count(f => f.Conflict)} / 未確認 {session.Workspace.Fields.Count(f => f.Observation?.Reason is not null)}";
            status.Text += $"\n構成変更 {session.Workspace.StructuralChanges.Count} / 無効化したUndo {session.Workspace.UndoWarnings.Count()}（比較画面に詳細）";
            selection.Text = active ? $"行 {anchorRow + 1} 列 {anchorColumn + 1} ～ 行 {currentRow + 1} 列 {currentColumn + 1}" : "セルを選択してください。Shift＋矢印で範囲選択。F2で編集。";
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Cells.Length; c++)
            {
                var cell = rows[r].Cells[c];
                var selected = active && r >= Math.Min(anchorRow, currentRow) && r <= Math.Max(anchorRow, currentRow) && c >= Math.Min(anchorColumn, currentColumn) && c <= Math.Max(anchorColumn, currentColumn);
                markers[r][c].Text = (selected ? "選択 " : "") + (session.Workspace.Changed(cell) ? "変更あり " : "") + (session.Workspace.Buffer(cell) is not null ? "編集中（未確定）" : cell.Reason ?? "");
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
            if (e.Key == VirtualKey.Z) { Run(() => session.Workspace.Undo(projectId)); e.Handled = true; }
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
                if (cell.Key?.Kind == "Select" && value is not null && !cell.Options.Any(o => o.Id == value)) throw new InvalidOperationException("保存された選択肢IDを確認できません。コピーを中止しました。");
                if (cell.Key is not null && cell.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                    throw new InvalidOperationException("未取得・非対応の値を空欄としてコピーできません。");
                values.Add(cell.Key is null ? cell.Display : cell.Key.Kind == "Select" ? cell.Options.SingleOrDefault(o => o.Id == value)?.Name ?? "" : value ?? "");
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
        try
        {
            var text = await Clipboard.GetContent().GetTextAsync();
            if (generation != requestGeneration || !IsLoaded) return;
            Run(() => { if (!selected) throw new InvalidOperationException("セルを選択してください。"); session.Workspace.Paste(projectId, rows, r, c, text); });
        }
        catch (Exception) { status.Text = "クリップボードを読み取れません。"; }
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
            GotFocus += (_, _) => owner.FocusedCell(row, column);
            TextCompositionStarted += (_, _) => { composing = true; Editing = true; };
            TextCompositionEnded += (_, _) => composing = false;
            TextChanging += (_, _) =>
            {
                if (restoring || !cell.Editable) return;
                Editing = true; owner.session.Workspace.SetBuffer(cell, Text); _ = owner.session.FlushAsync();
            };
        }
        public void Refresh()
        {
            var field = owner.session.Workspace.Field(cell);
            IsReadOnly = !cell.Editable || field?.Conflict == true || field?.Observation?.Reason is { } reason && !reason.StartsWith("未確定文字");
            var buffer = owner.session.Workspace.Buffer(cell);
            var committed = owner.session.Workspace.Value(cell);
            var value = buffer ?? (cell.Key is null ? cell.Display : cell.Key.Kind == "Select"
                ? cell.Options.SingleOrDefault(o => o.Id == committed)?.Name ?? (committed is not null ? "保存された選択肢IDを確認できません（要照合）" : EditingWorkspace.AvailabilityText(cell.Availability))
                : committed ?? cell.Reason ?? "閲覧不可");
            if (Text != value) { restoring = true; Text = value; restoring = false; }
            Editing = buffer is not null;
        }
        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (!Editing) { owner.Select(row, column, (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0); e.Handled = true; return; }
            owner.Select(row, column, false, false); base.OnPointerPressed(e);
        }
        protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
        {
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

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
    internal EditingGrid(ProjectRegistration registration, DraftSession session)
    {
        this.session = session; projectId = registration.Snapshot.Id.NodeId;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        rows = session.Workspace.Open(registration);
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Command(string text, string id, Action action)
        {
            var b = new Button { Content = text }; AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => Run(action); toolbar.Children.Add(b);
        }
        Command("コピー", "GridCopy", Copy);
        var paste = new Button { Content = "貼り付け" }; AutomationProperties.SetAutomationId(paste, "GridPaste");
        paste.Click += async (_, _) => await PasteAsync(); toolbar.Children.Add(paste);
        Command("値をクリア", "GridClear", ClearSelected);
        Command("操作を元に戻す", "GridUndo", () => session.Workspace.Undo(projectId));
        var save = new Button { Content = "ローカル保存を再試行" }; AutomationProperties.SetAutomationId(save, "GridSave");
        save.Click += async (_, _) => { await session.FlushAsync(); Update(); }; toolbar.Children.Add(save);
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
            selection.Text = active ? $"行 {anchorRow + 1} 列 {anchorColumn + 1} ～ 行 {currentRow + 1} 列 {currentColumn + 1}" : "セルを選択してください。Shift＋矢印で範囲選択。F2で編集。";
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Cells.Length; c++)
            {
                var cell = rows[r].Cells[c];
                var selected = active && r >= Math.Min(anchorRow, currentRow) && r <= Math.Max(anchorRow, currentRow) && c >= Math.Min(anchorColumn, currentColumn) && c <= Math.Max(anchorColumn, currentColumn);
                markers[r][c].Text = (selected ? "選択 " : "") + (session.Workspace.Changed(cell) ? "変更あり " : "") + (session.Workspace.Buffer(cell) is not null ? "編集中（未確定）" : cell.Reason ?? "");
                if (controls[r][c] is TitleCell text) text.Refresh();
                if (controls[r][c] is ComboBox combo) combo.SelectedItem = cell.Options.SingleOrDefault(o => o.Id == session.Workspace.Value(cell));
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

using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using System.Diagnostics;
using Windows.Foundation;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid : Grid
{
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(0) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock selection = new();
    private readonly TextBlock columnNotice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly List<FrameworkElement[]> controls = [];
    private readonly List<Grid> rowLines = [];
    private readonly List<TextBlock[]> markers = [];
    private readonly List<Border[]> cellBorders = [];
    private readonly SheetDiagnostics? diagnostics;
    private long diagnosticFlushSequence;
    private readonly Style cellStyle;
    private readonly Style selectedCellStyle;
    private readonly Grid headerGrid = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer headerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled, IsTabStop = false, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock selectedDetails = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock selectionMode = new();
    private readonly Border detailsPane = new() { Visibility = Visibility.Collapsed, Padding = new(12, 8, 12, 8), BorderThickness = new(0, 1, 0, 0) };
    private readonly TextBlock emptyView = new() { Text = "表示する行はありません。行の表示設定で条件を変更・リセットできます。", TextWrapping = TextWrapping.Wrap, Margin = new(20), Visibility = Visibility.Collapsed };
    private ScrollViewer? listScroll;
    private Button reapplyButton = null!;
    private Button detailsButton = null!;
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
        diagnostics = SheetDiagnostics.Create();
        using var measured = diagnostics?.Span("grid-constructor");
        cellStyle = (Style)Application.Current.Resources["SheetCellStyle"];
        selectedCellStyle = (Style)Application.Current.Resources["SheetSelectedCellStyle"];
        headerGrid.Style = (Style)Application.Current.Resources["SheetHeaderStyle"];
        detailsPane.Style = (Style)Application.Current.Resources["SheetDetailsStyle"];
        this.readClipboard = readClipboard ?? (async () => await Clipboard.GetContent().GetTextAsync());
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        layout = session.Workspace.Columns(registration);
        projection = previousProjection ?? new(registration.Snapshot.Id);
        if (previousProjection is null) projection.Reapply(session.Workspace, registration);
        else projection.Promote(session.Workspace);
        canonicalRows = session.Workspace.Open(registration); rows = layout.Resolve(projection.Resolve(canonicalRows));
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new());
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(toolbar, "GridCommandBar");
        AppBarButton Tool(string label, string id, Symbol icon, bool secondary = false)
        {
            var button = new AppBarButton { Label = label, Icon = new SymbolIcon(icon) };
            AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            if (secondary) toolbar.SecondaryCommands.Add(button); else toolbar.PrimaryCommands.Add(button);
            return button;
        }
        void RowCommand(string text, string id, Func<EditRow[], string[]?> action)
        {
            var button = Tool(text, id, id == "GridAddRow" ? Symbol.Add : id == "GridRemoveRows" ? Symbol.Delete : Symbol.Copy, id != "GridAddRow");
            button.Click += async (_, _) =>
            {
                var request = generation;
                var targets = active ? SelectedRows() : [];
                if (!CanRefresh || !await prepareLocalRows() || request != generation || !IsLoaded) return;
                Run(() => { var added = action(targets); projection.IncludeNew(session.Workspace.Open(registration), added ?? []); RebuildRows(); if (added is { Length: > 0 }) Select(Array.FindIndex(rows, r => r.ItemId == added[0]), 0, false); });
            };
        }
        RowCommand("新規行を追加", "GridAddRow", _ => [session.Workspace.AddRow(registration)]);
        RowCommand("選択行を複製", "GridDuplicateRows", targets => session.Workspace.DuplicateRows(registration, targets));
        RowCommand("新規行を削除", "GridRemoveRows", targets => { session.Workspace.RemoveRows(projectId, targets); return null; });
        var append = Tool("新規行として貼り付け", "GridAppendRows", Symbol.Paste, true);
        append.Click += async (_, _) => await AppendAsync();
        void Command(string text, string id, Symbol icon, Action action, bool secondary = false)
        {
            var b = Tool(text, id, icon, secondary); b.Click += (_, _) => Run(action);
        }
        var paste = Tool("貼り付け", "GridPaste", Symbol.Paste);
        paste.Click += async (_, _) => await PasteAsync();
        Command("コピー", "GridCopy", Symbol.Copy, Copy);
        Command("元に戻す", "GridUndo", Symbol.Undo, Undo);
        Command("値をクリア", "GridClear", Symbol.Clear, ClearSelected, true);
        var columnSettings = Tool("列", "GridColumns", Symbol.ViewAll);
        columnSettings.Click += async (_, _) => await ConfigureColumnsAsync(columnSettings);
        var viewSettings = Tool("並べ替え・フィルター", "GridRowSettings", Symbol.Filter);
        viewSettings.Click += async (_, _) => await ConfigureRowsAsync(viewSettings);
        var compare = Tool("競合・未確認を比較", "GridConflicts", Symbol.TwoPage, true);
        compare.Click += async (_, _) => await CompareAsync();
        var save = Tool("ローカル保存を再試行", "GridSave", Symbol.Save, true);
        save.Click += async (_, _) => { var request = generation; await FlushDraftsAsync("save-command"); if (IsLoaded && request == generation) Update("save-command"); };
        Children.Add(toolbar);
        var viewStrip = new Grid { Padding = new(8, 2, 8, 2), ColumnSpacing = 8 };
        viewStrip.ColumnDefinitions.Add(new()); viewStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        viewNotice.TextWrapping = TextWrapping.NoWrap; viewNotice.TextTrimming = TextTrimming.CharacterEllipsis; viewNotice.VerticalAlignment = VerticalAlignment.Center;
        viewStrip.Children.Add(viewNotice);
        reapplyButton = new Button { Content = "再適用", Padding = new(8, 4, 8, 4), MinHeight = 28 };
        AutomationProperties.SetAutomationId(reapplyButton, "GridReapply"); reapplyButton.Click += (_, _) => ReapplyRows(reapplyButton);
        SetColumn(reapplyButton, 1); viewStrip.Children.Add(reapplyButton); SetRow(viewStrip, 1); Children.Add(viewStrip);
        AutomationProperties.SetAutomationId(viewNotice, "RowViewStatus");
        AutomationProperties.SetAutomationId(columnNotice, "ColumnTransitionStatus");
        AutomationProperties.SetAutomationId(status, "DraftStatus"); AutomationProperties.SetAutomationId(selection, "GridSelection");
        AutomationProperties.SetAutomationId(headerGrid, "SheetHeader"); headerScroll.Content = headerGrid; SetRow(headerScroll, 2); Children.Add(headerScroll);
        AutomationProperties.SetAutomationId(list, "ProjectItems"); SetRow(list, 3); Children.Add(list);
        SetRow(emptyView, 3); Children.Add(emptyView);
        AutomationProperties.SetAutomationId(selectedDetails, "SelectedCellDetails");
        detailsPane.Child = new ScrollViewer { Content = selectedDetails, MaxHeight = 156, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(detailsPane, 4); Children.Add(detailsPane);
        var footer = new StackPanel { Spacing = 2, Padding = new(8, 4, 8, 4) };
        var selectionBar = new Grid { ColumnSpacing = 12 };
        selectionBar.ColumnDefinitions.Add(new()); selectionBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); selectionBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        selection.TextTrimming = TextTrimming.CharacterEllipsis; selectionBar.Children.Add(selection);
        SetColumn(selectionMode, 1); selectionBar.Children.Add(selectionMode);
        var detailButton = detailsButton = new Button { Content = "選択内容の詳細", Padding = new(8, 2, 8, 2), MinHeight = 26 };
        AutomationProperties.SetAutomationId(detailButton, "GridDetails"); detailButton.Click += (_, _) => { detailsPane.Visibility = detailsPane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; UpdateSelectedDetails(); };
        SetColumn(detailButton, 2); selectionBar.Children.Add(detailButton);
        footer.Children.Add(selectionBar); footer.Children.Add(new ScrollViewer { Content = status, MaxHeight = 36, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); footer.Children.Add(columnNotice);
        SetRow(footer, 5); Children.Add(footer);
        ToolTipService.SetToolTip(detailButton, "F6で表・コマンド・詳細に移動。Shift+F6で逆順。");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.F6 || !CanRefresh) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            var region = ReferenceEquals(focused, reapplyButton) ? 1 : ReferenceEquals(focused, detailsButton) ? 2 : 0;
            var next = (region + (Down(VirtualKey.Shift) ? 2 : 1)) % 3;
            if (next == 0 && rows.Length > 0)
            {
                if (active) RestoreWorkspaceFocus();
                else Select(0, 0, false);
            }
            else if (next == 2) detailsButton.Focus(FocusState.Keyboard);
            else reapplyButton.Focus(FocusState.Keyboard);
            e.Handled = true;
        };
        BuildRows();
        ActualThemeChanged += (_, _) => Update("theme");
        Unloaded += (_, _) => { generation++; session.Changed -= SessionChanged; if (listScroll is not null) { listScroll.ViewChanged -= ScrollChanged; listScroll.SizeChanged -= ScrollSizeChanged; } listScroll = null; diagnostics?.Detach(); };
        Loaded += (_, _) => { session.Changed -= SessionChanged; session.Changed += SessionChanged; listScroll = Descendants(list).OfType<ScrollViewer>().FirstOrDefault(); if (listScroll is not null) { listScroll.ViewChanged += ScrollChanged; listScroll.SizeChanged += ScrollSizeChanged; SyncHeader(); } diagnostics?.Attach(CaptureDiagnosticState); Update("loaded"); };
        if (diagnostics is not null)
        {
            GettingFocus += (_, args) => diagnostics.Record("getting-focus", new { oldTarget = DiagnosticId(args.OldFocusedElement), newTarget = DiagnosticId(args.NewFocusedElement) });
            BringIntoViewRequested += (_, args) => diagnostics.Record("bring-into-view-request", new { target = DiagnosticId(args.TargetElement), args.AnimationDesired,
                args.HorizontalAlignmentRatio, args.VerticalAlignmentRatio, args.HorizontalOffset, args.VerticalOffset });
        }
        Update("constructor"); _ = FlushDraftsAsync("constructor");
    }
    private EditRow[] SelectedRows()
    {
        if (!active) throw new InvalidOperationException("セルを選択してください。Shift＋上下で複数行を選択できます。");
        return rows[Math.Min(anchorRow, currentRow)..(Math.Max(anchorRow, currentRow) + 1)];
    }
    private void Undo()
    {
        if (!CanRefresh) throw new InvalidOperationException("IME変換中です。自然に確定・取消してからUndoしてください。");
        var before = session.Workspace.LocalRows.Select(row => row.Id).ToHashSet();
        session.Workspace.Undo(projectId);
        // A reopened projection never saw rows removed before restart. Make only
        // newly restored local identities reachable until explicit reapplication.
        var canonical = session.Workspace.Open(registration);
        projection.IncludeNew(canonical, canonical.Where(row => row.IsLocal && !before.Contains(row.ItemId)).Select(row => row.ItemId));
        // Undo restores original keys, including rows absent from the current projection.
        if (!canonicalRows.Select(r => r.ItemId).SequenceEqual(canonical.Select(r => r.ItemId))) RebuildRows();
    }
    private void RebuildRows()
    {
        using var measured = diagnostics?.Span("rebuild");
        diagnostics?.Record("rebuild-start", new { generation, retainedRows = controls.Count, retainedCells = controls.Sum(row => row.Length) });
        var identity = SelectionIdentity; generation++; active = false;
        projection.Promote(session.Workspace);
        canonicalRows = session.Workspace.Open(registration); rows = layout.Resolve(projection.Resolve(canonicalRows)); list.Items.Clear(); controls.Clear(); markers.Clear(); cellBorders.Clear(); rowLines.Clear();
        paintedSelection.Clear(); paintedCurrent = null;
        BuildRows(); RestoreSelection(identity);
    }
    private void BuildRows()
    {
        using var measured = diagnostics?.Span("build");
        headerGrid.Children.Clear(); headerGrid.ColumnDefinitions.Clear();
        headerGrid.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
        headerGrid.Children.Add(new TextBlock { Text = "行", Margin = new(8, 8, 0, 8) });
        for (var c = 0; c < layout.Visible.Length; c++)
        {
            headerGrid.ColumnDefinitions.Add(new() { Width = new GridLength(layout.Visible[c].Preference.Width) });
            var name = layout.Visible[c].Name;
            if (layout.Visible.Count(v => v.Name == name) > 1) name += $" [{layout.Visible[c].Id.FieldId ?? layout.Visible[c].Id.Role}]";
            var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 4, 8, 4) };
            AutomationProperties.SetAutomationId(label, $"GridHeader{c}");
            ToolTipService.SetToolTip(label, name);
            SetColumn(label, c + 1); headerGrid.Children.Add(label);
        }
        for (var r = 0; r < rows.Length; r++)
        {
            var index = r;
            var line = new Grid { MinHeight = 30 };
            rowLines.Add(line); controls.Add([]); markers.Add([]); cellBorders.Add([]);
            line.Loading += (_, _) => { if (CurrentLine(index, line)) EnsureRow(index); };
            line.Unloaded += (_, _) => { if (CurrentLine(index, line)) ReleaseRow(index); };
            var item = new ListViewItem { Content = line, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new(0), Margin = new(0), BorderThickness = new(0), MinHeight = 30, IsTabStop = false };
            AutomationProperties.SetName(item, rows[r].Cells[^1].Display); list.Items.Add(item);
        }
        ResizeSheetColumns();
        diagnostics?.Record("objects-created", new { rows = rows.Length, cells = controls.Sum(row => row.Length),
            titleCells = controls.Sum(row => row.Count(cell => cell is TitleCell)), comboBoxes = controls.Sum(row => row.Count(cell => cell is ComboBox)),
            rowContainers = list.Items.Count, borders = cellBorders.Sum(row => row.Length) });
        diagnostics?.RequestVisualCounts();
    }
    private bool CurrentLine(int row, Grid line) => row < rowLines.Count && ReferenceEquals(rowLines[row], line);
    private void EnsureRow(int r)
    {
        if (controls[r].Length != 0) return;
        using var measured = diagnostics?.Span("realize-row");
        var line = rowLines[r];
        var rowControls = new List<FrameworkElement>(); var rowMarkers = new List<TextBlock>(); var borders = new List<Border>();
            line.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
            var number = new TextBlock { Text = (r + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(number, $"GridRowNumber{r}"); line.Children.Add(number);
            for (var c = 0; c < rows[r].Cells.Length; c++)
            {
                var rr = r; var cc = c; var cell = rows[r].Cells[c];
                line.ColumnDefinitions.Add(new() { Width = new GridLength(layout.Visible[c].Preference.Width) });
                var container = new Grid(); container.ColumnDefinitions.Add(new()); container.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var marker = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0), Visibility = Visibility.Collapsed }; rowMarkers.Add(marker);
                SetColumn(marker, 1); container.Children.Add(marker);
                FrameworkElement editor;
                if (cell.Key?.Kind is "Select" or "LocalSelect" && cell.Editable)
                {
                    var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = cell.Options, DisplayMemberPath = "Name", MinHeight = 26, Padding = new(8, 2, 4, 2), BorderThickness = new(0), CornerRadius = new(0), PlaceholderText = "（空値）" };
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
                AutomationProperties.SetName(editor, $"行 {r + 1} 列 {c + 1} {layout.Visible[c].Name} {cell.Display} {cell.Reason}");
                if (cell.Reason is { } reason && reason != "参照専用") ToolTipService.SetToolTip(editor, reason);
                container.Children.Add(editor); rowControls.Add(editor);
                var border = new Border { Child = container, BorderThickness = new(1), MinHeight = 30 }; borders.Add(border);
                SetColumn(border, c + 1); line.Children.Add(border);
            }
        controls[r] = rowControls.ToArray(); markers[r] = rowMarkers.ToArray(); cellBorders[r] = borders.ToArray();
        var wasUpdating = updating; updating = true;
        try { for (var c = 0; c < controls[r].Length; c++) UpdateCell(r, c); }
        finally { updating = wasUpdating; }
    }
    private void ReleaseRow(int r)
    {
        // A focused or pending native editor keeps its identity and caret even offscreen.
        // Inactive controls are discarded, never rebound to a different row or field.
        if (active && r == currentRow || controls[r].OfType<TitleCell>().Any(cell => cell.Editing || cell.Composing)) return;
        controls[r] = []; markers[r] = []; cellBorders[r] = [];
        rowLines[r].Children.Clear(); rowLines[r].ColumnDefinitions.Clear();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private void ScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        diagnostics?.Record("scroll-view-changed", new { args.IsIntermediate, state = CaptureDiagnosticState(false) });
        if (!args.IsIntermediate) diagnostics?.RequestVisualCounts();
        SyncHeader();
    }
    private void ScrollSizeChanged(object sender, SizeChangedEventArgs args) => SyncHeader();
    private void SyncHeader()
    {
        using var measured = diagnostics?.Span("header-sync");
        if (listScroll is not { ViewportWidth: > 0 }) return;
        diagnostics?.Record("header-sync-request", new { listScroll.ViewportWidth, listScroll.HorizontalOffset, headerWidth = headerScroll.Width, headerOffset = headerScroll.HorizontalOffset });
        // Match the data viewport, including its scrollbar space, so the last column stays aligned.
        headerScroll.Width = listScroll.ViewportWidth;
        headerScroll.ChangeView(listScroll.HorizontalOffset, null, null, true);
    }
    private void FocusViewCommand() => reapplyButton.Focus(FocusState.Programmatic);
    private void RestoreWorkspaceFocus()
    {
        if (active)
        {
            EnsureRow(currentRow);
            list.ScrollIntoView(list.Items[currentRow]);
            var editor = (Control)controls[currentRow][currentColumn];
            if (!editor.Focus(FocusState.Keyboard)) { reapplyButton.Focus(FocusState.Keyboard); return; }
            // Focus returns to the same active identity without resetting the range.
            // Rebuilt unedited titles still need native replacement selection.
            if (editor is TitleCell text && !text.Editing) text.SelectAll();
        }
        else reapplyButton.Focus(FocusState.Keyboard);
    }
    private void ResizeSheetColumns()
    {
        foreach (var line in list.Items.Cast<ListViewItem>().Select(i => (Grid)i.Content).Prepend(headerGrid))
        {
            for (var c = 0; c + 1 < line.ColumnDefinitions.Count; c++) line.ColumnDefinitions[c + 1].Width = new(layout.Visible[c].Preference.Width);
            line.Width = 44 + layout.Visible.Sum(c => c.Preference.Width);
        }
    }
    private bool updating;
    private long presentedRevision = -1;
    private int presentedGeneration = -1;
    private EditingWorkspace? presentedWorkspace;
    private string statusBeforeSave = "", statusAfterSave = "";
    private readonly HashSet<(int Row, int Column)> paintedSelection = [];
    private (int Row, int Column)? paintedCurrent;
    private bool CurrentEditor(int r, int c, FrameworkElement editor) => IsLoaded && r >= 0 && r < controls.Count
        && c >= 0 && c < controls[r].Length && ReferenceEquals(controls[r][c], editor);
    private static string? DiagnosticId(DependencyObject? element) => element is null ? null : AutomationProperties.GetAutomationId(element);
    private object CaptureDiagnosticState(bool includeVisuals)
    {
        try
        {
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            EditCell? current = active && currentRow < rows.Length && currentColumn < rows[currentRow].Cells.Length ? rows[currentRow].Cells[currentColumn] : null;
            object? visuals = null;
            if (includeVisuals)
            {
                var tree = Descendants(this).ToHashSet();
                var editors = controls.SelectMany(row => row).ToArray();
                var viewport = listScroll is null ? new Rect() : listScroll.TransformToVisual(this).TransformBounds(new(0, 0, listScroll.ViewportWidth, listScroll.ViewportHeight));
                bool InViewport(FrameworkElement editor)
                {
                    if (!editor.IsLoaded || !tree.Contains(editor)) return false;
                    var bounds = editor.TransformToVisual(this).TransformBounds(new(0, 0, editor.ActualWidth, editor.ActualHeight));
                    return bounds.Left < viewport.Right && bounds.Right > viewport.Left && bounds.Top < viewport.Bottom && bounds.Bottom > viewport.Top;
                }
                var inViewport = controls.SelectMany((row, r) => row.Select((editor, c) => (editor, r, c)))
                    .Where(cell => InViewport(cell.editor)).ToArray();
                var columnIdentities = layout.Visible;
                visuals = new { visualDescendants = tree.Count, retainedEditors = editors.Length,
                    loadedEditors = editors.Count(editor => editor.IsLoaded), attachedEditors = editors.Count(tree.Contains),
                    viewportIntersectingEditors = inViewport.Length, retainedContainers = list.Items.Count,
                    loadedContainers = list.Items.Cast<ListViewItem>().Count(item => item.IsLoaded),
                    attachedContainers = list.Items.Cast<ListViewItem>().Count(tree.Contains),
                    nativeTextBoxesInTree = tree.OfType<TextBox>().Count(), nativeComboBoxesInTree = tree.OfType<ComboBox>().Count(),
                    viewport, pendingCells = rows.SelectMany(row => row.Cells).Select(cell => session.Workspace.Buffer(cell)?.Length).Count(length => length is not null),
                    viewportCellsTruncated = Math.Max(0, inViewport.Length - 256),
                    viewportCells = inViewport.Take(256).Select(cell => new { row = cell.r, column = cell.c, item = rows[cell.r].ItemId,
                        field = columnIdentities[cell.c].Id, key = rows[cell.r].Cells[cell.c].Key,
                        bufferLength = session.Workspace.Buffer(rows[cell.r].Cells[cell.c])?.Length,
                        bounds = cell.editor.TransformToVisual(this).TransformBounds(new(0, 0, cell.editor.ActualWidth, cell.editor.ActualHeight)) }).ToArray() };
            }
            return new { generation, IsLoaded, project = projectId, canonicalRows = canonicalRows.Length, projectedRows = rows.Length,
                columns = rows.Length == 0 ? 0 : rows[0].Cells.Length, active, currentRow, currentColumn, anchorRow, anchorColumn,
                item = current is null ? null : rows[currentRow].ItemId, key = current?.Key,
                bufferLength = current is null ? null : session.Workspace.Buffer(current)?.Length,
                editing = active && currentRow < controls.Count && currentColumn < controls[currentRow].Length && controls[currentRow][currentColumn] is TitleCell { Editing: true },
                composing = active && currentRow < controls.Count && currentColumn < controls[currentRow].Length && controls[currentRow][currentColumn] is TitleCell { Composing: true },
                focusedId = DiagnosticId(focused), focusedType = focused?.GetType().Name,
                horizontalOffset = listScroll?.HorizontalOffset, verticalOffset = listScroll?.VerticalOffset,
                viewportWidth = listScroll?.ViewportWidth, viewportHeight = listScroll?.ViewportHeight,
                extentWidth = listScroll?.ExtentWidth, extentHeight = listScroll?.ExtentHeight,
                headerOffset = headerScroll.HorizontalOffset, scale = XamlRoot?.RasterizationScale, visuals };
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A probe must not turn a disappearing native element into a product failure.
            return new { diagnosticStateUnavailable = error.GetType().Name };
        }
    }
    private Task<bool> FlushDraftsAsync(string reason)
    {
        if (diagnostics is null) return session.FlushAsync();
        var request = ++diagnosticFlushSequence;
        var start = Stopwatch.GetTimestamp();
        diagnostics.Record("flush-request", new { request, reason, revision = session.Workspace.Revision, durableRevision = session.DurableRevision });
        Task<bool> operation;
        // Save continuations retain their AsyncLocal scope; unrelated caller UI work must not.
        var trace = new PerformanceTrace();
        try
        {
            using (diagnostics.Span("flush-synchronous-call", reason)) operation = session.FlushAsync();
        }
        finally { trace.Dispose(); }
        diagnostics.Record("flush-returned-task", new { request, operation.IsCompleted, synchronousTicks = Stopwatch.GetTimestamp() - start });
        _ = ObserveFlushAsync(operation, request, start, trace);
        return operation;
    }
    private async Task ObserveFlushAsync(Task<bool> operation, long request, long start, PerformanceTrace trace)
    {
        try
        {
            var succeeded = await operation.ConfigureAwait(false);
            diagnostics!.Record("flush-task-completed", new { request, succeeded, wallTicks = Stopwatch.GetTimestamp() - start });
        }
        catch (Exception error)
        {
            diagnostics!.Record("flush-task-failed", new { request, wallTicks = Stopwatch.GetTimestamp() - start, exceptionType = error.GetType().Name });
        }
        finally
        {
            diagnostics!.Record("core-checkpoint-trace", new { request,
                boundary = "Inclusive synchronous work and async wall spans; no per-span thread or pure I/O claim.", samples = trace.Samples.ToArray() });
        }
    }
    private void FocusedCell(int r, int c)
    {
        if (selecting || active && currentRow == r && currentColumn == c) return;
        Select(r, c, false, false);
        if (controls[r][c] is TitleCell text && !text.Editing) text.SelectAll();
    }
    private void SessionChanged()
    {
        var request = generation;
        var queuedAt = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        diagnostics?.Record("session-changed", new { generation, inline = DispatcherQueue.HasThreadAccess, phase = session.Status.StartsWith("ローカル保存中") ? "saving" : "settled" });
        if (DispatcherQueue.HasThreadAccess) { if (IsLoaded) Update("session-inline"); }
        else if (!DispatcherQueue.TryEnqueue(() => {
            diagnostics?.Record("session-dispatch", new { queueTicks = Stopwatch.GetTimestamp() - queuedAt, stale = !IsLoaded || request != generation });
            if (IsLoaded && request == generation) Update("session-dispatch");
        }))
            throw new InvalidOperationException("The editing UI dispatcher is unavailable.");
    }
    private void Update(string updateReason = "caller")
    {
        using var measured = diagnostics?.Span("update", updateReason);
        diagnostics?.Record("update-request", new { reason = updateReason, generation, rows = rows.Length, cells = controls.Sum(row => row.Length) });
        if (!CanRefresh) return;
        // A selection or a durable-save acknowledgement does not change cell values.
        // Keep native editors untouched unless the workspace or its projection changed.
        if (presentedWorkspace == session.Workspace && presentedRevision == session.Workspace.Revision
            && presentedGeneration == generation && updateReason != "theme")
        {
            status.Text = statusBeforeSave + session.Status + statusAfterSave;
            UpdateSelection();
            UpdateSelectedDetails();
            return;
        }
        // The durable acknowledgement arrives before RegistrationWorkspace publishes the matching
        // complete snapshot. Keep the existing selection/editor identity until that panel handoff.
        if (canonicalRows.Any(row => row.IsLocal && !session.Workspace.LocalRows.Any(r => r.Id == row.ItemId)
            && session.Workspace.Creations.Any(c => c.LocalId == row.ItemId && c.Completed && c.ItemId is { } id
                && !registration.Snapshot.Items.Any(i => i.Id.NodeId == id)))) return;
        if (!canonicalRows.Where(r => r.IsLocal).Select(r => r.ItemId).SequenceEqual(session.Workspace.LocalRows.Where(r => r.ProjectId == projectId).Select(r => r.Id))) { projection.IncludeNew(session.Workspace.Open(registration), session.Workspace.LocalRows.Where(r => r.ProjectId == projectId && !canonicalRows.Any(old => old.ItemId == r.Id)).Select(r => r.Id)); RebuildRows(); }
        updating = true;
        try
        {
            using (diagnostics?.Span("update-aggregate-presentation"))
            {
            var canonical = canonicalRows;
            var definition = session.Workspace.RowView(registration);
            var displayed = DisplayedRowIds.ToHashSet();
            var sortName = definition.Sort switch { "Title" => "タイトル", "Field" => registration.Snapshot.Fields.SingleOrDefault(f => f.Id.NodeId == definition.FieldId)?.Name ?? "未確認の列", _ => "取得順" };
            var criteria = new List<string> { sortName + (definition.Descending ? " 降順" : " 昇順") };
            if (!string.IsNullOrEmpty(definition.Title)) criteria.Add($"タイトル「{definition.Title}」を含む");
            foreach (var filter in definition.Filters ?? [])
            {
                var field = registration.Snapshot.Fields.SingleOrDefault(f => f.Id.NodeId == filter.FieldId);
                criteria.Add($"{field?.Name ?? "未確認の列"}: " + string.Join("・", filter.OptionIds.Select(id => field?.Options.SingleOrDefault(o => o.Id == id)?.Name ?? "未確認の選択肢")
                    .Concat(filter.States.Select(s => s switch { "Empty" => "空値", "Unspecified" => "新規の未指定", _ => "不明・未取得" }))));
            }
            viewNotice.Text = $"全行 {canonical.Length} / 表示 {rows.Length} / 非表示の作業 {canonical.Count(r => !displayed.Contains(r.ItemId) && session.Workspace.RowHasWork(r))} / 一時表示 {rows.Count(r => projection.Temporary.Contains(r.ItemId))}  —  " + string.Join(" / ", criteria);
            if (projection.Problem is { } problem) viewNotice.Text += " / " + problem;
            if (deferredViewNotice is not null) viewNotice.Text = deferredViewNotice + "\n" + viewNotice.Text;
            bool needsReapply;
            using (diagnostics?.Span("view-fingerprint")) needsReapply = projection.NeedsReapply(session.Workspace, registration);
            reapplyButton.Content = needsReapply ? "変更した値で再適用" : "再適用";
            if (needsReapply) viewNotice.Text += " / 値が変わりました。行表示の再適用が必要です（Undoは非表示行にも反映）。";
            ToolTipService.SetToolTip(viewNotice, viewNotice.Text);
            emptyView.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var hidden = canonical.SelectMany(r => r.Cells).Where(c => layout.Hidden(c.Key?.FieldId)).ToArray();
            statusBeforeSave = $"このProject {canonical.SelectMany(r => r.Cells).Where(session.Workspace.Changed).Select(c => c.Key).Distinct().Count()} / プロフィール変更フィールド {session.Workspace.DifferenceCount} / ";
            status.Text = "";
            var hiddenChanges = hidden.Count(session.Workspace.Changed); var hiddenPending = hidden.Count(c => session.Workspace.Buffer(c) is not null);
            var hiddenConflicts = hidden.Count(c => session.Workspace.Field(c)?.Conflict == true);
            var hiddenLocal = session.Workspace.LocalRows.Where(r => r.ProjectId == projectId).SelectMany(r => r.Selects).Count(s => layout.Hidden(s.FieldId) && s.Intent != "Unspecified");
            if (hiddenChanges + hiddenPending + hiddenConflicts + hiddenLocal > 0)
                status.Text += $" / 非表示列: 変更 {hiddenChanges}・未確定 {hiddenPending}・競合 {hiddenConflicts}・新規設定 {hiddenLocal}";
            status.Text += $" / 競合 {session.Workspace.Fields.Count(f => f.Conflict)} / 未確認 {session.Workspace.Fields.Count(f => f.Observation?.Reason is not null)}";
            status.Text += $" / ローカル行 {canonical.Count(r => r.IsLocal)}";
            if (session.Workspace.StructuralChanges.Count + session.Workspace.UndoWarnings.Count() > 0)
                status.Text += $" / 構成変更 {session.Workspace.StructuralChanges.Count} / 無効化したUndo {session.Workspace.UndoWarnings.Count()}（比較画面に詳細）";
            statusAfterSave = status.Text;
            status.Text = statusBeforeSave + session.Status + statusAfterSave;
            }
            using (diagnostics?.Span("update-cell-presentation"))
            for (var r = 0; r < controls.Count; r++) for (var c = 0; c < controls[r].Length; c++) UpdateCell(r, c);
            using (diagnostics?.Span("update-selected-details")) UpdateSelectedDetails();
            UpdateSelection();
            presentedWorkspace = session.Workspace; presentedRevision = session.Workspace.Revision; presentedGeneration = generation;
        }
        finally { updating = false; }
    }
    private void UpdateCell(int r, int c)
    {
                var cell = rows[r].Cells[c];
                var selected = active && r >= Math.Min(anchorRow, currentRow) && r <= Math.Max(anchorRow, currentRow) && c >= Math.Min(anchorColumn, currentColumn) && c <= Math.Max(anchorColumn, currentColumn);
                markers[r][c].Text = (session.Workspace.Changed(cell) ? "変更あり " : "") + (session.Workspace.Buffer(cell) is not null ? "編集中（未確定）" : cell.Reason ?? "");
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
                var explanation = markers[r][c].Text;
                markers[r][c].Tag = explanation;
                var label = field?.Conflict == true ? "競合" : field?.Observation?.Reason is not null ? "確認"
                    : session.Workspace.Buffer(cell) is not null ? "入力" : session.Workspace.Changed(cell) ? "変更"
                    : rows[r].IsLocal && c == 0 ? (session.Workspace.LocalProblems(registration, rows[r].ItemId).Any() ? "要確認" : "新規")
                    : cell.Reason is not null && cell.Reason != "参照専用" ? "確認" : "";
                markers[r][c].Text = label; markers[r][c].Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                AutomationProperties.SetHelpText(controls[r][c], explanation);
                ToolTipService.SetToolTip(markers[r][c], explanation);
                cellBorders[r][c].Style = selected ? selectedCellStyle : cellStyle;
                cellBorders[r][c].BorderThickness = new(active && r == currentRow && c == currentColumn ? 2 : 1);
                if (controls[r][c] is TitleCell { Composing: false } text) text.Refresh();
                if (controls[r][c] is ComboBox combo)
                {
                    combo.IsEnabled = field?.Conflict != true && field?.Observation?.Reason is null;
                    combo.SelectedItem = cell.Options.SingleOrDefault(o => o.Id == session.Workspace.Value(cell));
                    combo.PlaceholderText = SelectDisplay(cell, session.Workspace.Value(cell));
                }
    }
    private void UpdateSelection()
    {
        var next = new HashSet<(int Row, int Column)>();
        if (active)
            for (var r = Math.Min(anchorRow, currentRow); r <= Math.Max(anchorRow, currentRow); r++)
                for (var c = Math.Min(anchorColumn, currentColumn); c <= Math.Max(anchorColumn, currentColumn); c++) next.Add((r, c));
        var changed = paintedSelection.Union(next).Where(cell => paintedSelection.Contains(cell) != next.Contains(cell)).ToHashSet();
        if (paintedCurrent is { } previous) changed.Add(previous);
        if (active) changed.Add((currentRow, currentColumn));
        foreach (var (r, c) in changed)
        {
            if (r >= cellBorders.Count || c >= cellBorders[r].Length) continue;
            cellBorders[r][c].Style = next.Contains((r, c)) ? selectedCellStyle : cellStyle;
            cellBorders[r][c].BorderThickness = new(active && r == currentRow && c == currentColumn ? 2 : 1);
        }
        paintedSelection.Clear(); paintedSelection.UnionWith(next);
        paintedCurrent = active ? (currentRow, currentColumn) : null;
        selection.Text = active ? $"行 {anchorRow + 1} 列 {anchorColumn + 1} ～ 行 {currentRow + 1} 列 {currentColumn + 1}" : "セルを選択してください。Shift＋矢印で範囲選択。F2で編集。";
    }
    private string SelectDisplay(EditCell cell, string? value)
    {
        if (value is not null) return cell.Options.SingleOrDefault(o => o.Id == value)?.Name ?? $"未確認 [{value}]";
        if (cell.Key?.Kind == "LocalSelect")
            return session.Workspace.LocalRows.Single(r => r.Id == cell.Key.NodeId).Selects.SingleOrDefault(s => s.FieldId == cell.Key.FieldId)?.ExplicitClear == true ? "明示的にクリア" : "未指定（送信しない）";
        if (session.Workspace.Field(cell)?.Change?.Clear == true) return "明示的にクリア";
        return cell.Availability == ValueAvailability.Empty ? "（空値）" : EditingWorkspace.AvailabilityText(cell.Availability);
    }
    private void UpdateSelectedDetails()
    {
        if (!active || currentRow >= rows.Length) { selectedDetails.Text = "セルを選択すると、値・入力状態・Issueの識別情報を表示します。"; selectionMode.Text = "選択モード"; return; }
        var row = rows[currentRow]; var cell = row.Cells[currentColumn]; var field = session.Workspace.Field(cell);
        var pending = session.Workspace.Buffer(cell);
        selectionMode.Text = controls[currentRow][currentColumn] is TitleCell { Composing: true } ? "IME変換中" : pending is not null ? "編集中・未確定" : cell.Editable ? "選択モード" : "参照専用";
        string Display(string? value) => cell.Key?.Kind is "Select" or "LocalSelect" ? SelectDisplay(cell, value) : value ?? "（空値）";
        string ObservedDisplay(string? value) => value is null ? "（空値）" : cell.Key?.Kind is "Select" or "LocalSelect"
            ? cell.Options.SingleOrDefault(o => o.Id == value)?.Name ?? $"未確認 [{value}]" : value;
        var lines = new List<string> { $"行 {currentRow + 1} / {layout.Visible[currentColumn].Name}  —  {selectionMode.Text}",
            $"値: {(cell.Key is null ? cell.Display : Display(session.Workspace.Value(cell)))}" };
        if (pending is not null) lines.Add($"未確定文字: {pending}\nEnter / Tabでセル確定、Escで未確定文字を取り消します。IMEの確定とセル確定は別です。");
        if (markers[currentRow][currentColumn].Tag is string explanation && explanation.Length > 0) lines.Add(explanation);
        if (field?.Observation is { } observed)
            lines.Add($"B 基準: {ObservedDisplay(field.Baseline)}\nL ローカル: {Display(field.Change is { } local ? local.Value : field.Baseline)}\nR GitHub: {(observed.Availability is ValueAvailability.Present or ValueAvailability.Empty ? ObservedDisplay(observed.Value) : EditingWorkspace.AvailabilityText(observed.Availability))}\n観測: {observed.At.LocalDateTime:g}");
        lines.Add($"{row.Cells[^1].Display}\nProject: {projectId} / 項目: {row.ItemId} / 所有: {cell.Key?.Kind ?? "参照"} / ID: {cell.Key?.NodeId} / フィールド: {cell.Key?.FieldId}");
        selectedDetails.Text = string.Join("\n", lines);
    }
    private IEnumerable<EditCell> Range()
    {
        if (!active) throw new InvalidOperationException("セルを選択してください。");
        for (var r = Math.Min(anchorRow, currentRow); r <= Math.Max(anchorRow, currentRow); r++)
            for (var c = Math.Min(anchorColumn, currentColumn); c <= Math.Max(anchorColumn, currentColumn); c++) yield return rows[r].Cells[c];
    }
    private void Select(int r, int c, bool extend, bool focus = true)
    {
        using var measured = diagnostics?.Span("select");
        diagnostics?.Record("select-request", new { row = r, column = c, extend, focus, item = rows[r].ItemId, key = rows[r].Cells[c].Key });
        EnsureRow(r);
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
        Update("select");
        diagnostics?.Record("select-result", CaptureDiagnosticState(false));
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
        using var measured = diagnostics?.Span("run");
        try { action(); Update("run"); _ = FlushDraftsAsync("run"); }
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
            MinHeight = 26; Padding = new(8, 2, 8, 2); BorderThickness = new(0); CornerRadius = new(0);
            IsReadOnly = !cell.Editable; Refresh();
            GotFocus += (_, _) => { if (owner.CurrentEditor(row, column, this)) owner.FocusedCell(row, column); };
            TextCompositionStarted += (_, _) => { composing = true; Editing = true; owner.selectionMode.Text = "IME変換中"; };
            TextCompositionEnded += (_, _) => { composing = false; owner.UpdateSelectedDetails(); };
            TextChanging += (_, _) =>
            {
                if (restoring || !cell.Editable || !owner.CurrentEditor(row, column, this)) return;
                using var measured = owner.diagnostics?.Span("text-changing");
                owner.diagnostics?.Record("text-changing", new { row, column, key = cell.Key, length = Text.Length, composing });
                Editing = true; owner.session.Workspace.SetBuffer(cell, Text); owner.UpdateSelectedDetails(); _ = owner.FlushDraftsAsync("text-changing");
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
            if (!Editing && e.Key == VirtualKey.F2 && cell.Editable) { Editing = true; owner.session.Workspace.SetBuffer(cell, Text); _ = owner.FlushDraftsAsync("edit-start"); e.Handled = true; }
            else if (Editing && e.Key == VirtualKey.Escape)
            { owner.session.Workspace.SetBuffer(cell, null); Refresh(); SelectAll(); _ = owner.FlushDraftsAsync("edit-cancel"); e.Handled = true; }
            else if (Editing && e.Key is VirtualKey.Enter or VirtualKey.Tab)
            {
                try { owner.session.Workspace.Commit(owner.projectId, cell, Text); Editing = false; owner.NavigateKey(row, column, e); _ = owner.FlushDraftsAsync("cell-commit"); }
                catch (InvalidOperationException ex) { owner.status.Text = ex.Message; e.Handled = true; }
            }
            else if (!Editing) owner.NavigateKey(row, column, e);
            // Native text Undo stays inside the editing/composition path.
            base.OnPreviewKeyDown(e);
        }
    }
}

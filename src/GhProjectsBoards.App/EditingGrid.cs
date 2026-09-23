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
using System.Text.Json;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid : Grid
{
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(0) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private string? operationProblem;
    private readonly TextBlock selection = new();
    private readonly TextBlock columnNotice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly List<FrameworkElement[]> controls = [];
    private readonly List<Grid> rowLines = [];
    private readonly LinkedList<int> dormantRows = [];
    private readonly HashSet<int> protectedUnloadedRows = [];
    private double synchronizedViewportWidth = -1, synchronizedHorizontalOffset = -1;
    private readonly List<TextBlock[]> markers = [];
    private readonly List<Border[]> cellBorders = [];
    private readonly List<Border?[]> selectionFrames = [];
    private readonly List<Button?[]> fillHandles = [];
    private readonly SheetDiagnostics? diagnostics;
    private long diagnosticFlushSequence;
    private readonly Style cellStyle;
    private readonly Grid headerGrid = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer headerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock selectedDetails = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock selectionMode = new();
    private readonly Border detailsPane = new() { Visibility = Visibility.Collapsed, Padding = new(12, 8, 12, 8), BorderThickness = new(0, 1, 0, 0) };
    private readonly TextBlock emptyView = new() { Text = "表示する行はありません。行の表示設定で条件を変更・リセットできます。", TextWrapping = TextWrapping.Wrap, Margin = new(20), Visibility = Visibility.Collapsed };
    private ScrollViewer? listScroll;
    private Button reapplyButton = null!;
    private Button detailsButton = null!;
    private Grid viewStrip = null!;
    private readonly DraftSession session;
    private EditRow[] rows;
    private EditRow[] canonicalRows;
    private ColumnLayout layout;
    private readonly ProjectRegistration registration;
    private readonly bool showRepositoryIdentity;
    private readonly Func<Task<bool>> prepareLocalRows;
    private sealed record SheetClipboard(string Text, CopiedCells? Cells);
    private const string ClipboardFormat = "GhProjectsBoards.Cells.v1";
    private readonly Func<Task<SheetClipboard>> readClipboard;
    private readonly string projectId;
    private int currentRow, currentColumn, anchorRow, anchorColumn;
    private bool active;
    private bool selecting;
    private int generation;
    internal void CancelPending() { generation++; CancelDrag(); }
    private readonly HashSet<TextBox> contextualComposition = [];
    internal bool CanRefresh => contextualComposition.Count == 0 && !controls.SelectMany(r => r).OfType<TitleCell>().Any(t => t.Composing);
    private void TrackContextInput(TextBox input)
    {
        input.TextCompositionStarted += (_, _) => contextualComposition.Add(input);
        void End()
        {
            contextualComposition.Remove(input);
            if (deferredRefresh && CanRefresh && IsLoaded) Update("context-composition-ended");
        }
        input.TextCompositionEnded += (_, _) => End(); input.Unloaded += (_, _) => End();
    }
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
    internal EditingGrid(ProjectRegistration registration, DraftSession session, Func<Task<bool>> prepareLocalRows, RowProjection? previousProjection = null, Func<Task<string>>? readClipboard = null, IEnumerable<string>? temporaryColumns = null, bool? allowSummary = null)
    {
        summaryEnabled = allowSummary ?? SummaryEvaluationEnabled();
        this.session = session; this.registration = registration; this.prepareLocalRows = prepareLocalRows; projectId = registration.Snapshot.Id.NodeId;
        recycledPresentation = Environment.GetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION") == "1";
        showRepositoryIdentity = ProjectIssueIdentity.NeedsRepository(registration.Snapshot);
        diagnostics = SheetDiagnostics.Create();
        using var measured = diagnostics?.Span("grid-constructor");
        cellStyle = (Style)Application.Current.Resources["SheetCellStyle"];
        headerGrid.Style = (Style)Application.Current.Resources["SheetHeaderStyle"];
        detailsPane.Style = (Style)Application.Current.Resources["SheetDetailsStyle"];
        this.readClipboard = readClipboard is null ? ReadClipboardAsync : async () => new(await readClipboard(), null);
        // Each row owns native editors. Realize the destination viewport without
        // speculative offscreen rows; ReleaseRow separately retains visited editors.
        list.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["SheetRowsPanel"];
        if (recycledPresentation) InitializeRecycling();
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        layout = session.Workspace.Columns(registration);
        temporaryApplyColumns.UnionWith(temporaryColumns ?? []);
        if (temporaryApplyColumns.Count > 0)
            layout = new(layout.Columns.Select(c => temporaryApplyColumns.Contains(c.Id.FieldId ?? "")
                ? c with { Preference = c.Preference with { Visible = true } } : c).ToArray());
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
        var copy = Tool("コピー", "GridCopy", Symbol.Copy);
        copy.Click += async (_, _) => await CopyAsync();
        Command("下へコピー (Ctrl+D)", "GridFillDown", Symbol.Download, FillDown);
        Command("元に戻す", "GridUndo", Symbol.Undo, Undo);
        Command("値をクリア", "GridClear", Symbol.Clear, ClearSelected, true);
        var planning = Tool("計画", "GridPlanning", Symbol.Calendar);
        planning.Click += async (_, _) => { toolbar.IsOpen = false; await ShowSchedulingEditorAsync(toolbar); };
        var taskDetails = Tool("タスクの詳細", "GridTaskDetails", Symbol.Edit, true);
        taskDetails.Click += async (_, _) => await PlanningDialogAsync(false);
        var planningSettings = Tool("計画設定", "GridPlanningSettings", Symbol.Setting, true);
        planningSettings.Click += async (_, _) => await PlanningDialogAsync(true);
        var initializePlans = Tool("自動計算を設定", "GridInitializePlans", Symbol.Calendar, true);
        initializePlans.Click += async (_, _) => await InitializeSelectedPlansAsync();
        var columnSettings = Tool("列", "GridColumns", Symbol.ViewAll);
        columnSettings.Click += async (_, _) => await ConfigureColumnsAsync(columnSettings);
        var viewSettings = Tool("並べ替え・フィルター", "GridRowSettings", Symbol.Filter);
        viewSettings.Click += async (_, _) => await ConfigureRowsAsync(viewSettings);
        var compare = Tool("競合・未確認を比較", "GridConflicts", Symbol.TwoPage, true);
        compare.Click += async (_, _) => await CompareAsync();
        var save = Tool("ローカル保存を再試行", "GridSave", Symbol.Save, true);
        save.Click += async (_, _) => { var request = generation; await FlushDraftsAsync("save-command"); if (IsLoaded && request == generation) Update("save-command"); };
        var commandRow = new Grid { ColumnSpacing = 8, Padding = new(0, 0, 8, 0) };
        commandRow.ColumnDefinitions.Add(new()); commandRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        commandRow.Children.Add(toolbar);
        var quickFilter = CreateQuickFilter(); SetColumn(quickFilter, 1); commandRow.Children.Add(quickFilter);
        Children.Add(commandRow);
        viewStrip = new Grid { Padding = new(8, 2, 8, 2), ColumnSpacing = 8, Visibility = Visibility.Collapsed };
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
        AutomationProperties.SetAutomationId(list, "ProjectItems");
        var sheetViewport = CreateSheetViewport(); SetRow(sheetViewport, 3); Children.Add(sheetViewport);
        SetRow(emptyView, 3); Children.Add(emptyView);
        AutomationProperties.SetAutomationId(selectedDetails, "SelectedCellDetails");
        detailsPane.Child = new ScrollViewer { Content = selectedDetails, MaxHeight = 156, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(detailsPane, 4); Children.Add(detailsPane);
        var footer = new StackPanel { Spacing = 0, Padding = new(8, 2, 8, 2) };
        InitializeActualInput(footer);
        InitializeDateInput(footer);
        InitializeApplyProblems(footer);
        var selectionBar = new Grid { ColumnSpacing = 12 };
        selectionBar.ColumnDefinitions.Add(new()); selectionBar.ColumnDefinitions.Add(new() { Width = new GridLength(1.7, GridUnitType.Star) }); selectionBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        selection.TextTrimming = TextTrimming.CharacterEllipsis; selection.VerticalAlignment = VerticalAlignment.Center; selectionBar.Children.Add(selection);
        status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis; status.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(status, 1); selectionBar.Children.Add(status);
        var detailButton = detailsButton = new Button { Content = "選択内容の詳細", Padding = new(8, 2, 8, 2), MinHeight = 26 };
        AutomationProperties.SetAutomationId(detailButton, "GridDetails"); detailButton.Click += (_, _) => { detailsPane.Visibility = detailsPane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; UpdateSelectedDetails(); };
        SetColumn(detailButton, 2); selectionBar.Children.Add(detailButton);
        footer.Children.Add(selectionBar); footer.Children.Add(columnNotice);
        SetRow(footer, 5); Children.Add(footer);
        ToolTipService.SetToolTip(detailButton, "F6で表・コマンド・詳細に移動。Shift+F6で逆順。");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && drag is not null) { CancelDrag(); e.Handled = true; return; }
            if (e.Key != VirtualKey.F6 || !CanRefresh) return;
            if (ShowingGantt) { gantt!.CycleFocus(Down(VirtualKey.Shift)); e.Handled = true; return; }
            if (ShowingSummary) { summaryView!.CycleFocus(Down(VirtualKey.Shift)); e.Handled = true; return; }
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            var region = ReferenceEquals(focused, reapplyButton) ? 1 : ReferenceEquals(focused, detailsButton) ? 2 : 0;
            var next = viewStrip.Visibility == Visibility.Visible
                ? (region + (Down(VirtualKey.Shift) ? 2 : 1)) % 3 : region == 2 ? 0 : 2;
            if (next == 0 && rows.Length > 0)
            {
                if (active) RestoreWorkspaceFocus();
                else Select(0, 0, false);
            }
            else if (next == 2) detailsButton.Focus(FocusState.Keyboard);
            else if (viewStrip.Visibility == Visibility.Visible) reapplyButton.Focus(FocusState.Keyboard);
            else detailsButton.Focus(FocusState.Keyboard);
            e.Handled = true;
        };
        BuildRows();
        InitializeProjectViews(commandRow);
        ActualThemeChanged += (_, _) => Update("theme");
        InitializeDrag();
        Unloaded += (_, _) => { generation++; CancelDrag(); session.Changed -= SessionChanged; DetachWheel(); if (listScroll is not null) { listScroll.ViewChanged -= ScrollChanged; listScroll.SizeChanged -= ScrollSizeChanged; } listScroll = null; diagnostics?.Detach(); };
        Loaded += (_, _) => { session.Changed -= SessionChanged; session.Changed += SessionChanged; AttachSheetScroll(); diagnostics?.Attach(CaptureDiagnosticState); Update("loaded"); };
        if (diagnostics is not null)
        {
            GettingFocus += (_, args) => diagnostics.Record("getting-focus", new { oldTarget = DiagnosticId(args.OldFocusedElement), newTarget = DiagnosticId(args.NewFocusedElement) });
            BringIntoViewRequested += (_, args) => diagnostics.Record("bring-into-view-request", new { target = DiagnosticId(args.TargetElement), targetType = args.TargetElement?.GetType().Name, originalType = args.OriginalSource?.GetType().Name, args.Handled, args.TargetRect, args.AnimationDesired,
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
        var identity = SelectionIdentity; generation++; CancelDrag(); active = false;
        projection.Promote(session.Workspace);
        if (recycledPresentation) ResetRecycling();
        canonicalRows = session.Workspace.Open(registration); rows = layout.Resolve(projection.Resolve(canonicalRows)); list.Items.Clear(); controls.Clear(); markers.Clear(); cellBorders.Clear(); selectionFrames.Clear(); fillHandles.Clear(); rowLines.Clear();
        paintedSelection.Clear(); paintedCurrent = null; dormantRows.Clear(); protectedUnloadedRows.Clear();
        synchronizedViewportWidth = synchronizedHorizontalOffset = -1;
        BuildRows(); RestoreSelection(identity);
    }
    private void BuildRows()
    {
        using var measured = diagnostics?.Span("build");
        headerGrid.Children.Clear(); headerGrid.ColumnDefinitions.Clear();
        headerGrid.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
        var corner = new Grid { Style = (Style)Application.Current.Resources["SheetHeaderStyle"] };
        corner.Children.Add(new TextBlock { Text = "行", Margin = new(8, 8, 0, 8) }); headerGrid.Children.Add(corner);
        for (var c = 0; c < layout.Visible.Length; c++)
        {
            headerGrid.ColumnDefinitions.Add(new() { Width = new GridLength(ColumnWidth(c)) });
            var name = layout.Visible[c].Name;
            if (layout.Visible.Count(v => v.Name == name) > 1) name += $" [{layout.Visible[c].Id.FieldId ?? layout.Visible[c].Id.Role}]";
            var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(label, $"GridHeader{c}");
            ToolTipService.SetToolTip(label, name);
            var heading = CreateColumnHeader(layout.Visible[c], c, label, name);
            SetColumn(heading, c + 1); headerGrid.Children.Add(heading);
        }
        if (recycledPresentation) { BuildRecycledRows(); ResizeSheetColumns(); return; }
        for (var r = 0; r < rows.Length; r++)
        {
            var index = r;
            var line = new Grid { MinHeight = 30, Height = 30 };
            rowLines.Add(line); controls.Add([]); markers.Add([]); cellBorders.Add([]); selectionFrames.Add([]); fillHandles.Add([]);
            line.Loading += (_, _) => { if (CurrentLine(index, line)) EnsureRow(index); };
            line.Unloaded += (_, _) =>
            {
                // Finish destination layout before dismantling the previous rendered rows.
                // Unloaded can run while the compositor still presents that viewport.
                // Low-priority work can starve under sustained native activity.
                // Queue behind the current layout turn at normal priority.
                DispatcherQueue.TryEnqueue(() =>
                {
                    // Window/dialog cancellation may advance generation without
                    // replacing this row. Its exact visual identity is the guard.
                    if (IsLoaded && CurrentLine(index, line) && !line.IsLoaded) ReleaseRow(index);
                });
            };
            var item = new ListViewItem { Content = line, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new(0), Margin = new(0), BorderThickness = new(0), MinHeight = 30, Height = 30, IsTabStop = false };
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
        if (recycledPresentation) { EnsureRecycledRow(r); return; }
        dormantRows.Remove(r);
        protectedUnloadedRows.Remove(r);
        if (controls[r].Length != 0)
        {
            // A cached row may return after a horizontal change while offscreen.
            FreezeIdentity(rowLines[r], listScroll?.HorizontalOffset ?? 0);
            UpdateColumnVisibility(r);
            return;
        }
        using var measured = diagnostics?.Span("realize-row");
        var line = rowLines[r];
        var count = rows[r].Cells.Length;
        controls[r] = new FrameworkElement[count]; markers[r] = new TextBlock[count]; cellBorders[r] = new Border[count];
        selectionFrames[r] = new Border[count]; fillHandles[r] = new Button[count];
        line.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
        var number = new TextBlock { Text = (r + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(number, $"GridRowNumber{r}");
        var gutter = new Grid { Style = (Style)Application.Current.Resources["SheetHeaderStyle"] };
        gutter.Children.Add(number); line.Children.Add(gutter);
        for (var c = 0; c < rows[r].Cells.Length; c++)
        {
            line.ColumnDefinitions.Add(new() { Width = new GridLength(ColumnWidth(c)) });
            // The empty border reserves the column slot; its entire content is
            // realized on reveal, including markers and interaction adornments.
            var border = new Border { BorderThickness = new(1), MinHeight = 30 };
            cellBorders[r][c] = border; controls[r][c] = border;
            SetColumn(border, c + 1); line.Children.Add(border);
        }
        var wasUpdating = updating; updating = true;
        try
        {
            for (var c = 0; c < count; c++)
                if (ColumnInViewport(c) || session.Workspace.Buffer(rows[r].Cells[c]) is not null) EnsureCell(r, c);
        }
        finally { updating = wasUpdating; }
        FreezeIdentity(line, listScroll?.HorizontalOffset ?? 0);
        UpdateColumnVisibility(r);
    }
    private FrameworkElement CreateCellEditor(int r, int c)
    {
        var cell = rows[r].Cells[c];
        FrameworkElement editor = cell.Key?.Kind is "Select" or "LocalSelect" && cell.Editable
            ? new ChoiceCell(this, r, c, cell) : new TitleCell(this, r, c, cell);
        AutomationProperties.SetAutomationId(editor, $"GridCell{r}_{c}");
        AutomationProperties.SetName(editor, $"行 {r + 1} 列 {c + 1} {layout.Visible[c].Name} {cell.Display} {cell.Reason}");
        if (cell.Reason is { } reason && reason != "参照専用") ToolTipService.SetToolTip(editor, reason);
        editor.Margin = new(0, 0, 12, 0);
        return editor;
    }
    private void EnsureCell(int r, int c)
    {
        if (recycledPresentation) { EnsureOwnedEditor(r, c); return; }
        if (controls[r][c] is TitleCell or ChoiceCell) return;
        var container = new Grid();
        var editor = CreateCellEditor(r, c);
        container.Children.Add(editor); controls[r][c] = editor;
        if (c == 0)
        {
            container.ColumnDefinitions.Add(new()); container.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var identity = new TextBlock { Text = RowIdentity(rows[r], compact: true), MaxWidth = Math.Min(132, ColumnWidth(0) * .4), TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 8, 0), FontSize = 11 };
            AutomationProperties.SetAutomationId(identity, $"GridRowIdentity{r}"); ToolTipService.SetToolTip(identity, RowIdentity(rows[r]));
            SetColumn(identity, 1); container.Children.Add(identity);
        }
        var marker = new TextBlock { FontSize = 10, Width = 10, Height = 12, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 0, 2, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(marker, $"GridMarker{r}_{c}"); markers[r][c] = marker;
        SetColumnSpan(marker, c == 0 ? 2 : 1); container.Children.Add(marker);
        cellBorders[r][c].Child = container;
        UpdateCell(r, c);
    }
    private bool ColumnInViewport(int column)
    {
        if (column == 0) return true;
        var width = listScroll is { ViewportWidth: > 0 } ? listScroll.ViewportWidth : ActualWidth > 0 ? ActualWidth : 800;
        var offset = listScroll?.HorizontalOffset ?? 0;
        var left = 44 + Enumerable.Range(0, column).Sum(ColumnWidth);
        return left + ColumnWidth(column) - offset > 44 + ColumnWidth(0) && left - offset < width;
    }
    private bool ProtectRow(int r) => active && r == currentRow || drag is { } gesture && r == gesture.SourceRow
        || controls[r].OfType<TitleCell>().Any(cell => cell.Editing || cell.Composing);
    private void ReleaseRow(int r)
    {
        // A focused or pending native editor keeps its identity and caret even offscreen.
        // Inactive controls are discarded, never rebound to a different row or field.
        if (ProtectRow(r)) { protectedUnloadedRows.Add(r); return; }
        protectedUnloadedRows.Remove(r);
        if (controls[r].Length == 0 || dormantRows.Contains(r)) return;
        // A small inactive cache avoids destroying/recreating the same native
        // controls on a scroll roundtrip. Visible, focused and pending rows are
        // never capped or rebound; their original row/field identity is retained.
        dormantRows.AddLast(r);
        while (dormantRows.Count > 64)
        {
            var release = dormantRows.First!.Value; dormantRows.RemoveFirst();
            if (rowLines[release].IsLoaded) continue;
            if (ProtectRow(release)) { protectedUnloadedRows.Add(release); continue; }
            using var measured = diagnostics?.Span("release-row");
            controls[release] = []; markers[release] = []; cellBorders[release] = []; selectionFrames[release] = []; fillHandles[release] = [];
            rowLines[release].Children.Clear(); rowLines[release].ColumnDefinitions.Clear();
        }
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private void ScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate) { wheelHorizontal = null; wheelVertical = null; }
        diagnostics?.Record("scroll-view-changed", new { args.IsIntermediate, state = CaptureDiagnosticState(false) });
        if (!args.IsIntermediate) diagnostics?.RequestVisualCounts();
        SyncHeader();
        if (recycledPresentation) PositionOwnedEditors();
    }
    private void ScrollSizeChanged(object sender, SizeChangedEventArgs args) => ResizeSheetColumns();
    private void SyncHeader(bool force = false)
    {
        using var measured = diagnostics?.Span("header-sync");
        if (listScroll is not { ViewportWidth: > 0 }) return;
        SyncScrollbar();
        if (!force && synchronizedViewportWidth == listScroll.ViewportWidth && synchronizedHorizontalOffset == listScroll.HorizontalOffset) return;
        synchronizedViewportWidth = listScroll.ViewportWidth; synchronizedHorizontalOffset = listScroll.HorizontalOffset;
        diagnostics?.Record("header-sync-request", new { listScroll.ViewportWidth, listScroll.HorizontalOffset, headerWidth = headerScroll.Width, headerOffset = headerScroll.HorizontalOffset });
        // Match the data viewport, including its scrollbar space, so the last column stays aligned.
        headerScroll.Width = listScroll.ViewportWidth;
        headerScroll.ChangeView(listScroll.HorizontalOffset, null, null, true);
        FreezeIdentity(headerGrid, listScroll.HorizontalOffset);
        for (var r = 0; r < rowLines.Count; r++)
            if (rowLines[r] is { IsLoaded: true } line) { FreezeIdentity(line, listScroll.HorizontalOffset); UpdateColumnVisibility(r); }
        if (recycledPresentation) PositionOwnedEditors();
    }
    private void UpdateColumnVisibility(int row)
    {
        if (recycledPresentation) { RefreshRecycledRow(row); return; }
        var width = listScroll is { ViewportWidth: > 0 } ? listScroll.ViewportWidth : ActualWidth > 0 ? ActualWidth : 800;
        var offset = listScroll?.HorizontalOffset ?? 0;
        var left = 44d; var frozen = 44 + ColumnWidth(0);
        for (var c = 0; c < cellBorders[row].Length; c++)
        {
            var right = left + ColumnWidth(c);
            // Offscreen columns keep their layout slots without realizing native templates.
            // The active/pending editor is never collapsed while it owns native input.
            var retain = c == 0 || active && currentRow == row && currentColumn == c
                || controls[row][c] is TitleCell { Editing: true };
            var visible = retain || right - offset > frozen && left - offset < width;
            if (visible) EnsureCell(row, c);
            cellBorders[row][c].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            left = right;
        }
    }
    private void RevealColumn(int row, int column)
    {
        EnsureCell(row, column);
        cellBorders[row][column].Visibility = Visibility.Visible;
        if (column == 0 || listScroll is not { ViewportWidth: > 0 }) return;
        var left = 44 + Enumerable.Range(0, column).Sum(ColumnWidth); var right = left + ColumnWidth(column);
        var frozen = 44 + ColumnWidth(0);
        var offset = listScroll.HorizontalOffset;
        if (left - offset < frozen) offset = left - frozen;
        else if (right - offset > listScroll.ViewportWidth) offset = right - listScroll.ViewportWidth;
        listScroll.ChangeView(Math.Clamp(offset, 0, listScroll.ScrollableWidth), null, null, true);
        if (recycledPresentation) PositionOwnedEditors();
    }
    private string RowIdentity(EditRow row, bool compact = false)
    {
        if (row.IsLocal) return "新規（ローカル）";
        var item = registration.Snapshot.Items.SingleOrDefault(item => item.Id.NodeId == row.ItemId);
        var issue = item?.ContentId is { } id ? registration.Snapshot.Issues.GetValueOrDefault(id) : null;
        return issue is null ? row.ItemId : compact && !showRepositoryIdentity ? $"#{issue.Number}" : $"#{issue.Number}  {issue.Repository.NameWithOwner}";
    }
    private void FreezeIdentity(Grid line, double offset)
    {
        foreach (var child in line.Children.OfType<FrameworkElement>().Where(child => GetColumn(child) < 2))
        {
            Canvas.SetZIndex(child, 2);
            if (child.RenderTransform is not TranslateTransform translate) child.RenderTransform = translate = new TranslateTransform();
            translate.X = offset;
        }
    }
    private void FocusViewCommand() => (viewStrip.Visibility == Visibility.Visible ? reapplyButton : detailsButton).Focus(FocusState.Programmatic);
    private void RestoreWorkspaceFocus()
    {
        if (active)
        {
            EnsureRow(currentRow);
            RevealColumn(currentRow, currentColumn);
            list.ScrollIntoView(list.Items[currentRow]);
            var editor = (Control)controls[currentRow][currentColumn];
            if (!editor.Focus(FocusState.Keyboard)) { FocusViewCommand(); return; }
            // Focus returns to the same active identity without resetting the range.
            // Rebuilt unedited titles still need native replacement selection.
            if (editor is TitleCell text && !text.Editing) text.SelectAll();
        }
        else FocusViewCommand();
    }
    private double ColumnWidth(int index)
    {
        var preferred = layout.Visible[index].Preference.Width;
        if (index != 0) return preferred;
        var viewport = listScroll is { ViewportWidth: > 0 } ? listScroll.ViewportWidth : ActualWidth > 0 ? ActualWidth : 800;
        // A saved wide title must not cover every editable field on a smaller window.
        // Preserve the preference; only constrain its current rendered width.
        return Math.Min(preferred, Math.Max(EditingWorkspace.MinimumColumnWidth, viewport - 44 - 160));
    }
    private void ResizeSheetColumns()
    {
        var widths = Enumerable.Range(0, layout.Visible.Length).Select(ColumnWidth).ToArray();
        foreach (var line in rowLines.Where(line => line is not null).Prepend(headerGrid))
        {
            for (var c = 0; c + 1 < line.ColumnDefinitions.Count; c++) line.ColumnDefinitions[c + 1].Width = new(widths[c]);
            line.Width = 44 + widths.Sum();
            foreach (var identity in Descendants(line).OfType<TextBlock>().Where(t => AutomationProperties.GetAutomationId(t).StartsWith("GridRowIdentity")))
                identity.MaxWidth = Math.Min(132, widths[0] * .4);
        }
        SyncHeader(force: true);
    }
    private bool updating;
    private bool deferredRefresh;
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
        using var measured = diagnostics?.Span("diagnostic-state", includeVisuals ? "visual-walk" : "light");
        try
        {
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            EditCell? current = active && currentRow < rows.Length && currentColumn < rows[currentRow].Cells.Length ? rows[currentRow].Cells[currentColumn] : null;
            object? visuals = null;
            if (includeVisuals)
            {
                var tree = Descendants(this).ToHashSet();
                var editors = controls.SelectMany(row => row).Where(editor => editor is not null).ToArray();
                var viewport = listScroll is null ? new Rect() : listScroll.TransformToVisual(this).TransformBounds(new(0, 0, listScroll.ViewportWidth, listScroll.ViewportHeight));
                bool InViewport(FrameworkElement editor)
                {
                    if (!editor.IsLoaded || !tree.Contains(editor)) return false;
                    var bounds = editor.TransformToVisual(this).TransformBounds(new(0, 0, editor.ActualWidth, editor.ActualHeight));
                    return bounds.Left < viewport.Right && bounds.Right > viewport.Left && bounds.Top < viewport.Bottom && bounds.Bottom > viewport.Top;
                }
                var inViewport = controls.SelectMany((row, r) => row.Select((editor, c) => (editor, r, c)))
                    .Where(cell => cell.editor is not null && InViewport(cell.editor)).ToArray();
                var columnIdentities = layout.Visible;
                visuals = new { visualDescendants = tree.Count, retainedEditors = editors.Length,
                    loadedEditors = editors.Count(editor => editor.IsLoaded), attachedEditors = editors.Count(tree.Contains),
                    viewportIntersectingEditors = inViewport.Length, retainedContainers = list.Items.Count,
                    loadedContainers = Descendants(list).OfType<ListViewItem>().Count(item => item.IsLoaded),
                    attachedContainers = Descendants(list).OfType<ListViewItem>().Count(),
                    nativeTextBoxesInTree = tree.OfType<TextBox>().Count(), nativeComboBoxesInTree = tree.OfType<ComboBox>().Count(),
                    nativeChoiceButtonsInTree = tree.OfType<ChoiceCell>().Count(),
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
        SheetDiagnostics.Observe(ObserveFlushAsync(operation, request, start, trace));
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
        // WinUI can deliver GotFocus after later key navigation and Shift release.
        // An earlier editor's notification must not reinterpret the current range.
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), controls[r][c])) return;
        if (selecting || active && currentRow == r && currentColumn == c) return;
        Select(r, c, Down(VirtualKey.Shift) && active, false);
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
        if (!CanRefresh) { deferredRefresh = true; return; }
        deferredRefresh = false;
        if (recycledPresentation) ReleaseCleanEditors();
        // Selection, pending input and drag protection can end after Unloaded.
        // Reconsider those original controls without waiting for another unload.
        foreach (var row in protectedUnloadedRows.ToArray())
            if (rowLines[row].IsLoaded) protectedUnloadedRows.Remove(row); else ReleaseRow(row);
        UpdateGantt();
        UpdateSummary();
        // A selection or a durable-save acknowledgement does not change cell values.
        // Keep native editors untouched unless the workspace or its projection changed.
        if (presentedWorkspace == session.Workspace && presentedRevision == session.Workspace.PresentationRevision
            && presentedGeneration == generation && updateReason != "theme")
        {
            RefreshStatus();
            UpdateSelection();
            UpdateSelectedDetails();
            return;
        }
        RefreshApplyProblems();
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
            var hiddenRows = canonical.Count(r => !displayed.Contains(r.ItemId) && session.Workspace.RowHasWork(r));
            var temporary = rows.Count(r => projection.Temporary.Contains(r.ItemId));
            viewNotice.Text = string.Join(" / ", criteria);
            if (temporary > 0) viewNotice.Text += $" / 一時表示 {temporary}行";
            if (projection.Problem is { } problem) viewNotice.Text += " / " + problem;
            if (deferredViewNotice is not null) viewNotice.Text = deferredViewNotice + "\n" + viewNotice.Text;
            bool needsReapply;
            using (diagnostics?.Span("view-fingerprint")) needsReapply = projection.NeedsReapply(session.Workspace, registration, canonicalRows);
            reapplyButton.Content = needsReapply ? "変更した値で再適用" : "再適用";
            if (needsReapply) viewNotice.Text += " / 値が変わりました。行表示の再適用が必要です（Undoは非表示行にも反映）。";
            viewStrip.Visibility = needsReapply || projection.Problem is not null || deferredViewNotice is not null || temporary > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(viewNotice, viewNotice.Text);
            emptyView.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var hidden = canonical.SelectMany(r => r.Cells).Where(c => layout.Hidden(c.Key?.FieldId)).ToArray();
            statusBeforeSave = $"{rows.Length}/{canonical.Length}行 · GitHub未反映 {canonical.SelectMany(r => r.Cells).Where(session.Workspace.Changed).Select(c => c.Key).Distinct().Count()}セル · ";
            var pending = canonical.SelectMany(r => r.Cells).Count(c => session.Workspace.Buffer(c) is not null);
            if (pending > 0) statusBeforeSave += $"未確定入力 {pending}セル · ";
            status.Text = "";
            var hiddenWork = hidden.Count(cell => session.Workspace.Changed(cell) || session.Workspace.Buffer(cell) is not null
                || session.Workspace.Field(cell)?.Conflict == true || cell.Key is { Kind: "LocalSelect" } local
                && session.Workspace.LocalRows.Single(row => row.Id == local.NodeId).Selects.Any(s => s.FieldId == local.FieldId && s.Intent != "Unspecified"));
            if (hiddenWork > 0) status.Text += $" / 非表示列の作業 {hiddenWork}セル";
            if (hiddenRows > 0) status.Text += $" / 非表示行の作業 {hiddenRows}行";
            var conflicts = canonical.SelectMany(r => r.Cells).Count(c => session.Workspace.Field(c)?.Conflict == true);
            var unknown = canonical.SelectMany(r => r.Cells).Count(c => session.Workspace.Field(c)?.Observation?.Reason is not null);
            if (conflicts > 0) status.Text += $" / 競合 {conflicts}セル（詳細）";
            if (unknown > 0) status.Text += $" / 要確認 {unknown}セル（詳細）";
            if (canonical.Any(r => r.IsLocal)) status.Text += $" / ローカル新規 {canonical.Count(r => r.IsLocal)}行";
            if (session.Workspace.StructuralChanges.Count + session.Workspace.UndoWarnings.Count() > 0)
                status.Text += $" / 構成変更 {session.Workspace.StructuralChanges.Count} / 無効化したUndo {session.Workspace.UndoWarnings.Count()}（比較画面に詳細）";
            statusAfterSave = status.Text;
            RefreshStatus();
            }
            using (diagnostics?.Span("update-cell-presentation"))
            for (var r = 0; r < controls.Count; r++) for (var c = 0; c < controls[r].Length; c++) UpdateCell(r, c);
            using (diagnostics?.Span("update-selected-details")) UpdateSelectedDetails();
            UpdateSelection();
            presentedWorkspace = session.Workspace; presentedRevision = session.Workspace.PresentationRevision; presentedGeneration = generation;
        }
        finally { updating = false; }
    }
    private void UpdateCell(int r, int c)
    {
        if (controls[r][c] is RecycledCell presentation) { presentation.Refresh(); PaintCellState(r, c); return; }
        if (controls[r][c] is not (TitleCell or ChoiceCell)) return;
        var cell = rows[r].Cells[c];
        markers[r][c].Text = (HasDraftMarker(cell) ? rows[r].IsLocal ? "新規・GitHub未作成 " : "変更あり " : "")
            + (session.Workspace.Buffer(cell) is not null ? "編集中（未確定）" : cell.Reason ?? "");
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
        if (ApplyProblem(cell) is { } problem) markers[r][c].Text += " / " + problem.Description;
        var explanation = markers[r][c].Text;
        markers[r][c].Tag = explanation;
        markers[r][c].Text = ApplyProblem(cell)?.Kind == ApplyAttentionKind.Uncertain ? "?" : CellHasProblem(cell) ? "!"
            : HasDraftMarker(cell) ? "◆" : !cell.Editable && !TypedPlanning(cell) ? "▧" : "";
        markers[r][c].Visibility = markers[r][c].Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(markers[r][c], explanation);
        AutomationProperties.SetHelpText(controls[r][c], explanation);
        ToolTipService.SetToolTip(markers[r][c], explanation);
        PaintCellState(r, c);
        if (controls[r][c] is TitleCell { Composing: false } text) text.Refresh();
        if (controls[r][c] is ChoiceCell choice) choice.Refresh();
    }
    private void UpdateSelection()
    {
        var next = new HashSet<(int Row, int Column)>();
        if (active)
            for (var r = Math.Min(anchorRow, currentRow); r <= Math.Max(anchorRow, currentRow); r++)
                for (var c = Math.Min(anchorColumn, currentColumn); c <= Math.Max(anchorColumn, currentColumn); c++) next.Add((r, c));
        // A cell can remain selected while its former outer edge becomes an
        // interior edge. Repaint retained range cells as well as membership changes.
        var changed = paintedSelection.Union(next).ToHashSet();
        if (paintedCurrent is { } previous) changed.Add(previous);
        if (active) changed.Add((currentRow, currentColumn));
        foreach (var (r, c) in changed)
        {
            if (r >= cellBorders.Count || c >= cellBorders[r].Length) continue;
            PaintCellState(r, c);
        }
        paintedSelection.Clear(); paintedSelection.UnionWith(next);
        paintedCurrent = active ? (currentRow, currentColumn) : null;
        var count = Math.Abs(anchorRow - currentRow) + 1; var columns = Math.Abs(anchorColumn - currentColumn) + 1;
        selection.Text = drag is { Fill: true } operation
            ? $"{layout.Visible[operation.Column].Name}：{Math.Abs(operation.EndRow - operation.SourceRow) + 1}行へコピー予定 · 離して確定 / Escで取消"
            : active ? $"{layout.Visible[Math.Min(anchorColumn, currentColumn)].Name}{(columns == 1 ? "" : " ～ " + layout.Visible[Math.Max(anchorColumn, currentColumn)].Name)}：{count}行・{count * columns}セル"
            : "セルを選択 · Shiftで範囲選択 · F2で編集";
        AutomationProperties.SetHelpText(selection, active ? $"先頭 {rows[anchorRow].ItemId} / アクティブ {rows[currentRow].ItemId}" : "");
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
        UpdateActualInput();
        UpdateDateInput();
        UpdateApplyProblemText();
        if (!active || currentRow >= rows.Length) { selectedDetails.Text = "セルを選択すると、値・入力状態・Issueの識別情報を表示します。"; selectionMode.Text = "選択モード"; return; }
        var row = rows[currentRow]; var cell = row.Cells[currentColumn]; var field = session.Workspace.Field(cell);
        var pending = session.Workspace.Buffer(cell);
        selectionMode.Text = controls[currentRow][currentColumn] is TitleCell { Composing: true } ? "IME変換中" : pending is not null ? "編集中・未確定" : cell.Editable || TypedPlanning(cell) ? "選択モード" : "参照専用";
        string Display(string? value) => cell.Key?.Kind is "Select" or "LocalSelect" ? SelectDisplay(cell, value) : value ?? "（空値）";
        string ObservedDisplay(string? value) => value is null ? "（空値）" : cell.Key?.Kind is "Select" or "LocalSelect"
            ? cell.Options.SingleOrDefault(o => o.Id == value)?.Name ?? $"未確認 [{value}]" : value;
        var lines = new List<string> { $"行 {currentRow + 1} / {layout.Visible[currentColumn].Name}  —  {selectionMode.Text}",
            $"値: {(cell.Key is null ? cell.Display : Display(session.Workspace.Value(cell)))}" };
        if (pending is not null) lines.Add($"未確定文字: {pending}\nEnter / Tabでセル確定、Escで未確定文字を取り消します。IMEの確定とセル確定は別です。");
        if (markers[currentRow][currentColumn]?.Tag is string explanation && explanation.Length > 0) lines.Add(explanation);
        if (field is not null)
        {
            var observed = field.Observation;
            lines.Add($"B 基準: {ObservedDisplay(field.Baseline)}\nL ローカル: {Display(field.Change is { } local ? local.Value : field.Baseline)}\nR GitHub（取得済み）: {(observed is null ? ObservedDisplay(field.Baseline) : observed.Availability is ValueAvailability.Present or ValueAvailability.Empty ? ObservedDisplay(observed.Value) : EditingWorkspace.AvailabilityText(observed.Availability))}\n観測: {(observed?.At ?? field.RetrievedAt).LocalDateTime:g}");
        }
        else if (row.IsLocal) lines.Add($"B 基準: 未作成\nL ローカル: {Display(session.Workspace.Value(cell))}\nR GitHub: 未作成・未取得");
        lines.Add($"{RowIdentity(row)}\nProject: {projectId} / 項目: {row.ItemId} / 所有: {cell.Key?.Kind ?? "参照"} / ID: {cell.Key?.NodeId} / フィールド: {cell.Key?.FieldId}");
        selectedDetails.Text = string.Join("\n", lines) + PlanningSummary(row);
        selectedDetails.Text += "\n" + status.Text + $"\nプロフィール全体: GitHub未反映 {session.Workspace.DifferenceCount}セル\n" + viewNotice.Text;
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
            RevealColumn(r, c);
            list.ScrollIntoView(list.Items[r]);
            if (recycledPresentation) { list.UpdateLayout(); PositionOwnedEditors(); }
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
            if (e.Key == VirtualKey.C) { _ = CopyAsync(); e.Handled = true; }
            if (e.Key == VirtualKey.V) { _ = PasteAsync(); e.Handled = true; }
            if (e.Key == VirtualKey.Z) { Run(Undo); e.Handled = true; }
            if (e.Key == VirtualKey.D) { Run(FillDown); e.Handled = true; }
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
        try { action(); operationProblem = null; Update("run"); _ = FlushDraftsAsync("run"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { diagnostics?.Record("command-failure", new { type = ex.GetType().Name, ex.HResult }); ShowOperationProblem(ex is InvalidOperationException ? ex.Message : "クリップボードを利用できません。"); }
    }
    private void ShowOperationProblem(string message)
    {
        operationProblem = message; RefreshStatus(); UpdateSelectedDetails();
    }
    private void RefreshStatus()
    {
        if (ShowingGantt) gantt!.ShowOperationStatus(operationProblem, session.Status);
        if (ShowingSummary) summaryView!.ShowOperationStatus(operationProblem, session.Status);
        var saved = session.Status.Replace("（GitHub未反映）", "");
        // Keep a save failure visible even when a bulk command also has a rejection.
        status.Text = saved.Contains("失敗") ? saved + " / " + operationProblem
            : operationProblem is not null ? operationProblem + " / " + saved : statusBeforeSave + saved + statusAfterSave;
        ToolTipService.SetToolTip(status, status.Text);
    }
    private bool CellHasProblem(EditCell cell) => ApplyProblem(cell) is not null || (session.Workspace.Field(cell) is { } field
        ? field.Conflict || field.Observation?.Reason is not null
        : cell.Key is { Kind: "LocalTitle" } title ? session.Workspace.LocalProblems(registration, title.NodeId).Any(p => p.StartsWith("タイトル"))
        : cell.Key is { Kind: "LocalRepository" } repository ? session.Workspace.LocalProblems(registration, repository.NodeId).Any(p => p.StartsWith("宛先"))
        : cell.Key is { Kind: "LocalSelect" } && session.Workspace.Value(cell) is { } value && !cell.Options.Any(o => o.Id == value));
    private bool HasDraftMarker(EditCell cell) => session.Workspace.Changed(cell) || cell.Key?.Kind switch
    {
        "LocalTitle" or "LocalRepository" => !string.IsNullOrEmpty(session.Workspace.Value(cell)),
        "LocalSelect" => session.Workspace.LocalRows.Single(row => row.Id == cell.Key.NodeId).Selects
            .Any(value => value.FieldId == cell.Key.FieldId && value.Intent != "Unspecified"),
        _ => false
    };
    private async Task CopyAsync()
    {
        var request = generation;
        try
        {
            var (package, metadata) = CopyPackage();
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var writer = new Windows.Storage.Streams.DataWriter(stream);
            writer.WriteBytes(metadata); await writer.StoreAsync(); stream.Seek(0);
            if (!IsLoaded || generation != request) return;
            package.SetData(ClipboardFormat, stream);
            Clipboard.SetContent(package);
            // Clipboard listeners can briefly hold the OLE clipboard after SetContent.
            // Retry only persistence of our already-published package, never replay a
            // cell edit or replace the clipboard again with an older selection.
            for (var attempt = 0; ; attempt++)
            {
                try { Clipboard.Flush(); break; }
                catch (System.Runtime.InteropServices.COMException error) when (error.HResult == unchecked((int)0x800401D0) && attempt < 4)
                { await Task.Delay(10 * (attempt + 1)); }
            }
            if (IsLoaded && generation == request) { operationProblem = null; Update("copy"); }
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            diagnostics?.Record("copy-failure", new { type = error.GetType().Name, error.HResult });
            if (IsLoaded && generation == request) ShowOperationProblem(error is InvalidOperationException ? error.Message : "クリップボードを利用できません。コピーをやり直してください。");
        }
    }
    private (DataPackage Package, byte[] Metadata) CopyPackage()
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
        var package = new DataPackage(); package.SetText(string.Join("\r\n", lines));
        return (package, JsonSerializer.SerializeToUtf8Bytes(session.Workspace.CopyCells(projectId, rows, SelectedRange())));
    }
    private static async Task<SheetClipboard> ReadClipboardAsync()
    {
        var view = Clipboard.GetContent();
        var text = await view.GetTextAsync();
        CopiedCells? cells = null;
        if (view.Contains(ClipboardFormat))
        {
            using var stream = (Windows.Storage.Streams.IRandomAccessStream)await view.GetDataAsync(ClipboardFormat);
            using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            var size = checked((uint)stream.Size); await reader.LoadAsync(size);
            var metadata = new byte[size]; reader.ReadBytes(metadata);
            cells = JsonSerializer.Deserialize<CopiedCells>(metadata) ?? throw new InvalidOperationException("内部コピーを確認できません。");
        }
        return new(text, cells);
    }
    private CellRange SelectedRange() => new(Math.Min(anchorRow, currentRow), Math.Min(anchorColumn, currentColumn),
        Math.Abs(anchorRow - currentRow) + 1, Math.Abs(anchorColumn - currentColumn) + 1);
    private void FillDown()
    {
        if (!active) throw new InvalidOperationException("同じ列の範囲を選択してください。");
        var range = SelectedRange();
        if (range.ColumnCount != 1 || range.RowCount < 2) throw new InvalidOperationException("元セルを先頭に含む同じ列の範囲を選択してください。");
        if (!CanRefresh) throw new InvalidOperationException("IME変換中です。確定または取消してから実行してください。");
        session.Workspace.Fill(projectId, rows, range.Row, range.Column, range.Row, range.Row + range.RowCount - 1);
    }
    private async Task PasteAsync()
    {
        // Capture targets before awaiting the clipboard; navigation cannot redirect this operation.
        var selected = active;
        var range = SelectedRange(); var revision = session.Workspace.Revision;
        var requestGeneration = generation;
        var destinations = rows.Select(row => row with { Cells = row.Cells.ToArray() }).ToArray();
        try
        {
            var clipboard = await readClipboard();
            if (generation != requestGeneration || revision != session.Workspace.Revision || !IsLoaded) { ShowOperationProblem("表示対象・入力が変わったため、遅れて届いた貼り付けを中止しました。"); return; }
            Run(() => { if (!selected) throw new InvalidOperationException("セルを選択してください。");
                if (!CanRefresh) throw new InvalidOperationException("IME変換中です。確定または取消してから貼り付けてください。");
                if (range.Single && TypedPlanning(destinations[range.Row].Cells[range.Column]))
                {
                    var values = EditingWorkspace.ParseTsv(clipboard.Text);
                    if (values.Length != 1 || values[0].Length != 1) throw new InvalidOperationException("実績・日時は対象を確認して1セルずつ入力してください。");
                    session.Workspace.SetPlanningBuffer(destinations[range.Row].Cells[range.Column], values[0][0]);
                    Select(range.Row, range.Column, false); UpdateCell(range.Row, range.Column);
                }
                else session.Workspace.PasteSelection(projectId, destinations, range, clipboard.Text, clipboard.Cells); });
        }
        catch (Exception error) { diagnostics?.Record("paste-read-failure", new { type = error.GetType().Name, error.HResult }); ShowOperationProblem("クリップボードを読み取れません。"); }
    }
    private async Task AppendAsync()
    {
        var request = generation; var revision = session.Workspace.Revision;
        var inputMapping = layout.Visible.Where(c => c.Id.Role != "Reference").ToArray();
        var destination = registration;
        try
        {
            var text = (await readClipboard()).Text;
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
    private sealed class ChoiceCell : Button
    {
        private readonly EditingGrid owner;
        private int row, column;
        private EditCell cell;
        private readonly TextBlock value = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button arrow;
        private MenuFlyout? choices;
        private int choicesGeneration = -1;
        private bool hovered, selected;
        public ChoiceCell(EditingGrid owner, int initialRow, int initialColumn, EditCell initialCell)
        {
            this.owner = owner; row = initialRow; column = initialColumn; cell = initialCell;
            HorizontalAlignment = HorizontalAlignment.Stretch; HorizontalContentAlignment = HorizontalAlignment.Stretch;
            MinHeight = 26; Padding = new(8, 2, 8, 2); BorderThickness = new(0); CornerRadius = new(0);
            // The sheet supplies the active-cell frame, including high contrast.
            // Avoid a second animated system focus rectangle over the range frame.
            Style = (Style)Application.Current.Resources["SheetChoiceCellStyle"];
            var content = new Grid(); content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            AutomationProperties.SetAutomationId(value, $"GridCell{row}_{column}Value"); content.Children.Add(value);
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            arrow = new Button { Content = new FontIcon { Glyph = "\uE70D", FontSize = 10 }, Width = 22, Height = 24,
                MinWidth = 0, MinHeight = 0, Padding = new(0), BorderThickness = new(0), IsTabStop = false,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Opacity = 0 };
            AutomationProperties.SetAutomationId(arrow, $"GridChoiceArrow{row}_{column}");
            AutomationProperties.SetName(arrow, "選択肢を開く (F2 / F4 / Space)");
            arrow.Click += (_, _) => OpenChoices();
            SetColumn(arrow, 1); content.Children.Add(arrow); Content = content;
            GotFocus += (_, _) => { if (owner.CurrentEditor(row, column, this)) owner.FocusedCell(row, column); };
            Click += (_, _) => { if (owner.drag is null && owner.CurrentEditor(row, column, this)) owner.Select(row, column, Down(VirtualKey.Shift)); };
            PointerEntered += (_, _) => { hovered = true; ShowArrow(selected); };
            PointerExited += (_, _) => { hovered = false; ShowArrow(selected); };
            PreviewKeyDown += (_, e) =>
            {
                if (!owner.CurrentEditor(row, column, this)) return;
                if (e.Key is VirtualKey.F2 or VirtualKey.F4 or VirtualKey.Space) { OpenChoices(); e.Handled = true; }
                else owner.NavigateKey(row, column, e);
            };
        }
        public void Reindex(int nextRow, int nextColumn, EditCell nextCell)
        {
            if (nextCell.Key != cell.Key) throw new InvalidOperationException("A native editor cannot change field identity.");
            choices?.Hide(); choices = null;
            row = nextRow; column = nextColumn; cell = nextCell;
            AutomationProperties.SetAutomationId(value, $"GridCell{row}_{column}Value");
            AutomationProperties.SetAutomationId(arrow, $"GridChoiceArrow{row}_{column}");
        }
        public void ShowArrow(bool isSelected) { selected = isSelected; arrow.Opacity = selected || hovered || FocusState != FocusState.Unfocused ? 1 : 0; }
        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (!owner.CurrentEditor(row, column, this)) return;
            for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, this); element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, arrow)) return;
            owner.BeginRange(row, column, e);
        }
        protected override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (owner.dragCapture == this) owner.DragReleased(this, e);
            else base.OnPointerReleased(e);
        }
        public void Refresh()
        {
            var field = owner.session.Workspace.Field(cell);
            arrow.IsEnabled = field?.Conflict != true && field?.Observation?.Reason is null;
            value.Text = owner.SelectDisplay(cell, owner.session.Workspace.Value(cell));
            AutomationProperties.SetName(this, $"行 {row + 1} 列 {column + 1} {owner.layout.Visible[column].Name} {value.Text}");
        }
        private void OpenChoices()
        {
            if (!arrow.IsEnabled || !owner.CurrentEditor(row, column, this)) return;
            if (owner.session.Workspace.Buffer(cell) is not null) { owner.status.Text = "未確定入力を確定または取消してから選択肢を開いてください。"; return; }
            owner.Select(row, column, Down(VirtualKey.Shift), false);
            if (Down(VirtualKey.Shift)) return;
            // Definitions are fixed for this retained editor's generation. Reuse the
            // native presenter on repeated opens; refresh the current-value checkmark.
            if (choices is null || choicesGeneration != owner.generation)
            {
                var request = owner.generation;
                choicesGeneration = request;
                choices = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
                foreach (var option in cell.Options)
                {
                    var label = option.Name + (cell.Options.Count(o => o.Name == option.Name) > 1 ? $" [{option.Id}]" : "");
                    var item = new MenuFlyoutItem { Text = label, Tag = option.Id };
                    AutomationProperties.SetAutomationId(item, "ChoiceOption-" + option.Id);
                    AutomationProperties.SetHelpText(item, option.Name);
                    item.Click += (_, _) =>
                    {
                        if (request == owner.generation && owner.CurrentEditor(row, column, this)) owner.Run(() => owner.session.Workspace.Commit(owner.projectId, cell, option.Id, true));
                    };
                    choices.Items.Add(item);
                }
            }
            foreach (var item in choices.Items.OfType<MenuFlyoutItem>())
                item.Icon = (string)item.Tag == owner.session.Workspace.Value(cell) ? item.Icon ?? new SymbolIcon(Symbol.Accept) : null;
            choices.ShowAt(this);
        }
    }
    private sealed class TitleCell : TextBox
    {
        private readonly EditingGrid owner; private int row, column; private EditCell cell;
        private bool restoring; private bool composing;
        public bool Composing => composing;
        public bool Editing { get; private set; }
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // Keep the TextBox's native view, input and caret scrolling. Only
            // the hidden inner scrollbar chrome is unnecessary in sheet cells.
            if (GetTemplateChild("ContentElement") is ScrollViewer scroll)
                scroll.Template = (ControlTemplate)Application.Current.Resources["SheetTextScrollTemplate"];
        }
        public TitleCell(EditingGrid owner, int initialRow, int initialColumn, EditCell initialCell)
        {
            this.owner = owner; row = initialRow; column = initialColumn; cell = initialCell;
            MinHeight = 26; Padding = new(8, 2, 8, 2); BorderThickness = new(0); CornerRadius = new(0);
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            IsReadOnly = !cell.Editable && !owner.TypedPlanning(cell); Refresh();
            if (owner.diagnostics is not null)
                TextChanged += (_, _) => owner.diagnostics.Record("text-changed", new { row, column, length = Text.Length, restoring });
            GotFocus += (_, _) => { if (owner.CurrentEditor(row, column, this)) owner.FocusedCell(row, column); };
            TextCompositionStarted += (_, _) => { composing = true; Editing = true; owner.applyProblemTip.IsOpen = false; owner.selectionMode.Text = "IME変換中"; };
            TextCompositionEnded += (_, _) =>
            {
                composing = false;
                if (owner.deferredRefresh) owner.Update("composition-ended");
                else owner.UpdateSelectedDetails();
            };
            TextChanging += (_, _) =>
            {
                if (restoring || !cell.Editable && !owner.TypedPlanning(cell) || !owner.CurrentEditor(row, column, this)) return;
                using var measured = owner.diagnostics?.Span("text-changing");
                owner.diagnostics?.Record("text-changing", new { row, column, key = cell.Key, length = Text.Length, composing });
                Editing = true;
                var hadBuffer = owner.session.Workspace.Buffer(cell) is not null;
                owner.SetCellBuffer(cell, Text);
                // Native typing already displays this buffer. Refresh only peers
                // sharing its identity; aggregate presentation changes on entry/exit.
                if (!hadBuffer) owner.Update("pending-state");
                else
                {
                    for (var r = 0; r < owner.controls.Count; r++)
                        for (var c = 0; c < owner.controls[r].Length; c++)
                            if (owner.rows[r].Cells[c].Key == cell.Key && !ReferenceEquals(owner.controls[r][c], this)) owner.UpdateCell(r, c);
                    owner.UpdateSelectedDetails();
                }
                _ = owner.FlushDraftsAsync("text-changing");
            };
        }
        public void Reindex(int nextRow, int nextColumn, EditCell nextCell)
        {
            if (nextCell.Key != cell.Key) throw new InvalidOperationException("A native editor cannot change field identity.");
            row = nextRow; column = nextColumn; cell = nextCell;
        }
        public void Refresh()
        {
            var field = owner.session.Workspace.Field(cell);
            IsReadOnly = !cell.Editable && !owner.TypedPlanning(cell) || field?.Conflict == true || field?.Observation?.Reason is { } reason && !reason.StartsWith("未確定文字");
            var buffer = owner.session.Workspace.Buffer(cell);
            var committed = owner.session.Workspace.Value(cell);
            var value = buffer ?? (cell.Key is null ? cell.Display : cell.Key.Kind is "Select" or "LocalSelect"
                ? cell.Options.SingleOrDefault(o => o.Id == committed)?.Name ?? (committed is not null ? "保存された選択肢IDを確認できません（要照合）" : EditingWorkspace.AvailabilityText(cell.Availability))
                : committed ?? cell.Reason ?? (cell.Availability == ValueAvailability.Empty ? "" : "閲覧不可"));
            if (Text != value) { restoring = true; Text = value; restoring = false; }
            Editing = buffer is not null;
        }
        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (!owner.CurrentEditor(row, column, this)) return;
            owner.diagnostics?.Record("cell-pointer-pressed", new { row, column, pointerTimestampMicroseconds = e.GetCurrentPoint(this).Timestamp, editing = Editing });
            if (!Editing) { owner.BeginRange(row, column, e); return; }
            owner.Select(row, column, false, false); base.OnPointerPressed(e);
        }
        protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
        {
            if (!owner.CurrentEditor(row, column, this)) return;
            owner.diagnostics?.Record("editor-preview-key", new { row, column, Editing, composing });
            if (composing) { base.OnPreviewKeyDown(e); return; }
            if (!Editing && e.Key == VirtualKey.F2 && (cell.Editable || owner.TypedPlanning(cell))) { Editing = true; owner.SetCellBuffer(cell, owner.TypedDate(cell) ? owner.ExactDateText(cell, row) : Text); Refresh(); SelectAll(); owner.Update("pending-state"); _ = owner.FlushDraftsAsync("edit-start"); e.Handled = true; }
            else if (Editing && e.Key == VirtualKey.Escape)
            { owner.SetCellBuffer(cell, null); Refresh(); SelectAll(); owner.Update("pending-state"); _ = owner.FlushDraftsAsync("edit-cancel"); e.Handled = true; }
            else if (Editing && e.Key is VirtualKey.Enter or VirtualKey.Tab)
            {
                if (owner.TypedActual(cell)) { owner.CommitActualCell(confirmContext: false); e.Handled = true; return; }
                if (owner.TypedDate(cell)) { if (owner.CommitDateCell()) owner.NavigateKey(row, column, e); e.Handled = true; return; }
                try { owner.session.Workspace.Commit(owner.projectId, cell, Text); Editing = false; owner.NavigateKey(row, column, e); _ = owner.FlushDraftsAsync("cell-commit"); }
                catch (InvalidOperationException ex) { owner.status.Text = ex.Message; e.Handled = true; }
            }
            else if (!Editing) owner.NavigateKey(row, column, e);
            // Native text Undo stays inside the editing/composition path.
            base.OnPreviewKeyDown(e);
        }
    }
}

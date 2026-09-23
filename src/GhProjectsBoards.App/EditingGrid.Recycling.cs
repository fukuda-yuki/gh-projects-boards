using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    // Native input ownership:
    // - ListView owns generated/recycled row containers and presentation. A binding
    //   carries the Project/item/field identity and a generation; presentation owns
    //   no edits. Rebinding clears values, state, handlers and automation metadata.
    // - The sheet viewport owns an independent, clipped native-editor layer. Each
    //   editor and its original host belong to one identity from creation through
    //   release. Scrolling only changes their anchor, never their parent/DataContext.
    //   Active, pending, composing and captured input stay attached, even offscreen.
    // - EditingWorkspace/DraftSession remain the authority for durable input. An
    //   unseen buffer does not instantiate an editor. Clean inactive editors can
    //   be released; visitation is not a retention reason. Hiding the sheet is not
    //   disposal. Native readiness is acquired on selection, before direct input.
    // GHPB_RECYCLED_PRESENTATION=0 retains the earlier path for explicit comparisons.
    private readonly bool recycledPresentation;
    private readonly Canvas editorLayer = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Dictionary<int, RecycledRow> recycledRows = [];
    private readonly Dictionary<(string Item, FieldKey? Field, ColumnIdentity Column), OwnedEditor> ownedEditors = [];
    private long bindingGeneration;
    private double RowPitch => rowLines.FirstOrDefault(line => line is { IsLoaded: true, ActualHeight: > 0 })?.ActualHeight ?? 30;

    private sealed record RowItem(string Project, string Item, int Index, string Name)
    {
        public override string ToString() => Name;
    }
    private sealed record CellBinding(string Project, string Item, FieldKey? Key, ColumnIdentity Column, int Row, int Index, long Generation);
    private sealed class OwnedEditor(CellBinding identity, FrameworkElement editor, Border host, TextBlock marker, EditCell cell, bool isLocal)
    {
        public CellBinding Identity { get; } = identity;
        public FrameworkElement Editor { get; } = editor;
        public Border Host { get; } = host;
        public TextBlock Marker { get; } = marker;
        public EditCell Cell { get; set; } = cell;
        public bool IsLocal { get; } = isLocal;
        public int Row { get; set; } = identity.Row;
        public int Column { get; set; } = identity.Index;
    }
    private sealed class RecycledRow(Grid line, TextBlock number, RecycledCell[] cells, Border[] borders, TextBlock[] stateMarkers, TextBlock identity)
    {
        public Grid Line { get; } = line;
        public TextBlock Number { get; } = number;
        public RecycledCell[] Cells { get; } = cells;
        public Border[] Borders { get; } = borders;
        public TextBlock[] StateMarkers { get; } = stateMarkers;
        public TextBlock Identity { get; } = identity;
        public RowItem? Item { get; set; }
    }

    private void InitializeRecycling()
    {
        list.ItemTemplate = (DataTemplate)Application.Current.Resources["RecycledSheetRowTemplate"];
        list.ItemContainerStyle = (Style)Application.Current.Resources["RecycledSheetContainerStyle"];
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is not Grid line) return;
            if (line.Tag is RecycledRow old) UnbindRecycledRow(old);
            if (line.Tag is not RecycledRow presentation || presentation.Cells.Length != layout.Visible.Length)
            {
                line.Children.Clear(); line.ColumnDefinitions.Clear();
                line.Tag = presentation = CreateRecycledRow(line);
            }
            if (!args.InRecycleQueue && args.Item is RowItem item) BindRecycledRow(presentation, item);
            args.Handled = true;
        };
        editorLayer.SizeChanged += (_, _) => PositionOwnedEditors();
        AutomationProperties.SetAutomationId(editorLayer, "SheetNativeEditors");
    }

    private void BuildRecycledRows()
    {
        for (var r = 0; r < rows.Length; r++)
        {
            rowLines.Add(null!); controls.Add([]); markers.Add([]); cellBorders.Add([]); selectionFrames.Add([]); fillHandles.Add([]);
        }
        ReindexOwnedEditors();
        list.ItemsSource = rows.Select((row, r) => new RowItem(projectId, row.ItemId, r,
            $"行 {r + 1} {RowIdentity(row)} {session.Workspace.Value(row.Cells[0]) ?? row.Cells[0].Display}")).ToArray();
    }

    private RecycledRow CreateRecycledRow(Grid line)
    {
        line.ColumnDefinitions.Add(new() { Width = new(44) });
        var number = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
        var gutter = new Grid { Style = (Style)Application.Current.Resources["SheetHeaderStyle"] };
        gutter.Children.Add(number); line.Children.Add(gutter);
        var cells = new RecycledCell[layout.Visible.Length]; var borders = new Border[cells.Length];
        var stateMarkers = new TextBlock[cells.Length];
        var identity = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(4, 0, 8, 0), FontSize = 11 };
        for (var c = 0; c < cells.Length; c++)
        {
            line.ColumnDefinitions.Add(new() { Width = new(ColumnWidth(c)) });
            var content = new Grid();
            cells[c] = new RecycledCell(this); content.Children.Add(cells[c]);
            if (c == 0)
            {
                content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                SetColumn(identity, 1); content.Children.Add(identity);
            }
            var marker = stateMarkers[c] = new TextBlock { FontSize = 10, Width = 10, Height = 12,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new(0, 0, 2, 0), Visibility = Visibility.Collapsed };
            SetColumnSpan(marker, c == 0 ? 2 : 1); content.Children.Add(marker);
            borders[c] = new Border { BorderThickness = new(1), MinHeight = 30, Child = content, Style = cellStyle };
            SetColumn(borders[c], c + 1); line.Children.Add(borders[c]);
        }
        return new(line, number, cells, borders, stateMarkers, identity);
    }

    private void AllocateRowSlots(int r)
    {
        if (controls[r].Length != 0) return;
        var count = rows[r].Cells.Length;
        controls[r] = new FrameworkElement[count]; markers[r] = new TextBlock[count]; cellBorders[r] = new Border[count];
        selectionFrames[r] = new Border?[count]; fillHandles[r] = new Button?[count];
    }

    private void BindRecycledRow(RecycledRow presentation, RowItem item)
    {
        using var measured = diagnostics?.Span("rebind-row");
        var r = item.Index;
        if (r >= rows.Length || rows[r].ItemId != item.Item || item.Project != projectId) return;
        presentation.Item = item; recycledRows[r] = presentation; rowLines[r] = presentation.Line;
        AllocateRowSlots(r);
        presentation.Number.Text = (r + 1).ToString(); AutomationProperties.SetAutomationId(presentation.Number, $"GridRowNumber{r}");
        presentation.Identity.Text = RowIdentity(rows[r], compact: true);
        presentation.Identity.MaxWidth = Math.Min(132, ColumnWidth(0) * .4);
        AutomationProperties.SetAutomationId(presentation.Identity, $"GridRowIdentity{r}");
        ToolTipService.SetToolTip(presentation.Identity, RowIdentity(rows[r]));
        for (var c = 0; c < presentation.Cells.Length; c++)
        {
            presentation.Line.ColumnDefinitions[c + 1].Width = new(ColumnWidth(c));
            var binding = new CellBinding(projectId, item.Item, rows[r].Cells[c].Key, layout.Visible[c].Id, r, c, ++bindingGeneration);
            presentation.Cells[c].Bind(binding);
            if (ownedEditors.TryGetValue((item.Item, binding.Key, binding.Column), out var owned)) InstallOwnedSlots(owned);
            else { controls[r][c] = presentation.Cells[c]; cellBorders[r][c] = presentation.Borders[c]; }
        }
        presentation.Line.Width = 44 + Enumerable.Range(0, layout.Visible.Length).Sum(ColumnWidth);
        FreezeIdentity(presentation.Line, listScroll?.HorizontalOffset ?? 0);
        RefreshRecycledRow(r);
        diagnostics?.Record("row-bound", new { row = r, item = item.Item, bindingGeneration, presenters = recycledRows.Count, editors = ownedEditors.Count });
    }

    private void UnbindRecycledRow(RecycledRow presentation)
    {
        if (presentation.Item is not { } item) return;
        var r = item.Index;
        if (recycledRows.GetValueOrDefault(r) == presentation)
        {
            recycledRows.Remove(r); rowLines[r] = null!;
            for (var c = 0; c < presentation.Cells.Length; c++)
            {
                if (controls[r][c] is RecycledCell)
                { controls[r][c] = null!; cellBorders[r][c] = null!; markers[r][c] = null!; selectionFrames[r][c] = null; fillHandles[r][c] = null; }
            }
        }
        foreach (var cell in presentation.Cells) cell.Unbind();
        foreach (var marker in presentation.StateMarkers)
        {
            marker.Text = ""; marker.Tag = null; marker.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(marker, ""); AutomationProperties.SetName(marker, "");
            ToolTipService.SetToolTip(marker, null);
        }
        foreach (var border in presentation.Borders)
        {
            border.Style = cellStyle; border.Visibility = Visibility.Visible;
            var content = (Grid)border.Child;
            foreach (var adornment in content.Children.Where(child => child is Border or FillHandle).ToArray()) content.Children.Remove(adornment);
        }
        presentation.Number.Text = ""; presentation.Identity.Text = "";
        AutomationProperties.SetAutomationId(presentation.Number, ""); AutomationProperties.SetAutomationId(presentation.Identity, "");
        ToolTipService.SetToolTip(presentation.Identity, null);
        presentation.Item = null;
    }

    private void EnsureRecycledRow(int r)
    {
        AllocateRowSlots(r);
        if (recycledRows.ContainsKey(r)) return;
        list.ScrollIntoView(list.Items[r]); list.UpdateLayout();
    }

    private void RefreshRecycledRow(int r)
    {
        if (!recycledRows.TryGetValue(r, out var presentation)) return;
        for (var c = 0; c < presentation.Cells.Length; c++)
        {
            var cell = presentation.Cells[c];
            var owned = controls[r][c] is TitleCell or ChoiceCell;
            cell.Visibility = owned ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetAutomationId(cell, owned ? "" : $"GridCell{r}_{c}");
            AutomationProperties.SetAccessibilityView(cell, owned ? AccessibilityView.Raw : AccessibilityView.Content);
            var marker = presentation.StateMarkers[c];
            AutomationProperties.SetAutomationId(marker, owned ? "" : $"GridMarker{r}_{c}");
            AutomationProperties.SetAccessibilityView(marker, owned ? AccessibilityView.Raw : AccessibilityView.Content);
            if (owned) marker.Visibility = Visibility.Collapsed;
            else { markers[r][c] = marker; UpdateCell(r, c); }
            presentation.Borders[c].Visibility = ColumnInViewport(c) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EnsureOwnedEditor(int r, int c)
    {
        AllocateRowSlots(r);
        var key = (rows[r].ItemId, rows[r].Cells[c].Key, layout.Visible[c].Id);
        if (ownedEditors.ContainsKey(key)) return;
        var identity = new CellBinding(projectId, key.ItemId, key.Key, key.Id, r, c, ++bindingGeneration);
        var editor = CreateCellEditor(r, c);
        var content = new Grid(); content.Children.Add(editor);
        if (c == 0)
        {
            content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = new TextBlock { Text = RowIdentity(rows[r], compact: true), MaxWidth = Math.Min(132, ColumnWidth(0) * .4),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 8, 0), FontSize = 11 };
            SetColumn(label, 1); content.Children.Add(label); ToolTipService.SetToolTip(label, RowIdentity(rows[r]));
        }
        var marker = new TextBlock { FontSize = 10, Width = 10, Height = 12, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 0, 2, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(marker, $"GridMarker{r}_{c}"); SetColumnSpan(marker, c == 0 ? 2 : 1); content.Children.Add(marker);
        var host = new Border { Child = content, BorderThickness = new(1), Height = RowPitch, Style = cellStyle };
        // Set an explicit identity once. Neither presenter binding nor inheritance
        // can ever replace the native editor's data context.
        host.DataContext = identity;
        var owned = new OwnedEditor(identity, editor, host, marker, rows[r].Cells[c], rows[r].IsLocal); ownedEditors.Add(key, owned);
        InstallOwnedSlots(owned); editorLayer.Children.Add(host);
        RefreshRecycledRow(r); UpdateCell(r, c); PositionOwnedEditors();
    }

    private void InstallOwnedSlots(OwnedEditor owned)
    {
        var r = owned.Row; var c = owned.Column;
        AllocateRowSlots(r);
        controls[r][c] = owned.Editor; cellBorders[r][c] = owned.Host; markers[r][c] = owned.Marker;
    }

    private void ReindexOwnedEditors()
    {
        var rowIndices = rows.Select((row, index) => (row.ItemId, index)).ToDictionary(pair => pair.ItemId, pair => pair.index);
        foreach (var owned in ownedEditors.Values)
        {
            var c = Array.FindIndex(layout.Visible, column => column.Id == owned.Identity.Column);
            if (!rowIndices.TryGetValue(owned.Identity.Item, out var r) || c < 0 || rows[r].Cells[c].Key != owned.Identity.Key) continue;
            owned.Row = r; owned.Column = c; owned.Cell = rows[r].Cells[c];
            if (owned.Editor is TitleCell title) title.Reindex(r, c, owned.Cell);
            else if (owned.Editor is ChoiceCell choice) choice.Reindex(r, c, owned.Cell);
            AutomationProperties.SetAutomationId(owned.Editor, $"GridCell{r}_{c}");
            AutomationProperties.SetAutomationId(owned.Marker, $"GridMarker{r}_{c}");
            AutomationProperties.SetAccessibilityView(owned.Editor, AccessibilityView.Content);
            owned.Editor.IsHitTestVisible = true;
            ((Control)owned.Editor).IsEnabled = true;
            InstallOwnedSlots(owned);
        }
    }

    private void PositionOwnedEditors()
    {
        if (listScroll is not { ViewportWidth: > 0 } || !editorLayer.IsLoaded) return;
        var origin = listScroll.TransformToVisual(editorLayer).TransformPoint(new(0, 0));
        var viewport = new Rect(origin.X, origin.Y, listScroll.ViewportWidth, listScroll.ViewportHeight);
        editorLayer.Clip = new RectangleGeometry { Rect = viewport };
        foreach (var owned in ownedEditors.Values)
        {
            var r = owned.Row; var c = owned.Column;
            if (r < 0 || c < 0)
            {
                Canvas.SetTop(owned.Host, -10000);
                owned.Host.Clip = new RectangleGeometry { Rect = new(0, 0, 0, 0) };
                continue;
            }
            var x = origin.X + 44 + Enumerable.Range(0, c).Sum(ColumnWidth) - (c == 0 ? 0 : listScroll.HorizontalOffset);
            var y = origin.Y + r * RowPitch - listScroll.VerticalOffset;
            // A reused container can still report its previous arranged location
            // during ViewChanged. Fixed-height logical slots avoid that stale anchor.
            Canvas.SetLeft(owned.Host, x); Canvas.SetTop(owned.Host, y);
            Canvas.SetZIndex(owned.Host, c == 0 ? 2 : 1);
            owned.Host.Width = ColumnWidth(c); owned.Host.Height = RowPitch;
            var clippedLeft = c == 0 ? 0 : Math.Clamp(origin.X + 44 + ColumnWidth(0) - x, 0, ColumnWidth(c));
            owned.Host.Clip = new RectangleGeometry { Rect = new(clippedLeft, 0, Math.Max(0, ColumnWidth(c) - clippedLeft), RowPitch) };
        }
    }

    private void ReleaseCleanEditors()
    {
        foreach (var (key, owned) in ownedEditors.ToArray())
        {
            var r = owned.Row; var c = owned.Column;
            // A removed local row has no buffer to query. A filtered-out row still
            // owns its input, including when its presentation position is -1.
            var removedLocal = owned.IsLocal && !session.Workspace.LocalRows.Any(row =>
                row.Id == owned.Identity.Item && row.ProjectId == owned.Identity.Project);
            if (!removedLocal && (active && currentRow == r && currentColumn == c || drag is { } gesture && gesture.SourceRow == r
                || owned.Editor is TitleCell { Editing: true } or TitleCell { Composing: true }
                || session.Workspace.Buffer(owned.Cell) is not null
                || XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), owned.Editor))) continue;
            if (removedLocal)
            {
                if (owned.Editor is TitleCell title) title.Reindex(-1, -1, owned.Cell);
                else if (owned.Editor is ChoiceCell choice) choice.Reindex(-1, -1, owned.Cell);
            }
            ownedEditors.Remove(key); editorLayer.Children.Remove(owned.Host);
            if (r < 0 || c < 0) continue;
            controls[r][c] = null!; cellBorders[r][c] = null!; markers[r][c] = null!; selectionFrames[r][c] = null; fillHandles[r][c] = null;
            if (recycledRows.TryGetValue(r, out var presentation))
            {
                controls[r][c] = presentation.Cells[c]; cellBorders[r][c] = presentation.Borders[c];
                if (!removedLocal) RefreshRecycledRow(r);
            }
        }
    }

    private void ResetRecycling()
    {
        // Retire presentation peers before changing the projection. Native editor
        // ownership is the stable item/field key, not its previous array position.
        foreach (var presentation in recycledRows.Values.ToArray()) UnbindRecycledRow(presentation);
        foreach (var owned in ownedEditors.Values)
        {
            owned.Row = owned.Column = -1;
            if (owned.Editor is TitleCell title) title.Reindex(-1, -1, owned.Cell);
            else if (owned.Editor is ChoiceCell choice) choice.Reindex(-1, -1, owned.Cell);
            AutomationProperties.SetAutomationId(owned.Editor, "");
            AutomationProperties.SetAccessibilityView(owned.Editor, AccessibilityView.Raw);
            owned.Editor.IsHitTestVisible = false;
            ((Control)owned.Editor).IsEnabled = false;
            // Range handles capture a projection position. Recreate only those
            // adornments; the native editor, parent and pending caret stay owned.
            var content = (Grid)owned.Host.Child;
            foreach (var child in content.Children.Where(child => child is Border or FillHandle).ToArray()) content.Children.Remove(child);
        }
        list.ItemsSource = null;
        recycledRows.Clear();
    }

    private string PresentationValue(EditCell cell)
    {
        var value = session.Workspace.Buffer(cell) ?? session.Workspace.Value(cell);
        return cell.Key?.Kind is "Select" or "LocalSelect" ? SelectDisplay(cell, value)
            : value ?? (cell.Key is null ? cell.Display : cell.Reason ?? (cell.Availability == ValueAvailability.Empty ? "" : "閲覧不可"));
    }

    private sealed class RecycledCell : Button
    {
        private readonly EditingGrid owner;
        private readonly TextBlock text = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private CellBinding? binding;
        public RecycledCell(EditingGrid owner)
        {
            this.owner = owner; Content = text;
            Style = (Style)Application.Current.Resources["SheetChoiceCellStyle"];
            Padding = new(8, 2, 8, 2); BorderThickness = new(0); MinHeight = 26;
            HorizontalContentAlignment = HorizontalAlignment.Stretch; Margin = new(0, 0, 12, 0);
            HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
            GettingFocus += (_, args) =>
            {
                // ListView's public scroll provider acquires collection focus.
                // That incidental focus must not promote its first visible cell
                // and end composition in the independently owned editor. Explicit
                // cell UIA, pointer and keyboard activation go through Select.
                if (!owner.CanRefresh || owner.ownedEditors.Values.Any(owned => ReferenceEquals(args.OldFocusedElement, owned.Editor)))
                { args.Cancel = true; return; }
                if (binding is not { } target) { args.Cancel = true; return; }
                owner.Select(target.Row, target.Index, false, false);
                owner.EnsureOwnedEditor(target.Row, target.Index); owner.PositionOwnedEditors();
                args.TrySetNewFocusedElement(owner.controls[target.Row][target.Index]);
            };
            Click += (_, _) => { if (binding is { } target) Activate(target); };
        }
        public void Bind(CellBinding target) { binding = target; Refresh(); }
        public void Unbind()
        {
            binding = null; text.Text = ""; ClearValue(DataContextProperty);
            AutomationProperties.SetAutomationId(this, ""); AutomationProperties.SetName(this, ""); AutomationProperties.SetHelpText(this, "");
            ToolTipService.SetToolTip(this, null); Visibility = Visibility.Visible;
        }
        public void Refresh()
        {
            if (binding is not { } target) return;
            var cell = owner.rows[target.Row].Cells[target.Index];
            text.Text = owner.PresentationValue(cell);
            AutomationProperties.SetName(this, $"行 {target.Row + 1} 列 {target.Index + 1} {owner.layout.Visible[target.Index].Name} {text.Text} {cell.Reason}");
            AutomationProperties.SetHelpText(this, cell.Reason ?? "");
        }
        private void Validate(CellBinding target)
        {
            if (binding != target || !owner.IsLoaded || owner.rows[target.Row].ItemId != target.Item
                || owner.rows[target.Row].Cells[target.Index].Key != target.Key || owner.layout.Visible[target.Index].Id != target.Column)
                throw new ElementNotAvailableException();
        }
        private void Activate(CellBinding target)
        {
            Validate(target);
            if (!owner.CanRefresh) throw new InvalidOperationException("IME変換を確定または取消してからセルを選択してください。");
            owner.Select(target.Row, target.Index, false);
        }
        protected override void OnPointerPressed(PointerRoutedEventArgs args)
        {
            if (binding is { } target)
            {
                Validate(target);
                owner.diagnostics?.Record("cell-pointer-pressed", new { row = target.Row, column = target.Index, pointerTimestampMicroseconds = args.GetCurrentPoint(this).Timestamp, editing = false });
                owner.BeginRange(target.Row, target.Index, args);
            }
        }
        protected override AutomationPeer OnCreateAutomationPeer() => new CellPeer(this);
        private sealed class CellPeer(RecycledCell cell) : FrameworkElementAutomationPeer(cell)
        {
            protected override string GetClassNameCore() => "SheetCell";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
            protected override object GetPatternCore(PatternInterface patternInterface) => cell.binding is { } target
                && patternInterface is PatternInterface.Invoke or PatternInterface.Value or PatternInterface.ScrollItem
                ? new BoundProvider(cell, target) : base.GetPatternCore(patternInterface);
            protected override void SetFocusCore() { if (cell.binding is { } target) cell.Activate(target); }
        }
        private sealed class BoundProvider(RecycledCell cell, CellBinding target) : IInvokeProvider, IValueProvider, IScrollItemProvider
        {
            public bool IsReadOnly { get { cell.Validate(target); return cell.owner.CellInputReadOnly(cell.owner.rows[target.Row].Cells[target.Index]); } }
            public string Value { get { cell.Validate(target); return cell.owner.PresentationValue(cell.owner.rows[target.Row].Cells[target.Index]); } }
            public void Invoke() => cell.Activate(target);
            public void ScrollIntoView() { cell.Validate(target); cell.owner.EnsureRecycledRow(target.Row); }
            public void SetValue(string value)
            {
                cell.Validate(target); if (IsReadOnly) throw new InvalidOperationException("The cell is read-only.");
                cell.Activate(target);
                var editor = cell.owner.controls[target.Row][target.Index];
                if (editor is TextBox text) ((IValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(text).GetPattern(PatternInterface.Value)).SetValue(value);
                else throw new InvalidOperationException("Use the native choice control.");
            }
        }
    }
}

using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using System.Globalization;
using NativePath = Microsoft.UI.Xaml.Shapes.Path;
namespace GhProjectsBoards.App;

// A recycled native row owns no plan state. Identity and pending input belong to the sheet.
public sealed class PlanSheetRow : Grid
{
    internal PlanSheetView? Owner { get; private set; }
    internal string Identity => boundIdentity ?? "";
    internal PlanSheetCell[] Cells { get; private set; } = [];
    private readonly Grid sheetClip = new(), chartClip = new();
    private readonly RectangleGeometry sheetGeometry = new(), chartGeometry = new();
    private readonly StackPanel line = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform lineTranslation = new();
    private readonly TranslateTransform chartTranslation = new();
    private readonly Border chartSeparator = new() { BorderThickness = new(0, 0, 0, 1), BorderBrush = PlanSheetView.Brush("SheetSeparatorBrush"), IsHitTestVisible = false };
    private readonly Canvas chart = new() { Height = 28 };
    private readonly Canvas dependencyChart = new(), taskChart = new();
    private readonly TextBlock id = new() { FontSize = 13, TextAlignment = TextAlignment.Right, Padding = new(2, 3, 6, 0) };
    // Excel's row header (#263): the position helps the eye find a row, and is never a task reference.
    private readonly TextBlock rowNumber = new() { FontSize = 11, Padding = new(5, 5, 0, 0), Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush") };
    private readonly Grid idContent = new() { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new(1, GridUnitType.Star) } } };
    private readonly Grid idCell = new();
    private readonly PlanRowHandle idHandle;
    private readonly Border selectionAccent = new() { Width = 3, Height = 16, CornerRadius = new(1.5),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly FontIcon indicator = new() { FontSize = 13, Width = 28, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = true };
    // #264: unpublished changes get a 3 px orange bar at the row's left edge, so a late row never hides one.
    private readonly Border unpublishedBar = new() { Width = 3, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<Polygon?> corners = [];
    // #231 slice B: the pending-state tag and the 中止 strike-through, both only on the title cell.
    private TextBlock? tagChip;
    private Border? strikeLine;
    // Read by the hosted UI cases and the verifier's run sheet: the pending state tag and its strike-through.
    internal string? StateTagText => tagChip is { Visibility: Visibility.Visible } ? tagChip.Text : null;
    internal bool StruckThrough => strikeLine is { Visibility: Visibility.Visible };
    // A recycled row can stay in the sheet's realized set after it lost its owner; the mark cases need to
    // tell "the row was never repainted" from "this instance is no longer the displayed one".
    internal bool IsBound => Owner is not null;
    // The mark's raw wiring, so a case that sees no tag can say whether the element is missing, hidden, or
    // simply in an instance the sheet no longer displays.
    internal string TagDiagnostics => $"cells={Cells.Length} chip={(tagChip is null ? "null" : tagChip.Visibility.ToString())} strike={(strikeLine is null ? "null" : strikeLine.Visibility.ToString())} paints={markPaints} lastTag={lastTag ?? "null"}";
    private readonly List<Border> frames = [];
    private readonly List<TextBlock?> markers = [];
    private readonly List<Button?> handles = [];
    private readonly Button fold = new() { Width = 20, MinWidth = 0, MinHeight = 0, Padding = new(0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, PlanSummaryCell> summaryCells = [];
    internal Control? SelectionTarget(string field) => summaryCells.TryGetValue(field, out var display) && display.Visibility == Visibility.Visible
        ? display : Cells.FirstOrDefault(c => c.Field == field);
    internal double FittedColumnWidth(string key)
    {
        if (key == "Id") return PlanSheetView.MeasureColumnText(id.Text, id.FontSize, id.FontFamily, id.FontWeight) + id.Padding.Left + id.Padding.Right
            + PlanSheetView.MeasureColumnText(rowNumber.Text, rowNumber.FontSize, rowNumber.FontFamily, rowNumber.FontWeight) + rowNumber.Padding.Left;
        var cell = Cells.Single(c => c.Field == key);
        var width = summaryCells.TryGetValue(key, out var summary) && summary.Visibility == Visibility.Visible
            ? PlanSheetView.MeasureColumnText(summary.Text.Text, summary.Text.FontSize, summary.Text.FontFamily, summary.Text.FontWeight)
            : cell.AssigneeNameDisplay is { } name && !cell.Editing
                ? PlanSheetView.MeasureColumnText(name.Text, name.FontSize, name.FontFamily, name.FontWeight)
                    + PlanSheetView.MeasureColumnText(cell.AssigneeCountDisplay!.Text, name.FontSize, name.FontFamily, name.FontWeight)
                : PlanSheetView.MeasureColumnText(cell.Text, cell.FontSize, cell.FontFamily, cell.FontWeight);
        return width + cell.Padding.Left + cell.Padding.Right + 4;
    }
    private string? boundIdentity;
    private ListViewItem? container;
    private int markPaints;
    private string? lastTag;
    private double chartOffset = double.NaN;
    private sealed record ChartPresentation(string Identity, int Number, int RowIndex, DateOnly? Start, DateOnly? End,
        bool Summary, bool Milestone, decimal? CompleteFraction, PlanTaskLateness? Lateness, DateOnly? ForecastEnd,
        bool ShowLabels, DateOnly FirstDay, double DateInset, double DayWidth, double Viewport, double Height, ElementTheme Theme, Brush ThemeBrush);
    private sealed class DependencyVisual
    {
        internal readonly Polyline Line = new() { StrokeThickness = 1 };
        internal Polygon? Head;
        internal Brush? Brush;
        internal Point[] Points = [];
        internal bool HeadDown;
        internal string LineId = "", HeadId = "", RowIdentity = "";
    }
    private readonly Dictionary<(string From, string To), DependencyVisual> dependencyVisuals = [];
    // #256: a rebound row reuses its bar shapes instead of allocating native shapes on every bind. A shape
    // the current task does not draw is collapsed and loses its automation id, so no stale bar is found.
    private static readonly string[] TaskShapeOrder = ["endpoint", "task", "summary", "milestone", "progress", "lateTask", "lateSummary", "lateLabel"];
    private readonly Dictionary<string, FrameworkElement> taskShapes = [];
    private NativePath? crossingPath;
    // #264: the reused off-screen predecessor stub (`#1204 →`) in the Gantt row.
    private TextBlock? predecessorStub;
    // #256: a link that passes a row draws a vertical stroke across it. One open figure walks down one
    // crossing and up the next; its connecting runs lie at -2 and RowHeight + 2, outside the row's clip, so
    // only the crossings show. A rebound row replaces only the run's points; a figure and a segment per
    // crossing allocated dozens of native objects on every bind and forced blocking collections.
    private readonly PathFigure crossingFigure = new() { IsClosed = false, IsFilled = false };
    private readonly PolyLineSegment crossingRun = new();
    private PathGeometry? crossingGeometry;
    private double[] crossingXs = [], visibleCrossingXs = [];
    private double crossingHeight;
    private Brush? crossingBrush;
    private string crossingId = "", crossingIdentity = "";
    private ChartPresentation? chartPresentation;
    internal long DependencyShapesCreated { get; private set; }
    internal long DependencyShapesReused { get; private set; }
    internal long DependencyShapesReusedAcrossBindings { get; private set; }
    internal long DependencyPointsUpdated { get; private set; }
    public PlanSheetRow()
    {
        Height = 28; HorizontalAlignment = HorizontalAlignment.Left; ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new());
        line.RenderTransform = lineTranslation;
        chart.RenderTransform = chartTranslation;
        chart.Children.Add(dependencyChart); chart.Children.Add(taskChart);
        sheetClip.Clip = sheetGeometry; chartClip.Clip = chartGeometry;
        sheetClip.Children.Add(line);
        sheetClip.Children.Add(new Border { BorderThickness = new(0, 0, 0, 1), BorderBrush = PlanSheetView.Brush("SheetSeparatorBrush"), IsHitTestVisible = false });
        chartClip.Children.Add(chartSeparator);
        chartClip.Children.Add(chart);
        chartClip.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        chartClip.PointerWheelChanged += (_, args) => Owner?.ScrollTimeline(args);
        BringIntoViewRequested += (_, args) => { if (Owner?.HoldingView == true) args.Handled = true; };
        Children.Add(sheetClip); Children.Add(chartClip); SetColumn(chartClip, 1);
        // The unpublished bar sits over the ID column's left edge, above the cells it belongs to.
        unpublishedBar.Background = PlanSheetView.Brush("SheetChangedMarkBrush");
        Children.Add(unpublishedBar); SetColumn(unpublishedBar, 0);
        AutomationProperties.SetAccessibilityView(rowNumber, AccessibilityView.Raw);
        idContent.Children.Add(rowNumber); idContent.Children.Add(id); Grid.SetColumn(id, 1);
        idHandle = new(this) { Content = idContent };
        idCell.Children.Add(idHandle); idCell.Children.Add(selectionAccent);
        idHandle.Click += (_, _) => Owner?.SelectWholeRow(Identity, PlanSheetView.Down(VirtualKey.Shift));
        idHandle.PreviewKeyDown += (_, args) => {
            if (Owner is not { } owner) return;
            if (args.Key == VirtualKey.Escape) { owner.CancelDrag(); args.Handled = true; }
            else if (PlanSheetView.Down(VirtualKey.Control) && PlanSheetView.Minus(args.Key)) { args.Handled = true; _ = owner.KeyboardCommand(args.Key); }
            else if (args.Key == VirtualKey.F10 && PlanSheetView.Down(VirtualKey.Shift) || (int)args.Key == 93) { args.Handled = true; owner.ShowRowMenu(Identity, owner.FirstField, this); }
            else if (owner.HandleOutlineShortcut(args.Key)) args.Handled = true;
            else if (args.Key == VirtualKey.Insert && !PlanSheetView.Down(VirtualKey.Control)) { args.Handled = true; _ = owner.InsertFromKeyboard(); }
        };
        // #231 slice B: the row menu opens from the parts of a row that are not a cell, from a cell that is
        // not being edited (with its clipboard items), and from Shift+F10 or the menu key inside any cell.
        foreach (var surface in new UIElement[] { idCell, indicator, fold }) surface.ContextRequested += (_, args) => {
            if (Owner is not { } owner) return;
            var position = args.TryGetPosition(this, out var point) ? point : (Point?)null;
            args.Handled = true;
            owner.ShowRowMenu(Identity, owner.FirstField, this, position);
        };
        line.Children.Add(idCell); line.Children.Add(indicator);
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(id, FontNumeralAlignment.Tabular);
        fold.Click += async (_, _) => {
            if (Owner is not { } owner) return;
            var identity = Identity;
            await owner.Run(() => owner.SetFold(identity, !owner.IsFolded(identity)), "Fold requirement");
        };
    }
    private PlanSheetView.Column[]? presentedColumns;
    // #298: the same calendar affordance the settings date pickers show, placed on the selected date cell. It is
    // pointer-only (no tab stop, no focus) so it never takes the editor or the range selection; the keyboard
    // route is Alt+↓ on the cell.
    private Button CreateDateCalendar(PlanSheetCell cell)
    {
        var button = new Button {
            Width = 18, Height = 18, MinWidth = 0, MinHeight = 0, Padding = new(0), BorderThickness = new(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new(2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 1, 1, 0),
            IsTabStop = false, AllowFocusOnInteraction = false, Visibility = Visibility.Collapsed,
            Content = new FontIcon { Glyph = "\uE787", FontSize = 12 }
        };
        ToolTipService.SetToolTip(button, "カレンダーから日付を選ぶ");
        button.Click += async (_, _) => {
            if (Owner is { } owner) await owner.Run(() => { owner.OpenDateCalendar(cell); return Task.CompletedTask; }, "Date calendar");
        };
        cell.CalendarButton = button;
        return button;
    }
    private void EnsureColumns(PlanSheetView owner)
    {
        if (ReferenceEquals(presentedColumns,owner.VisibleColumns)) return;
        presentedColumns = owner.VisibleColumns;
        foreach (var column in owner.VisibleColumns.Where(c => c.Field is not null && !Cells.Any(cell => cell.Field == c.Field)))
        {
            var cell = new PlanSheetCell(this, column.Field!);
            Cells = [.. Cells, cell];
            var grid = new Grid(); grid.Children.Add(cell);
            if (cell.TitleDisplay is { } display) grid.Children.Add(display);
            if (cell.ForecastDisplay is { } forecast) grid.Children.Add(forecast);
            if (cell.EstimateDisplay is { } estimate) grid.Children.Add(estimate);
            if (cell.AssigneeDisplay is { } assignees) grid.Children.Add(assignees);
            if (cell.Field == PlanField.Title) grid.Children.Add(fold);
            if (owner.IsDateField(cell.Field)) grid.Children.Add(CreateDateCalendar(cell));
            var frame = new Border { Child = grid, BorderThickness = new(0, 0, 0, 1) };
            line.Children.Add(frame); frames.Add(frame); markers.Add(null); corners.Add(null); handles.Add(null);
        }
        var order = owner.ConfiguredColumns.Select(c => c.Indicator ? (UIElement)indicator : c.Field is null ? idCell
            : Cells.Select((cell, index) => (cell, index)).Where(x => x.cell.Field == c.Field).Select(x => (UIElement)frames[x.index]).FirstOrDefault()).OfType<UIElement>().ToArray();
        for (var i = 0; i < order.Length; i++)
            if (line.Children.IndexOf(order[i]) != i) { line.Children.Remove(order[i]); line.Children.Insert(i, order[i]); }
    }
    // The pending-state mark for this row: a tag naming the command that produced it, and a strike line for
    // 中止. The row's cell pass owns building the elements, because that is the moment the container is
    // being realised; a command may only repaint them, so it never modifies the live tree mid-pass (#231).
    internal void RefreshStateMark() => PaintStateMark(build: true);
    internal void RepaintStateMark() => PaintStateMark(build: false);
    private void PaintStateMark(bool build)
    {
        if (Owner is not { } owner || boundIdentity is null) return;
        if (!build && tagChip is null && strikeLine is null) return;
        var index = -1;
        for (var i = 0; i < Cells.Length; i++) if (Cells[i].Field == PlanField.Title) { index = i; break; }
        if (index < 0) return;
        var depth = 0; var parent = owner.Rows.GetValueOrDefault(Identity)?.Parent;
        while (parent is not null && owner.Rows.TryGetValue(parent, out var ancestor) && depth < 30) { depth++; parent = ancestor.Parent; }
        var rowTag = owner.RowStateTag(Identity);
        if (tagChip is null) {
            tagChip = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0), IsHitTestVisible = false };
            ((Grid)frames[index].Child).Children.Add(tagChip);
        }
        tagChip.Text = rowTag ?? "";
        tagChip.Visibility = rowTag is null ? Visibility.Collapsed : Visibility.Visible;
        tagChip.Foreground = PlanSheetView.Brush(rowTag == "中止にする" ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        if (rowTag is not null) tagChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Cells[index].ReserveForTag(rowTag is null ? 0 : tagChip.DesiredSize.Width + 8);
        if (strikeLine is null) {
            strikeLine = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
            ((Grid)frames[index].Child).Children.Add(strikeLine);
        }
        strikeLine.Visibility = owner.IsCancelled(Identity) ? Visibility.Visible : Visibility.Collapsed;
        strikeLine.Background = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        strikeLine.Width = Math.Max(0, frames[index].Width - 140);
        strikeLine.Margin = new(24 + depth * 12, 0, 0, 0);
        markPaints++; lastTag = rowTag;
    }
    internal void SetOwner(PlanSheetView? owner, string? identity, ListViewItem? itemContainer)
    {
        if (owner is not null) ArgumentNullException.ThrowIfNull(identity);
        else identity = null;
        if (ReferenceEquals(Owner, owner) && boundIdentity == identity && ReferenceEquals(container, itemContainer)) return;
        // A recycled container can stay loaded while its task and visual parent change.
        // The native item notification owns both identity and lifetime. In particular,
        // an empty string item need not produce a string template DataContext.
        var previous = Owner;
        var hadBinding = boundIdentity is not null;
        previous?.Realized.Remove(this);
        Owner = null; boundIdentity = null;
        if (hadBinding) ClearIds();
        // The item is the row's node ID, which the container would otherwise announce (#242).
        if (container is not null) AutomationProperties.SetName(container, "");
        Owner = owner; boundIdentity = identity; container = owner is null ? null : itemContainer;
        if (owner is not null) {
            owner.Realized.Add(this);
            Refresh();
        }
        if (!ReferenceEquals(previous, owner)) previous?.UpdateInputProblem();
        owner?.UpdateInputProblem();
    }
    private void ClearIds()
    {        AutomationProperties.SetAutomationId(id, ""); AutomationProperties.SetAutomationId(idHandle, ""); AutomationProperties.SetAutomationId(indicator, "");
        AutomationProperties.SetAutomationId(selectionAccent, "");
        foreach (var cell in Cells) {
            AutomationProperties.SetAutomationId(cell, ""); cell.Rebind();
            if (cell.CalendarButton is { } calendar) AutomationProperties.SetAutomationId(calendar, "");
        }
        foreach (var display in summaryCells.Values) AutomationProperties.SetAutomationId(display, "");
        foreach (var handle in handles) if (handle is not null) AutomationProperties.SetAutomationId(handle, "");
        AutomationProperties.SetAutomationId(fold, "");
        AutomationProperties.SetAutomationId(chartSeparator, "");
        // A recycled row keeps native dependency geometry, but none of the old
        // task's paint or automation identity may escape before rebinding finishes.
        chart.Opacity = 0;
        HideTaskShapes(); chartPresentation = null;
        foreach (var visual in dependencyVisuals.Values) {
            AutomationProperties.SetAutomationId(visual.Line, ""); visual.LineId = "";
            if (visual.Head is { } head) AutomationProperties.SetAutomationId(head, "");
            visual.HeadId = "";
        }
        if (crossingPath is not null) AutomationProperties.SetAutomationId(crossingPath, "");
        crossingId = "";
        if (predecessorStub is not null) {
            predecessorStub.Visibility = Visibility.Collapsed; predecessorStub.Text = "";
            AutomationProperties.SetAutomationId(predecessorStub, ""); AutomationProperties.SetName(predecessorStub, "");
        }
    }
    internal void Refresh()
    {
        if (Owner is not { } owner || boundIdentity is null) return;
        EnsureColumns(owner);
        Height = chart.Height = owner.RowHeight;
        var number = owner.PlanIds.GetValueOrDefault(Identity);
        var name = number == 0 ? "新規" : owner.TaskNames[Identity];
        ColumnDefinitions[0].Width = new(owner.SheetViewport); ColumnDefinitions[1].Width = new(owner.ChartViewport);
        chartClip.Visibility = owner.GanttVisible ? Visibility.Visible : Visibility.Collapsed;
        Width = owner.SheetViewport + owner.ChartViewport;
        var sheetBounds = new Rect(0, 0, owner.SheetViewport, owner.RowHeight);
        var chartBounds = new Rect(0, 0, owner.ChartViewport, owner.RowHeight);
        if (sheetGeometry.Rect != sheetBounds) sheetGeometry.Rect = sheetBounds;
        if (chartGeometry.Rect != chartBounds) chartGeometry.Rect = chartBounds;
        lineTranslation.X = -owner.SheetOffset;
        idCell.Width = owner.ConfiguredColumns.Single(c => c.Field is null && !c.Indicator).Width; idCell.Visibility = owner.Hidden.Contains(null) ? Visibility.Collapsed : Visibility.Visible;
        selectionAccent.Background = PlanSheetView.Brush("SheetSelectionStrokeBrush");
        selectionAccent.Visibility = owner.IsSelectedRow(Identity) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(selectionAccent, "PlanSelectionAccent" + number);
        var warnings = owner.Schedule.GetValueOrDefault(Identity)?.Warnings ?? [];
        id.Text = number == 0 ? "+" : name;
        rowNumber.Text = number == 0 ? "" : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var remoteProblem = owner.RemoteProblem(Identity);
        ToolTipService.SetToolTip(id, string.Join("\n", warnings) + remoteProblem);
        AutomationProperties.SetName(id, number == 0 ? "新しいタスク" : name + (warnings.Count > 0 ? " " + string.Join(" / ", warnings) : ""));
        AutomationProperties.SetAutomationId(id, "PlanRowId" + number);
        AutomationProperties.SetAutomationId(idHandle, "PlanRowHandle" + number);
        AutomationProperties.SetName(idHandle, $"{name} 行を選択・移動");
        if (container is not null)
            AutomationProperties.SetName(container, number == 0 ? "新しいタスク" : $"{name} {owner.Rows.GetValueOrDefault(Identity)?.Title}".TrimEnd());
        var depth = 0; var parent = owner.Rows.GetValueOrDefault(Identity)?.Parent;
        while (parent is not null && owner.Rows.TryGetValue(parent, out var ancestor) && depth < 30) { depth++; parent = ancestor.Parent; }
        var summary = owner.SummaryIds.Contains(Identity);
        var late = owner.LatenessOf(Identity);
        fold.Visibility = summary ? Visibility.Visible : Visibility.Collapsed;
        fold.IsEnabled = owner.FoldingEnabled;
        fold.Height = owner.RowHeight - 2;
        fold.Margin = new(depth * 12, 0, 0, 0);
        var collapsed = owner.IsFolded(Identity);
        var glyph = collapsed ? "\uE76C" : "\uE70D";
        if (fold.Content is not FontIcon icon || icon.Glyph != glyph) fold.Content = new FontIcon { Glyph = glyph, FontSize = 10 };
        var foldName = (collapsed ? "展開: " : "折りたたむ: ") + owner.Rows.GetValueOrDefault(Identity)?.Title;
        AutomationProperties.SetAutomationId(fold, "PlanFold" + number);
        AutomationProperties.SetName(fold, foldName);
        ToolTipService.SetToolTip(fold, foldName);
        for (var i = 0; i < Cells.Length; i++)
        {
            var cell = Cells[i]; var field = cell.Field; var column = owner.ConfiguredColumns.FirstOrDefault(c => c.Field == field) ?? new(field, field, 140);
            var visible = !owner.VisibleColumns.Any(c => c.Field == field) ? Visibility.Collapsed : Visibility.Visible;
            frames[i].Width = column.Width; frames[i].Visibility = visible;
            cell.Visibility = summary && field != PlanField.Title ? Visibility.Collapsed : visible;
            var selected = owner.IsSelected(Identity, field);
            var conflict = owner.BlockingConflicts.Any(c => c.Identity == Identity && c.Field == field);
            // An untitled inserted row cannot be published, so its title carries the same critical
            // outline a conflict uses and the reason in its help text and tooltip (#247).
            var untitled = field == PlanField.Title && owner.IsUntitledNewRow(Identity);
            frames[i].BorderBrush = PlanSheetView.Brush(conflict || untitled ? "SystemFillColorCriticalBrush" : selected ? "SheetSelectionStrokeBrush" : "SheetSeparatorBrush");
            frames[i].BorderThickness = selected || conflict || untitled ? new(2) : new(0, 0, 0, 1);
            frames[i].Background = PlanSheetView.Brush(conflict ? "LayerFillColorDefaultBrush" : selected || owner.IsSelectedRow(Identity) ? "SheetSelectionBrush"
                : owner.IsChanged(Identity, field) && owner.Unpublished.InputKind(Identity, field) == PlanUnpublishedInputKind.Entered ? "SheetChangedBrush" : "LayerFillColorDefaultBrush");
            var changed = owner.IsChanged(Identity, field);
            var outcome = field == PlanField.Title && remoteProblem.Length > 0;
            if (outcome && markers[i] is null) {
                var marker = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new(0, -3, 3, 0), IsHitTestVisible = false };
                markers[i] = marker; ((Grid)frames[i].Child).Children.Add(marker);
            }
            if (markers[i] is { } outcomeMarker) {
                outcomeMarker.Text = owner.Session.Document.Sync.Unverified.Contains(Identity) ? "GitHub で未確認" : "失敗";
                outcomeMarker.Visibility = !conflict && outcome ? Visibility.Visible : Visibility.Collapsed;
                outcomeMarker.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush");
            }
            if ((conflict || changed) && corners[i] is null) {
                var corner = new Polygon { Points = [new(0, 0), new(6, 0), new(0, 6)], Width = 6, Height = 6,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
                corners[i] = corner; ((Grid)frames[i].Child).Children.Add(corner);
            }
            if (corners[i] is { } changeMarker) {
                changeMarker.Visibility = conflict || changed ? Visibility.Visible : Visibility.Collapsed;
                changeMarker.Fill = PlanSheetView.Brush(conflict ? "SystemFillColorCriticalBrush" : "SheetChangedMarkBrush");
            }
            // A row whose state command is pending carries a tag (the command's own name) so it is never
            // confused with the outlined lateness marks, and 中止 is struck through. The mark is painted by
            // its own method so the command can repaint it without waiting for a cell pass (#231).
            if (field == PlanField.Title) RefreshStateMark();
            cell.IsReadOnly = owner.ReadOnly(Identity, field);
            var showHandle = owner.IsRangeEnd(Identity, field) && !cell.Editing && !cell.IsReadOnly && owner.Display(Identity, field).Length > 0 && !owner.Pending.ContainsKey((Identity, field));
            if (showHandle && handles[i] is null) {
                var handle = new PlanFillHandle(this, field) { Width = 8, Height = 8, MinHeight = 0, MinWidth = 0, Padding = new(0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsTabStop = false };
                AutomationProperties.SetName(handle, "下へコピー"); ToolTipService.SetToolTip(handle, "下へコピー（Ctrl+D）: 下にドラッグしてコピー");
                handle.Click += async (_, _) => { if (Owner is { } current) await current.Run(() => current.Fill(PlanOperationKind.Fill)); };
                handles[i] = handle; ((Grid)frames[i].Child).Children.Add(handle);
            }
            if (handles[i] is { } fillHandle) {
                fillHandle.Visibility = showHandle ? Visibility.Visible : Visibility.Collapsed;
                fillHandle.Background = PlanSheetView.Brush("SheetSelectionStrokeBrush");
                AutomationProperties.SetAutomationId(fillHandle, $"PlanFillHandle{number}_{field}");
            }
            cell.FontStyle = Windows.UI.Text.FontStyle.Normal;
            // #264: only a passed 終了日 colours the end date; a passed start or a forecast slip keeps the
            // ordinary date colours (the mark and the wording carry those cases).
            cell.Foreground = PlanSheetView.Brush(field == PlanField.End && late is { Level: PlanLatenessLevel.Overdue, OverdueKind: PlanOverdueKind.Finish } ? "SystemFillColorCriticalBrush"
                : field == PlanField.Title && owner.IsDimmed(Identity) ? "TextFillColorSecondaryBrush"
                : owner.IsCalculated(Identity, field) ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
            cell.FontWeight = owner.Schedule.GetValueOrDefault(Identity)?.IsSummary == true ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            cell.MinHeight = cell.Height = owner.RowHeight - (selected || conflict || untitled ? 4 : 1);
            cell.Padding = field == PlanField.Title ? new(24 + depth * 12, 1, 4, 1) : new(4, 1, 4, 1);
            var problem = owner.Problems.GetValueOrDefault((Identity, field)) ?? (untitled ? "タイトル未入力" : remoteProblem.Length > 0 ? remoteProblem : null);
            // A pending 完了 is local, so GitHub has not closed the Issue yet; the GitHub-owned read-only
            // reason would mislead. This is information, not a cell error, so it never colours the frame (#231).
            var stateHint = problem is null && cell.IsReadOnly && owner.IsPendingClose(Identity) && field is PlanField.Remaining or PlanField.End
                ? owner.Rows[Identity].Closed ? "完了にする を発行すると確定します（未発行）" : "未完了に戻す を発行すると編集できます（未発行）" : null;
            if (cell.CalendarButton is { } calendar)
            {
                cell.CalendarAllowed = !summary && owner.IsDateField(field) && !owner.ReadOnly(Identity, field) && owner.IsRangeEnd(Identity, field);
                AutomationProperties.SetAutomationId(calendar, $"PlanDateCalendar{number}_{field}");
                AutomationProperties.SetName(calendar, "カレンダーから日付を選ぶ");
            }
            var text = owner.Pending.GetValueOrDefault((Identity, field))?.Text ?? (cell.Editing ? owner.EditForm(Identity, field) : owner.Display(Identity, field));
            cell.Refresh(text);
            if (cell.TitleDisplay is { } titleDisplay) AutomationProperties.SetAutomationId(titleDisplay, "PlanTitleDisplay" + number);
            if (cell.ForecastDisplay is { } forecastDisplay) AutomationProperties.SetAutomationId(forecastDisplay, "PlanForecastDisplay" + number);
            // #264: the 見積 h cell carries the overrun figure; the sentence travels with the cell's name.
            var overrunWording = field == PlanField.Estimate && owner.Schedule.GetValueOrDefault(Identity) is { } estimateTask
                ? PlanSheetText.EstimateOverrunWording(estimateTask, owner.Marks) : null;
            if (cell.EstimateDisplay is { } estimateDisplay) {
                AutomationProperties.SetAutomationId(estimateDisplay, summary ? "" : "PlanEstimateDisplay" + number);
                estimateDisplay.Visibility = summary ? Visibility.Collapsed : Visibility.Visible;
            }
            if (cell.AssigneeDisplay is { } assigneeDisplay) {
                assigneeDisplay.Visibility = cell.Visibility == Visibility.Visible && !cell.Editing ? Visibility.Visible : Visibility.Collapsed;
                AutomationProperties.SetAutomationId(cell.AssigneeNameDisplay!, "PlanAssigneeName" + number);
                AutomationProperties.SetAutomationId(cell.AssigneeCountDisplay!, "PlanAssigneeCount" + number);
            }
            AutomationProperties.SetAutomationId(cell, $"PlanCell{number}_{field}");
            AutomationProperties.SetName(cell, $"{name} {PlanSheetView.FullName(column)}"
                + (overrunWording is not null ? " " + overrunWording
                    : field == "ForecastEnd" ? (owner.ForecastAccessibleValue(Identity) is { Length: > 0 } forecastName ? " " + forecastName : "")
                    : owner.CellDate(Identity, field) is { } day ? " " + PlanSheetView.DateText(day, true) : ""));
            AutomationProperties.SetHelpText(cell, conflict ? "競合" + (problem is null ? "" : " · " + problem) : problem ?? overrunWording ?? stateHint ?? (changed ? "未発行" : owner.IsCalculated(Identity, field) ? "計算値" : ""));
            ToolTipService.SetToolTip(cell, PlanSheetView.EffortPrefix(column) + (problem ?? overrunWording ?? stateHint ?? (owner.CellDate(Identity, field) is { } date ? PlanSheetView.DateText(date, true)
                : field == PlanField.Assignees ? owner.AssigneeTooltip(Identity) : text)));
            if (summary && field != PlanField.Title) {
                if (!summaryCells.TryGetValue(field, out var display)) {
                    display = new PlanSummaryCell(this, field);
                    summaryCells.Add(field, display);
                    ((Grid)frames[i].Child).Children.Insert(1, display);
                }
                // The leaf cell's two-colour forecast paint hides behind the summary cell, which draws the
                // same value for a requirement's roll-up (#236 M3).
                if (cell.ForecastDisplay is { } leafForecast) leafForecast.Visibility = Visibility.Collapsed;
                AutomationProperties.SetAutomationId(cell, "");
                display.Visibility = visible;
                display.Height = cell.Height;
                var value = owner.Display(Identity, field);
                var missing = field == PlanField.Estimate ? owner.Schedule.GetValueOrDefault(Identity)?.MissingEstimates ?? 0
                    : field == PlanField.Remaining ? owner.Schedule.GetValueOrDefault(Identity)?.MissingRemaining ?? 0 : 0;
                display.Text.Inlines.Clear();
                display.Missing.Text = missing > 0 ? missing.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                display.MissingTag.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (field == "ForecastEnd") {
                    // The derived date is secondary text; a later forecast adds the critical red slip.
                    if (owner.ForecastEndOf(Identity) is { } forecastDate)
                        display.Text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = PlanSheetView.DateText(forecastDate),
                            Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush") });
                    if (owner.WorkingDaysLater(Identity) is { } later)
                        display.Text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " +" + later + "日",
                            Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush") });
                } else display.Text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value });
                display.Text.Foreground = PlanSheetView.Brush(field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Start or PlanField.End
                    or "ForecastEnd" ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
                AutomationProperties.SetAutomationId(display, $"PlanCell{number}_{field}");
                var accessibleValue = field is PlanField.Start or PlanField.End && owner.CellDate(Identity, field) is { } accessibleDate
                    ? PlanSheetView.DateText(accessibleDate, true)
                    : field == "ForecastEnd" ? owner.ForecastAccessibleValue(Identity)
                    : missing > 0 ? $"{value}（{(field == PlanField.Estimate ? "見積" : "残")}のないタスク {missing} 件を除く）" : value;
                AutomationProperties.SetName(display, $"{name} {PlanSheetView.FullName(column)} {accessibleValue} · {PlanOperations.SummaryReadOnlyReason(field)}");
                var fullValue = field == PlanField.Assignees ? owner.AssigneeTooltip(Identity) : accessibleValue;
                AutomationProperties.SetHelpText(display, string.Join(" · ", new[] { fullValue, AutomationProperties.GetHelpText(cell) }.Where(value => value.Length > 0)));
                ToolTipService.SetToolTip(display, PlanSheetView.EffortPrefix(column) + (problem ?? fullValue));
            } else if (summaryCells.TryGetValue(field, out var display)) {
                display.Visibility = Visibility.Collapsed;
                AutomationProperties.SetAutomationId(display, "");
            }
            if (problem is not null) frames[i].BorderBrush = PlanSheetView.Brush("SystemFillColorCriticalBrush");
        }
        var taskRow = owner.Rows.GetValueOrDefault(Identity);
        var effort = owner.Schedule.GetValueOrDefault(Identity);
        var unpublished = owner.Unpublished.Fields.ContainsKey(Identity);
        var mark = PlanSheetText.Mark(owner.TaskNames, taskRow, effort, late, remoteProblem, owner.Marks, owner.ConflictValues(Identity));
        // #264: one mark in the fixed priority; the two 遅れ cases share the outlined ⚠ shape and differ by
        // colour and by the wording the tooltip carries. 未発行 and 見積超過 are not in this column.
        indicator.Glyph = mark.Mark switch {
            PlanRowMark.Failure => "\uE783", PlanRowMark.Conflict => "\uE8AB",
            PlanRowMark.LatePassed or PlanRowMark.LateForecast => "\uE7BA",
            PlanRowMark.SetAside => "\uE733", PlanRowMark.Done => "\uE73E", _ => "" };
        indicator.Foreground = PlanSheetView.Brush(mark.Mark switch {
            PlanRowMark.Failure or PlanRowMark.Conflict or PlanRowMark.LatePassed => "SystemFillColorCriticalBrush",
            PlanRowMark.LateForecast => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush" });
        indicator.Visibility = owner.IndicatorVisible ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(indicator, "PlanIndicator" + number);
        AutomationProperties.SetName(indicator, mark.Text); ToolTipService.SetToolTip(indicator, mark.Text);
        // #264: the unpublished bar keeps the row's left edge, independent of the 状態 mark and its switch.
        unpublishedBar.Visibility = unpublished ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(unpublishedBar, unpublished ? "PlanUnpublishedBar" + number : "");
        AutomationProperties.SetName(unpublishedBar, unpublished ? "未発行の変更" : "");
        Background = owner.IsSelectedRow(Identity) ? PlanSheetView.Brush("SheetSelectionBrush") : null;
        // 中止 and pending removal dim the whole row; a pending completion keeps its ordinary weight
        // because the task is finished, not set aside (#231). A count filter keeps ancestor requirement rows
        // as context only, so they dim like those rows (#236 decision 5).
        Opacity = owner.IsDimmed(Identity) || owner.IsFilterContext(Identity) ? 0.55 : 1;
        id.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        id.FontWeight = summary ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        DrawChart();
    }
    internal void RefreshHorizontal(bool sheet, bool gantt)
    {
        if (Owner is not { } owner || boundIdentity is null) return;
        if (sheet && lineTranslation.X != -owner.SheetOffset) lineTranslation.X = -owner.SheetOffset;
        if (gantt && chartOffset != owner.ChartOffset) {
            RefreshChartOffset(owner);
            RefreshCrossings(owner, owner.PlanIds.GetValueOrDefault(Identity));
        }
    }
    private void RefreshChartOffset(PlanSheetView owner)
    {
        chartOffset = owner.ChartOffset;
        // Scroll in composition without reallocating every bar and dependency shape.
        chartTranslation.X = -chartOffset;
    }
    private static double ChartX(PlanSheetView owner, DateOnly day) =>
        owner.ChartDateInset + (day.DayNumber - owner.FirstDay.DayNumber) * owner.DayWidth;
    private void DrawChart()
    {
        // Recycle notifications hide the old bounded slice before assigning another item.
        if (Owner is not { } owner || boundIdentity is null) { chart.Opacity = 0; return; }
        // #298: the chart column is collapsed while the Gantt is hidden; keep the shapes for showing it again.
        if (!owner.GanttVisible) { chart.Opacity = 0; return; }
        AutomationProperties.SetAutomationId(chartSeparator, "PlanChartSeparator" + owner.PlanIds.GetValueOrDefault(Identity));
        if (!owner.Schedule.TryGetValue(Identity, out var task)) {
            dependencyChart.Children.Clear(); dependencyVisuals.Clear(); predecessorStub = null; HideTaskShapes();
            crossingXs = []; ClearCrossings();
            chartPresentation = null; chart.Opacity = 1;
            return;
        }
        var number = owner.PlanIds[Identity];
        var rowIndex = owner.RowIndexes.GetValueOrDefault(Identity, -1);
        RefreshChartOffset(owner);
        chart.Width = dependencyChart.Width = taskChart.Width = owner.ChartViewport;
        dependencyChart.Height = taskChart.Height = owner.RowHeight;
        DrawDependencies(owner, number, rowIndex);
        decimal? completeFraction = !task.IsSummary && task.Input.IsComplete ? 1m : task.WorkCompleteFraction;
        var presentation = new ChartPresentation(Identity, number, rowIndex, task.Start.Value, task.End.Value,
            task.IsSummary, task.IsMilestone, completeFraction, owner.LatenessOf(Identity), owner.ForecastEndOf(Identity),
            owner.ShowLatenessLabels, owner.FirstDay, owner.ChartDateInset, owner.DayWidth, owner.ChartViewport, owner.RowHeight,
            ActualTheme, PlanSheetView.Brush("GanttTaskBrush"));
        if (presentation != chartPresentation) {
            chartPresentation = presentation;
            HideTaskShapes(); DrawTask(owner, number, task);
        }
        chart.Opacity = 1;
    }
    // #264: redraw this row's dependency slice after the visible row window moved. The shapes are reused, so
    // a scroll that brings a predecessor into or out of view only rewrites text and geometry.
    internal void RefreshDependencies()
    {
        if (Owner is not { } owner || boundIdentity is null) return;
        if (!owner.Schedule.TryGetValue(Identity, out _)) return;
        DrawDependencies(owner, owner.PlanIds.GetValueOrDefault(Identity), owner.RowIndexes.GetValueOrDefault(Identity, -1));
    }
    private void DrawDependencies(PlanSheetView owner, int number, int rowIndex)
    {
        var current = new HashSet<(string From, string To)>();
        var crossings = new HashSet<double>();
        var brush = PlanSheetView.Brush("GanttArrowBrush");
        var edges = rowIndex >= 0 && rowIndex < owner.EdgesByRow.Length ? owner.EdgesByRow[rowIndex] : [];
        // A rebound row takes over the previous task's arrow shapes it no longer needs instead of
        // allocating new native shapes (#256).
        var wanted = edges.Where(e => e.From == rowIndex || e.To == rowIndex).Select(e => (owner.RowIds[e.From], owner.RowIds[e.To])).ToHashSet();
        var spare = new Stack<DependencyVisual>(dependencyVisuals.Where(p => !wanted.Contains(p.Key)).Select(p => p.Value));
        foreach (var key in dependencyVisuals.Keys.Where(key => !wanted.Contains(key)).ToArray()) dependencyVisuals.Remove(key);
        var offscreen = new List<string>();
        foreach (var edge in edges)
        {
            // #264: a link whose predecessor row is scrolled out of view draws no line across the chart;
            // the successor row shows a stub instead. Lines to a visible predecessor keep their arrow.
            if (!owner.RowIsOnScreen(edge.From)) {
                if (rowIndex == edge.To) offscreen.Add(owner.TaskNames[owner.RowIds[edge.From]]);
                continue;
            }
            var successor = owner.Schedule[owner.RowIds[edge.To]];
            var successorMilestone = successor.IsMilestone && successor.Start.Value == successor.End.Value;
            var predecessor = owner.Schedule[owner.RowIds[edge.From]];
            var predecessorMilestone = predecessor.IsMilestone && predecessor.Start.Value == predecessor.End.Value;
            // #236: every coordinate here comes from the schedule the bars are drawn from. The edge carries only
            // the row pair now, because its cached copies of these dates could lag the bar by a day once an edit
            // rescheduled an end; an arrow a day off from the bar it points at is a wrong picture, not a rounded
            // one. RefreshEdges only creates an edge when both dates are known, so the fallbacks are unreachable.
            if ((predecessor.Start.Value ?? predecessor.End.Value) is not { } predecessorStart
                || (successor.Start.Value ?? successor.End.Value) is not { } successorStart) continue;
            var predecessorEnd = predecessor.End.Value ?? predecessorStart;
            var successorEnd = successor.End.Value ?? successorStart;
            var predecessorWidth = predecessor.Start.Value is null ? 8 : predecessorMilestone ? 10 : Math.Max(2, (predecessorEnd.DayNumber - predecessorStart.DayNumber + 1) * owner.DayWidth);
            var x1 = ChartX(owner, predecessorStart) + predecessorWidth; var x2 = ChartX(owner, successorStart);
            var successorWidth = successorMilestone ? 10 : Math.Max(2, (successorEnd.DayNumber - successorStart.DayNumber + 1) * owner.DayWidth);
            var inset = Math.Min(3, successorWidth / 2);
            // Clamp locally: adding a large date coordinate can invert equal bounds through rounding.
            var entry = x2 + Math.Clamp(Math.Max(x1 - x2 + 5, 5), inset, successorWidth - inset);
            // Between endpoints only the vertical segment enters this row's clip.
            // Share coincident strokes instead of creating a native shape per link.
            if (rowIndex != edge.From && rowIndex != edge.To) { crossings.Add(entry); continue; }
            var key = (owner.RowIds[edge.From], owner.RowIds[edge.To]);
            current.Add(key);
            if (!dependencyVisuals.TryGetValue(key, out var visual)) {
                if (spare.TryPop(out var taken)) { visual = taken; DependencyShapesReused++; DependencyShapesReusedAcrossBindings++; }
                else { visual = new DependencyVisual(); dependencyChart.Children.Add(visual.Line); DependencyShapesCreated++; }
                dependencyVisuals.Add(key, visual);
            } else {
                DependencyShapesReused++;
                if (visual.RowIdentity != Identity) DependencyShapesReusedAcrossBindings++;
            }
            visual.RowIdentity = Identity;
            var down = edge.To > edge.From;
            var y1 = (edge.From - rowIndex) * owner.RowHeight + owner.RowHeight / 2;
            var center = (edge.To - rowIndex) * owner.RowHeight + owner.RowHeight / 2;
            var top = center - (successor.IsSummary ? 3 : successorMilestone ? 5 : 7);
            var tip = down ? top : top + (successor.IsSummary || successorMilestone ? 10 : 14);
            Point[] points = [new(x1, y1), new(entry, y1), new(entry, tip + (down ? -6 : 6))];
            if (entry < x1 + 5) {
                // Fixed/overlapping dates still leave the predecessor to the right, then return in the row gap.
                var gap = y1 + (down ? owner.RowHeight / 2 - 3 : -owner.RowHeight / 2 + 3);
                points = [new(x1, y1), new(x1 + 5, y1), new(x1 + 5, gap), new(entry, gap), new(entry, tip + (down ? -6 : 6))];
            }
            // Parent clipping does not bound the native shape's rasterization surface.
            // These orthogonal segments need only the row slice, with a guard for the stroke.
            for (var i = 0; i < points.Length; i++) points[i] = new(points[i].X, Math.Clamp(points[i].Y, -2, owner.RowHeight + 2));
            // Preserve the native endpoint geometry when its current row slice agrees.
            if (!visual.Points.SequenceEqual(points)) {
                visual.Line.Points = [.. points]; visual.Points = points; DependencyPointsUpdated++;
            }
            var brushChanged = !ReferenceEquals(visual.Brush, brush);
            if (brushChanged) { visual.Line.Stroke = brush; visual.Brush = brush; }
            var lineId = $"PlanArrow{edge.From + 1}_{edge.To + 1}_{number}";
            if (visual.LineId != lineId) { AutomationProperties.SetAutomationId(visual.Line, lineId); visual.LineId = lineId; }
            if (rowIndex == edge.To) {
                if (visual.Head is null) {
                    visual.Head = new Polygon { Width = 6, Height = 6, Fill = brush };
                    dependencyChart.Children.Add(visual.Head); DependencyShapesCreated++;
                    visual.HeadDown = !down;
                } else if (brushChanged) visual.Head.Fill = brush;
                var head = visual.Head;
                if (visual.HeadDown != down) {
                    head.Points = down ? [new(0, 0), new(6, 0), new(3, 6)] : [new(0, 6), new(6, 6), new(3, 0)];
                    visual.HeadDown = down;
                }
                var headId = $"PlanArrowHead{edge.From + 1}_{edge.To + 1}_{number}";
                if (visual.HeadId != headId) { AutomationProperties.SetAutomationId(head, headId); visual.HeadId = headId; }
                Canvas.SetLeft(head, entry - 3); Canvas.SetTop(head, down ? tip - 6 : tip);
            } else if (visual.Head is { } head) {
                dependencyChart.Children.Remove(head); visual.Head = null; visual.HeadId = "";
            }
        }
        // Retain only this row's current edges, never the union of its scroll history.
        foreach (var key in dependencyVisuals.Keys.Where(key => !current.Contains(key)).ToArray()) {
            spare.Push(dependencyVisuals[key]); dependencyVisuals.Remove(key);
        }
        foreach (var visual in spare) {
            dependencyChart.Children.Remove(visual.Line);
            if (visual.Head is { } head) dependencyChart.Children.Remove(head);
        }
        crossingXs = crossings.Order().ToArray();
        if (!ReferenceEquals(crossingBrush, brush)) {
            crossingBrush = brush;
            if (crossingPath is not null) crossingPath.Stroke = brush;
        }
        DrawPredecessorStub(owner, number, rowIndex, offscreen);
        RefreshCrossings(owner, number);
    }
    // The off-screen predecessor stub (#264): `#1204 →` just before the bar start, with the sentence in its
    // tooltip. One reused text block, so a rebound row never accumulates stale stubs.
    private void DrawPredecessorStub(PlanSheetView owner, int number, int rowIndex, List<string> offscreen)
    {
        if (predecessorStub is null) {
            predecessorStub = new TextBlock { FontSize = 11, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush") };
            dependencyChart.Children.Add(predecessorStub);
        }
        var stub = predecessorStub;
        if (offscreen.Count == 0 || rowIndex < 0 || !owner.Schedule.TryGetValue(Identity, out var task)
            || (task.Start.Value ?? task.End.Value) is not { } anchor) {
            stub.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(stub, "");
            AutomationProperties.SetName(stub, "");
            return;
        }
        var names = string.Join(", ", offscreen);
        var text = names + " →";
        var description = "先行 " + names + "（表示されていません）";
        if (stub.Text != text) stub.Text = text;
        stub.Visibility = Visibility.Visible;
        AutomationProperties.SetAutomationId(stub, "PlanPredStub" + number);
        AutomationProperties.SetName(stub, description); ToolTipService.SetToolTip(stub, description);
        stub.Measure(new Size(double.PositiveInfinity, owner.RowHeight));
        // Before the bar start; the chart clip keeps a stub for a bar at the left edge readable enough to scan.
        Canvas.SetLeft(stub, Math.Max(0, ChartX(owner, anchor) - 6 - stub.DesiredSize.Width));
        Canvas.SetTop(stub, (owner.RowHeight - stub.DesiredSize.Height) / 2);
    }
    private void ClearCrossings()
    {
        visibleCrossingXs = [];
        if (crossingPath is null) return;
        crossingPath.Data = null;
        crossingPath.Visibility = Visibility.Collapsed;
        if (crossingId.Length > 0) AutomationProperties.SetAutomationId(crossingPath, "");
        crossingId = "";
    }
    private void RefreshCrossings(PlanSheetView owner, int number)
    {
        // A single Path must not span the whole timeline's rasterization surface.
        // Keep only the current horizontal slice, with a guard for its one-pixel stroke.
        var visible = crossingXs.Where(x => x >= owner.ChartOffset - 1 && x <= owner.ChartOffset + owner.ChartViewport + 1).ToArray();
        if (visible.Length == 0 || owner.ChartViewport <= 0) { ClearCrossings(); return; }
        if (crossingPath is null) {
            crossingPath = new NativePath { StrokeThickness = 1, Stroke = crossingBrush, IsHitTestVisible = false };
            chart.Children.Insert(0, crossingPath); DependencyShapesCreated++;
        } else {
            DependencyShapesReused++;
            if (crossingIdentity != Identity) DependencyShapesReusedAcrossBindings++;
        }
        crossingIdentity = Identity;
        if (crossingPath.Data is null || crossingHeight != owner.RowHeight || !visibleCrossingXs.SequenceEqual(visible)) {
            var origin = visible[0]; double top = -2, bottom = owner.RowHeight + 2;
            var points = new PointCollection { new(0, bottom) };
            for (var i = 1; i < visible.Length; i++) {
                var from = i % 2 == 0 ? top : bottom;
                points.Add(new(visible[i] - origin, from)); points.Add(new(visible[i] - origin, from == top ? bottom : top));
            }
            crossingFigure.StartPoint = new(0, top); crossingRun.Points = points;
            if (crossingGeometry is null) {
                crossingFigure.Segments.Add(crossingRun);
                crossingGeometry = new PathGeometry(); crossingGeometry.Figures.Add(crossingFigure);
            }
            if (!ReferenceEquals(crossingPath.Data, crossingGeometry)) crossingPath.Data = crossingGeometry;
            Canvas.SetLeft(crossingPath, origin);
            visibleCrossingXs = visible; crossingHeight = owner.RowHeight; DependencyPointsUpdated++;
        }
        crossingPath.Visibility = Visibility.Visible;
        var id = "PlanArrowCrossings" + number;
        if (crossingId != id) { AutomationProperties.SetAutomationId(crossingPath, id); crossingId = id; }
    }
    private T TaskShape<T>(string kind, Func<T> create) where T : FrameworkElement
    {
        if (!taskShapes.TryGetValue(kind, out var shape)) {
            shape = create();
            // Keep the drawing order fixed (bar under progress, slip and label) whatever order a row first needs them in.
            var rank = Array.IndexOf(TaskShapeOrder, kind);
            taskChart.Children.Insert(taskShapes.Keys.Count(k => Array.IndexOf(TaskShapeOrder, k) < rank), shape);
            taskShapes.Add(kind, shape);
        }
        shape.Visibility = Visibility.Visible;
        return (T)shape;
    }
    private void HideTaskShapes()
    {
        foreach (var shape in taskShapes.Values) {
            if (shape.Visibility == Visibility.Collapsed) continue;
            shape.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(shape, ""); AutomationProperties.SetName(shape, "");
            ToolTipService.SetToolTip(shape, null);
        }
    }
    private void DrawTask(PlanSheetView owner, int number, ScheduledTask task)
    {
        if (task.Start.Value is not { } start || task.End.Value is not { } end)
        {
            if ((task.Start.Value ?? task.End.Value) is { } known)
            {
                var endpoint = TaskShape("endpoint", () => new Polygon { Width = 8, Height = 10, Points = [new(0, 0), new(8, 5), new(0, 10)] });
                endpoint.Fill = PlanSheetView.Brush("TextFillColorSecondaryBrush");
                Canvas.SetLeft(endpoint, ChartX(owner, known)); Canvas.SetTop(endpoint, (owner.RowHeight - 10) / 2);
                AutomationProperties.SetAutomationId(endpoint, "PlanEndpoint" + number);
                AutomationProperties.SetName(endpoint, $"{owner.TaskNames[Identity]} {(task.Start.Value is not null ? "開始" : "終了")} {known:yyyy-MM-dd}");
            }
            return;
        }
        if (end < start) return;
        var x = ChartX(owner, start); var width = Math.Max(2, (end.DayNumber - start.DayNumber + 1) * owner.DayWidth);
        var milestone = task.IsMilestone && start == end;
        decimal? share = !task.IsSummary && task.Input.IsComplete ? 1m : task.WorkCompleteFraction;
        Shape bar;
        if (task.IsSummary) {
            var summaryBar = TaskShape("summary", () => new Polygon { Height = 10 });
            summaryBar.Points = [new(0, 0), new(width, 0), new(width, 10), new(Math.Max(0, width - 4), 6), new(Math.Min(4, width), 6), new(0, 10)];
            summaryBar.Width = width; summaryBar.Fill = PlanSheetView.Brush("GanttSummaryBrush");
            bar = summaryBar;
        }
        else if (milestone) {
            bar = TaskShape("milestone", () => new Polygon { Points = [new(5, 0), new(10, 5), new(5, 10), new(0, 5)], Width = 10, Height = 10 });
            bar.Fill = PlanSheetView.Brush("GanttTaskBrush");
        }
        else {
            bar = TaskShape("task", () => new Rectangle { Height = 14, RadiusX = 3, RadiusY = 3, StrokeThickness = 1 });
            bar.Width = width; bar.Fill = PlanSheetView.Brush("GanttTaskTintBrush"); bar.Stroke = PlanSheetView.Brush("GanttTaskBrush");
        }
        Canvas.SetLeft(bar, x); Canvas.SetTop(bar, (owner.RowHeight - (task.IsSummary ? 6 : bar.Height)) / 2);
        var lateness = owner.LatenessOf(Identity);
        var late = lateness?.DaysLater;
        var overdue = lateness?.Level == PlanLatenessLevel.Overdue;
        var barName = $"{owner.TaskNames[Identity]} {start:yyyy-MM-dd} – {end:yyyy-MM-dd}" + (task.IsSummary ? " 集計" : milestone ? " マイルストーン" : "")
            + (share is { } percent ? $" 完了 {percent * 100:0}%" : "") + (late is { } days ? " " + PlanSheetText.BarLateness(overdue, days) : "");
        AutomationProperties.SetAutomationId(bar, "PlanBar" + number);
        AutomationProperties.SetName(bar, barName);
        ToolTipService.SetToolTip(bar, barName);
        if (!task.IsSummary && !milestone && share is > 0) {
            var progress = TaskShape("progress", () => new Rectangle { IsHitTestVisible = false, Height = 14, RadiusX = 3, RadiusY = 3 });
            progress.Width = width * (double)share.Value; progress.Fill = PlanSheetView.Brush("GanttTaskBrush");
            AutomationProperties.SetAutomationId(progress, "PlanProgress" + number);
            Canvas.SetLeft(progress, x); Canvas.SetTop(progress, (owner.RowHeight - 14) / 2);
        }
        if (!milestone && late is { } count && owner.ForecastEndOf(Identity) is { } forecast && forecast > end) {
            // The slip runs from the day after the plan end to the forecast end (#236 part B). It is the
            // plan-versus-forecast gap, never a comparison with what was published.
            var lateX = ChartX(owner, end) + owner.DayWidth;
            var lateWidth = ChartX(owner, forecast) + owner.DayWidth - lateX;
            if (lateWidth <= 0) return;
            Shape segment;
            if (task.IsSummary) {
                var summarySegment = TaskShape("lateSummary", () => new Polygon { Height = 10, IsHitTestVisible = false, StrokeThickness = 1, StrokeDashArray = [3, 2] });
                summarySegment.Points = [new(0, 0), new(lateWidth, 0), new(lateWidth, 10), new(Math.Max(0, lateWidth - 4), 6), new(0, 6)];
                segment = summarySegment;
            }
            else segment = TaskShape("lateTask", () => new Rectangle { Height = 14, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false, StrokeThickness = 1, StrokeDashArray = [3, 2] });
            segment.Width = lateWidth;
            segment.Fill = overdue ? PlanSheetView.Brush("GanttLateTintBrush") : null; segment.Stroke = PlanSheetView.Brush("GanttLateBrush");
            AutomationProperties.SetAutomationId(segment, "PlanLate" + number);
            Canvas.SetLeft(segment, lateX); Canvas.SetTop(segment, task.IsSummary ? Canvas.GetTop(bar) : (owner.RowHeight - 14) / 2);
            if (!overdue || !owner.ShowLatenessLabels) return;
            var label = TaskShape("lateLabel", () => new TextBlock { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            label.Text = $"+{count}日"; label.Foreground = PlanSheetView.Brush("GanttLateBrush");
            AutomationProperties.SetAutomationId(label, "PlanLateLabel" + number);
            Canvas.SetLeft(label, x + width + 6); Canvas.SetTop(label, (owner.RowHeight - 16) / 2);
        }
    }
}

internal sealed class PlanSheetCell : TextBox
{
    private readonly PlanSheetRow row;
    internal string Field { get; }
    internal bool Composing { get => composing; private set { composing = value; ApplyContentHost(); } }
    private bool composing;
    // While editing, the text box keeps its own menu for the characters being typed; otherwise a right click
    // opens the row menu (#231).
    internal bool Editing { get => editing; private set { editing = value; ContextFlyout = value ? textMenu : null; ApplyContentHost(); } }
    private bool editing;
    // #225: Escape closes the 担当者 candidate list for the rest of this edit; typing after that does not
    // reopen it until the edit starts again.
    internal bool AssigneeListDismissed { get; private set; }
    internal void DismissAssigneeList() => AssigneeListDismissed = true;
    internal void AllowAssigneeList() => AssigneeListDismissed = false;
    private readonly Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase? textMenu;
    internal TextBlock? TitleDisplay { get; }
    // #298: the calendar button the row places over this date cell while it is selected. The row owns the
    // control; the cell owns when a date edit may offer the calendar and hides it while the text editor shows.
    internal Button? CalendarButton { get; set; }
    internal bool CalendarAllowed { get; set; }
    internal string Identity => row.Identity;
    // 見込終了 needs two colours in one cell: the derived date in secondary text and its working-day slip
    // in critical red (#236 decision 5). A TextBlock with runs draws that; the TextBox keeps its text and
    // input path with its paint hidden, like the title display.
    internal TextBlock? ForecastDisplay { get; }
    // #264: 見積超過 reads as `+16h` in ochre next to the estimate value, in the cell that owns the value
    // instead of the one-icon 状態 column. Two runs draw it; the TextBox keeps its text and input path with
    // its paint hidden, like the title and forecast displays.
    internal TextBlock? EstimateDisplay { get; }
    // Width the row-state tag takes at the right of the title, so the ellipsis ends before the tag (#231).
    internal double TagReserve { get; private set; }
    internal void ReserveForTag(double width) { if (TagReserve == width) return; TagReserve = width; RefreshDisplay(); }
    internal Grid? AssigneeDisplay { get; }
    internal TextBlock? AssigneeNameDisplay { get; }
    internal TextBlock? AssigneeCountDisplay { get; }
    private bool refreshing, endedThisTurn, committing, preservePendingOnLostFocus;
    private string shownText = "", editingFrom = "";
    internal PlanSheetCell(PlanSheetRow row, string field)
    {
        this.row = row; Field = field;
        if (field == PlanField.Title) {
            TitleDisplay = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(TitleDisplay, AccessibilityView.Raw);
        }
        if (field == "ForecastEnd") {
            ForecastDisplay = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(ForecastDisplay, AccessibilityView.Raw);
        }
        if (field == PlanField.Estimate) {
            EstimateDisplay = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(EstimateDisplay, AccessibilityView.Raw);
        }
        if (field == PlanField.Assignees) {
            AssigneeDisplay = new Grid { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            AssigneeDisplay.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            AssigneeDisplay.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            AssigneeNameDisplay = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            AssigneeCountDisplay = new TextBlock();
            Grid.SetColumn(AssigneeCountDisplay, 1);
            AssigneeDisplay.Children.Add(AssigneeNameDisplay); AssigneeDisplay.Children.Add(AssigneeCountDisplay);
            AutomationProperties.SetAccessibilityView(AssigneeDisplay, AccessibilityView.Raw);
            AutomationProperties.SetAccessibilityView(AssigneeNameDisplay, AccessibilityView.Raw);
            AutomationProperties.SetAccessibilityView(AssigneeCountDisplay, AccessibilityView.Raw);
        }
        // Sheet Clear owns deletion, so the template has no delete button taking editor width.
        Style = (Style)Application.Current.Resources["PlanSheetCellStyle"];
        textMenu = ContextFlyout; ContextFlyout = null;
        MinWidth = 0; MinHeight = 26; Height = 26; Padding = new(4, 2, 4, 2); FontSize = 13; BorderThickness = new(0); Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Predecessors) TextAlignment = TextAlignment.Right;
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        GotFocus += (_, _) => {
            // GotFocus can arrive after focus has moved to another cell.
            if (FocusState == FocusState.Unfocused) return;
            if (row.Owner is { } owner) {
                if (!owner.IsSelected(row.Identity, Field)) owner.Select(row.Identity, Field, false);
                owner.UpdateInputProblem();
            }
            if (!Editing) SelectAll();
        };
        ContextRequested += (_, args) => {
            if (Editing || row.Owner is not { } owner) return;
            args.Handled = true;
            owner.ShowRowMenu(row.Identity, Field, this, args.TryGetPosition(this, out var point) ? point : null, clipboard: true);
        };
        TextChanging += (_, _) => {
            // WinUI may deliver a programmatic text notification after Refresh returns.
            if (refreshing || Text == shownText || row.Owner is not { } owner || IsReadOnly) return;
            if (!Editing) { editingFrom = EditOriginal(); AssigneeListDismissed = false; }
            Editing = true; owner.SetInput(row.Identity, Field, Text, editingFrom);
            shownText = Text;
            RefreshDisplay();
            // #225: the first typed change opens the 担当者 candidate list, later ones filter it. An IME
            // composition filters only after it is confirmed (TextCompositionEnded).
            if (Field == PlanField.Assignees && !Composing) owner.AssigneeEdited(this);
        };
        LosingFocus += (_, args) => {
            // LostFocus can arrive after a pane action has moved focus again. Decide from this
            // transition's destination, not the eventual global focus when the notification arrives.
            preservePendingOnLostFocus = row.Owner?.PreservePendingFocus?.Invoke(args.NewFocusedElement) == true
                || row.Owner?.PreserveForDateCalendar(args.NewFocusedElement) == true;
        };
        LostFocus += async (_, _) => {
            var preservePending = preservePendingOnLostFocus;
            preservePendingOnLostFocus = false;
            if (Composing) return;
            // Reading beside the plan must leave the native draft intact, even when a header button takes focus.
            if (preservePending) return;
            EndEditing();
            if (committing || !IsLoaded || row.Owner is not { } owner || !owner.Pending.ContainsKey((row.Identity, Field))) return;
            var identity = row.Identity; var text = Text;
            await owner.CommitCell(identity, Field, text);
        };
        TextCompositionStarted += (_, _) => { if (!Editing) editingFrom = EditOriginal(); Composing = true; Editing = true; RefreshDisplay(); };
        TextCompositionEnded += (_, _) => {
            Composing = false; endedThisTurn = true;
            // #225: the confirmed conversion is the first text the candidate list may filter on.
            if (Field == PlanField.Assignees && row.Owner is { } owner) owner.AssigneeEdited(this);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => endedThisTurn = false);
        };
        DoubleTapped += (_, args) => { if (!IsReadOnly) { BeginEditing(); args.Handled = true; } };
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) => {
            if (!Editing && row.Owner is { } owner) owner.BeginRange(row.Identity, Field, this, args);
        }), true);
    }
    private ScrollViewer? contentHost;
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        contentHost = GetTemplateChild("ContentElement") as ScrollViewer;
        ApplyContentHost();
    }
    // Title, forecast, estimate and assignee cells already paint with an overlay. The text host
    // still measures its hidden text on every refresh, so it stays collapsed until editing or an
    // IME composition needs the caret. The TextBox value, focus and input path stay.
    private void ApplyContentHost()
    {
        if (contentHost is null) return;
        if (TitleDisplay is null && ForecastDisplay is null && EstimateDisplay is null && AssigneeDisplay is null) return;
        contentHost.Visibility = Editing || Composing ? Visibility.Visible : Visibility.Collapsed;
    }
    private string EditOriginal()
    {
        if (row.Owner is not { } owner) return shownText;
        var text = (owner.IsDateField(Field) || Field is PlanField.Assignees or PlanField.Estimate or PlanField.Actual or PlanField.Remaining)
            && !owner.Pending.ContainsKey((row.Identity, Field)) ? owner.EditForm(row.Identity, Field) : shownText;
        return PlanSheetEditing.EditBaseline(owner.Rows.GetValueOrDefault(row.Identity), Field, text);
    }
    internal void BeginEditing() {
        if (IsReadOnly) return;
        AssigneeListDismissed = false;
        if (!Editing) {
            editingFrom = EditOriginal();
            // TextChanging can synchronously reenter presentation while Text is assigned.
            Editing = true;
            var owner = row.Owner;
            var editText = owner?.Pending.GetValueOrDefault((row.Identity, Field))?.Text
                ?? owner?.EditForm(row.Identity, Field) ?? editingFrom;
            Refresh(editText);
            if (owner is not null && editText != editingFrom && !owner.Pending.ContainsKey((row.Identity, Field)))
                owner.SetInput(row.Identity, Field, editText, editingFrom);
        }
        SelectAll();
        // #225: F2, a double-click and Alt+↓ open the 担当者 candidate list with the edit.
        if (Field == PlanField.Assignees) row.Owner?.AssigneeEdited(this);
    }
    internal void EndEditing()
    {
        if (Composing) return;
        Editing = false;
        if (Field == PlanField.Assignees) row.Owner?.CloseAssigneeCandidates(this);
        if (row.Owner is { } owner)
            Refresh(owner.Pending.GetValueOrDefault((row.Identity, Field))?.Text ?? owner.Display(row.Identity, Field));
    }
    internal void Rebind() { Editing = false; Composing = false; endedThisTurn = false;
        if (Field == PlanField.Assignees) { AssigneeListDismissed = false; row.Owner?.CloseAssigneeCandidates(this); }
        if (TitleDisplay is { } display) AutomationProperties.SetAutomationId(display, "");
        if (ForecastDisplay is { } forecast) AutomationProperties.SetAutomationId(forecast, "");
        if (EstimateDisplay is { } estimate) AutomationProperties.SetAutomationId(estimate, "");
        if (AssigneeNameDisplay is { } name) AutomationProperties.SetAutomationId(name, "");
        if (AssigneeCountDisplay is { } count) AutomationProperties.SetAutomationId(count, ""); }
    // #225: a chosen known person replaces the value being typed and commits as one Undo step; the list closes
    // when the commit ends the edit. Choosing the value the editor already shows ends the edit without an Undo
    // step, like returning text to its original value. A chosen person whose login equals the typed text is a
    // real change when the accepted value was another person, so it commits through the ordinary rule.
    internal void ApplyAssigneeChoice(string login)
    {
        if (row.Owner is not { } owner) return;
        var text = PlanAssigneeChoices.Compose(Text, login);
        if (text != Text) Text = text;
        if (!owner.Pending.ContainsKey((row.Identity, Field))) { EndEditing(); return; }
        _ = owner.CommitCell(row.Identity, Field, text);
    }
    // #298: a day chosen on the calendar becomes the cell's ISO edit form and commits through the ordinary cell
    // commit, so the choice is one operation and one Undo step; choosing the current date creates no edit.
    internal void ApplyDateChoice(DateOnly day)
    {
        var text = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Text != text) Text = text;
        if (row.Owner is not { } owner) { EndEditing(); return; }
        if (!owner.Pending.ContainsKey((row.Identity, Field))) { EndEditing(); return; }
        _ = owner.CommitCell(row.Identity, Field, text);
    }
    // #225: Escape closed the candidate list first (handled before this); this is the second Escape and every
    // other edit-cancel path. Native key handling and the hosted cases both enter here.
    internal void CancelOrClose()
    {
        if (row.Owner is not { } owner) { EndEditing(); return; }
        owner.CancelDrag(); owner.CancelEdit(row.Identity, Field); EndEditing();
    }
    internal void Refresh(string text)
    {
        shownText = text;
        RefreshDisplay();
        if (Text == text) return;
        refreshing = true;
        try { Text = text; }
        finally { refreshing = false; }
    }
    private void RefreshDisplay()
    {
        if (AssigneeDisplay is { } assignees) {
            Opacity = Editing ? 1 : 0;
            assignees.Visibility = !Editing && Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            assignees.Margin = Padding;
            var count = row.Owner?.Rows.GetValueOrDefault(row.Identity)?.Assignees.Length ?? 0;
            var suffix = count > 1 ? $" +{count - 1}" : "";
            // Pending input is not an accepted summary and must not acquire a count suffix.
            if (row.Owner?.Pending.ContainsKey((row.Identity, Field)) == true || !shownText.EndsWith(suffix, StringComparison.Ordinal)) suffix = "";
            AssigneeNameDisplay!.Text = suffix.Length == 0 ? shownText : shownText[..^suffix.Length];
            AssigneeCountDisplay!.Text = suffix;
            foreach (var part in new[] { AssigneeNameDisplay, AssigneeCountDisplay }) {
                part.FontSize = FontSize; part.FontFamily = FontFamily; part.FontWeight = FontWeight; part.Foreground = Foreground;
            }
        }
        if (ForecastDisplay is { } forecast) {
            Opacity = Editing ? 1 : 0;
            forecast.Visibility = Editing ? Visibility.Collapsed : Visibility.Visible;
            forecast.Margin = Padding;
            forecast.FontSize = FontSize; forecast.FontFamily = FontFamily; forecast.FontWeight = FontWeight;
            forecast.Inlines.Clear();
            if (row.Owner is { } owner && owner.Schedule.TryGetValue(row.Identity, out var task) && task.ForecastEnd is { } day) {
                forecast.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = PlanSheetView.DateText(day),
                    Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush") });
                if (owner.WorkingDaysLater(row.Identity) is { } later)
                    forecast.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " +" + later + "日",
                        Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush") });
            }
        }
        if (EstimateDisplay is { } estimate) {
            Opacity = Editing ? 1 : 0;
            estimate.Visibility = Editing ? Visibility.Collapsed : Visibility.Visible;
            estimate.Margin = Padding;
            estimate.FontSize = FontSize; estimate.FontFamily = FontFamily; estimate.FontWeight = FontWeight;
            estimate.Inlines.Clear();
            var overrun = row.Owner is { } owner && owner.Schedule.TryGetValue(row.Identity, out var task)
                ? PlanSheetText.EstimateOverrun(task, owner.Marks) : null;
            estimate.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = shownText, Foreground = Foreground });
            if (overrun is { })
                estimate.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " " + overrun,
                    Foreground = PlanSheetView.Brush("SystemFillColorCautionBrush") });
        }
        if (CalendarButton is { } calendar)
            calendar.Visibility = CalendarAllowed && !Editing && Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (TitleDisplay is { } display)
        {
            // Keep the full TextBox value, focus, hit testing and native input path intact.
            // Only its paint is hidden while the ellipsized display owns presentation.
            Opacity = Editing ? 1 : 0;
            display.Visibility = Editing ? Visibility.Collapsed : Visibility.Visible;
            display.Text = shownText; display.FontSize = FontSize; display.FontFamily = FontFamily;
            display.FontWeight = FontWeight; display.Foreground = Foreground;
            display.Margin = new(Padding.Left, Padding.Top, Padding.Right + TagReserve, Padding.Bottom);
        }
        ApplyContentHost();
    }
    protected override void OnPreviewKeyDown(KeyRoutedEventArgs args)
    {
        if (row.Owner is not { } owner) { base.OnPreviewKeyDown(args); return; }
        if (Composing || endedThisTurn) { base.OnPreviewKeyDown(args); return; }
        // #225: while the 担当者 candidate list is open, ↑/↓ move its highlight and Enter takes it. A key the
        // list does not use falls through so the cell keeps its ordinary editing behavior.
        if (Field == PlanField.Assignees && owner.AssigneePickerKey(this, args.Key)) { args.Handled = true; return; }
        var control = PlanSheetView.Down(VirtualKey.Control); var shift = PlanSheetView.Down(VirtualKey.Shift);
        if (owner.HandleOutlineShortcut(args.Key, Editing)) { args.Handled = true; }
        else if (!Editing && owner.HandleFocusShortcut(args.Key)) { args.Handled = true; }
        else if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true; CancelOrClose();
        }
        else if (control && args.Key == VirtualKey.Enter)
        {
            // #275: Ctrl+Enter writes the typed text into every selected cell; a cell not being edited stays put.
            args.Handled = true;
            if (Editing) CommitIntoSelection(owner, row.Identity, Text);
        }
        else if (!Editing && !control && args.Key == VirtualKey.Insert) { args.Handled = true; _ = owner.InsertFromKeyboard(); }
        else if (args.Key is VirtualKey.Enter or VirtualKey.Tab)
        {
            args.Handled = true;
            CommitAndNavigate(owner, row.Identity, Text, args.Key == VirtualKey.Tab, shift);
        }
        else if (!Editing && control && (args.Key is VirtualKey.Left or VirtualKey.Right) && Field == PlanField.Title
            && owner.SummaryIds.Contains(row.Identity) && !shift)
        {
            args.Handled = true;
            var identity = row.Identity; var collapse = args.Key == VirtualKey.Left;
            _ = owner.Run(() => owner.SetFold(identity, collapse), "Fold requirement");
        }
        else if (!Editing && control && args.Key is VirtualKey.Left or VirtualKey.Right)
        {
            // #228: Ctrl+←/→ is MS Project's first/last field. A parent row's title cell keeps its fold
            // meaning, so Shift+Ctrl+←/→ is what extends the selection there.
            args.Handled = true; owner.HandleEdgeKey(args.Key, true, shift);
        }
        else if (!Editing && control && !PlanSheetView.Down(VirtualKey.Menu) && args.Key == VirtualKey.F2)
        { args.Handled = true; _ = owner.LinkSelectedTasks(shift); }
        else if (!control && args.Key == VirtualKey.F2 && !IsReadOnly) { args.Handled = true; BeginEditing(); }
        else if (!IsReadOnly && !control && args.Key == VirtualKey.Down && PlanSheetView.Down(VirtualKey.Menu)
            && row.Owner?.IsDateField(Field) == true)
        {
            // #298: Alt+↓ is the keyboard route to the same calendar the selected cell's button opens.
            args.Handled = true; owner.OpenDateCalendar(this);
        }
        else if (!Editing && !IsReadOnly && Field == PlanField.Assignees && args.Key == VirtualKey.Down && PlanSheetView.Down(VirtualKey.Menu))
        {
            // #225: Alt+↓ opens the 担当者 candidate list from a selected cell without a pointer.
            args.Handled = true; owner.OpenAssigneeListFromSelection();
        }
        else if (!Editing && (args.Key == VirtualKey.F10 && shift || (int)args.Key == 93))
        {
            // The row menu from inside a cell, leaving its own right-click menu to the clipboard (#231).
            args.Handled = true;
            owner.ShowRowMenu(row.Identity, Field, row);
        }
        else if (!Editing && shift && !control && args.Key == VirtualKey.Space)
        {
            // Excel's Shift+Space takes the whole row: the anchor row when nothing is extended, otherwise
            // every row of the range the cell selection already spans (#240).
            args.Handled = true;
            owner.SelectWholeRow(row.Identity, true);
        }
        else if (!Editing && control && (args.Key is VirtualKey.C or VirtualKey.V or VirtualKey.D or VirtualKey.Z or VirtualKey.Y || PlanSheetView.Minus(args.Key)))
        { args.Handled = true; _ = owner.KeyboardCommand(args.Key); }
        else if (!Editing && args.Key == VirtualKey.Delete) { args.Handled = true; _ = owner.KeyboardCommand(args.Key); }
        else if (!Editing && args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right)
        { args.Handled = true; owner.MoveSelection(args.Key, shift); }
        base.OnPreviewKeyDown(args);
    }
    private async void CommitIntoSelection(PlanSheetView owner, string identity, string text)
    {
        committing = true;
        var input = owner.Pending.GetValueOrDefault((identity, Field));
        try { await owner.Run(async () => {
            await owner.CommitCellIntoSelection(identity, Field, text, input?.Generation ?? 0, input?.OriginalText ?? "");
            if (row.Owner == owner && row.Identity == identity && !owner.Pending.ContainsKey((identity, Field))) EndEditing();
        }); }
        finally { committing = false; }
    }
    private async void CommitAndNavigate(PlanSheetView owner, string identity, string text, bool across, bool reverse)
    {
        // Native key dispatch must finish synchronously. Never retain routed event
        // arguments or call the native base handler after a persistence await.
        committing = true;
        var input = owner.Pending.GetValueOrDefault((identity, Field));
        // An unchanged input may have no pending generation to finish in Commit.
        // Other edits end only when accepted; rejection must retain text-edit behavior.
        if (input is null) EndEditing();
        try { await owner.Run(async () => {
            await owner.CommitCellAndNavigate(identity, Field, text, input?.Generation ?? 0, input?.OriginalText ?? "", across, reverse);
            if (row.Owner == owner && row.Identity == identity && !owner.Pending.ContainsKey((identity, Field))) EndEditing();
        }); }
        finally { committing = false; }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new CellPeer(this);
    private sealed class CellPeer(PlanSheetCell cell) : TextBoxAutomationPeer(cell), ISelectionItemProvider
    {
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.SelectionItem ? this : base.GetPatternCore(pattern);
        public bool IsSelected => cell.row.Owner?.IsSelected(cell.row.Identity, cell.Field) == true;
        public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(FrameworkElementAutomationPeer.CreatePeerForElement(cell.row.Owner!.List));
        public void AddToSelection() => cell.row.Owner?.SelectAndFocus(cell.row.Identity, cell.Field, true);
        public void RemoveFromSelection() => cell.row.Owner?.SelectAndFocus(cell.row.Identity, cell.Field, false);
        public void Select() => cell.row.Owner?.SelectAndFocus(cell.row.Identity, cell.Field, false);
    }
}
// A focusable sheet selection target, with no text-input control or editing template.
internal sealed class PlanSummaryCell : ContentControl
{
    private readonly PlanSheetRow row;
    private readonly string field;
    internal TextBlock Text { get; }
    // A partial total counts the leaf tasks it leaves out in a separate caution badge after the sum, so the
    // count never reads as part of the number; the tooltip and accessible name carry the sentence.
    internal TextBlock Missing { get; } = new() { FontSize = 11, Foreground = PlanSheetView.Brush("SystemFillColorCautionBrush"),
        IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    internal Border MissingTag { get; }
    internal PlanSummaryCell(PlanSheetRow row, string field)
    {
        this.row = row; this.field = field;
        IsTabStop = true; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Text = new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new(4, 1, 4, 1), TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
            TextAlignment = field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Predecessors ? TextAlignment.Right : TextAlignment.Left };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(Text, FontNumeralAlignment.Tabular);
        AutomationProperties.SetAccessibilityView(Text, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(Missing, AccessibilityView.Raw);
        MissingTag = new Border { Child = Missing, Visibility = Visibility.Collapsed, Margin = new(0, 0, 3, 0), Padding = new(3, 0, 3, 0),
            MinWidth = 15, CornerRadius = new(8), BorderThickness = new(1), BorderBrush = PlanSheetView.Brush("SystemFillColorCautionBrush"),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var content = new Grid();
        content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.Children.Add(Text); content.Children.Add(MissingTag); Grid.SetColumn(MissingTag, 1);
        Content = content;
        GotFocus += (_, _) => {
            // GotFocus can arrive after focus has moved to another cell.
            if (FocusState != FocusState.Unfocused && row.Owner is { } owner && !owner.IsSelected(row.Identity, field))
                owner.Select(row.Identity, field, false);
        };
        PointerPressed += (_, args) => row.Owner?.BeginRange(row.Identity, field, this, args);
        DoubleTapped += (_, args) => args.Handled = true;
    }
    protected override void OnPreviewKeyDown(KeyRoutedEventArgs args)
    {
        if (row.Owner is not { } owner) { base.OnPreviewKeyDown(args); return; }
        var control = PlanSheetView.Down(VirtualKey.Control); var shift = PlanSheetView.Down(VirtualKey.Shift);
        if (owner.HandleOutlineShortcut(args.Key)) { args.Handled = true; return; }
        if (PlanSheetView.Down(VirtualKey.Menu)) { base.OnPreviewKeyDown(args); return; }
        if (owner.HandleFocusShortcut(args.Key)) { args.Handled = true; }
        else if (control && args.Key is VirtualKey.Left or VirtualKey.Right) {
            // #228: Ctrl+←/→ is MS Project's first/last field, the same as on a task cell.
            args.Handled = true; owner.HandleEdgeKey(args.Key, true, shift);
        }
        else if (control && args.Key == VirtualKey.F2) { args.Handled = true; _ = owner.LinkSelectedTasks(shift); }
        else if (!control && args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right) {
            args.Handled = true; owner.MoveSelection(args.Key, shift);
        }
        else if (!control && args.Key is VirtualKey.Tab or VirtualKey.Enter) {
            args.Handled = true; owner.NavigateCell(field, args.Key == VirtualKey.Tab, shift);
        }
        else if (!control && args.Key == VirtualKey.Insert) { args.Handled = true; _ = owner.InsertFromKeyboard(); }
        else if (!control && args.Key == VirtualKey.Delete || control && (args.Key is VirtualKey.C or VirtualKey.V or VirtualKey.D or VirtualKey.Z or VirtualKey.Y || PlanSheetView.Minus(args.Key))) {
            args.Handled = true; _ = owner.KeyboardCommand(args.Key);
        }
        else if (!control && args.Key == VirtualKey.Escape) { args.Handled = true; owner.CancelDrag(); }
        else if (args.Key == VirtualKey.F10 && shift || (int)args.Key == 93) {
            // A requirement row opens the same row menu from the keyboard (#231).
            args.Handled = true; owner.ShowRowMenu(row.Identity, field, row);
        }
        else if (!control && shift && args.Key == VirtualKey.Space) {
            // A requirement row takes its whole row the same way an ordinary cell does (#240).
            args.Handled = true; owner.SelectWholeRow(row.Identity, true);
        }
        else if (!control && (args.Key == VirtualKey.F2 || args.Key == VirtualKey.Space
            || args.Key is >= VirtualKey.Number0 and <= VirtualKey.Z
            || args.Key is >= VirtualKey.NumberPad0 and <= VirtualKey.Divide
            || (int)args.Key is >= 186 and <= 192 or >= 219 and <= 223 or 226)) args.Handled = true;
        base.OnPreviewKeyDown(args);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new SummaryPeer(this);
    private sealed class SummaryPeer(PlanSummaryCell cell) : FrameworkElementAutomationPeer(cell), ISelectionItemProvider
    {
        protected override string GetClassNameCore() => nameof(PlanSummaryCell);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.SelectionItem ? this : base.GetPatternCore(pattern);
        public bool IsSelected => cell.row.Owner?.IsSelected(cell.row.Identity, cell.field) == true;
        public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(CreatePeerForElement(cell.row.Owner!.List));
        public void AddToSelection() => cell.row.Owner?.SelectAndFocus(cell.row.Identity, cell.field, true);
        public void RemoveFromSelection() => cell.row.Owner?.SelectAndFocus(cell.row.Identity, cell.field, false);
        public void Select() => cell.row.Owner?.SelectAndFocus(cell.row.Identity, cell.field, false);
    }
}
internal sealed class PlanFillHandle(PlanSheetRow row, string field) : Button
{
    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        row.Owner?.BeginFill(row.Identity, field, this, args);
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs args)
    {
        row.Owner?.EndDrag(this, args);
    }
}
internal static class PlanListIndex
{
    internal static int IndexOf<T>(this IReadOnlyList<T> values, T value)
    {
        for (var i = 0; i < values.Count; i++) if (EqualityComparer<T>.Default.Equals(values[i], value)) return i;
        return -1;
    }
}

// Pointer gestures belong to the sheet; Button still supplies keyboard and automation invocation.
internal sealed class PlanRowHandle : Button
{
    private readonly PlanSheetRow row;
    internal PlanRowHandle(PlanSheetRow row)
    {
        this.row = row;
        Padding = new(0); MinWidth = MinHeight = 0;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        BorderThickness = new(0); Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
    protected override void OnPointerPressed(PointerRoutedEventArgs args)
        => row.Owner?.BeginRowMove(row.Identity, this, args);
    protected override void OnPointerReleased(PointerRoutedEventArgs args)
        => row.Owner?.EndDrag(this, args);
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs args)
    {
        row.Owner?.CancelDrag(); base.OnPointerCaptureLost(args);
    }
    protected override void OnPointerCanceled(PointerRoutedEventArgs args)
    {
        row.Owner?.CancelDrag(); base.OnPointerCanceled(args);
    }
}

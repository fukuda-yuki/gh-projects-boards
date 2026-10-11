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
    internal string Identity => DataContext as string ?? "";
    internal PlanSheetCell[] Cells { get; }
    private readonly Grid sheetClip = new(), chartClip = new();
    private readonly RectangleGeometry sheetGeometry = new(), chartGeometry = new();
    private readonly StackPanel line = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform lineTranslation = new();
    private readonly TranslateTransform chartTranslation = new();
    private readonly Border chartSeparator = new() { BorderThickness = new(0, 0, 0, 1), BorderBrush = PlanSheetView.Brush("SheetSeparatorBrush"), IsHitTestVisible = false };
    private readonly Canvas chart = new() { Height = 28 };
    private readonly Canvas dependencyChart = new(), taskChart = new();
    private readonly TextBlock id = new() { FontSize = 13, TextAlignment = TextAlignment.Right, Padding = new(4, 3, 8, 0) };
    private readonly Grid idCell = new();
    private readonly Border selectionAccent = new() { Width = 3, Height = 16, CornerRadius = new(1.5),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly FontIcon indicator = new() { FontSize = 13, Width = 28, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = true };
    private readonly List<Polygon?> corners = [];
    private readonly List<Border> frames = [];
    private readonly List<TextBlock?> markers = [];
    private readonly List<Button?> handles = [];
    private readonly Button fold = new() { Width = 20, MinWidth = 0, MinHeight = 0, Padding = new(0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<PlanField, PlanSummaryCell> summaryCells = [];
    internal Control? SelectionTarget(PlanField field) => summaryCells.TryGetValue(field, out var display) && display.Visibility == Visibility.Visible
        ? display : Cells.FirstOrDefault(c => c.Field == field);
    private string boundIdentity = "";
    private double chartOffset = double.NaN;
    private sealed record ChartPresentation(string Identity, int Number, int RowIndex, DateOnly? Start, DateOnly? End,
        bool Summary, bool Milestone, decimal? CompleteFraction, PlanTaskLateness? Lateness, DateOnly? PublishedEnd,
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
    private NativePath? crossingPath;
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
        Children.Add(sheetClip); Children.Add(chartClip); SetColumn(chartClip, 1);
        idCell.Children.Add(id); idCell.Children.Add(selectionAccent);
        line.Children.Add(idCell); line.Children.Add(indicator);
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(id, FontNumeralAlignment.Tabular);
        Cells = PlanSheetView.Columns.Where(c => c.Field is not null).Select(c => new PlanSheetCell(this, c.Field!.Value)).ToArray();
        foreach (var cell in Cells)
        {
            var grid = new Grid(); grid.Children.Add(cell);
            if (cell.TitleDisplay is { } display) grid.Children.Add(display);
            if (cell.Field == PlanField.Title) grid.Children.Add(fold);
            var frame = new Border { Child = grid, BorderThickness = new(0, 0, 0, 1) };
            line.Children.Add(frame); frames.Add(frame); markers.Add(null); corners.Add(null); handles.Add(null);
        }
        fold.Click += async (_, _) => {
            if (Owner is not { } owner) return;
            var identity = Identity;
            await owner.Run(() => owner.SetFold(identity, !owner.IsFolded(identity)), "Fold requirement");
        };
        DataContextChanged += (_, _) => { ClearIds(); Refresh(); Owner?.UpdateInputProblem(); };
    }
    internal void SetOwner(PlanSheetView? owner, string? expectedIdentity)
    {
        if (ReferenceEquals(Owner, owner)) return;
        // A recycled container can stay loaded while its task and visual parent change.
        // The ListView's content/recycle notification owns this lifetime.
        var previous = Owner;
        previous?.Realized.Remove(this);
        Owner = owner;
        if (owner is not null) {
            owner.Realized.Add(this);
            // The native content notification precedes DataContext assignment. A
            // new binding paints in DataContextChanged; a same-item reattach paints now.
            if (DataContext is string identity && identity == expectedIdentity) Refresh();
        }
        previous?.UpdateInputProblem(); owner?.UpdateInputProblem();
    }
    private void ClearIds()
    {
        AutomationProperties.SetAutomationId(id, ""); AutomationProperties.SetAutomationId(indicator, "");
        AutomationProperties.SetAutomationId(selectionAccent, "");
        foreach (var cell in Cells) { AutomationProperties.SetAutomationId(cell, ""); cell.Rebind(); }
        foreach (var display in summaryCells.Values) AutomationProperties.SetAutomationId(display, "");
        foreach (var handle in handles) if (handle is not null) AutomationProperties.SetAutomationId(handle, "");
        AutomationProperties.SetAutomationId(fold, "");
        // A recycled row keeps native dependency geometry, but none of the old
        // task's paint or automation identity may escape before rebinding finishes.
        chart.Opacity = 0;
        taskChart.Children.Clear(); chartPresentation = null;
        foreach (var visual in dependencyVisuals.Values) {
            AutomationProperties.SetAutomationId(visual.Line, ""); visual.LineId = "";
            if (visual.Head is { } head) AutomationProperties.SetAutomationId(head, "");
            visual.HeadId = "";
        }
        if (crossingPath is not null) AutomationProperties.SetAutomationId(crossingPath, "");
        crossingId = "";
    }
    internal void Refresh()
    {
        if (Owner is not { } owner) return;
        if (boundIdentity != Identity) { foreach (var cell in Cells) cell.Rebind(); boundIdentity = Identity; }
        Height = chart.Height = owner.RowHeight;
        var number = owner.PlanIds.GetValueOrDefault(Identity);
        ColumnDefinitions[0].Width = new(owner.SheetViewport); ColumnDefinitions[1].Width = new(owner.ChartViewport);
        Width = owner.SheetViewport + owner.ChartViewport;
        var sheetBounds = new Rect(0, 0, owner.SheetViewport, owner.RowHeight);
        var chartBounds = new Rect(0, 0, owner.ChartViewport, owner.RowHeight);
        if (sheetGeometry.Rect != sheetBounds) sheetGeometry.Rect = sheetBounds;
        if (chartGeometry.Rect != chartBounds) chartGeometry.Rect = chartBounds;
        lineTranslation.X = -owner.SheetOffset;
        idCell.Width = PlanSheetView.Columns[0].Width; idCell.Visibility = owner.Hidden.Contains(null) ? Visibility.Collapsed : Visibility.Visible;
        selectionAccent.Background = PlanSheetView.Brush("SheetSelectionStrokeBrush");
        selectionAccent.Visibility = owner.IsSelectedRow(Identity) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(selectionAccent, "PlanSelectionAccent" + number);
        var warnings = owner.Schedule.GetValueOrDefault(Identity)?.Warnings ?? [];
        id.Text = number == 0 ? "+" : number.ToString();
        var remoteProblem = owner.RemoteProblem(Identity);
        ToolTipService.SetToolTip(id, string.Join("\n", warnings) + remoteProblem);
        AutomationProperties.SetName(id, number == 0 ? "新しいタスク" : $"ID {number}" + (warnings.Count > 0 ? " " + string.Join(" / ", warnings) : ""));
        AutomationProperties.SetAutomationId(id, "PlanRowId" + number);
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
            var cell = Cells[i]; var column = PlanSheetView.Columns[i + 2]; var field = cell.Field;
            var visible = owner.Hidden.Contains(field) ? Visibility.Collapsed : Visibility.Visible;
            frames[i].Width = column.Width; frames[i].Visibility = visible;
            cell.Visibility = summary && field != PlanField.Title ? Visibility.Collapsed : visible;
            var selected = owner.IsSelected(Identity, field);
            var conflict = owner.BlockingConflicts.Any(c => c.Identity == Identity && c.Field == field);
            frames[i].BorderBrush = PlanSheetView.Brush(conflict ? "SystemFillColorCriticalBrush" : selected ? "SheetSelectionStrokeBrush" : "SheetSeparatorBrush");
            frames[i].BorderThickness = selected || conflict ? new(2) : new(0, 0, 0, 1);
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
                outcomeMarker.Text = owner.Session.Document.Sync.Unverified.Contains(Identity) ? "未検証" : "失敗";
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
            cell.IsReadOnly = owner.ReadOnly(Identity, field);
            var showHandle = owner.IsRangeEnd(Identity, field) && !cell.Editing && !cell.IsReadOnly && owner.Display(Identity, field).Length > 0 && !owner.Pending.ContainsKey((Identity, field));
            if (showHandle && handles[i] is null) {
                var handle = new PlanFillHandle(this, field) { Width = 8, Height = 8, MinHeight = 0, MinWidth = 0, Padding = new(0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsTabStop = false };
                AutomationProperties.SetName(handle, "選択範囲へコピー"); ToolTipService.SetToolTip(handle, "上下にドラッグしてコピー");
                handle.Click += async (_, _) => { if (Owner is { } current) await current.Run(() => current.Fill(PlanOperationKind.Fill)); };
                handles[i] = handle; ((Grid)frames[i].Child).Children.Add(handle);
            }
            if (handles[i] is { } fillHandle) {
                fillHandle.Visibility = showHandle ? Visibility.Visible : Visibility.Collapsed;
                fillHandle.Background = PlanSheetView.Brush("SheetSelectionStrokeBrush");
                AutomationProperties.SetAutomationId(fillHandle, $"PlanFillHandle{number}_{field}");
            }
            cell.FontStyle = Windows.UI.Text.FontStyle.Normal;
            cell.Foreground = PlanSheetView.Brush(field == PlanField.End && late?.Level == PlanLatenessLevel.Overdue
                && owner.CellDate(Identity, field) > owner.PublishedEnd(Identity) ? "SystemFillColorCriticalBrush"
                : owner.IsCalculated(Identity, field) ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
            cell.FontWeight = owner.Schedule.GetValueOrDefault(Identity)?.IsSummary == true ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            cell.MinHeight = cell.Height = owner.RowHeight - (selected || conflict ? 4 : 1);
            cell.Padding = field == PlanField.Title ? new(24 + depth * 12, 1, 4, 1) : new(4, 1, 4, 1);
            var problem = owner.Problems.GetValueOrDefault((Identity, field)) ?? (remoteProblem.Length > 0 ? remoteProblem : null);
            var text = owner.Pending.GetValueOrDefault((Identity, field))?.Text ?? (cell.Editing ? owner.EditForm(Identity, field) : owner.Display(Identity, field));
            cell.Refresh(text);
            if (cell.TitleDisplay is { } titleDisplay) AutomationProperties.SetAutomationId(titleDisplay, "PlanTitleDisplay" + number);
            AutomationProperties.SetAutomationId(cell, $"PlanCell{number}_{field}");
            AutomationProperties.SetName(cell, $"ID {(number == 0 ? "新規" : number)} {owner.Header(column)}" + (owner.CellDate(Identity, field) is { } day ? " " + PlanSheetView.DateText(day, true) : ""));
            AutomationProperties.SetHelpText(cell, conflict ? "競合" + (problem is null ? "" : " · " + problem) : problem ?? (changed ? "未発行" : owner.IsCalculated(Identity, field) ? "計算値" : ""));
            ToolTipService.SetToolTip(cell, problem ?? (owner.CellDate(Identity, field) is { } date ? PlanSheetView.DateText(date, true)
                : field == PlanField.Assignees ? owner.AssigneeTooltip(Identity) : text));
            if (summary && field != PlanField.Title) {
                if (!summaryCells.TryGetValue(field, out var display)) {
                    display = new PlanSummaryCell(this, field);
                    summaryCells.Add(field, display);
                    ((Grid)frames[i].Child).Children.Insert(1, display);
                }
                AutomationProperties.SetAutomationId(cell, "");
                display.Visibility = visible;
                display.Height = cell.Height;
                display.Text.Text = owner.Display(Identity, field);
                display.Text.Foreground = PlanSheetView.Brush(field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Start or PlanField.End
                    ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
                AutomationProperties.SetAutomationId(display, $"PlanCell{number}_{field}");
                var accessibleValue = field is PlanField.Start or PlanField.End && owner.CellDate(Identity, field) is { } accessibleDate
                    ? PlanSheetView.DateText(accessibleDate, true) : display.Text.Text;
                AutomationProperties.SetName(display, $"ID {number} {owner.Header(column)} {accessibleValue} · {PlanOperations.SummaryReadOnlyReason(field)}");
                var fullValue = field == PlanField.Assignees ? owner.AssigneeTooltip(Identity) : accessibleValue;
                AutomationProperties.SetHelpText(display, string.Join(" · ", new[] { fullValue, AutomationProperties.GetHelpText(cell) }.Where(value => value.Length > 0)));
                ToolTipService.SetToolTip(display, problem ?? fullValue);
            } else if (summaryCells.TryGetValue(field, out var display)) {
                display.Visibility = Visibility.Collapsed;
                AutomationProperties.SetAutomationId(display, "");
            }
            if (problem is not null) frames[i].BorderBrush = PlanSheetView.Brush("SystemFillColorCriticalBrush");
        }
        var states = new List<string>();
        var taskRow = owner.Rows.GetValueOrDefault(Identity);
        var done = taskRow?.Closed == true;
        var effort = owner.Schedule.GetValueOrDefault(Identity);
        var typed = taskRow?.StartNoEarlierThan is not null || taskRow?.Fixed == true;
        var unpublished = owner.Unpublished.Fields.ContainsKey(Identity);
        if (remoteProblem.Length > 0) states.Add(remoteProblem);
        if (late?.MissedPublishedDate is { } missed) {
            var date = missed.ToString("M/d", CultureInfo.InvariantCulture);
            var remaining = effort?.Remaining;
            var remainingText = remaining is { } work
                ? $"（残 {work.ToString("0.############################", CultureInfo.InvariantCulture)}h）" : "";
            states.Add(late.OverdueKind == PlanOverdueKind.Finish
                ? $"期限超過: 完了予定 {date} を過ぎて未完了{remainingText}"
                : $"期限超過: 開始予定 {date} を過ぎて未着手");
        }
        if (late?.DaysLater is { } days && owner.PublishedEnd(Identity) is { } published)
            states.Add($"予定より遅れ: 発行済み {published.ToString("M/d", CultureInfo.InvariantCulture)} から +{days} 日");
        if (summary && late is not null) {
            var descendants = new List<string>();
            if (late.OverdueDescendantTasks > 0) descendants.Add($"期限超過 {late.OverdueDescendantTasks}");
            if (late.LaterDescendantTasks > 0) descendants.Add($"予定より遅れ {late.LaterDescendantTasks}");
            if (descendants.Count > 0) states.Add("配下に" + string.Join("、", descendants));
        }
        if (effort?.IsOverEstimate == true)
            states.Add(effort.Forecast is null ? "見積超過: 見込・差異が数値の範囲外です" : $"見積超過: 見込 {effort.Forecast:0.############################}h（見積 {effort.Estimate:0.############################}h、差異 {effort.Variance:+0.############################;-0.############################;0}h）");
        if (effort?.IsZeroRemainingOpen == true) states.Add("残 0・未完了: 見込み残存工数が 0 ですが Issue は閉じていません");
        if (done) states.Add("完了");
        if (typed) states.Add(taskRow?.Fixed == true ? "日程固定" : "開始日を指定");
        if (unpublished) states.Add("未発行の変更あり");
        indicator.Glyph = remoteProblem.Length > 0 ? "\uEA39" : late?.Level == PlanLatenessLevel.Overdue ? "\uE814"
            : late?.Level == PlanLatenessLevel.Later ? "\uE7BA" : effort?.IsOverEstimate == true || effort?.IsZeroRemainingOpen == true ? "\uE7BA" : done ? "\uE73E" : typed ? "\uE718" : unpublished ? "\u2022" : "";
        indicator.FontFamily = new FontFamily(indicator.Glyph == "\u2022" ? "Segoe UI" : "Segoe Fluent Icons");
        indicator.Foreground = PlanSheetView.Brush(remoteProblem.Length > 0 || late?.Level is PlanLatenessLevel.Overdue or PlanLatenessLevel.Later ? "SystemFillColorCriticalBrush" : done ? "IndicatorDoneBrush" : typed ? "IndicatorTypedBrush" : "SheetChangedMarkBrush");
        indicator.Visibility = owner.IndicatorVisible ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(indicator, "PlanIndicator" + number);
        AutomationProperties.SetName(indicator, string.Join("、", states)); ToolTipService.SetToolTip(indicator, string.Join("、", states));
        Background = owner.IsSelectedRow(Identity) ? PlanSheetView.Brush("SheetSelectionBrush") : null;
        id.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        id.FontWeight = summary ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        DrawChart();
    }
    internal void RefreshHorizontal(bool sheet, bool gantt)
    {
        if (Owner is not { } owner) return;
        if (sheet && lineTranslation.X != -owner.SheetOffset) lineTranslation.X = -owner.SheetOffset;
        if (gantt && chartOffset != owner.ChartOffset) {
            RefreshChartOffset(owner);
            if (DataContext is string) RefreshCrossings(owner, owner.PlanIds.GetValueOrDefault(Identity));
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
        AutomationProperties.SetAutomationId(chartSeparator, Owner is { } view ? "PlanChartSeparator" + view.PlanIds.GetValueOrDefault(Identity) : "");
        // Native recycling can unbind before supplying the next identity. Keep
        // that row's bounded geometry hidden through the intermediate state.
        if (DataContext is not string) { chart.Opacity = 0; return; }
        if (Owner is not { } owner || !owner.Schedule.TryGetValue(Identity, out var task)) {
            dependencyChart.Children.Clear(); dependencyVisuals.Clear(); taskChart.Children.Clear();
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
            task.IsSummary, task.IsMilestone, completeFraction, owner.LatenessOf(Identity), owner.PublishedEnd(Identity),
            owner.ShowLatenessLabels, owner.FirstDay, owner.ChartDateInset, owner.DayWidth, owner.ChartViewport, owner.RowHeight,
            ActualTheme, PlanSheetView.Brush("GanttTaskBrush"));
        if (presentation != chartPresentation) {
            chartPresentation = presentation;
            taskChart.Children.Clear(); DrawTask(owner, number, task);
        }
        chart.Opacity = 1;
    }
    private void DrawDependencies(PlanSheetView owner, int number, int rowIndex)
    {
        var current = new HashSet<(string From, string To)>();
        var crossings = new HashSet<double>();
        var brush = PlanSheetView.Brush("GanttArrowBrush");
        foreach (var edge in owner.Edges)
        {
            if (rowIndex < Math.Min(edge.From, edge.To) || rowIndex > Math.Max(edge.From, edge.To)) continue;
            var successor = owner.Schedule[owner.RowIds[edge.To]];
            var successorMilestone = successor.IsMilestone && successor.Start.Value == successor.End.Value;
            var predecessor = owner.Schedule[owner.RowIds[edge.From]];
            var predecessorMilestone = predecessor.IsMilestone && predecessor.Start.Value == predecessor.End.Value;
            var predecessorStart = predecessor.Start.Value ?? edge.End;
            var predecessorWidth = predecessor.Start.Value is null ? 8 : predecessorMilestone ? 10 : Math.Max(2, (edge.End.DayNumber - predecessorStart.DayNumber + 1) * owner.DayWidth);
            var x1 = ChartX(owner, predecessorStart) + predecessorWidth; var x2 = ChartX(owner, edge.Start);
            var successorWidth = successorMilestone ? 10 : Math.Max(2, ((successor.End.Value ?? edge.Start).DayNumber - edge.Start.DayNumber + 1) * owner.DayWidth);
            var inset = Math.Min(3, successorWidth / 2);
            // Clamp locally: adding a large date coordinate can invert equal bounds through rounding.
            var entry = x2 + Math.Clamp(Math.Max(x1 - x2 + 5, 5), inset, successorWidth - inset);
            // Between endpoints only the vertical segment enters this row's clip.
            // Share coincident strokes instead of creating a native shape per link.
            if (rowIndex != edge.From && rowIndex != edge.To) { crossings.Add(entry); continue; }
            var key = (owner.RowIds[edge.From], owner.RowIds[edge.To]);
            current.Add(key);
            if (!dependencyVisuals.TryGetValue(key, out var visual)) {
                visual = new DependencyVisual(); dependencyVisuals.Add(key, visual);
                dependencyChart.Children.Add(visual.Line); DependencyShapesCreated++;
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
            var visual = dependencyVisuals[key];
            dependencyChart.Children.Remove(visual.Line);
            if (visual.Head is { } head) dependencyChart.Children.Remove(head);
            dependencyVisuals.Remove(key);
        }
        crossingXs = crossings.Order().ToArray();
        if (!ReferenceEquals(crossingBrush, brush)) {
            crossingBrush = brush;
            if (crossingPath is not null) crossingPath.Stroke = brush;
        }
        RefreshCrossings(owner, number);
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
        if (crossingHeight != owner.RowHeight || !visibleCrossingXs.SequenceEqual(visible)) {
            var origin = visible[0];
            var geometry = new PathGeometry();
            foreach (var x in visible) {
                var figure = new PathFigure { StartPoint = new(x - origin, -2), IsClosed = false, IsFilled = false };
                figure.Segments.Add(new LineSegment { Point = new(x - origin, owner.RowHeight + 2) });
                geometry.Figures.Add(figure);
            }
            crossingPath.Data = geometry; Canvas.SetLeft(crossingPath, origin);
            visibleCrossingXs = visible; crossingHeight = owner.RowHeight; DependencyPointsUpdated++;
        }
        crossingPath.Visibility = Visibility.Visible;
        var id = "PlanArrowCrossings" + number;
        if (crossingId != id) { AutomationProperties.SetAutomationId(crossingPath, id); crossingId = id; }
    }
    private void DrawTask(PlanSheetView owner, int number, ScheduledTask task)
    {
        if (task.Start.Value is not { } start || task.End.Value is not { } end)
        {
            if ((task.Start.Value ?? task.End.Value) is { } known)
            {
                var endpoint = new Polygon { Width = 8, Height = 10,
                    Points = [new(0, 0), new(8, 5), new(0, 10)],
                    Fill = PlanSheetView.Brush("TextFillColorSecondaryBrush") };
                Canvas.SetLeft(endpoint, ChartX(owner, known)); Canvas.SetTop(endpoint, (owner.RowHeight - 10) / 2);
                AutomationProperties.SetAutomationId(endpoint, "PlanEndpoint" + number);
                AutomationProperties.SetName(endpoint, $"ID {number} {(task.Start.Value is not null ? "開始" : "終了")} {known:yyyy-MM-dd}");
                taskChart.Children.Add(endpoint);
            }
            return;
        }
        if (end < start) return;
        var x = ChartX(owner, start); var width = Math.Max(2, (end.DayNumber - start.DayNumber + 1) * owner.DayWidth);
        var milestone = task.IsMilestone && start == end;
        decimal? share = !task.IsSummary && task.Input.IsComplete ? 1m : task.WorkCompleteFraction;
        Shape bar;
        if (task.IsSummary)
            bar = new Polygon { Points = [new(0, 0), new(width, 0), new(width, 10), new(Math.Max(0, width - 4), 6), new(Math.Min(4, width), 6), new(0, 10)],
                Fill = PlanSheetView.Brush("GanttSummaryBrush"), Width = width, Height = 10 };
        else if (milestone)
            bar = new Polygon { Points = [new(5, 0), new(10, 5), new(5, 10), new(0, 5)], Width = 10, Height = 10,
                Fill = PlanSheetView.Brush("GanttTaskBrush") };
        else bar = new Rectangle { Width = width, Height = 14, RadiusX = 3, RadiusY = 3,
            Fill = PlanSheetView.Brush("GanttTaskTintBrush"), Stroke = PlanSheetView.Brush("GanttTaskBrush"), StrokeThickness = 1 };
        Canvas.SetLeft(bar, x); Canvas.SetTop(bar, (owner.RowHeight - (task.IsSummary ? 6 : bar.Height)) / 2);
        var lateness = owner.LatenessOf(Identity);
        var late = lateness?.DaysLater;
        var overdue = lateness?.Level == PlanLatenessLevel.Overdue;
        AutomationProperties.SetAutomationId(bar, "PlanBar" + number);
        AutomationProperties.SetName(bar, $"ID {number} {start:yyyy-MM-dd} – {end:yyyy-MM-dd}" + (task.IsSummary ? " 集計" : milestone ? " マイルストーン" : "")
            + (share is { } percent ? $" 完了 {percent * 100:0}%" : "") + (late is { } days ? $" {(overdue ? "期限超過" : "予定より遅れ")} +{days} 日" : ""));
        ToolTipService.SetToolTip(bar, AutomationProperties.GetName(bar));
        taskChart.Children.Add(bar);
        if (!task.IsSummary && !milestone && share is > 0) {
            var progress = PlanSheetView.Id(new Rectangle { IsHitTestVisible = false, Width = width * (double)share.Value, Height = 14, RadiusX = 3, RadiusY = 3,
                Fill = PlanSheetView.Brush("GanttTaskBrush") }, "PlanProgress" + number);
            Canvas.SetLeft(progress, x); Canvas.SetTop(progress, (owner.RowHeight - 14) / 2); taskChart.Children.Add(progress);
        }
        if (!milestone && late is { } count && owner.PublishedEnd(Identity) is { } published) {
            var lateX = Math.Max(x, ChartX(owner, published) + owner.DayWidth);
            var lateWidth = x + width - lateX;
            if (lateWidth <= 0) return;
            Shape segment = task.IsSummary
                ? new Polygon { Width = lateWidth, Height = 10, Points = [new(0, 0), new(lateWidth, 0), new(lateWidth, 10), new(Math.Max(0, lateWidth - 4), 6), new(0, 6)] }
                : new Rectangle { Width = lateWidth, Height = 14, RadiusX = 3, RadiusY = 3 };
            segment.IsHitTestVisible = false;
            segment.Fill = overdue ? PlanSheetView.Brush("GanttLateTintBrush") : null; segment.Stroke = PlanSheetView.Brush("GanttLateBrush");
            segment.StrokeThickness = 1; segment.StrokeDashArray = [3, 2];
            AutomationProperties.SetAutomationId(segment, "PlanLate" + number);
            Canvas.SetLeft(segment, lateX); Canvas.SetTop(segment, task.IsSummary ? Canvas.GetTop(bar) : (owner.RowHeight - 14) / 2); taskChart.Children.Add(segment);
            if (!overdue || !owner.ShowLatenessLabels) return;
            var label = PlanSheetView.Id(new TextBlock { Text = $"+{count}日", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = PlanSheetView.Brush("GanttLateBrush") }, "PlanLateLabel" + number);
            Canvas.SetLeft(label, x + width + 6); Canvas.SetTop(label, (owner.RowHeight - 16) / 2); taskChart.Children.Add(label);
        }
    }
}

internal sealed class PlanSheetCell : TextBox
{
    private readonly PlanSheetRow row;
    internal PlanField Field { get; }
    internal bool Composing { get; private set; }
    internal bool Editing { get; private set; }
    internal TextBlock? TitleDisplay { get; }
    private bool refreshing, endedThisTurn, committing;
    private string shownText = "", editingFrom = "";
    internal PlanSheetCell(PlanSheetRow row, PlanField field)
    {
        this.row = row; Field = field;
        if (field == PlanField.Title) {
            TitleDisplay = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(TitleDisplay, AccessibilityView.Raw);
        }
        MinWidth = 0; MinHeight = 26; Height = 26; Padding = new(4, 2, 4, 2); FontSize = 13; BorderThickness = new(0); Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Predecessors) TextAlignment = TextAlignment.Right;
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        GotFocus += (_, _) => { if (row.Owner is { } owner) { if (!owner.IsSelected(row.Identity, Field)) owner.Select(row.Identity, Field, false); owner.UpdateInputProblem(); } if (!Editing) SelectAll(); };
        TextChanging += (_, _) => {
            // WinUI may deliver a programmatic text notification after Refresh returns.
            if (refreshing || Text == shownText || row.Owner is not { } owner || IsReadOnly) return;
            if (!Editing) editingFrom = EditOriginal();
            Editing = true; owner.SetInput(row.Identity, Field, Text, editingFrom);
            shownText = Text;
            RefreshTitleDisplay();
        };
        LostFocus += async (_, _) => {
            if (Composing) return;
            EndEditing();
            if (committing || !IsLoaded || row.Owner is not { } owner || !owner.Pending.ContainsKey((row.Identity, Field))) return;
            var identity = row.Identity; var text = Text;
            await owner.CommitCell(identity, Field, text);
        };
        TextCompositionStarted += (_, _) => { if (!Editing) editingFrom = EditOriginal(); Composing = true; Editing = true; RefreshTitleDisplay(); };
        TextCompositionEnded += (_, _) => {
            Composing = false; endedThisTurn = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => endedThisTurn = false);
        };
        DoubleTapped += (_, args) => { if (!IsReadOnly) { BeginEditing(); args.Handled = true; } };
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) => {
            if (!Editing && row.Owner is { } owner) owner.BeginRange(row.Identity, Field, this, args);
        }), true);
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("DeleteButton") is Button button) {
            // Sheet Clear already owns deletion; the narrow editor needs the entire content width.
            // ButtonVisible can change Visibility, so also keep its layout width at zero.
            button.Visibility = Visibility.Collapsed;
            button.MinWidth = button.Width = button.MaxWidth = 0;
            button.IsHitTestVisible = false;
        }
    }
    private string EditOriginal()
    {
        if (row.Owner is not { } owner) return shownText;
        var text = Field is PlanField.Start or PlanField.End or PlanField.StartNoEarlierThan or PlanField.Assignees
            && !owner.Pending.ContainsKey((row.Identity, Field)) ? owner.EditForm(row.Identity, Field) : shownText;
        return PlanSheetEditing.EditBaseline(owner.Rows.GetValueOrDefault(row.Identity), Field, text);
    }
    internal void BeginEditing() {
        if (IsReadOnly) return;
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
    }
    internal void EndEditing()
    {
        if (Composing) return;
        Editing = false;
        if (row.Owner is { } owner)
            Refresh(owner.Pending.GetValueOrDefault((row.Identity, Field))?.Text ?? owner.Display(row.Identity, Field));
    }
    internal void Rebind() { Editing = false; Composing = false; endedThisTurn = false;
        if (TitleDisplay is { } display) AutomationProperties.SetAutomationId(display, ""); }
    internal void Refresh(string text)
    {
        shownText = text;
        RefreshTitleDisplay();
        if (Text == text) return;
        refreshing = true;
        try { Text = text; }
        finally { refreshing = false; }
    }
    private void RefreshTitleDisplay()
    {
        if (TitleDisplay is not { } display) return;
        // Keep the full TextBox value, focus, hit testing and native input path intact.
        // Only its paint is hidden while the ellipsized display owns presentation.
        Opacity = Editing ? 1 : 0;
        display.Visibility = Editing ? Visibility.Collapsed : Visibility.Visible;
        display.Text = shownText; display.FontSize = FontSize; display.FontFamily = FontFamily;
        display.FontWeight = FontWeight; display.Foreground = Foreground; display.Margin = Padding;
    }
    protected override void OnPreviewKeyDown(KeyRoutedEventArgs args)
    {
        if (row.Owner is not { } owner) { base.OnPreviewKeyDown(args); return; }
        if (Composing || endedThisTurn) { base.OnPreviewKeyDown(args); return; }
        var control = PlanSheetView.Down(VirtualKey.Control); var shift = PlanSheetView.Down(VirtualKey.Shift);
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true; owner.CancelDrag(); owner.CancelEdit(row.Identity, Field); EndEditing();
        }
        else if (args.Key is VirtualKey.Enter or VirtualKey.Tab)
        {
            args.Handled = true;
            CommitAndNavigate(owner, row.Identity, Text, args.Key == VirtualKey.Tab, shift);
        }
        else if (!Editing && control && Field == PlanField.Title && owner.SummaryIds.Contains(row.Identity) && args.Key is VirtualKey.Left or VirtualKey.Right)
        {
            args.Handled = true;
            var identity = row.Identity; var collapse = args.Key == VirtualKey.Left;
            _ = owner.Run(() => owner.SetFold(identity, collapse), "Fold requirement");
        }
        else if (args.Key == VirtualKey.F2 && !IsReadOnly) { args.Handled = true; BeginEditing(); }
        else if (!Editing && control && args.Key is VirtualKey.C or VirtualKey.V or VirtualKey.D or VirtualKey.Z or VirtualKey.Y)
        { args.Handled = true; _ = owner.KeyboardCommand(args.Key); }
        else if (!Editing && args.Key == VirtualKey.Delete) { args.Handled = true; _ = owner.KeyboardCommand(args.Key); }
        else if (!Editing && args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right)
        { args.Handled = true; owner.MoveSelection(args.Key, shift); }
        base.OnPreviewKeyDown(args);
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
    private readonly PlanField field;
    internal TextBlock Text { get; }
    internal PlanSummaryCell(PlanSheetRow row, PlanField field)
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
        Content = Text;
        GotFocus += (_, _) => {
            if (row.Owner is { } owner && !owner.IsSelected(row.Identity, field)) owner.Select(row.Identity, field, false);
        };
        PointerPressed += (_, args) => row.Owner?.BeginRange(row.Identity, field, this, args);
        DoubleTapped += (_, args) => args.Handled = true;
    }
    protected override void OnPreviewKeyDown(KeyRoutedEventArgs args)
    {
        if (row.Owner is not { } owner) { base.OnPreviewKeyDown(args); return; }
        var control = PlanSheetView.Down(VirtualKey.Control); var shift = PlanSheetView.Down(VirtualKey.Shift);
        if (PlanSheetView.Down(VirtualKey.Menu)) { base.OnPreviewKeyDown(args); return; }
        if (!control && args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right) {
            args.Handled = true; owner.MoveSelection(args.Key, shift);
        }
        else if (!control && args.Key is VirtualKey.Tab or VirtualKey.Enter) {
            args.Handled = true; owner.NavigateCell(field, args.Key == VirtualKey.Tab, shift);
        }
        else if (!control && args.Key == VirtualKey.Delete || control && args.Key is VirtualKey.C or VirtualKey.V or VirtualKey.D or VirtualKey.Z or VirtualKey.Y) {
            args.Handled = true; _ = owner.KeyboardCommand(args.Key);
        }
        else if (!control && args.Key == VirtualKey.Escape) { args.Handled = true; owner.CancelDrag(); }
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
internal sealed class PlanFillHandle(PlanSheetRow row, PlanField field) : Button
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

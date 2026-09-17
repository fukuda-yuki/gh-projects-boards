using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private sealed record SheetDrag(bool Fill, int SourceRow, int Column, int Generation, long Revision, uint PointerId)
    {
        public int EndRow { get; set; } = SourceRow;
        public Point Position { get; set; }
    }
    private SheetDrag? drag;
    private UIElement? dragCapture;
    private readonly DispatcherTimer dragScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private void InitializeDrag()
    {
        AddHandler(PointerMovedEvent, new PointerEventHandler(DragMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(DragReleased), true);
        PointerCaptureLost += (_, _) => CancelDrag();
        PointerCanceled += (_, _) => CancelDrag();
        dragScroll.Tick += (_, _) =>
        {
            if (drag is not { } operation || !DragIsCurrent(operation) || listScroll is null) { CancelDrag(); return; }
            var bounds = listScroll.TransformToVisual(this).TransformBounds(new(0, 0, listScroll.ViewportWidth, listScroll.ViewportHeight));
            var distance = operation.Position.Y < bounds.Top + 28 ? -30d : operation.Position.Y > bounds.Bottom - 28 ? 30d : 0;
            if (distance == 0) return;
            listScroll.ChangeView(null, Math.Clamp(listScroll.VerticalOffset + distance, 0, listScroll.ScrollableHeight), null, true);
            UpdateDragTarget(operation.Position);
        };
    }
    private bool DragIsCurrent(SheetDrag operation) => IsLoaded && operation.Generation == generation && CanRefresh
        && (!operation.Fill || operation.Revision == session.Workspace.Revision);

    private Button CreateFillHandle(int row, int column)
    {
        var handle = new FillHandle(this) { Width = 10, Height = 10, MinHeight = 0, MinWidth = 0, Padding = new(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            IsTabStop = false, Visibility = Visibility.Collapsed, Style = (Style)Application.Current.Resources["SheetFillHandleStyle"] };
        AutomationProperties.SetAutomationId(handle, $"GridFillHandle{row}_{column}");
        AutomationProperties.SetName(handle, "上下にドラッグして同じ列へコピー");
        ToolTipService.SetToolTip(handle, "上下にドラッグしてコピー。Escで取消。範囲選択してCtrl+Dでも下へコピーできます。");
        handle.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CurrentEditor(row, column, controls[row][column])) return;
            args.Handled = true;
            try
            {
                if (!CanRefresh) throw new InvalidOperationException("IME変換中です。確定または取消してから実行してください。");
                // Validate the source without changing any value or creating history.
                session.Workspace.Fill(projectId, rows, row, column, row, row);
                StartDrag(true, row, column, args);
            }
            catch (InvalidOperationException error) { ShowOperationProblem(error.Message); }
        }), true);
        return handle;
    }
    private sealed class FillHandle(EditingGrid owner) : Button
    {
        // Button's default release drops its capture before the bubbling release
        // handler. Finish the drag first so a normal release is not a cancellation.
        protected override void OnPointerPressed(PointerRoutedEventArgs args) { }
        protected override void OnPointerReleased(PointerRoutedEventArgs args) => owner.DragReleased(this, args);
    }
    private void BeginRange(int row, int column, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!CanRefresh) { status.Text = "IME変換を確定または取消してから範囲を選択してください。"; args.Handled = true; return; }
        Select(row, column, (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
        StartDrag(false, row, column, args); args.Handled = true;
    }
    private void StartDrag(bool fill, int row, int column, PointerRoutedEventArgs args)
    {
        CancelDrag();
        var target = fill ? fillHandles[row][column] : controls[row][column];
        if (!target.CapturePointer(args.Pointer) && !target.PointerCaptures.Any(pointer => pointer.PointerId == args.Pointer.PointerId))
        { status.Text = "ドラッグを開始できません。もう一度操作してください。"; return; }
        dragCapture = target;
        drag = new(fill, row, column, generation, session.Workspace.Revision, args.Pointer.PointerId) { Position = args.GetCurrentPoint(this).Position };
        dragScroll.Start(); PaintRealizedSelection();
    }
    private void DragMoved(object sender, PointerRoutedEventArgs args)
    {
        if (drag is not { } operation || operation.PointerId != args.Pointer.PointerId) return;
        if (!DragIsCurrent(operation)) { CancelDrag(); return; }
        UpdateDragTarget(args.GetCurrentPoint(this).Position); args.Handled = true;
    }
    private void UpdateDragTarget(Point point)
    {
        if (drag is not { } operation || listScroll is null || rows.Length == 0) return;
        operation.Position = point;
        var bounds = listScroll.TransformToVisual(this).TransformBounds(new(0, 0, listScroll.ViewportWidth, listScroll.ViewportHeight));
        // DPI rounding can turn a 30-DIP slot into e.g. 30.4 DIPs. Resolve to the captured logical projection,
        // never to recycled visuals or just the currently realized controls.
        var pitch = list.Items.Cast<ListViewItem>().FirstOrDefault(item => item.ActualHeight > 0)?.ActualHeight ?? 30;
        var row = Math.Clamp((int)Math.Floor((Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 1) - bounds.Top + listScroll.VerticalOffset) / pitch), 0, rows.Length - 1);
        diagnostics?.Record("drag-target", new { operation.Fill, point.X, point.Y, bounds, row, pitch, listScroll.VerticalOffset, listScroll.ScrollableHeight });
        operation.EndRow = row;
        if (operation.Fill)
        {
            selection.Text = $"{layout.Visible[operation.Column].Name}：{Math.Abs(row - operation.SourceRow) + 1}行へコピー予定 · 離して確定 / Escで取消";
            PaintRealizedSelection();
        }
        else
        {
            var x = point.X - bounds.Left;
            if (x > 44 + ColumnWidth(0)) x += listScroll.HorizontalOffset;
            var column = 0; var right = 44 + ColumnWidth(0);
            while (column < layout.Visible.Length - 1 && x >= right) right += ColumnWidth(++column);
            if (currentRow != row || currentColumn != column) Select(row, column, true, false);
        }
    }
    private void DragReleased(object sender, PointerRoutedEventArgs args)
    {
        if (drag is not { } operation || operation.PointerId != args.Pointer.PointerId) return;
        if (!DragIsCurrent(operation)) { CancelDrag(); return; }
        UpdateDragTarget(args.GetCurrentPoint(this).Position);
        var lastRow = operation.EndRow;
        drag = null; dragScroll.Stop(); dragCapture?.ReleasePointerCaptures(); dragCapture = null; args.Handled = true;
        if (operation.Fill)
        {
            Run(() => session.Workspace.Fill(projectId, rows, operation.SourceRow, operation.Column,
                Math.Min(operation.SourceRow, lastRow), Math.Max(operation.SourceRow, lastRow)));
        }
        else if (active) Select(currentRow, currentColumn, true);
        PaintRealizedSelection(); UpdateSelection();
    }
    private void CancelDrag()
    {
        if (drag is null) return;
        var fill = drag.Fill; drag = null; dragScroll.Stop(); dragCapture?.ReleasePointerCaptures(); dragCapture = null;
        if (!IsLoaded) return;
        PaintRealizedSelection(); UpdateSelection();
        if (fill) status.Text = "フィルを取り消しました。値は変更していません。";
    }
    private void PaintRealizedSelection()
    {
        for (var r = 0; r < controls.Count; r++) for (var c = 0; c < controls[r].Length; c++) PaintCellState(r, c);
    }
    private void PaintCellState(int row, int column)
    {
        if (row >= selectionFrames.Count || column >= selectionFrames[row].Length) return;
        var minRow = Math.Min(anchorRow, currentRow); var maxRow = Math.Max(anchorRow, currentRow);
        var minColumn = Math.Min(anchorColumn, currentColumn); var maxColumn = Math.Max(anchorColumn, currentColumn);
        var selected = active && row >= minRow && row <= maxRow && column >= minColumn && column <= maxColumn;
        var current = active && row == currentRow && column == currentColumn;
        var preview = drag is { Fill: true } operation && column == operation.Column
            && row >= Math.Min(operation.SourceRow, operation.EndRow) && row <= Math.Max(operation.SourceRow, operation.EndRow);
        var cell = rows[row].Cells[column]; var changed = HasDraftMarker(cell);
        var problem = CellHasProblem(cell);
        cellBorders[row][column].Style = (Style)Application.Current.Resources[selected || preview ? "SheetSelectedCellStyle"
            : problem ? "SheetProblemCellStyle" : changed ? "SheetChangedCellStyle" : "SheetCellStyle"];
        // Frame overlays reserve no extra content space as selection/edit states change.
        var frame = selectionFrames[row][column];
        frame.BorderThickness = current ? new(2) : preview ? new(1) : selected
            ? new(column == minColumn ? 1 : 0, row == minRow ? 1 : 0, column == maxColumn ? 1 : 0, row == maxRow ? 1 : 0) : new(0);
        frame.Style = (Style)Application.Current.Resources[controls[row][column] is TitleCell { Editing: true } ? "SheetEditingFrameStyle" : "SheetSelectionFrameStyle"];
        fillHandles[row][column].Visibility = current && minRow == maxRow && minColumn == maxColumn && cell.Editable
            && !problem && session.Workspace.Buffer(cell) is null && !string.IsNullOrEmpty(session.Workspace.Value(cell))
            ? Visibility.Visible : Visibility.Collapsed;
        if (controls[row][column] is ChoiceCell choice) choice.ShowArrow(selected);
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Input;
using Microsoft.UI.Dispatching;
using DispatcherQueueController = Microsoft.UI.Dispatching.DispatcherQueueController;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
namespace GhProjectsBoards.App;

internal sealed record PlanClipboardContent(string Text, string? Metadata);

internal sealed partial class PlanSheetView
{
    private string selected = "", anchor = "";
    internal string Selected => selected;
    private string selectedField = PlanField.Title, anchorField = PlanField.Title;
    // #240: a whole-row selection must be distinguishable from a rectangle that happens to cover every
    // column, so it is explicit state set by the ID cells or Shift+Space, never inferred from the range.
    private bool wholeRowSelection;
    private const string TitleNotCleared = "タスク名は消せません。行ごと消すときは 行を削除";
    private Column[]? fieldColumns;
    private string[] visibleFields = [];
    private string[] Fields
    {
        get {
            if (!ReferenceEquals(fieldColumns, VisibleColumns)) {
                fieldColumns = VisibleColumns;
                visibleFields = VisibleColumns.Where(c => c.Field is not null).Select(c => c.Field!).ToArray();
            }
            return visibleFields;
        }
    }
    // #236: an edge names only the row pair. It used to carry the predecessor's end and the successor's start
    // as copies taken when the edges were built, while the bars are drawn from the current schedule. An edit
    // that reschedules an end (EndDayOnly lets a successor start on the stored end day) left the copy a day
    // behind the bar, so the arrow pointed a day away from the bar it belongs to. Keeping the dates out of the
    // type is what stops the second source coming back: every drawing path reads the schedule the bars use.
    internal sealed record Edge(int From, int To);
    internal IReadOnlyList<Edge> Edges { get; private set; } = [];
    // The links whose arrow passes through each row, in Edges order, so a row draws its slice without
    // scanning every link in the version (#256).
    internal IReadOnlyList<Edge>[] EdgesByRow { get; private set; } = [];
    internal static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    // Ctrl+-: the numeric keypad reports Subtract, the main row reports the OEM minus code (Excel's
    // Ctrl+- accepts both), so 行を削除 must too (#247).
    internal static bool Minus(VirtualKey key) => key == VirtualKey.Subtract || (int)key == 189;
    private CellRange Range
    {
        get
        {
            var a = Math.Max(0, RowIndexes.GetValueOrDefault(anchor, -1)); var b = Math.Max(0, RowIndexes.GetValueOrDefault(selected, -1));
            var c = Math.Max(0, Array.IndexOf(Fields, anchorField)); var d = Math.Max(0, Array.IndexOf(Fields, selectedField));
            return new(Math.Min(a, b), Math.Min(c, d), Math.Abs(a - b) + 1, Math.Abs(c - d) + 1);
        }
    }
    private void ReconcileSelection()
    {
        var previous = selected;
        if (!RowIds.Contains(selected)) selected = VisibleAncestor(selected) ?? RowIds.FirstOrDefault() ?? "";
        if (!RowIds.Contains(anchor)) anchor = selected;
        if (!Fields.Contains(selectedField)) selectedField = Fields.FirstOrDefault() ?? PlanField.Title;
        if (!Fields.Contains(anchorField)) anchorField = selectedField;
        RefreshEdges();
        if (previous.Length > 0 && previous != selected && filter.FocusState == FocusState.Unfocused) FocusSelected();
    }
    private void RefreshEdges()
    {
        var indexes = RowIndexes;
        Edges = RowIds.Where(Rows.ContainsKey).SelectMany(id => Rows[id].Predecessors
            .Where(p => indexes.ContainsKey(p) && Schedule.GetValueOrDefault(p)?.End.Value is not null && Schedule[id].Start.Value is not null)
            .Select(p => new Edge(indexes[p], indexes[id]))).ToArray();
        var byRow = new List<Edge>?[RowIds.Count];
        foreach (var edge in Edges)
            for (var row = Math.Min(edge.From, edge.To); row <= Math.Max(edge.From, edge.To); row++) (byRow[row] ??= []).Add(edge);
        EdgesByRow = byRow.Select(edges => (IReadOnlyList<Edge>?)edges ?? []).ToArray();
    }
    internal bool IsSelected(string identity, string field)
    {
        var range = Range; var row = RowIndexes.GetValueOrDefault(identity, -1); var column = Array.IndexOf(Fields, field);
        return row >= range.Row && row < range.Row + range.RowCount && column >= range.Column && column < range.Column + range.ColumnCount;
    }
    internal bool IsSelectedRow(string identity) => identity == selected;
    internal Func<DependencyObject?, bool>? PreservePendingFocus { get; set; }
    internal bool HasActiveCellEdit => Pending.Count > 0 || Realized.SelectMany(r => r.Cells).Any(c => c.Editing || c.Composing);
    internal event Action? SelectionChanged;
    internal bool IsRangeEnd(string identity, string field) => identity == selected && field == selectedField;
    internal void Select(string identity, string field, bool extend)
    {
        if (!RowIds.Contains(identity) || !Fields.Contains(field)) return;
        var previousRange = Range;
        wholeRowSelection = false;
        selected = identity; selectedField = field;
        if (!extend) { anchor = identity; anchorField = field; }
        // A refusal explains the operation it refused; choosing a row or cell dismisses it (#241).
        error.Text = "";
        EnsureColumnVisible(field);
        // First selection can precede the horizontal viewport's initial layout.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (!disposed && IsLoaded && selectedField == field) EnsureColumnVisible(field);
        });
        UpdateReason(); RefreshOverviewCommands(); RefreshRealized(previousRange); SelectionChanged?.Invoke();
    }
    // Selection that leaves keyboard focus on another cell lets that cell reclaim it
    // through GotFocus, so typing or a command would act on the previous task.
    internal void SelectAndFocus(string identity, string field, bool extend)
    {
        Select(identity, field, extend); FocusSelected();
    }
    private void EnsureColumnVisible(string field)
    {
        var left = 0d;
        foreach (var column in VisibleColumns)
        {
            if (column.Field == field)
            {
                var target = left < SheetOffset ? left : left + column.Width > SheetOffset + SheetViewport
                    ? left + column.Width - SheetViewport : SheetOffset;
                if (target != SheetOffset) sheetHorizontal.ChangeView(Math.Max(0, target), null, null, true);
                return;
            }
            left += column.Width;
        }
    }
    internal string RemoteProblem(string identity) => PlanSheetText.RemoteProblem(Session.Document.Sync, identity);
    private void UpdateReason()
    {
        selection.MaxWidth = double.PositiveInfinity;
        ToolTipService.SetToolTip(selection, null);
        selection.Text = TaskNames.Contains(selected) ? TaskNames[selected] : "";
        selectedTitle.Text = Rows.GetValueOrDefault(selected)?.Title ?? "";
        ToolTipService.SetToolTip(selectedTitle, selectedTitle.Text);
        AutomationProperties.SetName(selection, selection.Text + " " + selectedTitle.Text);
        var hasSelection = Schedule.TryGetValue(selected, out var task);
        detailToggle.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        successorLink.Visibility = Visibility.Collapsed; successorLink.Content = "";
        predecessorLink.Visibility = Visibility.Collapsed; predecessorLink.Content = "";
        var late = LatenessOf(selected);
        slip.Text = "";
        latenessDetail.Text = "";
        progressLine.Text = "";
        estimateDetail.Text = "";
        if (late is not null && task is not null) {
            if (task.IsSummary) {
                // A requirement row's dates are its children's: it answers with the sets below it instead.
                var counts = LatenessCounts(late);
                latenessDetail.Text = counts.Length > 0 ? "配下: " + counts : "";
            } else {
                // The tag keeps the red/ochre distinction while the strip counts one 遅れ set (#260 D0).
                // A requirement row states the slip of its roll-up too; the red-case tag belongs to tasks.
                if (!task.IsSummary && late.OverdueKind is { } kind && late.MissedDate is { } missed) {
                    var missedDate = missed.ToString("M/d", CultureInfo.InvariantCulture);
                    slip.Text = kind == PlanOverdueKind.Finish ? $"終了日 {missedDate} を過ぎて未完了" : $"開始日 {missedDate} を過ぎて未着手";
                    if (late.DaysLater is { } days) slip.Text += $" · +{days}日";
                } else if (late.DaysLater is { } days && task.End.Value is { } planned) {
                    slip.Text = $"見込終了 {ForecastLabel(selected)} · {planned.ToString("M/d", CultureInfo.InvariantCulture)} から +{days}日";
                }
                // The effect or the cause, with its link (#260 2).
                var delayed = late.IsLate ? DelayedSuccessorsOf(selected) : [];
                if (delayed.Length > 0) {
                    successorLink.Content = $"後続 {delayed.Length} タスクが遅れ · 後続を表示";
                    successorLink.Visibility = Visibility.Visible;
                    AutomationProperties.SetName(successorLink, $"後続を表示（{delayed.Length} タスク）");
                }
                if (late.CausePredecessorRowId is { } cause) {
                    var name = TaskNames.AtPosition(cause);
                    predecessorLink.Content = $"先行 {name} の遅れ · {name} へ移動";
                    predecessorLink.Visibility = Visibility.Visible;
                    AutomationProperties.SetName(predecessorLink, $"先行 {name} へ移動");
                }
                if (late.IsLate && delayed.Length == 0 && late.CausePredecessorRowId is null) latenessDetail.Text = "自分の残による";
                // #238: the last change of 実績 and 残 on GitHub for this open task, from the latest read.
                if (!task.Input.Closed && Session.Document.Sync.IssueLinks.ContainsKey(selected))
                    progressLine.Text = PlanAttention.ProgressLine(Session.Document, selected, StatusDate) ?? "";
                if (late.OverEstimate && task.Estimate is { } estimate && task.Forecast is { } forecastEffort)
                    estimateDetail.Text = $"見積超過 見込 {forecastEffort.ToString(CultureInfo.CurrentCulture)}h / 見積 {estimate.ToString(CultureInfo.CurrentCulture)}h";
            }
        }
        var overdue = late?.Level == PlanLatenessLevel.Overdue;
        slipPill.Background = overdue ? Brush("GanttLateTintBrush") : null;
        slipPill.BorderBrush = Brush(overdue ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");
        slipPill.BorderThickness = new(overdue ? 0 : 1);
        slip.Foreground = Brush(overdue ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");
        slipPill.Visibility = slip.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        latenessDetail.Visibility = latenessDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        progressLine.Visibility = progressLine.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        estimateDetail.Visibility = estimateDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(slip, slip.Text);
        AutomationProperties.SetName(slipPill, slip.Text);
        AutomationProperties.SetName(latenessDetail, latenessDetail.Text);
        AutomationProperties.SetName(progressLine, progressLine.Text);
        AutomationProperties.SetName(estimateDetail, estimateDetail.Text);
        AutomationProperties.SetName(selectedTitle, selectedTitle.Text);
        if (Session.Document.Sync.IssueLinks.TryGetValue(selected, out var link) && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == Session.Document.Project.Scope.Host) {
            issueLink.Content = link.Caption; issueLink.NavigateUri = uri; issueLink.Visibility = Visibility.Visible;
        } else { issueLink.Content = ""; issueLink.NavigateUri = null; issueLink.Visibility = Visibility.Collapsed; }
        AutomationProperties.SetName(issueLink, issueLink.Content?.ToString() ?? "");
        // The plan dates and the scheduler's reasons in secondary text; a requirement row's dates are its
        // children's, so it leaves the line to the 配下 summary instead.
        var reasons = new List<string>();
        if (task is { IsSummary: false }) {
            if (task.Start.Value is { } start && task.End.Value is { } end)
                reasons.Add($"{DateText(start)} – {DateText(end)}");
            reasons.Add(task.StartReason is "完了" or "工数なし" or "入力エラー" ? task.StartReason : "開始: " + TaskNames.StartReason(task.StartReason));
        }
        if (task is { IsSummary: false } && PlanLateness.EndReason(task) is { } endReason) reasons.Add(endReason);
        if (task?.Warnings.Count > 0) reasons.Add(string.Join(" / ", task.Warnings));
        var remoteProblem = RemoteProblem(selected);
        if (remoteProblem.Length > 0) reasons.Add(remoteProblem);
        reason.Text = string.Join(" · ", reasons);
        if (BlockingConflicts.FirstOrDefault(c => c.Identity == selected && c.Field == selectedField) is { } conflict)
            reason.Text = "競合: GitHub では " + PlanWorkspaceView.ReviewValue(Session.Document, selectedField, conflict.Remote);
        if (ReadOnlyReason(selected, selectedField) is { } readOnlyReason)
            reason.Text = readOnlyReason + (reason.Text.Length > 0 ? " · " + reason.Text : "");
        // #231: the selected row's pending state command belongs on the selection line too, so the state is
        // readable without hunting for the tag in the title cell.
        if (RowStateTag(selected) is { } stateTag)
            reason.Text = (reason.Text.Length > 0 ? reason.Text + " · " : "") + stateTag + "（未発行）";
        AutomationProperties.SetName(reason, reason.Text);
        ToolTipService.SetToolTip(reason, reason.Text);
    }
    private static string LatenessCounts(PlanTaskLateness late)
    {
        var parts = new List<string>();
        var lateCount = late.OverdueDescendantTasks + late.LaterDescendantTasks;
        if (lateCount > 0) parts.Add($"遅れ {lateCount}");
        if (late.OverEstimateDescendantTasks > 0) parts.Add($"見積超過 {late.OverEstimateDescendantTasks}");
        return string.Join(" · ", parts);
    }
    private string ForecastLabel(string identity) => ForecastEndOf(identity) is { } day
        ? PlanSheetView.DateText(day) : "—";
    internal void MoveSelection(VirtualKey key, bool extend)
    {
        // Excel keeps a whole-row selection whole while it extends vertically: Shift+Down after
        // Shift+Space still selects rows for Delete (#240). Any other move narrows back to cells.
        var wholeRow = wholeRowSelection && extend && key is VirtualKey.Up or VirtualKey.Down;
        var row = Math.Max(0, RowIds.IndexOf(selected)); var column = Math.Max(0, Array.IndexOf(Fields, selectedField));
        if (key == VirtualKey.Up) row--; if (key == VirtualKey.Down) row++;
        if (key == VirtualKey.Left) column--; if (key == VirtualKey.Right) column++;
        if (Fields.Length == 0) return;
        Select(RowIds[Math.Clamp(row, 0, RowIds.Count - 1)], Fields[Math.Clamp(column, 0, Fields.Length - 1)], extend);
        if (wholeRow) SelectWholeRow(selected, true);
        FocusSelected();
    }
    // #256: the native ListView answers PageDown/PageUp by rebinding about three pages of rows for each page
    // it moves. The sheet moves one page itself, as Excel does: the selection moves by the rows the viewport
    // shows and the rows scroll by the same amount, so the selection keeps its place on screen.
    private double? pageOffset;
    internal void MovePage(bool down, bool extend)
    {
        if (Fields.Length == 0 || RowIds.Count == 0 || rowScroll is not { } scroll) return;
        var wholeRow = wholeRowSelection && extend;
        var page = Math.Max(1, (int)(scroll.ViewportHeight / RowHeight)) * (down ? 1 : -1);
        var row = Math.Clamp(Math.Max(0, RowIds.IndexOf(selected)) + page, 0, RowIds.Count - 1);
        // A press that arrives before the previous page is drawn adds to that page's offset, so held keys
        // draw only where they end.
        var offset = Math.Clamp((pageOffset ?? scroll.VerticalOffset) + page * RowHeight, 0, scroll.ScrollableHeight);
        var top = row * RowHeight;
        if (top < offset) offset = top;
        else if (top + RowHeight > offset + scroll.ViewportHeight) offset = top + RowHeight - scroll.ViewportHeight;
        pageOffset = offset == scroll.VerticalOffset ? null : offset;
        scroll.ChangeView(null, offset, null, true);
        Select(RowIds[row], Fields[Math.Max(0, Array.IndexOf(Fields, selectedField))], extend);
        if (wholeRow) SelectWholeRow(selected, true);
        FocusCell(selected, selectedField, scrollIntoView: false);
        HoldingView = true;
    }
    // The page move has already placed the rows. The target cell takes focus in a container that may not
    // be arranged at its new place yet, and the focus's later bring-into-view requests would scroll to
    // that stale position, as far as the top of the plan. They are ignored until the next other key,
    // click or focus request.
    internal bool HoldingView { get; private set; }
    private void PageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.PageDown or VirtualKey.PageUp) || Down(VirtualKey.Control) || Down(VirtualKey.Menu)) {
            if (args.Key is not (VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu)) HoldingView = false;
            return;
        }
        // An open editor and the IME keep their own keys.
        if (args.OriginalSource is PlanSheetCell { Editing: true } or PlanSheetCell { Composing: true }) return;
        args.Handled = true;
        MovePage(args.Key == VirtualKey.PageDown, Down(VirtualKey.Shift));
    }
    // #228: the grid edge chords move the selection to the visible first/last row or field column, and F5
    // opens ジャンプ. Both live here so a page move and an edge move share one placement rule.
    private void EdgeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Up or VirtualKey.Down or VirtualKey.F5)) return;
        // A plain arrow stays with the cell handler, which also keeps a whole-row selection whole.
        if (args.Key is VirtualKey.Up or VirtualKey.Down && !Down(VirtualKey.Control)) return;
        if (Down(VirtualKey.Menu)) return;
        // An open editor and the IME keep their own keys; the caret's Home/End stay in the text box.
        if (args.OriginalSource is PlanSheetCell { Editing: true } or PlanSheetCell { Composing: true }) return;
        if (args.Key == VirtualKey.F5) { args.Handled = true; _ = Run(OpenGoTo, "Go To"); return; }
        args.Handled = HandleEdgeKey(args.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift));
    }
    internal bool HandleEdgeKey(VirtualKey key, bool control, bool extend)
    {
        if (Fields.Length == 0 || RowIds.Count == 0) return false;
        var row = Math.Max(0, RowIndexes.GetValueOrDefault(selected, 0));
        var column = Math.Max(0, Array.IndexOf(Fields, selectedField));
        var last = LastVisibleTaskIndex();
        switch (key)
        {
            case VirtualKey.Home when control: row = 0; column = 0; break;
            case VirtualKey.End when control: row = last; column = Fields.Length - 1; break;
            case VirtualKey.Home: column = 0; break;
            case VirtualKey.End: column = Fields.Length - 1; break;
            case VirtualKey.Up when control: row = 0; break;
            case VirtualKey.Down when control: row = last; break;
            case VirtualKey.Left when control: column = 0; break;
            case VirtualKey.Right when control: column = Fields.Length - 1; break;
            default: return false;
        }
        // Shift+Ctrl+↑/↓ keep a whole-row selection whole, as Shift+PageUp/PageDown already do; every
        // other edge chord narrows to field cells.
        var wholeRow = wholeRowSelection && extend && control && key is VirtualKey.Up or VirtualKey.Down;
        var target = RowIds[row];
        if (row != RowIndexes.GetValueOrDefault(selected, row)) JumpTo(target, Fields[column], extend, wholeRow);
        else
        {
            Select(target, Fields[column], extend);
            if (wholeRow) SelectWholeRow(target, true);
            else FocusSelected();
        }
        return true;
    }
    // The empty new-task row is not a task, so Ctrl+End and Ctrl+↓ stop on the last real task.
    private int LastVisibleTaskIndex()
    {
        for (var index = RowIds.Count - 1; index >= 0; index--)
            if (Rows.ContainsKey(RowIds[index])) return index;
        return 0;
    }
    // Place the row and focus its cell without a second bring-into-view: a far jump can otherwise be
    // snapped back by a row container that is still being rebound from its previous identity (#256).
    private void JumpTo(string identity, string field, bool extend, bool wholeRow = false)
    {
        if (Fields.Length == 0 || !RowIndexes.TryGetValue(identity, out var index) || !Fields.Contains(field)) return;
        if (rowScroll is { } scroll)
        {
            // A filter or fold change replaces the item set; the native extent must be arranged before the
            // offset is clamped, or the jump is clamped into the old, shorter list and the row is never realized.
            List.UpdateLayout();
            var top = index * RowHeight;
            var offset = Math.Clamp(top, 0, scroll.ScrollableHeight);
            if (top + RowHeight > offset + scroll.ViewportHeight)
                offset = Math.Clamp(top + RowHeight - scroll.ViewportHeight, 0, scroll.ScrollableHeight);
            scroll.ChangeView(null, offset, null, true);
            // Realize the rows at the new offset before the focus request, so the target container exists
            // when it is asked to take focus; the request itself then needs no further retries.
            List.UpdateLayout();
        }
        Select(identity, field, extend);
        if (wholeRow) SelectWholeRow(identity, true, focus: false);
        FocusCell(selected, selectedField, scrollIntoView: false);
        HoldingView = true;
    }
    private Flyout? goToFlyout;
    internal async Task OpenGoTo()
    {
        await CommitPending();
        if (disposed) return;
        goToFlyout?.Hide();
        var input = Id(new TextBox { PlaceholderText = "#524・repo#524・新2" }, "PlanGoToInput");
        AutomationProperties.SetName(input, "ジャンプ先のタスク名");
        var feedback = Id(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("SystemFillColorCriticalBrush") }, "PlanGoToError");
        AutomationProperties.SetLiveSetting(feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var confirm = Id(new Button { Content = "移動", Style = (Style)Application.Current.Resources["AccentButtonStyle"] }, "PlanGoToConfirm");
        var cancel = Id(new Button { Content = "キャンセル" }, "PlanGoToCancel");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(confirm); actions.Children.Add(cancel);
        var content = new StackPanel { Width = Math.Min(360, Math.Max(240, ActualWidth - 80)), Spacing = 8 };
        content.Children.Add(Id(new TextBlock { Text = "ジャンプ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, "PlanGoToLabel"));
        content.Children.Add(input); content.Children.Add(feedback); content.Children.Add(actions);
        var flyout = goToFlyout = new Flyout {
            AreOpenCloseAnimationsEnabled = false,
            Content = content, Placement = FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)) { BasedOn = (Style)Application.Current.Resources["PlanFlyoutPresenterStyle"], Setters = {
                new Setter(FrameworkElement.MaxWidthProperty, Math.Max(0, XamlRoot.Size.Width - 32))
            } }
        };
        var navigating = false;
        void Submit()
        {
            try
            {
                var identity = TaskNames.Resolve(PlanInput.Fold(input.Text).Trim(), "タスク名");
                if (!Rows.ContainsKey(identity)) throw new ArgumentException($"{input.Text.Trim()} はこの版にありません。");
                navigating = true;
                flyout.Hide();
                RevealTask(identity);
                JumpTo(identity, PlanField.Title, false);
                error.Text = "";
            }
            catch (ArgumentException ex)
            {
                feedback.Text = ex.Message;
                AutomationProperties.SetName(feedback, ex.Message);
                input.Focus(FocusState.Keyboard);
            }
        }
        confirm.Click += (_, _) => Submit();
        input.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { args.Handled = true; Submit(); } };
        cancel.Click += (_, _) => flyout.Hide();
        content.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { args.Handled = true; flyout.Hide(); } };
        flyout.Opened += (_, _) => input.Focus(FocusState.Keyboard);
        flyout.Closed += (_, _) => {
            if (ReferenceEquals(goToFlyout, flyout)) goToFlyout = null;
            // Cancelling returns focus to the cell the PMO was on; an accepted name already moved it.
            if (!navigating && !disposed) FocusSelected();
        };
        flyout.ShowAt(commandBar);
    }
    // Go To names the task the PMO asked for, so a target the title filter or folding hides is revealed
    // rather than refused: the accepted filter is cleared and the folded ancestors are expanded.
    private void RevealTask(string identity)
    {
        if (!RowIds.Contains(identity) && FilterActive) ClearFilters();
        if (RowIds.Contains(identity)) return;
        var revealed = false;
        var visited = new HashSet<string>();
        for (var parent = Rows.GetValueOrDefault(identity)?.Parent; parent is not null && visited.Add(parent);)
        {
            if (!Rows.TryGetValue(parent, out var row)) break;
            if (folded.Remove(parent)) revealed = true;
            parent = row.Parent;
        }
        if (revealed) Refresh();
    }
    internal event Action? CommandRowFocusRequested;
    private (string Identity, string Field)? filterReturnCell;
    internal bool HandleFocusShortcut(VirtualKey key)
    {
        if (Down(VirtualKey.Menu)) return false;
        if (key == VirtualKey.F && Down(VirtualKey.Control)) {
            filterReturnCell = (selected, selectedField);
            CancelRequestedFocus();
            return filter.Focus(FocusState.Keyboard);
        }
        if (key == VirtualKey.F6 && !Down(VirtualKey.Control) && CommandRowFocusRequested is not null) {
            CancelRequestedFocus();
            CommandRowFocusRequested();
            return true;
        }
        return false;
    }
    private async Task ReturnFromFilter()
    {
        await ApplyFilter();
        if (filterReturnCell is { } cell && RowIds.Contains(cell.Identity) && Fields.Contains(cell.Field))
            Select(cell.Identity, cell.Field, false);
        filterReturnCell = null;
        FocusSelected();
    }
    private (string Identity, string Field)? requestedFocus;
    internal void FocusSelected() => FocusCell(selected, selectedField);
    private void FocusCell(string identity, string field, bool scrollIntoView = true)
    {
        requestedFocus = (identity, field); HoldingView = false;
        List.LayoutUpdated -= FocusAfterLayout;
        List.LayoutUpdated += FocusAfterLayout;
        if (scrollIntoView) List.ScrollIntoView(identity);
        TryRestoreFocus();
    }
    private void FocusAfterLayout(object? sender, object args) => TryRestoreFocus();
    private void TryRestoreFocus()
    {
        if (requestedFocus is not { } target || disposed) return;
        // A native container can remain loaded while it is being rebound. Only the
        // current task's owned row may consume a pending focus request.
        var row = (List.ContainerFromItem(target.Identity) as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
        if (row is null || row.Identity != target.Identity || !ReferenceEquals(row.Owner, this)) return;
        var cell = row.SelectionTarget(target.Field);
        if (cell is not { IsLoaded: true, ActualWidth: > 0 } || !cell.Focus(FocusState.Keyboard)) return;
        requestedFocus = null;
        List.LayoutUpdated -= FocusAfterLayout;
    }
    private void CancelRequestedFocus()
    {
        requestedFocus = null;
        List.LayoutUpdated -= FocusAfterLayout;
    }
    internal async Task CommitCellAndNavigate(string identity, string field, string text, long generation, string originalText, bool across, bool reverse)
    {
        await Commit(identity, field, text, generation, originalText);
        if (Pending.ContainsKey((identity, field)) || disposed) return;
        NavigateCell(field, across, reverse);
    }
    internal void NavigateCell(string field, bool across, bool reverse)
    {
        var row = RowIds.IndexOf(selected); var column = Array.IndexOf(Fields, field);
        if (across)
        {
            column += reverse ? -1 : 1;
            if (column >= Fields.Length) { column = 0; row++; }
            if (column < 0) { column = Fields.Length - 1; row--; }
        }
        else row += reverse ? -1 : 1;
        if (Fields.Length == 0) return;
        Select(RowIds[Math.Clamp(row, 0, RowIds.Count - 1)], Fields[Math.Clamp(column, 0, Fields.Length - 1)], false);
        FocusSelected();
    }
    internal Task KeyboardCommand(VirtualKey key)
    {
        // Run yields to native focus events before executing. Preserve command
        // targets at invocation, including paste's matrix expansion order.
        var range = Range;
        var deleting = Minus(key);
        var rows = key is VirtualKey.C or VirtualKey.Delete || deleting ? RowIds.Skip(range.Row).Take(range.RowCount).ToArray() : [];
        var fields = key is VirtualKey.C or VirtualKey.Delete ? Fields.Skip(range.Column).Take(range.ColumnCount).ToArray() : [];
        var visible = key == VirtualKey.V ? RowIds.Where(Rows.ContainsKey).ToArray() : [];
        var columns = key == VirtualKey.V ? Fields.ToArray() : [];
        return Run(async () => {
            switch (key)
            {
                case VirtualKey.C: await Copy(rows, fields); break;
                case VirtualKey.V: await Paste(range, visible, columns); break;
                case VirtualKey.D: await Fill(PlanOperationKind.CtrlD); break;
                case VirtualKey.Delete:
                    // #240: a whole-row selection removes the rows; a cell range clears, and never タスク名.
                    if (!wholeRowSelection) { await Clear(rows, fields); break; }
                    var rowTargets = rows.Where(Rows.ContainsKey).ToArray();
                    if (rowTargets.Length > 0) await DeleteRows(rowTargets);
                    break;
                case VirtualKey.Z: await ChangeHistory(false); break;
                case VirtualKey.Y: await ChangeHistory(true); break;
                default: if (deleting) await DeleteRows(rows); break;
            }
        });
    }
    private string[] RangeRows(CellRange range)
    {
        var rows = RowIds.Skip(range.Row).Take(range.RowCount).ToArray();
        if (rows.Any(id => !Rows.ContainsKey(id))) throw new ArgumentException("空行を除いて範囲を選択してください。");
        return rows;
    }
    private void RequireNoPending(IEnumerable<string> rows, IEnumerable<string> fields)
    {
        var ids = rows.ToHashSet(); var columns = fields.ToHashSet();
        if (Pending.Keys.Any(k => ids.Contains(k.Identity) && columns.Contains(k.Field)))
            throw new InvalidOperationException("未確定入力があります。確定または取消してください。");
    }
    private readonly Func<CancellationToken, Task<PlanClipboardContent>> readClipboard;
    private readonly Action<PlanClipboardContent, CancellationToken> writeClipboard;
    private static async Task<PlanClipboardContent> ReadClipboard(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) throw new ArgumentException("貼り付けるテキストがありません。");
        token.ThrowIfCancellationRequested();
        return new(await content.GetTextAsync().AsTask(token), content.Contains(ClipboardFormat) ? await content.GetDataAsync(ClipboardFormat).AsTask(token) as string : null);
    }
    private static void WriteClipboard(PlanClipboardContent content, CancellationToken token)
    {
        var data = new DataPackage(); data.SetText(content.Text);
        if (content.Metadata is not null) data.SetData(ClipboardFormat, content.Metadata);
        token.ThrowIfCancellationRequested();
        Clipboard.SetContent(data);
        token.ThrowIfCancellationRequested();
        Clipboard.Flush();
    }
    private const string ClipboardFormat = "GhProjectsBoards.PlanCells.v1";
    private sealed record SheetCopy(ScopedId Project, string[] Fields, string[] Identities, string[][] Values);
    // One process-owned worker keeps synchronous delayed-rendering calls away from
    // the window. A blocked owner cannot cause an unbounded number of worker threads.
    private static readonly Lazy<DispatcherQueueController> clipboardQueue = new(DispatcherQueueController.CreateOnDedicatedThread);
    private static async Task<T> InvokeClipboard<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        if (!token.IsCancellationRequested && !clipboardQueue.Value.DispatcherQueue.TryEnqueue(async () => {
            if (token.IsCancellationRequested) return;
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            try { completion.TrySetResult(await action(token).WaitAsync(token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        })) throw new InvalidOperationException("Clipboard worker is unavailable.");
        return await completion.Task;
    }
    private const string ClipboardError = "クリップボードを使用できません。もう一度お試しください。";
    private string clipboardWork = "none";
    private async Task<T> WithClipboard<T>(Func<CancellationToken, Task<T>> action)
    {
        var unloaded = lifetime.Token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(unloaded);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var token = deadline.Token;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                clipboardWork = $"access attempt {attempt + 1}";
                try
                {
                    var result = await InvokeClipboard(action, token);
                    token.ThrowIfCancellationRequested();
                    return result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < 4)
                { clipboardWork = "retry delay"; await Task.Delay(100, token); }
                catch (Exception ex) { throw new InvalidOperationException(ClipboardError, ex); }
            }
        }
        catch (OperationCanceledException error) when (!unloaded.IsCancellationRequested)
        { throw new InvalidOperationException(ClipboardError, error); }
        finally { clipboardWork = "none"; }
    }
    private async Task Copy(string[] rows, string[] fields)
    {
        if (rows.Any(id => !Rows.ContainsKey(id))) throw new ArgumentException("空行を除いて範囲を選択してください。");
        RequireNoPending(rows, fields);
        var text = string.Join("\n", rows.Select(id => string.Join("	", fields.Select(f => EditForm(id, f)))));
        var typed = rows.Select(id => fields.Select(field => JsonSerializer.Serialize(CopyValue(id, field), PlanJson.Options)).ToArray()).ToArray();
        var content = new PlanClipboardContent(text, JsonSerializer.Serialize(new SheetCopy(Session.Document.Project, fields, rows, typed), PlanJson.Options));
        var write = writeClipboard;
        await WithClipboard(token => { write(content, token); return Task.FromResult(true); });
    }
    private async Task Paste(CellRange range, string[] visible, string[] columns)
    {
        var content = await WithClipboard(readClipboard);
        var text = content.Text;
        EditPlanCells command;
        if (content.Metadata is { } metadata)
        {
            SheetCopy copied;
            try { copied = JsonSerializer.Deserialize<SheetCopy>(metadata, PlanJson.Options) ?? throw new ArgumentException("コピー元を確認できません。"); }
            catch (JsonException ex) { throw new ArgumentException("コピー元を確認できません。", ex); }
            if (copied.Project != Session.Document.Project) throw new ArgumentException("別のプロジェクトからコピーしたセルは貼り付けできません。");
            RequireNoPending(copied.Identities, copied.Fields);
            if (copied.Values.Length == 0 || copied.Values.Any(row => row is null || row.Length != copied.Fields.Length))
                throw new ArgumentException("コピー元の形状を確認できません。");
            var matrix = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.Split('\t')).ToArray();
            if (copied.Values.Length != matrix.Length || matrix.Any(row => row.Length != copied.Fields.Length))
                throw new ArgumentException("コピー元の形状を確認できません。");
            var target = PlanSheetEditing.PasteRange(range, copied.Values.Length, copied.Fields.Length, visible.Length, columns.Length);
            if (visible.Skip(target.Row).Take(target.RowCount).Any(id => !Rows.ContainsKey(id)))
                throw new ArgumentException("空行を除いて範囲を選択してください。");
            var single = copied.Values.Length == 1 && copied.Fields.Length == 1;
            var changes = ImmutableArray.CreateBuilder<PlanCellChange>();
            for (var r = 0; r < target.RowCount; r++)
                for (var c = 0; c < target.ColumnCount; c++)
                {
                    var sr = single ? 0 : r; var sc = single ? 0 : c;
                    var field = columns[target.Column + c]; var source = copied.Fields[sc];
                    string Kind(string f) => PlanSheetColumns.IsExtra(f) ? PlanExtraEditing.Definition(Session.Document,f).DataType.ToLowerInvariant()
                        : f == PlanField.Title ? "text" : f is PlanField.Estimate or PlanField.Remaining or PlanField.Actual ? "number"
                        : f is PlanField.Start or PlanField.End ? "date" : f.ToString();
                    if (!PlanField.IsDefined(source) || Kind(source) != Kind(field)) throw new ArgumentException("コピー元と対象の列の型が一致しません。");
                    if (matrix[sr][sc].Length == 0) continue;
                    var identity = visible[target.Row + r];
                    var parsed = PlanValues.Set(Rows[identity], field, copied.Values[sr][sc]);
                    changes.Add(new(identity, field, PlanOperations.Value(parsed, field)));
                }
            command = new(PlanOperationKind.Paste, changes.ToImmutable());
        }
        else
        {
            // #253: a pasted login the sheet cannot resolve locally is verified first, one lookup per distinct
            // login and repository, and the verified people ride in the same paste command.
            var pending = PlanSheetEditing.PendingAssignees(Session.Document, visible, columns, range, text);
            var resolved = await ResolveAssignees(pending);
            error.Text = "";
            command = PlanSheetEditing.Paste(Session.Document, visible, columns, range, text, Today, resolved);
            if (!resolved.IsEmpty) command = command with { NewPeople = resolved };
        }
        foreach (var change in command.Cells) RequireNoPending([change.Identity], [change.Field]);
        Check(await Session.Execute(command, Today)); Refresh();
    }
    private object? CopyValue(string identity, string field)
        => PlanSheetEditing.CopyValue(Rows[identity], field, EditForm(identity, field));
    private EditPlanCells FillCommand(PlanOperationKind kind, string source, IEnumerable<string> targets, string field)
    {
        if (!Rows.ContainsKey(source)) throw new ArgumentException("空行を除いて範囲を選択してください。");
        return new(kind, targets.Where(id => id != source).Select(id => new PlanCellChange(id, field, CopyValue(source, field))).ToImmutableArray());
    }
    internal async Task Fill(PlanOperationKind kind)
    {
        var range = Range; var rows = RangeRows(range);
        if (kind == PlanOperationKind.CtrlD && range.RowCount == 1 && range.ColumnCount == 1) {
            if (range.Row == 0) return;
            var source = RowIds[range.Row - 1]; var sourceField = Fields[range.Column];
            if (!Rows.ContainsKey(source) || ReadOnly(source, sourceField)) return;
            RequireNoPending([source, .. rows], [sourceField]);
            Check(await Session.Execute(FillCommand(kind, source, rows, sourceField), Today)); Refresh();
            return;
        }
        if (range.ColumnCount != 1 || rows.Length < 2) throw new ArgumentException("同じ列の2行以上を選択してください。");
        var field = Fields[range.Column]; RequireNoPending(rows, [field]);
        if (Display(rows[0], field).Length == 0) throw new ArgumentException("空値からコピーできません。クリアを使ってください。");
        Check(await Session.Execute(FillCommand(kind, rows[0], rows.Skip(1), field), Today)); Refresh();
    }
    private async Task Clear(string[] rows, string[] fields)
    {
        if (rows.Any(id => !Rows.ContainsKey(id))) throw new ArgumentException("空行を除いて範囲を選択してください。");
        RequireNoPending(rows, fields);
        var skippedTitle = fields.Contains(PlanField.Title);
        Check(await Session.Execute(new ClearPlanCells(rows.ToImmutableArray(), fields.ToImmutableArray()), Today)); Refresh();
        // Core skips タスク名; say so, because the rest of the range did clear (#240). The line goes when
        // the PMO chooses another row or cell, like every other refusal in the sheet.
        if (skippedTitle) error.Text = TitleNotCleared;
    }
    private async Task Insert()
    {
        if (Session.Document.State.Settings.DefaultRepository is not { Length: > 0 } repository)
            throw new ArgumentException("設定で新規 Issue の作成先を選んでください。");
        var row = PlanRow.New("", repository);
        Check(await Session.Execute(new InsertPlanRows([row], selected.Length == 0 ? null : selected), Today));
        RevealAddedRow(row.Identity);
    }
    // 行を削除 removes the rows of the selection with their subtrees, whatever columns are selected.
    // A published Issue is refused by Core and points at 完了・中止・この版から外す (#231, #247).
    private Task DeleteRows() => DeleteRows(RangeRows(Range));
    internal async Task DeleteRows(IReadOnlyList<string> rows)
    {
        if (rows.Count == 0) throw new ArgumentException("削除する行を選択してください。");
        if (rows.Any(id => !Rows.ContainsKey(id))) throw new ArgumentException("空行を除いて範囲を選択してください。");
        // As in Excel, the selection stays where the deleted rows were: on the next row, or the previous
        // one when the deleted rows were last (#247).
        var before = RowIds.ToArray(); var first = before.Select((id, i) => (id, i)).Where(p => rows.Contains(p.id)).Select(p => p.i).DefaultIfEmpty(0).Min();
        Check(await Session.Execute(new DeletePlanRows([.. rows]), Today));
        wholeRowSelection = false;
        var remaining = Session.Document.State.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        if (before.Skip(first).Concat(before.Take(first).Reverse()).FirstOrDefault(remaining.Contains) is { } next) selected = anchor = next;
        Refresh();
    }
    internal void RevealAddedRow(string identity)
    {
        activeFilter = PlanFilter.None; filterContext = [];
        rendering = true;
        try { filter.Text = ""; }
        finally { rendering = false; }
        selected = anchor = identity; selectedField = anchorField = PlanField.Title; wholeRowSelection = false; Refresh(); FocusSelected();
    }
    private async Task Indent(bool outdent)
    {
        var rows = RangeRows(Range); Check(await Session.Execute(new IndentPlanRows(rows.ToImmutableArray(), outdent), Today)); Refresh();
    }
    private sealed record Drag(bool Fill, string Source, string Field, uint PointerId, PlanState InitialState)
    {
        internal PlanState State = InitialState;
        internal bool Move, Ready;
        internal ImmutableArray<string> Targets = [];
        internal PlanOutlineGaps? Gaps;
        internal PlanOutlineGap? Destination;
        internal string? MoveReason;
        internal string MoveScope = "";
        internal int MoveCount;
        internal Point Position;
        internal string End = Source;
        internal bool Moved;
        // A press inside the selected rows keeps them for a drag; a plain click then narrows to the pressed row.
        internal bool Narrow;
    }
    private Drag? drag;
    private UIElement? capture;
    private readonly DispatcherTimer dragScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private void InitializeInteraction()
    {
        // #228: the grid edge chords and F5 are handled once for the whole list, before a cell's own keys.
        List.PreviewKeyDown += EdgeKeyDown;
        // The selection line's impact links (#236 part B): filter to the delayed successors, or move to the
        // predecessor that caused the delay.
        successorLink.Click += (_, _) => {
            if (selected.Length == 0) return;
            var ids = DelayedSuccessorsOf(selected).ToHashSet(StringComparer.Ordinal);
            if (ids.Count > 0) FilterTasks(ids, $"後続 {ids.Count}");
        };
        predecessorLink.Click += (_, _) => {
            if (LatenessOf(selected)?.CausePredecessorRowId is not { } rowId || IdentityOfRow(rowId) is not { Length: > 0 } identity) return;
            ClearFilters();
            SelectAndFocus(identity, PlanField.Title, false);
        };
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, args) => {
            if (drag is not { } operation || args.Pointer.PointerId != operation.PointerId) return;
            UpdateDrag(args.GetCurrentPoint(this).Position); args.Handled = true;
        }), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(EndDrag), true);
        PointerCaptureLost += (_, _) => CancelDrag(); PointerCanceled += (_, _) => CancelDrag();
        dragScroll.Tick += (_, _) => {
            if (drag is not { } operation) return;
            var scroll = Descendants(List).OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is null) return;
            var top = List.TransformToVisual(this).TransformPoint(new()).Y;
            var distance = operation.Position.Y < top + 28 ? -28 : operation.Position.Y > top + List.ActualHeight - 28 ? 28 : 0;
            if (distance != 0) { scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + distance, 0, scroll.ScrollableHeight), null, true); UpdateDrag(operation.Position); }
        };
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    internal void BeginRange(string identity, string field, UIElement target, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Select(identity, field, (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
        target.Focus(FocusState.Pointer);
        if (target is TextBox cell) cell.SelectAll();
        StartDrag(false, identity, field, target, args);
    }
    internal void BeginFill(string identity, string field, UIElement target, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!Rows.ContainsKey(identity) || Display(identity, field).Length == 0 || ReadOnly(identity, field)) return;
        try { RequireNoPending([identity], [field]); }
        catch (InvalidOperationException ex) { error.Text = ex.Message; return; }
        StartDrag(true, identity, field, target, args);
    }
    private void StartDrag(bool fill, string identity, string field, UIElement target, PointerRoutedEventArgs args)
    {
        CancelDrag();
        if (!target.CapturePointer(args.Pointer)) return;
        capture = target; drag = new(fill, identity, field, args.Pointer.PointerId, Session.Document.State) { Position = args.GetCurrentPoint(this).Position };
        args.Handled = true; dragScroll.Start();
    }
    private void UpdateDrag(Point point)
    {
        if (drag is not { } operation) return;
        if (operation.Move && !operation.Ready) return;
        if (!ReferenceEquals(operation.State, Session.Document.State)) { CancelDrag(); return; }
        if (!operation.Moved && Math.Abs(point.Y - operation.Position.Y) < 4 && Math.Abs(point.X - operation.Position.X) < 4) return;
        operation.Moved = true; operation.Position = point;
        var scroll = Descendants(List).OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        var top = List.TransformToVisual(this).TransformPoint(new()).Y;
        if (operation.Move) {
            var gap = (point.Y - top + scroll.VerticalOffset) / RowHeight;
            if (point.Y < top || point.Y > top + List.ActualHeight || point.X < 0 || point.X > SheetViewport) gap = -1;
            UpdateMoveDrag(operation, gap, scroll.VerticalOffset); return;
        }
        var index = Math.Clamp((int)((point.Y - top + scroll.VerticalOffset) / RowHeight), 0, RowIds.Count - 1);
        operation.End = RowIds[index];
        if (operation.Fill)
        {
            selected = operation.End; anchor = operation.Source; selectedField = anchorField = operation.Field;
            wholeRowSelection = false;
            selection.Text = $"{Range.RowCount}行へコピー"; RefreshRealized();
        }
        else
        {
            ExtendRangeTo(operation.End, point);
        }
    }
    internal void ExtendRangeTo(string identity, Point point)
    {
        var x = point.X + SheetOffset;
        var column = 0;
        while (column < VisibleColumns.Length - 1 && x >= VisibleColumns[column].Width) { x -= VisibleColumns[column].Width; column++; }
        var field = VisibleColumns[column].Field ?? VisibleColumns.Skip(column).Select(c => c.Field).FirstOrDefault(f => f is not null)
            ?? VisibleColumns.Take(column).Reverse().Select(c => c.Field).FirstOrDefault(f => f is not null);
        if (field is not null) Select(identity, field, true);
    }
    internal async void EndDrag(object sender, PointerRoutedEventArgs args)
    {
        if (drag is not { } operation || operation.PointerId != args.Pointer.PointerId) return;
        UpdateDrag(args.GetCurrentPoint(this).Position);
        var valid = ReferenceEquals(operation.State, Session.Document.State);
        drag = null; dragScroll.Stop(); capture?.ReleasePointerCaptures(); capture = null; args.Handled = true;
        moveIndicator.Visibility = Visibility.Collapsed;
        if (operation.Move && !disposed) UpdateReason();
        if (operation.Move && operation.Narrow && !operation.Moved && !disposed) SelectWholeRow(operation.Source, false);
        if (valid && operation.Move && operation.Moved && operation.Destination is { } destination) {
            await Run(async () => {
                await CommitPending();
                if (MoveUnavailable is { } reason) throw new ArgumentException(reason);
                if (ReferenceEquals(operation.State, Session.Document.State)) await ExecuteOutline(new MovePlanSubtrees(operation.Targets, destination.Before));
            }, "Move outline");
        }
        else if (valid && operation.Fill && operation.Moved)
        {
            await Run(async () => {
                var range = Range; var targets = RangeRows(range); RequireNoPending(targets, [operation.Field]);
                Check(await Session.Execute(FillCommand(PlanOperationKind.Fill, operation.Source, targets, operation.Field), Today));
                Refresh();
            });
        }
    }
    internal void CancelDrag()
    {
        if (drag is not { } operation) return;
        drag = null; dragScroll.Stop(); capture?.ReleasePointerCaptures(); capture = null;
        moveIndicator.Visibility = Visibility.Collapsed;
        if (operation.Move && !disposed) UpdateReason();
        if (operation.Fill && !disposed) Select(operation.Source, operation.Field, false);
    }
}

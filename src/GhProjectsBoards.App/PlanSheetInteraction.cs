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
    private PlanField selectedField = PlanField.Title, anchorField = PlanField.Title;
    private PlanField[] Fields => VisibleColumns.Where(c => c.Field is not null).Select(c => c.Field!.Value).ToArray();
    internal sealed record Edge(int From, int To, DateOnly End, DateOnly Start);
    internal IReadOnlyList<Edge> Edges { get; private set; } = [];
    internal static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    private CellRange Range
    {
        get
        {
            var a = Math.Max(0, RowIds.IndexOf(anchor)); var b = Math.Max(0, RowIds.IndexOf(selected));
            var c = Math.Max(0, Array.IndexOf(Fields, anchorField)); var d = Math.Max(0, Array.IndexOf(Fields, selectedField));
            return new(Math.Min(a, b), Math.Min(c, d), Math.Abs(a - b) + 1, Math.Abs(c - d) + 1);
        }
    }
    private void ReconcileSelection()
    {
        var previous = selected;
        if (!RowIds.Contains(selected)) selected = VisibleAncestor(selected) ?? RowIds.FirstOrDefault() ?? "";
        if (!RowIds.Contains(anchor)) anchor = selected;
        if (!Fields.Contains(selectedField)) selectedField = Fields.FirstOrDefault();
        if (!Fields.Contains(anchorField)) anchorField = selectedField;
        var indexes = RowIds.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        Edges = RowIds.Where(Rows.ContainsKey).SelectMany(id => Rows[id].Predecessors
            .Where(p => indexes.ContainsKey(p) && Schedule.GetValueOrDefault(p)?.End.Value is not null && Schedule[id].Start.Value is not null)
            .Select(p => new Edge(indexes[p], indexes[id], Schedule[p].End.Value!.Value, Schedule[id].Start.Value!.Value))).ToArray();
        if (previous.Length > 0 && previous != selected && filter.FocusState == FocusState.Unfocused) FocusSelected();
    }
    internal bool IsSelected(string identity, PlanField field)
    {
        var range = Range; var row = RowIds.IndexOf(identity); var column = Array.IndexOf(Fields, field);
        return row >= range.Row && row < range.Row + range.RowCount && column >= range.Column && column < range.Column + range.ColumnCount;
    }
    internal bool IsSelectedRow(string identity) => identity == selected;
    internal bool IsRangeEnd(string identity, PlanField field) => identity == selected && field == selectedField;
    internal void Select(string identity, PlanField field, bool extend)
    {
        if (!RowIds.Contains(identity) || !Fields.Contains(field)) return;
        selected = identity; selectedField = field;
        if (!extend) { anchor = identity; anchorField = field; }
        EnsureColumnVisible(field);
        // First selection can precede the horizontal viewport's initial layout.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (!disposed && IsLoaded && selectedField == field) EnsureColumnVisible(field);
        });
        UpdateReason(); RefreshOverviewCommands(); RefreshRealized();
    }
    private void EnsureColumnVisible(PlanField field)
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
    internal string RemoteProblem(string identity)
    {
        var sync = Session.Document.Sync;
        if (sync.Unverified.Contains(identity)) return "未検証 — 最新の情報に更新で確認";
        var failures = sync.Failures.Where(f => f.Identity == identity).Select(PlanPublishText.Failure).Distinct().ToArray();
        if (failures.Length > 0) return (sync.Failures.Where(f => f.Identity == identity).All(f => f.Reason == "NotDispatched") ? "未送信: " : "発行失敗: ") + string.Join(" / ", failures);
        return sync.Unavailable.Contains(identity) ? "GitHubで取得できません — 発行で確認" : "";
    }
    private void UpdateReason()
    {
        selection.Text = PlanIds.TryGetValue(selected, out var number) ? $"ID {number}" : "";
        selectedTitle.Text = Rows.GetValueOrDefault(selected)?.Title ?? "";
        ToolTipService.SetToolTip(selectedTitle, selectedTitle.Text);
        AutomationProperties.SetName(selection, selection.Text + " " + selectedTitle.Text);
        var hasSelection = Schedule.TryGetValue(selected, out var task);
        emptyHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        totals.Text = hasSelection ? "" : LatenessCounts(OverdueTasks, LaterTasks);
        totals.Visibility = totals.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(totals, totals.Text);
        var late = LatenessOf(selected);
        slip.Text = "";
        latenessDetail.Text = "";
        if (late is not null && task is not null) {
            if (task.IsSummary) {
                var counts = LatenessCounts(late.OverdueDescendantTasks, late.LaterDescendantTasks);
                latenessDetail.Text = counts.Length > 0 ? "配下: " + counts : "";
            }
            if (!task.IsSummary && late.OverdueKind is { } kind && late.MissedPublishedDate is { } missed) {
                var missedDate = missed.ToString("M/d", CultureInfo.InvariantCulture);
                slip.Text = kind == PlanOverdueKind.Finish ? $"完了予定 {missedDate} を過ぎて未完了" : $"開始予定 {missedDate} を過ぎて未着手";
                if (late.DaysLater is { } days) slip.Text += $" · +{days} 日";
            } else if (late.DaysLater is { } days && PublishedEnd(selected) is { } published) {
                slip.Text = $"発行済み {published.ToString("M/d", CultureInfo.InvariantCulture)} から +{days} 日";
                if (!task.IsSummary && late.StartDelayedByPredecessor) latenessDetail.Text = "先行の遅れによる";
            }
        }
        slipPill.Background = late?.Level == PlanLatenessLevel.Overdue ? Brush("GanttLateTintBrush") : null;
        slipPill.BorderBrush = Brush("SystemFillColorCriticalBrush");
        slipPill.BorderThickness = new(late?.Level == PlanLatenessLevel.Overdue ? 0 : 1);
        slipPill.Visibility = slip.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        latenessDetail.Visibility = latenessDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(slip, slip.Text);
        AutomationProperties.SetName(slipPill, slip.Text);
        AutomationProperties.SetName(latenessDetail, latenessDetail.Text);
        AutomationProperties.SetName(selectedTitle, selectedTitle.Text);
        if (Session.Document.Sync.IssueLinks.TryGetValue(selected, out var link) && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == Session.Document.Project.Scope.Host) {
            issueLink.Content = link.Caption; issueLink.NavigateUri = uri; issueLink.Visibility = Visibility.Visible;
        } else { issueLink.Content = ""; issueLink.NavigateUri = null; issueLink.Visibility = Visibility.Collapsed; }
        AutomationProperties.SetName(issueLink, issueLink.Content?.ToString() ?? "");
        reason.Text = task is not null ? (task.StartReason is "子タスクの集計" or "完了" or "日程固定" or "工数なし" or "入力エラー" ? task.StartReason : "開始: " + task.StartReason) : "";
        if (task is not null && PlanLateness.EndReason(task, StatusDate) is { } endReason) reason.Text += " · " + endReason;
        if (task?.Warnings.Count > 0) reason.Text += " · " + string.Join(" / ", task.Warnings);
        var remoteProblem = RemoteProblem(selected);
        if (remoteProblem.Length > 0) reason.Text += " · " + remoteProblem;
        AutomationProperties.SetName(reason, reason.Text);
        if (Problems.TryGetValue((selected, selectedField), out var problem)) error.Text = problem;
        ToolTipService.SetToolTip(reason, reason.Text);
    }
    private static string LatenessCounts(int overdue, int later) =>
        overdue > 0 ? $"期限超過 {overdue}" + (later > 0 ? $" · 予定より遅れ {later}" : "") : later > 0 ? $"予定より遅れ {later}" : "";
    internal void MoveSelection(VirtualKey key, bool extend)
    {
        var row = Math.Max(0, RowIds.IndexOf(selected)); var column = Math.Max(0, Array.IndexOf(Fields, selectedField));
        if (key == VirtualKey.Up) row--; if (key == VirtualKey.Down) row++;
        if (key == VirtualKey.Left) column--; if (key == VirtualKey.Right) column++;
        if (Fields.Length == 0) return;
        Select(RowIds[Math.Clamp(row, 0, RowIds.Count - 1)], Fields[Math.Clamp(column, 0, Fields.Length - 1)], extend);
        FocusSelected();
    }
    private (string Identity, PlanField Field)? requestedFocus;
    private void FocusSelected()
    {
        requestedFocus = (selected, selectedField);
        List.LayoutUpdated -= FocusAfterLayout;
        List.LayoutUpdated += FocusAfterLayout;
        List.ScrollIntoView(selected);
        TryRestoreFocus();
    }
    private void FocusAfterLayout(object? sender, object args) => TryRestoreFocus();
    private void TryRestoreFocus()
    {
        if (requestedFocus is not { } target || disposed) return;
        // A container from a replaced item source can still be loaded with the same task.
        // Focus placed there falls to the next tab stop when the list discards it.
        var row = (List.ContainerFromItem(target.Identity) as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
        var cell = row?.Cells.FirstOrDefault(c => c.Field == target.Field);
        if (cell is not { IsLoaded: true, ActualWidth: > 0 } || !cell.Focus(FocusState.Keyboard)) return;
        requestedFocus = null;
        List.LayoutUpdated -= FocusAfterLayout;
    }
    private void CancelRequestedFocus()
    {
        requestedFocus = null;
        List.LayoutUpdated -= FocusAfterLayout;
    }
    internal async Task CommitCellAndNavigate(string identity, PlanField field, string text, long generation, string originalText, bool across, bool reverse)
    {
        await Commit(identity, field, text, generation, originalText);
        if (Pending.ContainsKey((identity, field)) || disposed) return;
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
    internal Task KeyboardCommand(VirtualKey key) => Run(async () => {
        switch (key)
        {
            case VirtualKey.C: await Copy(); break;
            case VirtualKey.V: await Paste(); break;
            case VirtualKey.D: await Fill(PlanOperationKind.CtrlD); break;
            case VirtualKey.Delete: await Clear(); break;
            case VirtualKey.Z: await ChangeHistory(false); break;
            case VirtualKey.Y: await ChangeHistory(true); break;
        }
    });
    private string[] RangeRows(CellRange range)
    {
        var rows = RowIds.Skip(range.Row).Take(range.RowCount).ToArray();
        if (rows.Any(id => !Rows.ContainsKey(id))) throw new ArgumentException("空行を除いて範囲を選択してください。");
        return rows;
    }
    private void RequireNoPending(IEnumerable<string> rows, IEnumerable<PlanField> fields)
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
    private sealed record SheetCopy(ScopedId Project, PlanField[] Fields, string[] Identities, string[][] Values);
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
    private async Task Copy()
    {
        var range = Range; var rows = RangeRows(range); var fields = Fields.Skip(range.Column).Take(range.ColumnCount).ToArray();
        RequireNoPending(rows, fields);
        var text = string.Join("\n", rows.Select(id => string.Join("	", fields.Select(f => EditForm(id, f)))));
        var typed = rows.Select(id => fields.Select(field => JsonSerializer.Serialize(CopyValue(id, field), PlanJson.Options)).ToArray()).ToArray();
        var content = new PlanClipboardContent(text, JsonSerializer.Serialize(new SheetCopy(Session.Document.Project, fields, rows, typed), PlanJson.Options));
        var write = writeClipboard;
        await WithClipboard(token => { write(content, token); return Task.FromResult(true); });
    }
    private async Task Paste()
    {
        var range = Range; var visible = RowIds.Where(Rows.ContainsKey).ToArray(); var columns = Fields;
        var content = await WithClipboard(readClipboard);
        var text = content.Text;
        EditPlanCells command;
        if (content.Metadata is { } metadata)
        {
            SheetCopy copied;
            try { copied = JsonSerializer.Deserialize<SheetCopy>(metadata, PlanJson.Options) ?? throw new ArgumentException("コピー元を確認できません。"); }
            catch (JsonException ex) { throw new ArgumentException("コピー元を確認できません。", ex); }
            if (copied.Project != Session.Document.Project) throw new ArgumentException("別Projectの内部コピーは貼り付けできません。");
            RequireNoPending(copied.Identities, copied.Fields);
            if (copied.Values.Length == 0 || copied.Values.Any(row => row is null || row.Length != copied.Fields.Length))
                throw new ArgumentException("コピー元の形状を確認できません。");
            var matrix = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.Split('\t')).ToArray();
            if (copied.Values.Length != matrix.Length || matrix.Any(row => row.Length != copied.Fields.Length))
                throw new ArgumentException("コピー元の形状を確認できません。");
            var target = PlanSheetEditing.PasteRange(range, copied.Values.Length, copied.Fields.Length, visible.Length, columns.Length);
            var single = copied.Values.Length == 1 && copied.Fields.Length == 1;
            var changes = ImmutableArray.CreateBuilder<PlanCellChange>();
            for (var r = 0; r < target.RowCount; r++)
                for (var c = 0; c < target.ColumnCount; c++)
                {
                    var sr = single ? 0 : r; var sc = single ? 0 : c;
                    var field = columns[target.Column + c]; var source = copied.Fields[sc];
                    string Kind(PlanField f) => f is PlanField.Estimate or PlanField.Remaining or PlanField.Actual ? "number"
                        : f is PlanField.Start or PlanField.End or PlanField.StartNoEarlierThan ? "date" : f.ToString();
                    if (!Enum.IsDefined(source) || Kind(source) != Kind(field)) throw new ArgumentException("コピー元と対象の列の型が一致しません。");
                    if (matrix[sr][sc].Length == 0) continue;
                    var identity = visible[target.Row + r];
                    var parsed = PlanValues.Set(Rows[identity], field, copied.Values[sr][sc]);
                    changes.Add(new(identity, field, PlanOperations.Value(parsed, field)));
                }
            command = new(PlanOperationKind.Paste, changes.ToImmutable());
        }
        else command = PlanSheetEditing.Paste(Session.Document, visible, columns, range, text);
        foreach (var change in command.Cells) RequireNoPending([change.Identity], [change.Field]);
        Check(await Session.Execute(command, Today)); Refresh();
    }
    private object? CopyValue(string identity, PlanField field) => field is PlanField.Assignees or PlanField.Predecessors
        ? PlanOperations.Value(Rows[identity], field) : PlanSheetEditing.Parse(Session.Document, field, EditForm(identity, field));
    private EditPlanCells FillCommand(PlanOperationKind kind, string source, IEnumerable<string> targets, PlanField field)
        => new(kind, targets.Where(id => id != source).Select(id => new PlanCellChange(id, field, CopyValue(source, field))).ToImmutableArray());
    internal async Task Fill(PlanOperationKind kind)
    {
        var range = Range; var rows = RangeRows(range);
        if (range.ColumnCount != 1 || rows.Length < 2) throw new ArgumentException("同じ列の2行以上を選択してください。");
        var field = Fields[range.Column]; RequireNoPending(rows, [field]);
        if (Display(rows[0], field).Length == 0) throw new ArgumentException("空値からコピーできません。クリアを使ってください。");
        Check(await Session.Execute(FillCommand(kind, rows[0], rows.Skip(1), field), Today)); Refresh();
    }
    private async Task Clear()
    {
        var range = Range; var rows = RangeRows(range); var fields = Fields.Skip(range.Column).Take(range.ColumnCount).ToImmutableArray();
        RequireNoPending(rows, fields);
        Check(await Session.Execute(new ClearPlanCells(rows.ToImmutableArray(), fields), Today)); Refresh();
    }
    private async Task Insert()
    {
        if (Session.Document.State.Settings.DefaultRepository is not { Length: > 0 } repository)
            throw new ArgumentException("設定で既定リポジトリを選んでください。");
        var row = PlanRow.New("", repository);
        Check(await Session.Execute(new InsertPlanRows([row], selected.Length == 0 ? null : selected), Today));
        RevealAddedRow(row.Identity);
    }
    internal void RevealAddedRow(string identity)
    {
        acceptedFilter = "";
        rendering = true;
        try { filter.Text = ""; }
        finally { rendering = false; }
        selected = anchor = identity; selectedField = anchorField = PlanField.Title; Refresh(); FocusSelected();
    }
    private async Task Indent(bool outdent)
    {
        var rows = RangeRows(Range); Check(await Session.Execute(new IndentPlanRows(rows.ToImmutableArray(), outdent), Today)); Refresh();
    }
    private sealed record Drag(bool Fill, string Source, PlanField Field, uint PointerId, PlanState State)
    {
        internal Point Position;
        internal string End = Source;
        internal bool Moved;
    }
    private Drag? drag;
    private UIElement? capture;
    private readonly DispatcherTimer dragScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private void InitializeInteraction()
    {
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
    internal void BeginRange(string identity, PlanField field, UIElement target, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Select(identity, field, (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
        target.Focus(FocusState.Pointer);
        if (target is TextBox cell) cell.SelectAll();
        StartDrag(false, identity, field, target, args);
    }
    internal void BeginFill(string identity, PlanField field, UIElement target, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!Rows.ContainsKey(identity) || Display(identity, field).Length == 0 || ReadOnly(identity, field)) return;
        try { RequireNoPending([identity], [field]); }
        catch (InvalidOperationException ex) { error.Text = ex.Message; return; }
        StartDrag(true, identity, field, target, args);
    }
    private void StartDrag(bool fill, string identity, PlanField field, UIElement target, PointerRoutedEventArgs args)
    {
        CancelDrag();
        if (!target.CapturePointer(args.Pointer)) return;
        capture = target; drag = new(fill, identity, field, args.Pointer.PointerId, Session.Document.State) { Position = args.GetCurrentPoint(this).Position };
        args.Handled = true; dragScroll.Start();
    }
    private void UpdateDrag(Point point)
    {
        if (drag is not { } operation) return;
        if (!ReferenceEquals(operation.State, Session.Document.State)) { CancelDrag(); return; }
        if (!operation.Moved && Math.Abs(point.Y - operation.Position.Y) < 4 && Math.Abs(point.X - operation.Position.X) < 4) return;
        operation.Moved = true; operation.Position = point;
        var scroll = Descendants(List).OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        var top = List.TransformToVisual(this).TransformPoint(new()).Y;
        var index = Math.Clamp((int)((point.Y - top + scroll.VerticalOffset) / RowHeight), 0, RowIds.Count - 1);
        operation.End = RowIds[index];
        if (operation.Fill)
        {
            selected = operation.End; anchor = operation.Source; selectedField = anchorField = operation.Field;
            selection.Text = $"{Range.RowCount}行へコピー"; RefreshRealized();
        }
        else
        {
            ExtendRangeTo(operation.End, point);
        }
    }
    internal void ExtendRangeTo(string identity, Point point)
    {
        var x = point.X + SheetOffset - VisibleColumns.TakeWhile(c => c.Field is null).Sum(c => c.Width);
        var column = 0; var widths = VisibleColumns.Where(c => c.Field is not null).ToArray();
        while (column < widths.Length - 1 && x >= widths[column].Width) { x -= widths[column].Width; column++; }
        if (widths.Length > 0) Select(identity, widths[column].Field!.Value, true);
    }
    internal async void EndDrag(object sender, PointerRoutedEventArgs args)
    {
        if (drag is not { } operation || operation.PointerId != args.Pointer.PointerId) return;
        UpdateDrag(args.GetCurrentPoint(this).Position);
        var valid = ReferenceEquals(operation.State, Session.Document.State);
        drag = null; dragScroll.Stop(); capture?.ReleasePointerCaptures(); capture = null; args.Handled = true;
        if (valid && operation.Fill && operation.Moved)
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
        if (operation.Fill && !disposed) Select(operation.Source, operation.Field, false);
    }
}

using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView : Grid
{
    internal readonly PlanSession Session;
    private bool remoteBusy;
    private CommandBar commandBar = null!;
    internal void SetRemoteBusy(bool value)
    {
        remoteBusy = value;
        commandBar.IsEnabled = filter.IsEnabled = zoom.IsEnabled = statusDate.IsEnabled = !value;
        RefreshRealized();
    }

    internal readonly HashSet<PlanSheetRow> Realized = [];
    internal readonly ListView List = Id(new ListView { SelectionMode = ListViewSelectionMode.None, Padding = new(0) }, "PlanTasks");
    internal sealed record Input(string Text, long Generation, string OriginalText);
    private long inputGeneration;
    internal readonly Dictionary<(string Identity, PlanField Field), Input> Pending = [];
    private readonly PlanInputProblem inputProblem = new("SheetInputProblem");
    internal void SetInput(string identity, PlanField field, string text, string originalText) => Pending[(identity, field)] = new(text, ++inputGeneration, originalText);
    internal long Generation(string identity, PlanField field) => Pending.GetValueOrDefault((identity, field))?.Generation ?? 0;
    internal readonly Dictionary<(string Identity, PlanField Field), string> Problems = [];
    internal IReadOnlyList<string> RowIds { get; private set; } = [];
    internal Dictionary<string, int> PlanIds { get; private set; } = [];
    internal Dictionary<string, PlanRow> Rows { get; private set; } = [];
    internal Dictionary<string, ScheduledTask> Schedule { get; private set; } = [];
    internal PlanUnpublished Unpublished { get; private set; } = new(ImmutableDictionary<string, ImmutableArray<PlanField>>.Empty);
    internal sealed record Column(PlanField? Field, string Label, double Width);
    internal static readonly Column[] Columns = [
        new(null, "ID", 32), new(PlanField.Title, "タイトル", 128), new(PlanField.Assignees, "担当者", 80),
        new(PlanField.Estimate, "Estimate", 60), new(PlanField.Remaining, "Remaining", 70), new(PlanField.Actual, "Actual", 52),
        new(PlanField.Start, "start", 80), new(PlanField.End, "end", 80), new(PlanField.Predecessors, "先行タスク", 72),
        new(PlanField.StartNoEarlierThan, "開始日指定", 116), new(PlanField.Fixed, "日程固定", 88), new(PlanField.Status, "Status", 110)];
    internal readonly HashSet<PlanField?> Hidden = [PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status];
    internal Column[] VisibleColumns { get; private set; } = Columns.Take(9).ToArray();
    internal double RowHeight { get; private set; } = 28;
    private double? dividerWidth;
    private PlanSheetDivider divider = null!;
    internal double SheetWidth => VisibleColumns.Sum(c => c.Width);
    internal double SheetViewport { get; private set; }
    internal double ChartViewport { get; private set; }
    internal double SheetOffset => sheetHorizontal.HorizontalOffset;
    internal double ChartOffset => chartHorizontal.HorizontalOffset;
    internal double DayWidth { get; private set; } = 24;
    internal DateOnly FirstDay { get; private set; }
    internal int DayCount { get; private set; } = 365;
    internal DateOnly StatusDate => Session.Document.State.Settings.StatusDate ?? DateOnly.FromDateTime(DateTime.Today);
    internal static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private readonly ScrollViewer sheetHorizontal = Horizontal("PlanSheetHorizontal"), chartHorizontal = Horizontal("PlanGanttHorizontal");
    private readonly Grid headers = new(), scrollbars = new(), sheetClip = new() { Height = 40 };
    private readonly StackPanel sheetHead = new() { Orientation = Orientation.Horizontal };
    private readonly Canvas chartHead = new() { Height = 40 };
    private readonly TextBlock reason = Id(new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }, "PlanStartReason");
    private readonly TextBlock error = Id(new TextBlock { TextWrapping = TextWrapping.Wrap }, "PlanSheetError");
    private readonly Button retrySave = Id(new Button { Content = "保存を再試行", Visibility = Visibility.Collapsed }, "PlanSheetRetrySave");
    private string? headerKey, timelineKey;
    private string acceptedFilter = "";
    private int acceptedZoom;
    private readonly TextBlock selection = Id(new TextBlock(), "PlanSheetSelection");
    private readonly CalendarDatePicker statusDate = Id(new CalendarDatePicker { MinWidth = 135 }, "PlanStatusDate");
    private readonly ComboBox zoom = Id(new ComboBox { ItemsSource = new[] { "日", "週", "月" }, SelectedIndex = 0, MinWidth = 65 }, "PlanGanttZoom");
    private readonly TextBox filter = Id(new TextBox { PlaceholderText = "タイトルで絞り込み", Width = 170 }, "PlanSheetFilter");
    private readonly PlanFrameMetrics metrics = new();
    private int pendingFrame;
    private bool rendering, disposed;
    private CancellationTokenSource lifetime = new();
    private Task tail = Task.CompletedTask;
    private long commandSequence;
    private readonly Dictionary<long, string> commands = [];
    private bool frameSubscribed;
    internal string WorkDescription => $"loaded={IsLoaded}, disposed={disposed}, commands=[{string.Join(", ", commands.Values)}], tail={tail.Status}, clipboard={clipboardWork}, dragTimer={dragScroll.IsEnabled}, frame={frameSubscribed}, focusTarget={requestedFocus}, pendingCells={Pending.Count}, realizedRows={Realized.Count}, zoomOpen={zoom.IsDropDownOpen}, calendarOpen={statusDate.IsCalendarOpen}";
    internal event Action? Changed;
    internal PlanSheetView(PlanSession session, Func<Task<PlanClipboardContent>>? readClipboard = null, Action<PlanClipboardContent>? writeClipboard = null, Func<Task>? importCsv = null)
    {
        Session = session;
        this.readClipboard = readClipboard is null ? ReadClipboard : _ => readClipboard();
        this.writeClipboard = writeClipboard is null ? WriteClipboard : (content, _) => writeClipboard(content);
        for (var r = 0; r < 4; r++) RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var dates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dates.Children.Add(new TextBlock { Text = "状況日", VerticalAlignment = VerticalAlignment.Center }); dates.Children.Add(statusDate);
        var scales = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        scales.Children.Add(new TextBlock { Text = "ガント", VerticalAlignment = VerticalAlignment.Center }); scales.Children.Add(zoom);
        controls.Children.Add(dates); controls.Children.Add(scales); SetColumn(scales, 1);
        filter.HorizontalAlignment = HorizontalAlignment.Right; controls.Children.Add(filter); SetColumn(filter, 2);
        AutomationProperties.SetName(statusDate, "状況日"); AutomationProperties.SetName(zoom, "ガントの表示単位"); AutomationProperties.SetName(filter, "タイトルで絞り込み");
        Children.Add(controls);
        var commands = commandBar = Id(new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch }, "PlanSheetCommands");
        AddCommand(commands, "コピー", "PlanSheetCopy", Symbol.Copy, Copy);
        AddCommand(commands, "貼り付け", "PlanSheetPaste", Symbol.Paste, Paste);
        AddCommand(commands, "下へコピー", "PlanSheetFillDown", Symbol.Download, () => Fill(PlanOperationKind.CtrlD));
        AddCommand(commands, "クリア", "PlanSheetClear", Symbol.Clear, Clear);
        commands.PrimaryCommands.Add(new AppBarSeparator());
        AddCommand(commands, "元に戻す", "PlanSheetUndo", Symbol.Undo, () => ChangeHistory(false));
        AddCommand(commands, "やり直す", "PlanSheetRedo", Symbol.Redo, () => ChangeHistory(true));
        AddCommand(commands, "行を挿入", "PlanSheetInsert", Symbol.Add, Insert);
        if (importCsv is not null) AddCommand(commands, "CSVから追加", "PlanSheetCsv", Symbol.OpenFile, importCsv, queueInSheet: false);
        AddCommand(commands, "インデント", "PlanSheetIndent", Symbol.Forward, () => Indent(false));
        AddCommand(commands, "アウトデント", "PlanSheetOutdent", Symbol.Back, () => Indent(true));
        var columns = Id(new AppBarButton { Label = "列", Icon = new SymbolIcon(Symbol.List) }, "PlanSheetColumns");
        AutomationProperties.SetName(columns, "表示列"); ToolTipService.SetToolTip(columns, "表示列");
        var choices = new StackPanel { Spacing = 4 };
        foreach (var column in Columns)
        {
            var toggle = Id(new CheckBox { Content = column.Label, IsChecked = !Hidden.Contains(column.Field) }, "PlanColumn" + (column.Field?.ToString() ?? "Id"));
            var synchronizing = false;
            async void VisibilityChanged(object sender, RoutedEventArgs args) {
                if (synchronizing) return;
                var proposed = toggle.IsChecked == true;
                await Run(async () => {
                await CommitPending();
                if (proposed) Hidden.Remove(column.Field); else Hidden.Add(column.Field);
                VisibleColumns = Columns.Where(c => !Hidden.Contains(c.Field)).ToArray();
                if (VisibleColumns.Length == 0) { Hidden.Remove(column.Field); VisibleColumns = Columns.Where(c => !Hidden.Contains(c.Field)).ToArray(); }
                RefreshLayout(); ReconcileSelection(); }, "Column visibility");
                synchronizing = true;
                try { toggle.IsChecked = !Hidden.Contains(column.Field); }
                finally { synchronizing = false; }
                if (Problems.Count > 0) { columns.Flyout?.Hide(); FocusSelected(); }
            }
            toggle.Checked += VisibilityChanged; toggle.Unchecked += VisibilityChanged;
            choices.Children.Add(toggle);
        }
        columns.Flyout = new Flyout { Content = choices }; commands.PrimaryCommands.Add(columns);
        commands.Content = selection;
        Children.Add(commands); SetRow(commands, 1);
        var feedback = new StackPanel { Spacing = 2 }; feedback.Children.Add(reason); feedback.Children.Add(error); feedback.Children.Add(retrySave);
        retrySave.Click += async (_, _) => await Run(async () => { Check(await Session.RetrySaveAsync()); }, "Retry save");
        Children.Add(feedback); SetRow(feedback, 2);
        foreach (var grid in new[] { headers, scrollbars })
        { grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); }
        sheetClip.Children.Add(sheetHead);
        headers.Children.Add(sheetClip); headers.Children.Add(chartHead); SetColumn(chartHead, 1);
        chartHead.Clip = new RectangleGeometry();
        Children.Add(headers); SetRow(headers, 3);
        List.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["PlanSheetRowsPanel"];
        List.ItemTemplate = (DataTemplate)Application.Current.Resources["PlanSheetRowTemplate"];
        List.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(Control.BorderThicknessProperty, new Thickness(0)),
            new Setter(FrameworkElement.MinHeightProperty, 0d),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        ScrollViewer.SetHorizontalScrollMode(List, ScrollMode.Disabled); ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        Children.Add(List); SetRow(List, 4);
        sheetHorizontal.Content = new Border { Height = 1 }; chartHorizontal.Content = new Border { Height = 1 };
        scrollbars.Children.Add(sheetHorizontal); scrollbars.Children.Add(chartHorizontal); SetColumn(chartHorizontal, 1);
        Children.Add(scrollbars); SetRow(scrollbars, 5);
        divider = Id(new PlanSheetDivider(() => SheetViewport, () => Math.Max(160, Math.Max(320, ActualWidth - 20) - 230), value => {
            dividerWidth = value; RefreshLayout();
        }) { Width = 6, HorizontalAlignment = HorizontalAlignment.Left }, "PlanSheetDivider");
        AutomationProperties.SetName(divider, "シートとガントの幅");
        ToolTipService.SetToolTip(divider, "ドラッグまたは左右キーで幅を変更");
        Children.Add(divider); SetRow(divider, 3); SetRowSpan(divider, 3);
        sheetHorizontal.ViewChanged += (_, _) => { sheetHead.RenderTransform = new TranslateTransform { X = -SheetOffset }; RefreshRealized(); };
        chartHorizontal.ViewChanged += (_, _) => { RenderTimelineHeader(); RefreshRealized(); };
        SizeChanged += (_, _) => RefreshLayout();
        statusDate.DateChanged += async (_, _) => {
            if (rendering) return;
            var value = statusDate.Date is { } date ? DateOnly.FromDateTime(date.Date) : (DateOnly?)null;
            await Run(async () => { await CommitPending(); Check(await Session.Execute(new ReplacePlanSettings(Session.Document.State.Settings with { StatusDate = value }), Today)); Refresh(); }, "Status date");
        };
        zoom.SelectionChanged += async (_, _) => {
            if (rendering || zoom.SelectedIndex == acceptedZoom) return;
            var proposed = zoom.SelectedIndex;
            await Run(async () => {
                try { await CommitPending(); }
                catch {
                    rendering = true;
                    try { zoom.SelectedIndex = acceptedZoom; }
                    finally { rendering = false; }
                    throw;
                }
                acceptedZoom = proposed;
                DayWidth = acceptedZoom switch { 1 => 8, 2 => 2, _ => 24 }; RefreshLayout();
            }, "Zoom");
        };
        filter.TextChanged += async (_, _) => {
            if (rendering || filter.Text == acceptedFilter) return;
            var proposed = filter.Text;
            await Run(async () => {
                try { await CommitPending(); }
                catch {
                    rendering = true;
                    try { filter.Text = acceptedFilter; }
                    finally { rendering = false; }
                    throw;
                }
                acceptedFilter = proposed; Refresh();
            }, "Filter");
        };
        InitializeInteraction();
        Unloaded += (_, _) => { disposed = true; inputProblem.Close(); lifetime.Cancel(); CancelRequestedFocus(); CancelDrag(); CompositionTarget.Rendered -= FrameRendered; frameSubscribed = false; metrics.End(pendingFrame, "unloaded-before-frame"); };
        Loaded += (_, _) => { if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); } disposed = false; RefreshLayout(); };
        ActualThemeChanged += (_, _) => { headerKey = timelineKey = null; RefreshHeaders(); RefreshRealized(); };

        Refresh();
    }
    private static ScrollViewer Horizontal(string id) => Id(new ScrollViewer {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
        IsTabStop = false, Height = 18 }, id);
    internal static T Id<T>(T value, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(value, id); return value; }
    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private void AddCommand(CommandBar bar, string label, string id, Symbol icon, Func<Task> action, bool queueInSheet = true)
    {
        var button = Id(new AppBarButton { Label = label, Icon = new SymbolIcon(icon) }, id);
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        button.Click += async (_, _) => {
            // Workspace commands own cancellation and flush this sheet's queue.
            // Nesting them inside that same queue would wait on themselves.
            if (!queueInSheet) { await action(); return; }
            await Run(async () => {
                if (id is "PlanSheetInsert" or "PlanSheetIndent" or "PlanSheetOutdent") await CommitPending();
                await action();
            }, id);
        };
        bar.PrimaryCommands.Add(button);
    }
    internal Task Run(Func<Task> action, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        if (remoteBusy) return Task.CompletedTask;
        var previous = tail;
        var token = lifetime.Token;
        var number = ++commandSequence; commands[number] = operation;
        tail = Execute(); return tail;
        async Task Execute()
        {
            try
            {
                await Task.Yield(); await previous;
                if (token.IsCancellationRequested) return;
                try { error.Text = ""; await action(); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
                { if (!token.IsCancellationRequested) { error.Text = ex.Message; Refresh(); UpdateReason(); } }
            }
            finally { commands.Remove(number); }
        }
    }
    internal void Check(PlanSaveResult result)
    {
        if (disposed) return;
        retrySave.Visibility = result.Succeeded ? Visibility.Collapsed : Visibility.Visible;
        if (!result.Succeeded) throw new IOException(result.Error);
    }
    internal async Task FlushInput()
    {
        await tail;
        await CommitPending();
    }
    private async Task ChangeHistory(bool redo)
    {
        await CommitPending();
        Check(redo ? await Session.Redo(Today) : await Session.Undo(Today));
        Refresh();
    }
    private async Task CommitPending()
    {
        var composing = Realized.SelectMany(r => r.Cells).FirstOrDefault(c => c.Composing);
        if (composing is not null) {
            var identity = Realized.First(r => r.Cells.Contains(composing)).Identity;
            Problems[(identity, composing.Field)] = "IME変換を確定または取消してください。";
            Select(identity, composing.Field, false); FocusSelected(); UpdateInputProblem();
            throw new InvalidOperationException(Problems[(identity, composing.Field)]);
        }
        foreach (var input in Pending.ToArray())
        {
            try { await Commit(input.Key.Identity, input.Key.Field, input.Value.Text, input.Value.Generation, input.Value.OriginalText); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Select(input.Key.Identity, input.Key.Field, false); FocusSelected(); throw;
            }
        }
    }
    internal Task CommitCell(string identity, PlanField field, string text)
    {
        var input = Pending.GetValueOrDefault((identity, field));
        return Run(() => Commit(identity, field, text, input?.Generation ?? 0, input?.OriginalText ?? ""));
    }
    private async Task Commit(string identity, PlanField field, string text, long generation, string originalText)
    {
        if (generation == 0 || !Pending.ContainsKey((identity, field))) return;
        pendingFrame = metrics.Begin(Session.Document.State.Rows.Length);
        try
        {
            // Display-only predecessors and calculated dates are not new inputs.
            // Returning to that display must preserve identities and scheduling.
            if (text == originalText)
            {
                if (Generation(identity, field) == generation) { Pending.Remove((identity, field)); Problems.Remove((identity, field)); }
                Refresh(); metrics.End(pendingFrame, "unchanged"); return;
            }
            var value = PlanSheetEditing.Parse(Session.Document, field, text);
            PlanCommand command;
            if (identity.Length == 0)
            {
                if (Session.Document.State.Settings.DefaultRepository is not { Length: > 0 } repository)
                    throw new ArgumentException("設定で既定リポジトリを選んでください。");
                var row = PlanRow.New("", repository);
                // Use the same typed edit semantics (Estimate -> Remaining, End -> Fixed) for a new row.
                var temporary = Session.Document with { State = Session.Document.State with { Rows = Session.Document.State.Rows.Add(row) } };
                var edited = PlanOperations.Apply(temporary, new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, field, value)]), Today).State.Rows[^1];
                command = new InsertPlanRows([edited]);
                selected = anchor = row.Identity;
            }
            else command = new EditPlanCells(PlanOperationKind.Cell, [new(identity, field, value)]);
            // Execute accepts the immutable local operation synchronously. Present it now;
            // durable persistence still gates the next command and normal window close.
            var save = Session.Execute(command, Today);
            metrics.Mark("accepted");
            if (Generation(identity, field) == generation) { Pending.Remove((identity, field)); Problems.Remove((identity, field)); }
            Refresh();
            metrics.Mark("presented");
            CompositionTarget.Rendered -= FrameRendered; CompositionTarget.Rendered += FrameRendered; frameSubscribed = true;
            Check(await save);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Problems[(identity, field)] = ex.Message; RefreshRealized(); metrics.End(pendingFrame, "rejected"); throw;
        }
    }
    private void FrameRendered(object? sender, RenderedEventArgs args)
    {
        CompositionTarget.Rendered -= FrameRendered; frameSubscribed = false;
        metrics.End(pendingFrame, "rendered");
    }
    internal void CancelEdit(string identity, PlanField field)
    {
        Pending.Remove((identity, field)); Problems.Remove((identity, field)); error.Text = "";
        Refresh();
    }
    internal void Refresh()
    {
        if (disposed) return;
        var document = Session.Document;
        Rows = document.State.Rows.ToDictionary(r => r.Identity);
        PlanIds = document.State.Rows.Select((r, i) => (r.Identity, Id: i + 1)).ToDictionary(p => p.Identity, p => p.Id);
        Schedule = Session.Schedule(Today).ToDictionary(r => r.Input.Identity);
        Unpublished = Session.Changes(Today);
        var pendingRows = Pending.Keys.Select(k => k.Identity)
            .Concat(Realized.Where(r => r.Cells.Any(c => c.Composing)).Select(r => r.Identity)).ToHashSet();
        var next = document.State.Rows.Where(r => pendingRows.Contains(r.Identity) || r.Title.Contains(acceptedFilter, StringComparison.CurrentCultureIgnoreCase)).Select(r => r.Identity).Append("").ToArray();
        if (!RowIds.SequenceEqual(next)) { RowIds = next; List.ItemsSource = next; }
        var dates = Schedule.Values.SelectMany(r => new[] { r.Start.Value, r.End.Value }).Where(d => d is not null).Select(d => d!.Value).Append(StatusDate).ToArray();
        FirstDay = DateOnly.FromDayNumber(Math.Max(0, dates.Min().DayNumber - 5));
        DayCount = Math.Max(365, dates.Max().DayNumber - FirstDay.DayNumber + 15);
        rendering = true;
        try { statusDate.Date = new DateTimeOffset(StatusDate.ToDateTime(TimeOnly.MinValue)); }
        finally { rendering = false; }
        ReconcileSelection(); RefreshLayout(); UpdateReason(); Changed?.Invoke();
    }
    internal string Header(Column column) => column.Field is { } field
        ? Session.Document.State.Settings.Columns.SingleOrDefault(m => m.Role == field)?.Name ?? column.Label : column.Label;
    internal string Display(string identity, PlanField field)
    {
        if (!Rows.TryGetValue(identity, out var row)) return "";
        var computed = Schedule[identity];
        string Date(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        return field switch {
            PlanField.Start => Date(computed.Start.Value), PlanField.End => Date(computed.End.Value),
            PlanField.Estimate => computed.Estimate?.ToString(CultureInfo.CurrentCulture) ?? "",
            PlanField.Remaining => computed.Remaining?.ToString(CultureInfo.CurrentCulture) ?? "",
            PlanField.Actual => computed.Actual?.ToString(CultureInfo.CurrentCulture) ?? "",
            PlanField.StartNoEarlierThan => Date(row.StartNoEarlierThan),
            PlanField.Fixed => row.Fixed ? "固定" : "",
            PlanField.Predecessors => string.Join(", ", row.Predecessors.Select(p => PlanIds.TryGetValue(p, out var id) ? id.ToString() : "計画外")),
            PlanField.Assignees => string.Join(", ", row.Assignees.Select(id => Session.Document.Sync.PeopleNames.GetValueOrDefault(id)
                ?? Session.Document.State.Settings.People.FirstOrDefault(p => p.Identity == id)?.Name ?? "担当者（未確認）")),
            _ => PlanOperations.Value(row, field)?.ToString() ?? ""
        };
    }
    internal bool IsCalculated(string identity, PlanField field) => Schedule.TryGetValue(identity, out var result) &&
        (field == PlanField.Start && result.Start.Origin == DateOrigin.Calculated
        || field == PlanField.End && result.End.Origin == DateOrigin.Calculated
        || result.IsSummary && field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual);
    internal bool ReadOnly(string identity, PlanField field) => remoteBusy || Schedule.TryGetValue(identity, out var result) && result.IsSummary &&
        field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Start or PlanField.End or PlanField.StartNoEarlierThan or PlanField.Fixed;
    internal bool IsChanged(string identity, PlanField field) => !(Schedule.GetValueOrDefault(identity)?.IsSummary == true
        && field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual) &&
        Unpublished.Fields.TryGetValue(identity, out var fields) && fields.Contains(field);
    internal void RefreshRealized() { foreach (var row in Realized.ToArray()) row.Refresh(); UpdateInputProblem(); }
    internal void UpdateInputProblem()
    {
        if (disposed || !IsLoaded || Problems.Count == 0) { inputProblem.Close(); return; }
        var cells = Realized.Where(r => r.IsLoaded && ReferenceEquals(r.Owner, this))
            .SelectMany(r => r.Cells.Where(c => c.IsLoaded && Problems.ContainsKey((r.Identity, c.Field)))
            .Select(c => (Cell: c, Problem: Problems[(r.Identity, c.Field)]))).ToArray();
        var target = cells.OrderByDescending(c => c.Cell.FocusState != FocusState.Unfocused).FirstOrDefault();
        if (target.Cell is null) { inputProblem.Close(); return; }
        inputProblem.Show(target.Cell, target.Problem);
    }
    private void RefreshLayout()
    {
        if (disposed) return;
        var width = Math.Max(320, ActualWidth - 20);
        SheetViewport = Math.Clamp(dividerWidth ?? SheetWidth + 6, 160, Math.Max(160, width - 230));
        ChartViewport = width - SheetViewport;
        if (divider is not null) divider.Margin = new(SheetViewport - 3, 0, 0, 0);
        RowHeight = Math.Max(22, 28 / (XamlRoot?.RasterizationScale ?? 1));
        sheetClip.Width = SheetViewport;
        sheetClip.Clip = new RectangleGeometry { Rect = new(0, 0, SheetViewport, 40) };
        foreach (var grid in new[] { headers, scrollbars })
        { grid.ColumnDefinitions[0].Width = new(SheetViewport); grid.ColumnDefinitions[1].Width = new(ChartViewport); }
        ((FrameworkElement)sheetHorizontal.Content).Width = SheetWidth;
        ((FrameworkElement)chartHorizontal.Content).Width = DayCount * DayWidth;
        sheetHorizontal.Width = SheetViewport; chartHorizontal.Width = ChartViewport;
        RefreshHeaders(); RenderTimelineHeader(); RefreshRealized();
    }
    private void RefreshHeaders()
    {
        var key = string.Join("|", VisibleColumns.Select(c => $"{c.Field}:{Header(c)}"));
        if (headerKey == key) return;
        headerKey = key;
        sheetHead.Children.Clear();
        foreach (var column in VisibleColumns)
        {
            var text = Id(new TextBlock { Text = Header(column), Width = column.Width, Padding = new(4, 4, 0, 0), FontSize = 12 },
                "PlanHeader" + (column.Field?.ToString() ?? "Id"));
            ToolTipService.SetToolTip(text, text.Text); sheetHead.Children.Add(text);
        }
        sheetHead.RenderTransform = new TranslateTransform { X = -SheetOffset };
    }
    private void RenderTimelineHeader()
    {
        var key = $"{FirstDay}:{DayCount}:{DayWidth}:{ChartViewport}:{ChartOffset}";
        if (timelineKey == key) return;
        timelineKey = key;
        chartHead.Children.Clear(); chartHead.Width = ChartViewport;
        chartHead.Clip = new RectangleGeometry { Rect = new(0, 0, ChartViewport, 40) };
        var left = Math.Max(0, (int)(ChartOffset / DayWidth));
        var right = Math.Min(DayCount, (int)((ChartOffset + ChartViewport) / DayWidth) + 32);
        if (acceptedZoom == 0)
        {
            var first = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, FirstDay.DayNumber + left));
            var lastVisible = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, FirstDay.DayNumber + Math.Min(DayCount - 1, (int)((ChartOffset + ChartViewport - 1) / DayWidth))));
            var label = first.ToString("yyyy/M");
            if (first.Year != lastVisible.Year) label += " – " + lastVisible.ToString("yyyy/M");
            else if (first.Month != lastVisible.Month) label += " – " + lastVisible.Month.ToString(CultureInfo.InvariantCulture);
            var context = Id(new TextBlock { Text = label, FontSize = 11, MaxWidth = ChartViewport - 4,
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("TextFillColorSecondaryBrush") }, "PlanTimelineMonths");
            Canvas.SetLeft(context, 2); chartHead.Children.Add(context);
        }
        var last = "";
        for (var day = left; day < right && FirstDay.DayNumber + day <= DateOnly.MaxValue.DayNumber; day++)
        {
            var date = FirstDay.AddDays(day);
            var label = acceptedZoom switch { 1 => DateOnly.FromDayNumber(Math.Max(0, date.DayNumber - ((int)date.DayOfWeek + 6) % 7)).ToString("M/d"), 2 => date.ToString("yyyy/M"), _ => date.ToString("dd") };
            if (label == last) continue; last = label;
            var text = new TextBlock { Text = label, FontSize = 11, Margin = new(2, 5, 0, 0), Foreground = Brush("TextFillColorSecondaryBrush") };
            var groupEnd = acceptedZoom switch {
                1 => day + 7 - ((int)date.DayOfWeek + 6) % 7,
                2 => day + DateTime.DaysInMonth(date.Year, date.Month) - date.Day + 1,
                _ => day + 1 };
            var x = day * DayWidth - ChartOffset;
            var available = Math.Min(ChartViewport, groupEnd * DayWidth - ChartOffset) - Math.Max(0, x);
            text.Measure(new Size(double.PositiveInfinity, 28));
            if (text.DesiredSize.Width + 4 > available) continue;
            AutomationProperties.SetAutomationId(text, "PlanTimelineLabel" + day);
            Canvas.SetLeft(text, Math.Max(0, x)); Canvas.SetTop(text, acceptedZoom == 0 ? 16 : 5); chartHead.Children.Add(text);
        }
    }
    internal double X(DateOnly day) => (day.DayNumber - FirstDay.DayNumber) * DayWidth - ChartOffset;
}


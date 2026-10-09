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
    private PlanLatenessResult lateness = new(ImmutableDictionary<string, PlanTaskLateness>.Empty, 0, 0);
    internal PlanUnpublished Unpublished { get; private set; } = new(ImmutableDictionary<string, ImmutableArray<PlanField>>.Empty);
    internal sealed record Column(PlanField? Field, string Label, double Width, bool Indicator = false);
    internal static readonly Column[] Columns = [
        new(null, "ID", 52), new(null, "インジケーター", 28, true), new(PlanField.Title, "タスク名", 272), new(PlanField.Assignees, "担当者", 80),
        new(PlanField.Estimate, "見積 h", 56), new(PlanField.Remaining, "残 h", 56), new(PlanField.Actual, "実績 h", 56),
        new(PlanField.Start, "開始日", 92), new(PlanField.End, "終了日", 92), new(PlanField.Predecessors, "先行", 64),
        new(PlanField.StartNoEarlierThan, "開始日指定", 100), new(PlanField.Fixed, "日程固定", 80), new(PlanField.Status, "ステータス", 100)];
    internal bool IndicatorVisible { get; private set; } = true;
    internal readonly HashSet<PlanField?> Hidden = [PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status];
    internal Column[] VisibleColumns { get; private set; } = Columns.Take(10).ToArray();
    internal double RowHeight { get; private set; } = 28;
    private double? dividerWidth;
    private PlanSheetDivider divider = null!;
    internal double SheetWidth => VisibleColumns.Sum(c => c.Width);
    internal double SheetViewport { get; private set; }
    internal double ChartViewport { get; private set; }
    internal double SheetOffset => sheetHorizontal.HorizontalOffset;
    internal double ChartOffset => chartHorizontal.HorizontalOffset;
    internal double DayWidth { get; private set; } = 8;
    internal DateOnly FirstDay { get; private set; }
    internal int DayCount { get; private set; } = 365;
    internal DateOnly StatusDate => Session.Document.State.Settings.StatusDate ?? DateOnly.FromDateTime(DateTime.Today);
    internal static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private readonly ScrollViewer sheetHorizontal = Horizontal("PlanSheetHorizontal"), chartHorizontal = Horizontal("PlanGanttHorizontal");
    private readonly Grid headers = new(), scrollbars = new(), sheetClip = new() { Height = 48 };
    private readonly StackPanel sheetHead = new() { Orientation = Orientation.Horizontal };
    private readonly Canvas chartHead = new() { Height = 48 };
    private readonly TextBlock reason = Id(new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }, "PlanStartReason");
    private readonly TextBlock error = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanSheetError");
    private readonly Button retrySave = Id(new Button { Content = "保存を再試行", Visibility = Visibility.Collapsed }, "PlanSheetRetrySave");
    private string? headerKey, timelineKey;
    private string acceptedFilter = "";
    private int acceptedZoom = 1;
    private bool initialChartPositioned;
    private int statusLeadDays = 5;
    private readonly Canvas chartBackground = Id(new Canvas { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left }, "PlanChartBackground");
    private readonly Canvas chartStatus = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private HashSet<DateOnly> nonWorkingDates = [];
    private Dictionary<string, DateOnly?> publishedEnds = [];
    internal DateOnly? PublishedEnd(string identity) => publishedEnds.GetValueOrDefault(identity);
    private readonly TextBlock selection = Id(new TextBlock { Foreground = Brush("TextFillColorSecondaryBrush") }, "PlanSheetSelection");
    private readonly TextBlock selectedTitle = new() { MaxWidth = 272, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock slip = Id(new TextBlock { Foreground = Brush("SystemFillColorCriticalBrush") }, "PlanSheetSlip");
    private readonly Border slipPill = new() { Background = Brush("GanttLateTintBrush"), CornerRadius = new(11), Padding = new(8, 2, 8, 2), Visibility = Visibility.Collapsed };
    private readonly HyperlinkButton issueLink = Id(new HyperlinkButton { Padding = new(0), MinHeight = 0 }, "PlanSheetIssue");
    internal readonly CalendarDatePicker statusDate = Id(new CalendarDatePicker { MinWidth = 170, Language = "ja-JP", DateFormat = "{year.full}/{month.integer(2)}/{day.integer(2)} ({dayofweek.abbreviated})" }, "PlanStatusDate");
    private readonly ComboBox zoom = Id(new ComboBox { ItemsSource = new[] { "日", "週", "月", "全期間" }, SelectedIndex = 1, MinWidth = 80 }, "PlanGanttZoom");
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
    internal PlanSheetView(PlanSession session, Func<Task<PlanClipboardContent>>? readClipboard = null, Action<PlanClipboardContent>? writeClipboard = null, Func<Task>? importCsv = null, Func<DateOnly?, Task>? changeStatusDate = null)
    {
        Session = session;
        this.readClipboard = readClipboard is null ? ReadClipboard : _ => readClipboard();
        this.writeClipboard = writeClipboard is null ? WriteClipboard : (content, _) => writeClipboard(content);
        for (var r = 0; r < 4; r++) RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var controls = new Grid { ColumnSpacing = 8, BorderBrush = Brush("WorkspaceCardStrokeBrush"), BorderThickness = new(0, 0, 0, 1) };
        controls.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var scales = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        scales.Children.Add(new TextBlock { Text = "尺度", VerticalAlignment = VerticalAlignment.Center }); scales.Children.Add(zoom); scales.Children.Add(filter);
        controls.Children.Add(scales); SetColumn(scales, 1);
        AutomationProperties.SetName(statusDate, "状況日"); AutomationProperties.SetName(zoom, "ガントの表示単位"); AutomationProperties.SetName(filter, "タイトルで絞り込み");
        Children.Add(controls);
        var commands = commandBar = Id(new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, HorizontalAlignment = HorizontalAlignment.Left }, "PlanSheetCommands");
        AddCommand(commands, "行を挿入", "PlanSheetInsert", Symbol.Add, Insert);
        AddCommand(commands, "インデント", "PlanSheetIndent", Symbol.Forward, () => Indent(false)).Icon = CommandIcon("M2,2 H14 V3 H2 Z M7,6 H14 V7 H7 Z M7,10 H14 V11 H7 Z M2,14 H14 V15 H2 Z M2,5 L5,8 L2,11 L1,10 L3,8 L1,6 Z");
        AddCommand(commands, "アウトデント", "PlanSheetOutdent", Symbol.Back, () => Indent(true)).Icon = CommandIcon("M2,2 H14 V3 H2 Z M7,6 H14 V7 H7 Z M7,10 H14 V11 H7 Z M2,14 H14 V15 H2 Z M4,5 L1,8 L4,11 L5,10 L3,8 L5,6 Z");
        InitializeOverview(commands);
        AppBarButton Overflow(string label, string id, Symbol icon, Func<Task> action, string shortcut = "", bool queued = true) {
            var button = AddCommand(commands, label, id, icon, action, queued);
            button.KeyboardAcceleratorTextOverride = shortcut;
            commands.PrimaryCommands.Remove(button); commands.SecondaryCommands.Add(button); return button;
        }
        Overflow("コピー", "PlanSheetCopy", Symbol.Copy, Copy, "Ctrl+C");
        Overflow("貼り付け", "PlanSheetPaste", Symbol.Paste, Paste, "Ctrl+V");
        Overflow("下へコピー", "PlanSheetFillDown", Symbol.Download, () => Fill(PlanOperationKind.CtrlD), "Ctrl+D").Icon = CommandIcon("F0 M2,1 H14 V5 H2 Z M3,2 V4 H13 V2 Z M7,7 H9 V11 H12 L8,15 L4,11 H7 Z");
        Overflow("クリア", "PlanSheetClear", Symbol.Clear, Clear, "Delete");
        commands.SecondaryCommands.Add(new AppBarSeparator());
        if (importCsv is not null) Overflow("CSV から追加", "PlanSheetCsv", Symbol.OpenFile, importCsv, queued: false);
        var columns = Id(new AppBarButton { Label = "表示列", Icon = new SymbolIcon(Symbol.List) }, "PlanSheetColumns");
        AutomationProperties.SetName(columns, "表示列"); ToolTipService.SetToolTip(columns, "表示列");
        var choices = new StackPanel { Spacing = 4 };
        foreach (var column in Columns)
        {
            var toggle = Id(new CheckBox { Content = column.Label, IsChecked = column.Indicator ? IndicatorVisible : !Hidden.Contains(column.Field) }, "PlanColumn" + (column.Indicator ? "Indicator" : column.Field?.ToString() ?? "Id"));
            var synchronizing = false;
            async void VisibilityChanged(object sender, RoutedEventArgs args) {
                if (synchronizing) return;
                var proposed = toggle.IsChecked == true;
                await Run(async () => {
                await CommitPending();
                if (column.Indicator) IndicatorVisible = proposed;
                else if (proposed) Hidden.Remove(column.Field); else Hidden.Add(column.Field);
                VisibleColumns = Columns.Where(c => c.Indicator ? IndicatorVisible : !Hidden.Contains(c.Field)).ToArray();
                if (VisibleColumns.Length == 0) { if (column.Indicator) IndicatorVisible = true; else Hidden.Remove(column.Field); VisibleColumns = Columns.Where(c => c.Indicator ? IndicatorVisible : !Hidden.Contains(c.Field)).ToArray(); }
                RefreshLayout(); ReconcileSelection(); }, "Column visibility");
                synchronizing = true;
                try { toggle.IsChecked = column.Indicator ? IndicatorVisible : !Hidden.Contains(column.Field); }
                finally { synchronizing = false; }
                if (Problems.Count > 0) { columns.Flyout?.Hide(); FocusSelected(); }
            }
            toggle.Checked += VisibilityChanged; toggle.Unchecked += VisibilityChanged;
            choices.Children.Add(toggle);
        }
        columns.Flyout = new Flyout { Content = choices }; commands.SecondaryCommands.Add(columns);
        controls.Children.Add(commands);
        var selectedLine = new Grid { Height = 36, ColumnSpacing = 12, Padding = new(8, 0, 8, 0),
            Background = Brush("SheetSelectionLineBrush"), BorderBrush = Brush("WorkspaceCardStrokeBrush"), BorderThickness = new(0, 0, 0, 1) };
        for (var i = 0; i < 4; i++) selectedLine.ColumnDefinitions.Add(new() { Width = i == 2 ? new(1, GridUnitType.Star) : GridLength.Auto });
        selectedLine.Children.Add(selection); selectedLine.Children.Add(selectedTitle); SetColumn(selectedTitle, 1);
        var explanation = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Left };
        explanation.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        explanation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        reason.VerticalAlignment = VerticalAlignment.Center;
        reason.Foreground = Brush("TextFillColorSecondaryBrush");
        slip.FontSize = 12; slip.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        slipPill.Child = slip;
        explanation.Children.Add(reason); explanation.Children.Add(slipPill); SetColumn(slipPill, 1);
        selectedLine.Children.Add(explanation); SetColumn(explanation, 2);
        selectedLine.Children.Add(issueLink); SetColumn(issueLink, 3);
        foreach (var child in selectedLine.Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(selectedLine); SetRow(selectedLine, 2);
        var feedback = new StackPanel { Spacing = 2 }; feedback.Children.Add(error); feedback.Children.Add(retrySave);
        retrySave.Click += async (_, _) => await Run(async () => { Check(await Session.RetrySaveAsync()); }, "Retry save");
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible);
        Children.Add(feedback); SetRow(feedback, 1);
        foreach (var grid in new[] { headers, scrollbars })
        { grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); }
        sheetClip.Children.Add(sheetHead);
        sheetClip.Children.Add(new Border { BorderThickness = new(0, 0, 0, 1), BorderBrush = Brush("WorkspaceCardStrokeBrush"), IsHitTestVisible = false });
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
        Children.Add(chartBackground); SetRow(chartBackground, 4);
        Children.Add(List); SetRow(List, 4);
        Children.Add(chartStatus); SetRow(chartStatus, 4);
        List.SizeChanged += (_, _) => RenderTimelineHeader();
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
            if (changeStatusDate is not null) { await changeStatusDate(value); Refresh(); }
            else await Run(async () => { await CommitPending(); Check(await Session.Execute(new ReplacePlanSettings(Session.Document.State.Settings with { StatusDate = value }), Today)); Refresh(); }, "Status date");
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
                DayWidth = acceptedZoom switch { 1 => 8, 2 => 2, _ => 24 };
                UpdateTimelineRange(); RefreshLayout();
                if (acceptedZoom == 3) chartHorizontal.ChangeView(0, null, null, true);
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
        Unloaded += (_, _) => { disposed = true; inputProblem.Close(); predecessorFlyout?.Hide(); lifetime.Cancel(); CancelRequestedFocus(); CancelDrag(); CompositionTarget.Rendered -= FrameRendered; frameSubscribed = false; metrics.End(pendingFrame, "unloaded-before-frame"); };
        Loaded += (_, _) => { if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); } disposed = false; RefreshLayout();
            if (!initialChartPositioned) {
                chartHorizontal.UpdateLayout();
                chartHorizontal.ChangeView(Math.Max(0, (StatusDate.DayNumber - FirstDay.DayNumber) * DayWidth - ChartViewport / 4), null, null, true);
                initialChartPositioned = true;
            }
        };
        ActualThemeChanged += (_, _) => { headerKey = timelineKey = null; RefreshHeaders(); RenderTimelineHeader(); RefreshRealized(); };

        Refresh();
    }
    private static ScrollViewer Horizontal(string id) => Id(new ScrollViewer {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
        IsTabStop = false, Height = 18 }, id);
    internal static T Id<T>(T value, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(value, id); return value; }
    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static PathIcon CommandIcon(string data) => (PathIcon)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        $"<PathIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}'/>");
    private AppBarButton AddCommand(CommandBar bar, string label, string id, Symbol icon, Func<Task> action, bool queueInSheet = true)
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
        return button;
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
        void FinishInput()
        {
            if (Generation(identity, field) != generation) return;
            Pending.Remove((identity, field)); Problems.Remove((identity, field));
            foreach (var cell in Realized.Where(r => r.Identity == identity).SelectMany(r => r.Cells).Where(c => c.Field == field))
                cell.EndEditing();
        }
        pendingFrame = metrics.Begin(Session.Document.State.Rows.Length);
        try
        {
            // Returning to the original edit form must preserve predecessor identities and automatic dates.
            if (text == originalText)
            {
                FinishInput();
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
            FinishInput();
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
        var today = Today;
        Rows = document.State.Rows.ToDictionary(r => r.Identity);
        PlanIds = document.State.Rows.Select((r, i) => (r.Identity, Id: i + 1)).ToDictionary(p => p.Identity, p => p.Id);
        Schedule = Session.Schedule(today).ToDictionary(r => r.Input.Identity);
        publishedEnds = document.Baseline.Rows.ToDictionary(r => r.Identity, r => r.End);
        var calendar = new PlanCalendar { ImportedHolidays = document.State.Settings.ImportedHolidays?.ToPreset(), CompanyDaysOff = document.State.Settings.CompanyDaysOff.ToHashSet() };
        nonWorkingDates = calendar.Holidays.Dates.Select(d => d.Date).Concat(calendar.ImportedHolidays?.Dates.Select(d => d.Date) ?? []).Concat(calendar.CompanyDaysOff).ToHashSet();
        timelineKey = null;
        lateness = PlanLateness.Classify(Schedule.Values.ToArray(), document.Baseline, calendar, document.State.Settings.StatusDate ?? today);
        Unpublished = Session.Changes(today);
        var pendingRows = Pending.Keys.Select(k => k.Identity)
            .Concat(Realized.Where(r => r.Cells.Any(c => c.Composing)).Select(r => r.Identity)).ToHashSet();
        SummaryIds = Rows.Values.Where(r => r.Parent is not null && Rows.ContainsKey(r.Parent)).Select(r => r.Parent!).ToHashSet();
        folded.IntersectWith(SummaryIds);
        var next = document.State.Rows.Where(r => pendingRows.Contains(r.Identity) ||
            (acceptedFilter.Length > 0 ? r.Title.Contains(acceptedFilter, StringComparison.CurrentCultureIgnoreCase) : !HiddenByFold(r)))
            .Select(r => r.Identity).Append("").ToArray();
        if (!RowIds.SequenceEqual(next))
        {
            // Recycling the focused cell can synchronously select a new container.
            // Reconcile the user's task selection after the source has been replaced.
            var priorSelection = (selected, anchor, selectedField, anchorField);
            RowIds = next; List.ItemsSource = next;
            (selected, anchor, selectedField, anchorField) = priorSelection;
        }
        UpdateTimelineRange();
        rendering = true;
        try { statusDate.Date = new DateTimeOffset(StatusDate.ToDateTime(TimeOnly.MinValue)); }
        finally { rendering = false; }
        ReconcileSelection(); RefreshLayout(); UpdateReason(); RefreshOverviewCommands(); Changed?.Invoke();
    }
    internal string Header(Column column) => column.Label;
    private string HeaderHelp(Column column) => column.Field is { } field
        ? Session.Document.State.Settings.Columns.SingleOrDefault(m => m.Role == field)?.Name is { } name ? "GitHub: " + name
            : field == PlanField.Title ? "GitHub: Title" : field == PlanField.Assignees ? "GitHub: Assignees" : "未設定"
        : column.Label;
    internal static string DateText(DateOnly? day, bool full = false) => day is { } value
        ? value.ToString(full ? "yyyy-MM-dd" : "M/d", CultureInfo.InvariantCulture) + " (" + "日月火水木金土"[(int)value.DayOfWeek] + ")" : "";
    internal DateOnly? CellDate(string identity, PlanField field) => field switch {
        PlanField.Start => Schedule.GetValueOrDefault(identity)?.Start.Value,
        PlanField.End => Schedule.GetValueOrDefault(identity)?.End.Value,
        PlanField.StartNoEarlierThan => Rows.GetValueOrDefault(identity)?.StartNoEarlierThan, _ => null };
    internal string EditForm(string identity, PlanField field) => field is PlanField.Start or PlanField.End or PlanField.StartNoEarlierThan
        ? CellDate(identity, field)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "" : Display(identity, field);
    internal int? Lateness(string identity) => lateness.Tasks.GetValueOrDefault(identity)?.DaysLater;
    internal string Display(string identity, PlanField field)
    {
        if (!Rows.TryGetValue(identity, out var row)) return "";
        var computed = Schedule[identity];
        string Date(DateOnly? day) => DateText(day);
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
        if (!initialChartPositioned) {
            statusLeadDays = Math.Max(5, (int)Math.Ceiling(ChartViewport / (4 * DayWidth)));
            UpdateTimelineRange();
        }
        if (acceptedZoom == 3) DayWidth = ChartViewport / DayCount;
        if (divider is not null) divider.Margin = new(SheetViewport - 3, 0, 0, 0);
        RowHeight = 28;
        sheetClip.Width = SheetViewport;
        sheetClip.Clip = new RectangleGeometry { Rect = new(0, 0, SheetViewport, 48) };
        foreach (var grid in new[] { headers, scrollbars })
        { grid.ColumnDefinitions[0].Width = new(SheetViewport); grid.ColumnDefinitions[1].Width = new(ChartViewport); }
        ((FrameworkElement)sheetHorizontal.Content).Width = SheetWidth;
        ((FrameworkElement)chartHorizontal.Content).Width = DayCount * DayWidth;
        sheetHorizontal.Width = SheetViewport; chartHorizontal.Width = ChartViewport;
        RefreshHeaders(); RenderTimelineHeader(); RefreshRealized();
    }
    private void RefreshHeaders()
    {
        var key = string.Join("|", VisibleColumns.Select(c => $"{c.Field}:{c.Indicator}:{HeaderHelp(c)}"));
        if (headerKey == key) return;
        headerKey = key;
        sheetHead.Children.Clear();
        foreach (var column in VisibleColumns)
        {
            var text = Id(new TextBlock { Text = column.Indicator ? "\uE946" : Header(column), Width = column.Width, Padding = new(8, 0, 4, 6), FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("TextFillColorSecondaryBrush") },
                "PlanHeader" + (column.Indicator ? "Indicator" : column.Field?.ToString() ?? "Id"));
            if (column.Indicator) text.FontFamily = new FontFamily("Segoe Fluent Icons");
            AutomationProperties.SetName(text, column.Label); AutomationProperties.SetHelpText(text, HeaderHelp(column));
            ToolTipService.SetToolTip(text, HeaderHelp(column)); sheetHead.Children.Add(text);
        }
        sheetHead.RenderTransform = new TranslateTransform { X = -SheetOffset };
    }
    private void RenderTimelineHeader()
    {
        var height = List.ActualHeight;
        var key = $"{FirstDay}:{DayCount}:{DayWidth}:{ChartViewport}:{ChartOffset}:{StatusDate}:{height}:{acceptedZoom}";
        if (timelineKey == key) return;
        timelineKey = key;
        chartHead.Children.Clear(); chartHead.Width = ChartViewport;
        chartHead.Clip = new RectangleGeometry { Rect = new(0, 0, ChartViewport, 48) };
        foreach (var layer in new[] { chartBackground, chartStatus }) {
            layer.Children.Clear(); layer.Width = ChartViewport;
            layer.Margin = new(SheetViewport, 0, 0, 0);
            layer.Clip = new RectangleGeometry { Rect = new(0, 0, ChartViewport, height) };
        }
        var first = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, FirstDay.DayNumber + (int)(ChartOffset / DayWidth)));
        var last = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber,
            FirstDay.DayNumber + Math.Min(DayCount - 1, (int)((ChartOffset + ChartViewport) / DayWidth))));
        var upperBounds = new List<(double Left, double Right)>();
        bool AddLabel(string label, double x, double end, bool upper, string id) {
            var text = Id(new TextBlock { Text = label, FontSize = 11,
                FontWeight = upper ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = Brush("TextFillColorSecondaryBrush") }, id);
            chartHead.Children.Add(text);
            text.Measure(new Size(double.PositiveInfinity, 24));
            var left = Math.Max(0, x) + 3;
            var availableEnd = Math.Min(ChartViewport, end);
            if (upper && upperBounds.Count == 0) availableEnd = ChartViewport;
            if (upper && upperBounds.Count > 0) left = Math.Max(left, upperBounds[^1].Right + 6);
            if (left + text.DesiredSize.Width + 3 > availableEnd) { chartHead.Children.Remove(text); return false; }
            Canvas.SetLeft(text, left); Canvas.SetTop(text, upper ? 3 : 27);
            if (upper) upperBounds.Add((left, left + text.DesiredSize.Width));
            return true;
        }
        var monthScale = acceptedZoom >= 2;
        var period = new DateOnly(first.Year, monthScale ? 1 : first.Month, 1);
        var firstUpper = true;
        while (period <= last) {
            var endDay = monthScale ? Math.Min(DateOnly.MaxValue.DayNumber + 1, period.DayNumber + (DateTime.IsLeapYear(period.Year) ? 366 : 365))
                : period.DayNumber + DateTime.DaysInMonth(period.Year, period.Month);
            var label = monthScale ? $"{period.Year}年" : firstUpper || period.Month == 1 ? $"{period.Year}年{period.Month}月" : $"{period.Month}月";
            if (AddLabel(label, X(period), (endDay - FirstDay.DayNumber) * DayWidth - ChartOffset, true, "PlanTimelineUpper" + period.DayNumber)) firstUpper = false;
            if (endDay > DateOnly.MaxValue.DayNumber) break;
            period = DateOnly.FromDayNumber(endDay);
        }
        var month = new DateOnly(first.Year, first.Month, 1);
        while (month <= last) {
            var x = X(month);
            if (x >= 0) chartHead.Children.Add(new Microsoft.UI.Xaml.Shapes.Line {
                X1 = x, X2 = x, Y1 = upperBounds.Any(b => x > b.Left && x < b.Right) ? 24 : 0, Y2 = 48, StrokeThickness = 1, Stroke = Brush("WorkspaceCardStrokeBrush") });
            if (month.Year == 9999 && month.Month == 12) break;
            month = month.AddMonths(1);
        }
        var day = monthScale ? new DateOnly(first.Year, first.Month, 1) : acceptedZoom == 1
            ? DateOnly.FromDayNumber(Math.Max(0, first.DayNumber - ((int)first.DayOfWeek + 6) % 7)) : first;
        while (day <= last) {
            var endDay = day.DayNumber + (monthScale ? DateTime.DaysInMonth(day.Year, day.Month) : acceptedZoom == 1 ? 7 : 1);
            AddLabel(monthScale ? $"{day.Month}月" : acceptedZoom == 1 ? day.ToString("M/d") : day.Day.ToString(CultureInfo.InvariantCulture),
                X(day), (endDay - FirstDay.DayNumber) * DayWidth - ChartOffset, false, "PlanTimelineLabel" + day.DayNumber);
            if (endDay > DateOnly.MaxValue.DayNumber) break;
            day = DateOnly.FromDayNumber(endDay);
        }
        if (!monthScale) {
            for (var date = first; date <= last;) {
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || nonWorkingDates.Contains(date)) {
                    var shade = Id(new Microsoft.UI.Xaml.Shapes.Rectangle { Width = DayWidth, Height = height,
                        Fill = Brush("GanttNonWorkingBrush") }, "PlanNonWorking" + date.ToString("yyyyMMdd"));
                    Canvas.SetLeft(shade, X(date)); chartBackground.Children.Add(shade);
                }
                if (date == DateOnly.MaxValue) break;
                date = date.AddDays(1);
            }
        }
        var statusX = X(StatusDate);
        chartStatus.Children.Add(Id(new Microsoft.UI.Xaml.Shapes.Line { X1 = statusX, X2 = statusX, Y1 = 0, Y2 = height,
            Stroke = Brush("SystemControlHighlightAccentBrush"), StrokeThickness = 2 }, "PlanStatusLine"));
        if (statusX < 0 || statusX > ChartViewport) return;
        var pill = Id(new Border { CornerRadius = new(9), Padding = new(7, 1, 7, 1), Background = Brush("SystemControlHighlightAccentBrush"),
            Child = new TextBlock { Text = $"状況日 {StatusDate:M/d}", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("TextOnAccentFillColorPrimaryBrush") } }, "PlanStatusDateLabel");
        AutomationProperties.SetName(pill, $"状況日 {StatusDate:M/d}");
        chartHead.Children.Add(pill);
        pill.Measure(new Size(double.PositiveInfinity, 24));
        var pillWidth = pill.DesiredSize.Width;
        // Prefer the date coordinate; otherwise use the nearest gap without hiding month/year context.
        var gaps = new List<(double Left, double Right)>();
        var gapStart = 0d;
        foreach (var bound in upperBounds) { gaps.Add((gapStart, bound.Left - 4)); gapStart = bound.Right + 4; }
        gaps.Add((gapStart, ChartViewport));
        var candidates = gaps.Where(g => g.Right - g.Left >= pillWidth)
            .Select(g => Math.Clamp(statusX - pillWidth / 2, g.Left, g.Right - pillWidth)).OrderBy(x => Math.Abs(x + pillWidth / 2 - statusX)).ToArray();
        if (candidates.Length == 0) { chartHead.Children.Remove(pill); return; }
        Canvas.SetLeft(pill, candidates[0]); Canvas.SetTop(pill, 2);
    }
    internal double X(DateOnly day) => (day.DayNumber - FirstDay.DayNumber) * DayWidth - ChartOffset;
}


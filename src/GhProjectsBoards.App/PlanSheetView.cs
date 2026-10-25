using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView : Grid
{
    internal readonly PlanSession Session;
    private bool remoteBusy;
    private SheetCommandBar commandBar = null!;
    internal void SetRemoteBusy(bool value)
    {
        if (value) { CancelPendingFilter(); pendingMenuAction = null; commandMenu.Hide(); }
        remoteBusy = value;
        commandBar.IsEnabled = moreCommands.IsEnabled = filter.IsEnabled = zoom.IsEnabled = statusDate.IsEnabled = !value;
        RefreshOverviewCommands();
        RefreshRealized();
    }

    internal readonly HashSet<PlanSheetRow> Realized = [];
    internal readonly ListView List = Id(new ListView { SelectionMode = ListViewSelectionMode.None, Padding = new(0), ShowsScrollingPlaceholders = false }, "PlanTasks");
    internal sealed record Input(string Text, long Generation, string OriginalText);
    private long inputGeneration;
    internal readonly Dictionary<(string Identity, string Field), Input> Pending = [];
    private readonly PlanInputProblem inputProblem = new("SheetInputProblem");
    private sealed class InputRejectedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
    internal void SetInput(string identity, string field, string text, string originalText) => Pending[(identity, field)] = new(text, ++inputGeneration, originalText);
    internal long Generation(string identity, string field) => Pending.GetValueOrDefault((identity, field))?.Generation ?? 0;
    internal readonly Dictionary<(string Identity, string Field), string> Problems = [];
    internal IReadOnlyList<string> RowIds { get; private set; } = [];
    private PlanRowItems rowItems = [];
    internal Dictionary<string, int> RowIndexes { get; private set; } = [];
    // Row positions: the unlabelled row number and the Gantt crossings only. Tasks are named by TaskNames (#263).
    internal Dictionary<string, int> PlanIds { get; private set; } = [];
    internal PlanTaskNames TaskNames { get; private set; } = PlanTaskNames.None;
    internal ImmutableArray<PlanConflict> BlockingConflicts { get; private set; } = [];
    internal Dictionary<string, PlanRow> Rows { get; private set; } = [];
    internal Dictionary<string, ScheduledTask> Schedule { get; private set; } = [];
    private PlanDocument? presentedDocument;
    private DateOnly presentedToday;
    private PlanLatenessResult lateness = new(ImmutableDictionary<string, PlanTaskLateness>.Empty, 0, 0, 0, 0);
    internal int OverdueTasks => lateness.OverdueTasks;
    internal int LaterTasks => lateness.LaterTasks;
    internal int CauseTasks => lateness.CauseTasks;
    internal int OverEstimateTasks => lateness.OverEstimateTasks;
    private IReadOnlySet<string> Set(Func<PlanTaskLateness, bool> matches) =>
        lateness.Tasks.Where(pair => matches(pair.Value)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
    // The four status-bar filters (#236 part B) are identity sets over the same classification the marks use.
    internal IReadOnlySet<string> OverdueSet => Set(late => late.Level == PlanLatenessLevel.Overdue);
    internal IReadOnlySet<string> LaterSet => Set(late => late.Level == PlanLatenessLevel.Later);
    internal IReadOnlySet<string> CauseSet => Set(late => late.IsCause);
    internal IReadOnlySet<string> OverEstimateSet => Set(late => late.OverEstimate);
    internal PlanMarkVisibility Marks => PlanMarkVisibility.From(Session.Document.State.Settings);
    internal DateOnly? ForecastEndOf(string identity) => Schedule.GetValueOrDefault(identity)?.ForecastEnd;
    internal int? WorkingDaysLater(string identity) => LatenessOf(identity)?.DaysLater;
    // Ancestor requirement rows a lateness filter keeps for context: shown, but dimmed.
    private HashSet<string> filterContext = [];
    internal bool IsFilterContext(string identity) => filterContext.Contains(identity);
    internal ImmutableArray<string> LaterDescendantsOf(string identity) =>
        PlanLateness.LaterDescendants(Schedule.Values.OrderBy(t => t.Input.RowId).ToArray(), identity, lateness);
    internal string IdentityOfRow(int rowId) => Schedule.Values.FirstOrDefault(t => t.Input.RowId == rowId)?.Input.Identity ?? "";
    private Dictionary<string, string> peopleNames = new();
    internal PlanUnpublished Unpublished { get; private set; } = new(ImmutableDictionary<string, ImmutableArray<string>>.Empty);
    private ImmutableArray<string> untitledNewRows = [];
    // An inserted row the PMO has not titled yet: the publish skips it and the sheet marks its title (#247).
    internal bool IsUntitledNewRow(string identity) => untitledNewRows.Contains(identity);
    internal sealed record Column(string? Field, string Label, double Width, bool Indicator = false);
    internal static readonly Column[] Columns = [
        new(null, "ID", 76), new(null, "状態", 28, true), new(PlanField.Title, "タスク名", 200), new(PlanField.Phase, "工程", 48), new(PlanField.Assignees, "担当者", 80),
        new(PlanField.Estimate, "見積 h", 72), new(PlanField.Remaining, "残 h", 72), new(PlanField.Actual, "実績 h", 56),
        new(PlanField.Start, "開始日", 92), new(PlanField.End, "終了日", 92), new("ForecastEnd", "見込終了", 108), new(PlanField.Predecessors, "先行", 64),
        new(PlanField.Status, "ステータス", 100)];
    internal bool IndicatorVisible { get; private set; } = true;
    internal readonly HashSet<string?> Hidden = [PlanField.Status];
    internal Column[] VisibleColumns { get; private set; } = Columns.Take(12).ToArray();
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
    internal DateOnly StatusDate => Session.Document.State.Settings.StatusDate ?? PlanClock.Today;
    internal static DateOnly Today => PlanClock.Today;
    private readonly ScrollViewer sheetHorizontal = Horizontal("PlanSheetHorizontal"), chartHorizontal = Horizontal("PlanGanttHorizontal");
    private ScrollViewer? rowScroll;
    private readonly Grid headers = new(), scrollbars = new(), sheetClip = new() { Height = 48 };
    private readonly Canvas sheetHead = new() { Height = 48, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Canvas chartHead = Id(new Canvas { Height = 48, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) }, "PlanGanttHeader");
    private readonly TextBlock reason = Id(new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }, "PlanStartReason");
    private readonly TextBlock error = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanSheetError");
    // Derived from the document, so it names the untitled rows and disappears with them (#247).
    private readonly TextBlock untitledRows = Id(new TextBlock { TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed }, "PlanSheetUntitledRows");
    internal readonly InfoBar SaveFailure = Id(new InfoBar { Title = "保存できませんでした", Severity = InfoBarSeverity.Error, IsClosable = false }, "PlanSheetSaveFailure");
    private readonly Button retrySave = Id(new Button { Content = "保存を再試行", Visibility = Visibility.Collapsed }, "PlanSheetRetrySave");
    // A changed stored plan cannot be resolved by retrying: these are the two ways out (#248).
    private readonly StackPanel conflictActions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Button reapplyStored = Id(new Button { Content = "読み直して編集を適用" }, "PlanSaveReapply");
    private readonly Button saveCopy = Id(new Button { Content = "コピーとして保存…" }, "PlanSaveCopy");
    private string? headerKey, timelineKey;
    private string acceptedFilter = "";
    private Func<PlanRow, bool>? filterPredicate;
    private long filterGeneration;
    private readonly DispatcherTimer filterDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly TextBlock filterCount = Id(new TextBlock { Visibility = Visibility.Collapsed,
        Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center }, "PlanSheetFilterCount");
    private int acceptedZoom = 1;
    private bool initialChartPositioned;
    internal bool ShowLatenessLabels => acceptedZoom is 0 or 1;
    // Reserve the widest fixed mark (10 px) plus 2 px clearance, independent of the date span.
    internal double ChartDateInset => acceptedZoom == 3 ? 12 : 0;
    private readonly Canvas chartBackground = Id(new Canvas { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left }, "PlanChartBackground");
    private readonly Canvas chartStatus = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private HashSet<DateOnly> nonWorkingDates = [];
    private readonly TextBlock selection = Id(new TextBlock { Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }, "PlanSheetSelection");
    private readonly TextBlock selectedTitle = new() { MaxWidth = 272, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock slip = Id(new TextBlock { Foreground = Brush("SystemFillColorCriticalBrush") }, "PlanSheetSlip");
    private readonly TextBlock latenessDetail = Id(new TextBlock { Foreground = Brush("TextFillColorSecondaryBrush") }, "PlanSheetLatenessDetail");
    // The selection line's two impact links (#236 part B): what this task's lateness delayed, and the
    // predecessor that delayed it. One button per action, shown only when the lateness has that cause.
    private readonly HyperlinkButton successorLink = Id(new HyperlinkButton { Padding = new(0), MinHeight = 0, Visibility = Visibility.Collapsed }, "PlanSelectionSuccessorFilter");
    private readonly HyperlinkButton predecessorLink = Id(new HyperlinkButton { Padding = new(0), MinHeight = 0, Visibility = Visibility.Collapsed }, "PlanSelectionPredecessorLink");
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
    // #253: an on-demand lookup of typed 担当者 logins. The workspace supplies the read; Escape or unload
    // cancels it through this token and nothing is written.
    private readonly Func<string, string, CancellationToken, Task<PlanResource>>? resolveAssignee;
    private CancellationTokenSource? lookup;
    private bool frameSubscribed;
    internal string WorkDescription => $"loaded={IsLoaded}, disposed={disposed}, commands=[{string.Join(", ", commands.Values)}], tail={tail.Status}, clipboard={clipboardWork}, dragTimer={dragScroll.IsEnabled}, frame={frameSubscribed}, focusTarget={requestedFocus}, pendingCells={Pending.Count}, realizedRows={Realized.Count}, zoomOpen={zoom.IsDropDownOpen}, calendarOpen={statusDate.IsCalendarOpen}";
    internal event Action? Changed;
    internal event Action<PlanSaveResult>? SaveFeedbackChanged;
    internal PlanSheetView(PlanSession session, Func<Task<PlanClipboardContent>>? readClipboard = null, Action<PlanClipboardContent>? writeClipboard = null, Func<Task>? importCsv = null, Func<DateOnly?, Task>? changeStatusDate = null, bool hostSaveFailure = false, Func<Task>? openColumns = null, Func<Task>? exportCsv = null,
        Func<Task>? reapplyStoredPlan = null, Func<Task>? savePlanCopy = null, Func<string, string, CancellationToken, Task<PlanResource>>? resolveAssignee = null)
    {
        Session = session;
        this.resolveAssignee = resolveAssignee;
        this.readClipboard = readClipboard is null ? ReadClipboard : _ => readClipboard();
        this.writeClipboard = writeClipboard is null ? WriteClipboard : (content, _) => writeClipboard(content);
        // Rows: controls, selection line, headers, rows, scrollbars, then the message line. The message line is
        // last so a message never pushes the rows down under the pointer (#233, DESIGN M1).
        for (var r = 0; r < 3; r++) RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var controls = new Grid { ColumnSpacing = 8, BorderBrush = Brush("WorkspaceCardStrokeBrush"), BorderThickness = new(0, 0, 0, 1) };
        controls.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var scales = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        scales.Children.Add(new TextBlock { Text = "表示単位", VerticalAlignment = VerticalAlignment.Center }); scales.Children.Add(zoom);
        var filterBox = new Grid();
        filter.Padding = new(30, 4, 24, 4);
        filterBox.Children.Add(filter);
        filterBox.Children.Add(Id(new FontIcon { Glyph = "\uE721", FontSize = 14, Width = 14, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0),
            IsHitTestVisible = false, IsTabStop = false, Foreground = Brush("TextFillColorSecondaryBrush") }, "PlanSheetFilterSearch"));
        scales.Children.Add(filterBox); scales.Children.Add(filterCount);
        controls.Children.Add(scales); SetColumn(scales, 1);
        AutomationProperties.SetName(statusDate, "基準日"); AutomationProperties.SetName(zoom, "ガントの表示単位"); AutomationProperties.SetName(filter, "タイトルで絞り込み");
        Children.Add(controls);
        var commands = commandBar = Id(new SheetCommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left }, "PlanSheetCommands");
        AddCommand(commands, "行を挿入", "PlanSheetInsert", Symbol.Add, Insert);
        deletePrimary = AddCommand(commands, "行を削除", "PlanSheetDelete", Symbol.Delete, DeleteRows);
        deletePrimary.KeyboardAcceleratorTextOverride = "Ctrl+-";
        AddCommand(commands, "インデント", "PlanSheetIndent", Symbol.Forward, () => Indent(false)).Icon = CommandIcon("M2,2 H14 V3 H2 Z M7,6 H14 V7 H7 Z M7,10 H14 V11 H7 Z M2,14 H14 V15 H2 Z M2,5 L5,8 L2,11 L1,10 L3,8 L1,6 Z");
        AddCommand(commands, "アウトデント", "PlanSheetOutdent", Symbol.Back, () => Indent(true)).Icon = CommandIcon("M2,2 H14 V3 H2 Z M7,6 H14 V7 H7 Z M7,10 H14 V11 H7 Z M2,14 H14 V15 H2 Z M4,5 L1,8 L4,11 L5,10 L3,8 L5,6 Z");
        InitializeOverview(commands);
        AppBarButton Overflow(string label, string id, Symbol icon, Func<Task> action, string shortcut = "", bool queued = true) {
            var button = AddCommand(commands, label, id, icon, action, queued);
            button.KeyboardAcceleratorTextOverride = shortcut;
            commands.PrimaryCommands.Remove(button); commands.SecondaryCommands.Add(button); return button;
        }
        moveUp = Overflow("上へ移動", "PlanSheetMoveUp", Symbol.Up, () => MoveOneSibling(false), "Alt+Shift+↑");
        moveDown = Overflow("下へ移動", "PlanSheetMoveDown", Symbol.Download, () => MoveOneSibling(true), "Alt+Shift+↓");
        moveUp.Icon = CommandIcon("M7,15 V4 L3,8 L2,7 L8,1 L14,7 L13,8 L9,4 V15 Z");
        moveDown.Icon = CommandIcon("M7,1 V12 L3,8 L2,9 L8,15 L14,9 L13,8 L9,12 V1 Z");
        arrangeOutline = Overflow("階層順に並べ替え", "PlanSheetArrangeOutline", Symbol.Sort, ArrangeOutline);
        commands.SecondaryCommands.Add(new AppBarSeparator());
        Overflow("コピー", "PlanSheetCopy", Symbol.Copy, () => KeyboardCommand(VirtualKey.C), "Ctrl+C", queued: false);
        Overflow("貼り付け", "PlanSheetPaste", Symbol.Paste, () => KeyboardCommand(VirtualKey.V), "Ctrl+V", queued: false);
        Overflow("下へコピー", "PlanSheetFillDown", Symbol.Download, () => Fill(PlanOperationKind.CtrlD), "Ctrl+D").Icon = CommandIcon("F0 M2,1 H14 V5 H2 Z M3,2 V4 H13 V2 Z M7,7 H9 V11 H12 L8,15 L4,11 H7 Z");
        Overflow("クリア", "PlanSheetClear", Symbol.Clear, () => KeyboardCommand(VirtualKey.Delete), "Delete", queued: false);
        commands.SecondaryCommands.Add(new AppBarSeparator());
        // #231: the row-state commands from the keyboard. The row context menu is the pointer route; both use
        // the same targets, eligibility and reasons.
        deleteOverflow = Overflow("行を削除", "PlanSheetDeleteOverflow", Symbol.Delete, DeleteRows, "Ctrl+-");
        completeRows = Overflow("完了にする", "PlanSheetCompleteRows", Symbol.Accept, () => RowStateReport(CommandTargets, PlanRowAction.Complete), queued: false);
        cancelRows = Overflow("中止にする", "PlanSheetCancelRows", Symbol.Cancel, () => RowStateReport(CommandTargets, PlanRowAction.Cancel), queued: false);
        removeRows = Overflow("この版から外す", "PlanSheetRemoveRows", Symbol.Remove, () => RowStateReport(CommandTargets, PlanRowAction.Remove), queued: false);
        reopenRows = Overflow("未完了に戻す", "PlanSheetReopenRows", Symbol.Refresh, () => RowStateReport(CommandTargets, PlanRowAction.Reopen), queued: false);
        commands.SecondaryCommands.Add(new AppBarSeparator());
        if (importCsv is not null) Overflow("CSV から追加…", "PlanSheetCsv", Symbol.OpenFile, importCsv, queued: false);
        if (exportCsv is not null) Overflow("CSV に書き出し…", "PlanSheetCsvExport", Symbol.Save, exportCsv, queued: false);
        Overflow("表示列", "PlanSheetColumns", Symbol.List, async () => {
            var accepted = false;
            await Run(async () => { await CommitPending(); accepted = true; }, "Column settings");
            // The workspace flushes this queue, so invoke navigation only after it completes.
            if (accepted && openColumns is not null) await openColumns();
        }, queued: false);
        controls.Children.Add(InitializeCommandMenu(commands));
        var selectedLine = new Grid { Height = 36, ColumnSpacing = 12, Padding = new(8, 0, 8, 0),
            Background = Brush("SheetSelectionLineBrush"), BorderBrush = Brush("WorkspaceCardStrokeBrush"), BorderThickness = new(0, 0, 0, 1) };
        for (var i = 0; i < 5; i++) selectedLine.ColumnDefinitions.Add(new() { Width = i == 2 ? new(1, GridUnitType.Star) : GridLength.Auto });
        selectedLine.Children.Add(selection); selectedLine.Children.Add(selectedTitle); SetColumn(selectedTitle, 1);
        var explanation = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Left };
        // 0 start reason, 1 typed-start notice (#229), 2 lateness pill, 3 lateness detail, 4 impact links.
        explanation.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (var column = 0; column < 4; column++) explanation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        reason.VerticalAlignment = VerticalAlignment.Center;
        reason.Foreground = Brush("TextFillColorSecondaryBrush");
        slip.FontSize = 12; slip.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        slipPill.Child = slip;
        explanation.Children.Add(reason);
        InitializeStartNotice(explanation);
        explanation.Children.Add(slipPill); SetColumn(slipPill, 2);
        explanation.Children.Add(latenessDetail); SetColumn(latenessDetail, 3);
        var impact = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        impact.Children.Add(successorLink); impact.Children.Add(predecessorLink);
        explanation.Children.Add(impact); SetColumn(impact, 4);
        selectedLine.Children.Add(explanation); SetColumn(explanation, 2);
        InitializeDetailToggle(selectedLine);
        selectedLine.Children.Add(issueLink); SetColumn(issueLink, 4);
        foreach (var child in selectedLine.Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(selectedLine); SetRow(selectedLine, 1);
        var feedback = new StackPanel { Spacing = 2 }; feedback.Children.Add(untitledRows); feedback.Children.Add(error);
        SaveFailure.ActionButton = retrySave;
        conflictActions.Children.Add(reapplyStored); conflictActions.Children.Add(saveCopy);
        SaveFailure.Content = conflictActions;
        if (!hostSaveFailure) feedback.Children.Add(SaveFailure);
        retrySave.Click += async (_, _) => await Run(async () => { Check(await Session.RetrySaveAsync()); }, "Retry save");
        reapplyStored.Click += async (_, _) => { if (reapplyStoredPlan is { } reapply) await Run(reapply, "Reapply stored plan"); };
        saveCopy.Click += async (_, _) => { if (savePlanCopy is { } copy) await Run(copy, "Save plan copy"); };
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible);
        untitledRows.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => untitledRows.Visibility = string.IsNullOrEmpty(untitledRows.Text) ? Visibility.Collapsed : Visibility.Visible);
        Children.Add(feedback); SetRow(feedback, 5);
        foreach (var grid in new[] { headers, scrollbars })
        { grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); }
        sheetClip.Children.Add(sheetHead);
        sheetClip.Children.Add(new Border { BorderThickness = new(0, 0, 0, 1), BorderBrush = Brush("WorkspaceCardStrokeBrush"), IsHitTestVisible = false });
        headers.Children.Add(sheetClip); headers.Children.Add(chartHead); SetColumn(chartHead, 1);
        chartHead.Clip = new RectangleGeometry();
        chartHead.PointerWheelChanged += (_, args) => ScrollTimeline(args);
        Children.Add(headers); SetRow(headers, 2);
        List.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["PlanSheetRowsPanel"];
        List.ItemTemplate = (DataTemplate)Application.Current.Resources["PlanSheetRowTemplate"];
        List.ContainerContentChanging += RowContentChanging;
        List.PreviewKeyDown += PageKeyDown;
        List.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => HoldingView = false), true);
        List.ItemContainerStyle = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["PlanListViewItemStyle"], Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(Control.BorderThicknessProperty, new Thickness(0)),
            new Setter(FrameworkElement.MinHeightProperty, 0d),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        ScrollViewer.SetHorizontalScrollMode(List, ScrollMode.Disabled); ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        List.PointerWheelChanged += (_, args) => {
            var point = args.GetCurrentPoint(List).Position;
            if (point.X >= SheetViewport && point.X < SheetViewport + ChartViewport) ScrollTimeline(args);
        };
        Children.Add(chartBackground); SetRow(chartBackground, 3);
        Children.Add(List); SetRow(List, 3);
        Children.Add(chartStatus); SetRow(chartStatus, 3);
        moveIndicator.Background = Brush("SheetSelectionStrokeBrush");
        Children.Add(moveIndicator); SetRow(moveIndicator, 3);
        List.SizeChanged += (_, _) => { RenderTimelineHeader(); if (Problems.Count > 0) UpdateInputProblem(); };
        List.Loaded += (_, _) => {
            var current = Descendants(List).OfType<ScrollViewer>().FirstOrDefault();
            if (ReferenceEquals(rowScroll, current)) return;
            if (rowScroll is not null) rowScroll.ViewChanged -= RowViewportChanged;
            rowScroll = current;
            if (rowScroll is not null) rowScroll.ViewChanged += RowViewportChanged;
        };
        List.Unloaded += (_, _) => {
            if (rowScroll is not null) rowScroll.ViewChanged -= RowViewportChanged;
            rowScroll = null;
        };
        sheetHorizontal.Content = new Border { Height = 1 }; chartHorizontal.Content = new Border { Height = 1 };
        scrollbars.Children.Add(sheetHorizontal); scrollbars.Children.Add(chartHorizontal); SetColumn(chartHorizontal, 1);
        Children.Add(scrollbars); SetRow(scrollbars, 4);
        divider = Id(new PlanSheetDivider(() => SheetViewport, () => Math.Max(160, Math.Max(320, ActualWidth - 20) - 230), value => {
            dividerWidth = value; RefreshLayout();
        }) { Width = 6, HorizontalAlignment = HorizontalAlignment.Left }, "PlanSheetDivider");
        AutomationProperties.SetName(divider, "シートとガントの幅");
        ToolTipService.SetToolTip(divider, "ドラッグまたは左右キーで幅を変更");
        Children.Add(divider); SetRow(divider, 2); SetRowSpan(divider, 3);
        sheetHorizontal.ViewChanged += (_, _) => RefreshScrolledRows(sheet: true, chart: false);
        chartHorizontal.ViewChanged += (_, args) => {
            if (!args.IsIntermediate) pendingWheelOffset = null;
            RefreshScrolledRows(sheet: false, chart: true);
        };
        SizeChanged += (_, _) => RefreshLayout();
        statusDate.DateChanged += async (_, _) => {
            if (rendering) return;
            var value = statusDate.Date is { } date ? DateOnly.FromDateTime(date.Date) : (DateOnly?)null;
            if (value is null) { Refresh(); return; }
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
                RefreshLayout(preserveDateViewport: false);
                PositionTimelineAnchor();
            }, "Zoom");
        };
        filter.TextChanged += async (_, _) => {
            filterDelay.Stop();
            filterGeneration++;
            if (rendering || disposed || filter.Text == acceptedFilter) return;
            if (filter.Text.Length == 0) await ApplyFilter();
            else filterDelay.Start();
        };
        filterDelay.Tick += async (_, _) => await ApplyFilter();
        // Escape during or just after IME conversion belongs to the IME, not to leaving the filter.
        bool filterComposing = false, filterCompositionEnded = false;
        filter.TextCompositionStarted += (_, _) => filterComposing = true;
        filter.TextCompositionEnded += (_, _) => {
            filterComposing = false; filterCompositionEnded = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => filterCompositionEnded = false);
        };
        filter.PreviewKeyDown += async (_, args) => {
            if (args.Key != Windows.System.VirtualKey.Escape || filterComposing || filterCompositionEnded) return;
            args.Handled = true; await ReturnFromFilter();
        };
        filter.KeyDown += async (_, args) => {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true; await ApplyFilter();
        };
        InitializeInteraction();
        Unloaded += (_, _) => { disposed = true; pendingWheelOffset = null; CancelPendingFilter();
            inputProblem.Close(); predecessorFlyout?.Hide(); assigneePicker?.Hide(); pendingMenuAction = null; commandMenu.Hide(); lifetime.Cancel(); CancelRequestedFocus(); CancelDrag(); CompositionTarget.Rendered -= FrameRendered; frameSubscribed = false; metrics.End(pendingFrame, "unloaded-before-frame"); };
        Loaded += (_, _) => { if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); } disposed = false; RefreshLayout();
            // The first culture-aware comparison loads collation data (about 90 ms measured);
            // pay it while idle instead of on the first typed filter character.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _ = "a".Contains("A", StringComparison.CurrentCultureIgnoreCase));
            if (!initialChartPositioned) {
                PositionTimelineAnchor();
                initialChartPositioned = true;
            }
        };
        ActualThemeChanged += (_, _) => { headerKey = timelineKey = null; RefreshHeaders(); RenderTimelineHeader(); RefreshRealized(); };

        Refresh();
    }
    internal void CancelPendingFilter()
    {
        filterDelay.Stop();
        filterGeneration++;
        var wasRendering = rendering;
        rendering = true;
        try { filter.Text = acceptedFilter; }
        finally { rendering = wasRendering; }
    }
    internal void FilterTasks(IReadOnlySet<string> identities, string label, bool includeSummaries = false, bool dimAncestors = false)
    {
        CancelPendingFilter();
        acceptedFilter = label;
        // Ancestor requirement rows stay visible as context when a count filter is applied (#236 part B).
        filterContext = dimAncestors ? AncestorsOf(identities) : [];
        filterPredicate = row => identities.Contains(row.Identity) && (includeSummaries || !SummaryIds.Contains(row.Identity))
            || filterContext.Contains(row.Identity);
        rendering = true;
        try { filter.Text = acceptedFilter; }
        finally { rendering = false; }
        Refresh();
    }
    private HashSet<string> AncestorsOf(IReadOnlySet<string> identities)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
            for (var parent = Rows.GetValueOrDefault(identity)?.Parent; parent is not null; parent = Rows.GetValueOrDefault(parent)?.Parent)
                if (!found.Add(parent)) break;
        return found;
    }
    internal void ClearIdentityFilter()
    {
        if (filterPredicate is null) return;
        CancelPendingFilter();
        acceptedFilter = ""; filterPredicate = null; filterContext = [];
        rendering = true;
        try { filter.Text = ""; }
        finally { rendering = false; }
        Refresh();
    }
    private bool MatchesFilter(PlanRow row) => filterPredicate?.Invoke(row)
        ?? row.Title.Contains(acceptedFilter, StringComparison.CurrentCultureIgnoreCase);
    private Task ApplyFilter()
    {
        filterDelay.Stop();
        var proposed = filter.Text;
        var generation = filterGeneration;
        if (disposed || proposed == acceptedFilter) return Task.CompletedTask;
        return Run(async () => {
            if (generation != filterGeneration || filter.Text != proposed || proposed == acceptedFilter) return;
            try { await CommitPending(); }
            catch {
                rendering = true;
                try { if (generation == filterGeneration && filter.Text == proposed) filter.Text = acceptedFilter; }
                finally { rendering = false; }
                throw;
            }
            if (generation != filterGeneration || filter.Text != proposed || disposed) return;
            pendingFrame = metrics.Begin(Session.Document.State.Rows.Length, "filter");
            acceptedFilter = proposed; filterPredicate = null;
            metrics.Mark("accepted");
            Refresh();
            metrics.Mark("presented");
            CompositionTarget.Rendered -= FrameRendered; CompositionTarget.Rendered += FrameRendered; frameSubscribed = true;
        }, "Filter");
    }
    private void RefreshScrolledRows(bool sheet, bool chart)
    {
        if (disposed) return;
        if (sheet) sheetHead.RenderTransform = new TranslateTransform { X = -SheetOffset };
        if (chart) RenderTimelineHeader();
        foreach (var row in Realized.ToArray()) row.RefreshHorizontal(sheet, chart);
        if (sheet) UpdateInputProblem();
    }
    private static ScrollViewer Horizontal(string id) => Id(new ScrollViewer {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
        IsTabStop = false, Height = 18 }, id);
    internal static T Id<T>(T value, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(value, id); return value; }
    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static PathIcon CommandIcon(string data)
    {
        var icon = (PathIcon)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<PathIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}'/>");
        // Geometry has one XAML parent. Retain its source so an overflow item can
        // create its own geometry instead of reparenting the toolbar icon's Data.
        icon.Tag = data;
        return icon;
    }
    private AppBarButton AddCommand(CommandBar bar, string label, string id, Symbol icon, Func<Task> action, bool queueInSheet = true)
    {
        var button = Id(new AppBarButton { Label = label, Icon = new SymbolIcon(icon) }, id);
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        commandActions[button] = async () => {
            // Workspace commands own cancellation and flush this sheet's queue.
            // Nesting them inside that same queue would wait on themselves.
            if (!queueInSheet) { await action(); return; }
            await Run(async () => {
                if (id is "PlanSheetInsert" or "PlanSheetDelete" or "PlanSheetDeleteOverflow" or "PlanSheetIndent" or "PlanSheetOutdent") await CommitPending();
                await action();
            }, id);
        };
        button.Click += async (_, _) => await commandActions[button]();
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
                { if (!token.IsCancellationRequested) {
                    if (ex is not InputRejectedException && (ex is not IOException || !SaveFailure.IsOpen)) error.Text = ex.Message;
                    Refresh(); UpdateReason();
                } }
            }
            finally { commands.Remove(number); }
        }
    }
    internal void Check(PlanSaveResult result)
    {
        if (disposed) return;
        SaveFeedbackChanged?.Invoke(result);
        if (result.Succeeded) ClearSaveFailure();
        else {
            error.Text = ""; SaveFailure.Message = result.Error ?? "保存できません。再試行してください。";
            SaveFailure.IsOpen = true;
            // Retrying cannot resolve a plan another writer stored; the two escapes replace it (#248).
            var conflict = result.Failure == PlanSaveFailure.ChangedFile;
            retrySave.Visibility = conflict ? Visibility.Collapsed : Visibility.Visible;
            conflictActions.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
            throw new PlanSaveFailedException(result);
        }
    }
    internal void ClearSaveFailure()
    {
        SaveFailure.IsOpen = false; retrySave.Visibility = Visibility.Collapsed; conflictActions.Visibility = Visibility.Collapsed;
    }
    internal async Task FlushInput()
    {
        await tail;
        await CommitPending();
    }
    // The status line explains one operation; a history change makes that explanation stale, so the two
    // history paths dismiss it explicitly instead of clearing on unrelated renders (#231 C2).
    internal void DismissStatus() => error.Text = "";
    private async Task ChangeHistory(bool redo)
    {
        DismissStatus();
        await CommitPending();
        var before = Session.Document.State.Rows.Select(r => r.Identity).ToArray();
        Check(redo ? await Session.Redo(Today) : await Session.Undo(Today));
        var after = Session.Document.State.Rows.Select(r => r.Identity).ToArray();
        if (!before.SequenceEqual(after) && before.Length == after.Length && before.ToHashSet().SetEquals(after)) {
            var active = selected;
            RefreshOrderKeepingViewport(() => {
                Refresh();
                if (RowIndexes.ContainsKey(active)) FocusCell(active, selectedField, false);
            }, active);
        } else Refresh();
    }
    private async Task CommitPending()
    {
        var composing = Realized.SelectMany(r => r.Cells).FirstOrDefault(c => c.Composing);
        if (composing is not null) {
            var identity = Realized.First(r => r.Cells.Contains(composing)).Identity;
            Problems[(identity, composing.Field)] = "IME変換を確定または取消してください。";
            Select(identity, composing.Field, false); FocusSelected(); UpdateInputProblem();
            throw new InputRejectedException(Problems[(identity, composing.Field)]);
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
    internal Task CommitCell(string identity, string field, string text)
    {
        var input = Pending.GetValueOrDefault((identity, field));
        return Run(() => Commit(identity, field, text, input?.Generation ?? 0, input?.OriginalText ?? ""));
    }
    private async Task Commit(string identity, string field, string text, long generation, string originalText)
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
            // #253: a typed login the sheet cannot resolve locally is verified against the row's repository
            // before the cell commits; the typed text stays in the editing cell and the status line names it.
            var resolved = ImmutableArray<PlanResource>.Empty;
            if (field == PlanField.Assignees) {
                var repository = identity.Length == 0
                    ? Session.Document.State.Settings.DefaultRepository ?? ""
                    : Session.Document.State.Rows.SingleOrDefault(r => r.Identity == identity)?.Repository ?? "";
                var pending = PlanSheetEditing.UnresolvedAssignees(Session.Document, text)
                    .Select(login => (Repository: repository, Login: login)).ToArray();
                resolved = await ResolveAssignees(pending);
                error.Text = "";
            }
            var value = PlanSheetEditing.Parse(Session.Document, field, text, Today, identity, resolved);
            PlanCommand command;
            if (identity.Length == 0)
            {
                if (Session.Document.State.Settings.DefaultRepository is not { Length: > 0 } repository)
                    throw new ArgumentException("設定で新規 Issue の作成先を選んでください。");
                var row = PlanRow.New("", repository);
                // Use the same typed edit semantics (a typed end stays) for a new row.
                var temporary = Session.Document with { State = Session.Document.State with { Rows = Session.Document.State.Rows.Add(row) } };
                var edited = PlanOperations.Apply(temporary, new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, field, value)]), Today).State.Rows[^1];
                command = new InsertPlanRows([edited]) { NewPeople = resolved };
                selected = anchor = row.Identity;
            }
            else command = new EditPlanCells(PlanOperationKind.Cell, [new(identity, field, value)]) { NewPeople = resolved };
            // Execute accepts the immutable local operation synchronously. Present it now;
            // durable persistence still gates the next command and normal window close.
            var save = Session.Execute(command, Today);
            metrics.Mark("accepted");
            FinishInput();
            Refresh();
            // #229: a typed start that a predecessor moved later is reported here, at the commit that
            // produced it, from the schedule Refresh has already calculated. A start that holds, or any
            // other edit, leaves no notice behind.
            if (field == PlanField.Start && identity.Length > 0 && value is DateOnly typed)
            {
                if (Schedule.TryGetValue(identity, out var scheduledTask)
                    && PlanStartNotice.Detect(Session.Document, identity, typed, scheduledTask) is { } notice)
                    PresentStartNotice(notice);
                else DismissStartNotice();
            }
            metrics.Mark("presented");
            CompositionTarget.Rendered -= FrameRendered; CompositionTarget.Rendered += FrameRendered; frameSubscribed = true;
            Check(await save);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            // Escape or a cancelled workspace operation cancels the lookup: nothing is written and the cell
            // keeps its typed text for the PMO to correct or confirm again.
            error.Text = ""; metrics.End(pendingFrame, "cancelled");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Problems[(identity, field)] = ex.Message; RefreshRealized(); metrics.End(pendingFrame, "rejected");
            // #225: a refused 担当者 login keeps the message and the candidate list stays open with the closest
            // people, so correcting the value needs no second step. The in-flight lookup sentence no longer
            // applies once the lookup has ended.
            if (field == PlanField.Assignees) { error.Text = ""; ShowClosestAssigneeCandidates(identity, field, text); }
            throw new InputRejectedException(ex.Message, ex);
        }
    }
    // #253: verify the logins the sheet could not resolve locally, one read per login and repository. The
    // status line names the login while the typed text stays in the editing cell. A failed lookup refuses the
    // cell; the caller writes nothing.
    private async Task<ImmutableArray<PlanResource>> ResolveAssignees(IReadOnlyList<(string Repository, string Login)> pending)
    {
        if (pending.Count == 0) return ImmutableArray<PlanResource>.Empty;
        if (resolveAssignee is null) throw new ArgumentException("担当者を確認できません: " + pending[0].Login);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        lookup = cancellation;
        try
        {
            var resolved = ImmutableArray.CreateBuilder<PlanResource>();
            foreach (var (repository, login) in pending)
            {
                if (string.IsNullOrWhiteSpace(repository)) throw new ArgumentException("担当者のリポジトリを確認できません。");
                error.Text = $"担当者 {login} を確認しています…";
                resolved.Add(await resolveAssignee(repository, login, cancellation.Token));
            }
            return resolved.ToImmutable();
        }
        finally { if (ReferenceEquals(lookup, cancellation)) lookup = null; }
    }
    private void CancelLookup()
    {
        lookup?.Cancel(); lookup = null;
    }
    private void FrameRendered(object? sender, RenderedEventArgs args)
    {
        CompositionTarget.Rendered -= FrameRendered; frameSubscribed = false;
        metrics.End(pendingFrame, "rendered");
    }
    internal void CancelEdit(string identity, string field)
    {
        CancelLookup();
        Pending.Remove((identity, field)); Problems.Remove((identity, field)); error.Text = "";
        DismissStartNotice();
        Refresh();
    }
    internal Column[] ConfiguredColumns { get; private set; } = Columns;
    internal void Refresh()
    {
        if (disposed) return;
        var document = Session.Document;
        // #229: the typed-start notice explains one calculation; another operation makes its message stale.
        if (shownStartNotice is not null && !ReferenceEquals(startNoticeDocument, document)) DismissStartNotice();
        var configured = PlanSheetColumns.All(document).Select(c => {
            var standard = Columns.FirstOrDefault(s => (s.Indicator ? "Indicator" : s.Field ?? "Id") == c.Key);
            // repo#N needs room for the repository name (#263).
            var width = c.Key == "Id" && PlanTaskNames.Of(document).CarryRepository ? 112 : standard?.Width ?? 140;
            return standard is null ? new Column(c.Key, c.Name, c.Width ?? width) : standard with { Label = c.Name, Width = c.Width ?? width };
        }).ToArray();
        if (!ConfiguredColumns.SequenceEqual(configured)) ConfiguredColumns = configured;
        var choices = PlanSheetColumns.All(document).ToDictionary(c => c.Key);
        Hidden.Clear();
        foreach (var c in ConfiguredColumns.Where(c => !c.Indicator && !choices[c.Field ?? "Id"].Visible)) Hidden.Add(c.Field);
        IndicatorVisible = choices["Indicator"].Visible;
        var visible = ConfiguredColumns.Where(c => choices[c.Indicator ? "Indicator" : c.Field ?? "Id"].Visible).ToArray();
        if (!VisibleColumns.SequenceEqual(visible)) VisibleColumns = visible;
        var today = Today;
        metrics.Mark("derived-start");
        // A filter or fold only changes presentation. Document identity also invalidates
        // baseline and sync-derived values, including changes that leave local rows intact.
        if (!ReferenceEquals(presentedDocument, document) || presentedToday != today)
        {
            BlockingConflicts = PlanOperations.BlockingConflicts(document);
            Rows = PlanOperations.EffectiveRows(document).ToDictionary(r => r.Identity);
            peopleNames = document.State.Settings.People.ToDictionary(p => p.Identity, p => p.Name);
            PlanIds = document.State.Rows.Select((r, i) => (r.Identity, Id: i + 1)).ToDictionary(p => p.Identity, p => p.Id);
            TaskNames = PlanTaskNames.Of(document);
            var scheduled = PlanOperations.Schedule(document, today);
            Schedule = scheduled.ToDictionary(r => r.Input.Identity);
            var calendar = new PlanCalendar { ImportedHolidays = document.State.Settings.ImportedHolidays?.ToPreset(), CompanyDaysOff = document.State.Settings.CompanyDaysOff.ToHashSet() };
            var nextNonWorkingDates = calendar.Holidays.Dates.Select(d => d.Date).Concat(calendar.ImportedHolidays?.Dates.Select(d => d.Date) ?? []).Concat(calendar.CompanyDaysOff).ToHashSet();
            if (!nonWorkingDates.SetEquals(nextNonWorkingDates)) timelineKey = null;
            nonWorkingDates = nextNonWorkingDates;
            lateness = PlanLateness.Classify(scheduled, calendar, document.State.Settings.StatusDate ?? today);
            Unpublished = PlanOperations.Changes(document, today, scheduled);
            untitledNewRows = PlanOperations.UntitledNewRows(document);
            SummaryIds = Rows.Values.Where(r => r.Parent is not null && Rows.ContainsKey(r.Parent)).Select(r => r.Parent!).ToHashSet();
            folded.IntersectWith(SummaryIds);
            presentedDocument = document; presentedToday = today;
        }
        metrics.Mark("derived");
        RefreshVisibleRows(document);
        metrics.Mark("visible-rows");
        rendering = true;
        try { statusDate.Date = new DateTimeOffset(StatusDate.ToDateTime(TimeOnly.MinValue)); }
        finally { rendering = false; }
        ReconcileSelection(); RefreshLayout();
        metrics.Mark("layout");
        RenderUntitledRows(); UpdateReason(); RefreshOverviewCommands(); Changed?.Invoke();
    }
    // The rows a publish will not create because they have no title (#247). Both the line and the cell
    // mark are derived from the document, so they vanish with the cause.
    private void RenderUntitledRows()
    {
        var untitled = untitledNewRows.Where(PlanIds.ContainsKey).OrderBy(id => PlanIds[id]).ToArray();
        // The red outline already says where; the line only names the rows. 行を削除 is the toolbar's own command.
        var text = untitled.Length == 0 ? "" : "タイトル未入力: " + TaskNames.List(untitled.Take(10))
            + (untitled.Length > 10 ? "（ほか " + (untitled.Length - 10).ToString("N0") + " 件）" : "");
        if (untitledRows.Text == text) return;
        untitledRows.Text = text; AutomationProperties.SetName(untitledRows, text);
    }
    private void RefreshVisibleRows(PlanDocument document)
    {
        var pendingRows = Pending.Keys.Select(k => k.Identity)
            .Concat(Realized.Where(r => r.Cells.Any(c => c.Composing)).Select(r => r.Identity)).ToHashSet();
        var next = new List<string>(document.State.Rows.Length + 1);
        var matches = 0;
        foreach (var row in document.State.Rows)
        {
            var matchesFilter = acceptedFilter.Length > 0 && MatchesFilter(row);
            if (matchesFilter) matches++;
            if (pendingRows.Contains(row.Identity) || (acceptedFilter.Length > 0 ? matchesFilter : !HiddenByFold(row))) next.Add(row.Identity);
        }
        next.Add("");
        filterCount.Text = acceptedFilter.Length == 0 ? "" : $"{matches:N0} 件";
        filterCount.Visibility = acceptedFilter.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!RowIds.SequenceEqual(next))
        {
            // Collection notifications can synchronously refresh or focus containers.
            // Publish a coherent row topology before notifying, then restore selection.
            var priorSelection = (selected, anchor, selectedField, anchorField);
            RowIds = next;
            RowIndexes = next.Select((identity, index) => (identity, index)).ToDictionary(p => p.identity, p => p.index);
            RefreshEdges();
            if (List.ItemsSource is null) List.ItemsSource = rowItems = new(next);
            else if (FocusWithinRows() || ChangedIdentities(next) <= RowResetThreshold) ReconcileRowItems(next);
            // One notification per row makes a version-scale filter cost hundreds of milliseconds.
            // Without a focused editor to keep, a single reset only re-realizes the viewport.
            else rowItems.Replace(next);
            (selected, anchor, selectedField, anchorField) = priorSelection;
        }
    }
    private const int RowResetThreshold = 100;
    private int ChangedIdentities(IReadOnlyList<string> next)
    {
        var current = rowItems.ToHashSet();
        var wanted = next.ToHashSet();
        return current.Count(identity => !wanted.Contains(identity)) + wanted.Count(identity => !current.Contains(identity));
    }
    private bool FocusWithinRows()
    {
        for (DependencyObject? element = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, List)) return true;
        return false;
    }
    private void ReconcileRowItems(IReadOnlyList<string> next)
    {
        // Filtering keeps canonical document order. Removing only absent rows retains
        // native editors for matching identities instead of resetting every container.
        var wanted = next.ToHashSet();
        for (var i = rowItems.Count - 1; i >= 0; i--)
            if (!wanted.Contains(rowItems[i])) rowItems.RemoveAt(i);
        var retained = rowItems.ToHashSet();
        for (var i = 0; i < next.Count; i++)
        {
            if (i < rowItems.Count && rowItems[i] == next[i]) continue;
            if (retained.Contains(next[i])) rowItems.Move(rowItems.IndexOf(next[i]), i);
            else rowItems.Insert(i, next[i]);
        }
    }
    internal string Header(Column column) => column.Label;
    internal static string FullName(Column column) => Columns.FirstOrDefault(c => c.Field == column.Field && c.Indicator == column.Indicator) is { } standard && standard.Label != column.Label ? column.Label : column.Field switch {
        PlanField.Estimate => "見積工数", PlanField.Actual => "実績工数", PlanField.Remaining => "見込み残存工数", _ => column.Label };

    internal static string EffortPrefix(Column column) => column.Field is PlanField.Estimate or PlanField.Actual or PlanField.Remaining ? FullName(column) + " · " : "";
    private string HeaderHelp(Column column)
    {
        var source = PlanSheetColumns.All(Session.Document).First(c => c.Key == (column.Indicator ? "Indicator" : column.Field ?? "Id")).GitHub;
        return EffortPrefix(column) + (source == "—" && column.Field is not null ? "未設定" : "GitHub: " + source)
            + (column.Field is { } field && PlanSheetColumns.IsIssueField(Session.Document, field) ? "\n" + PlanSheetColumns.IssueFieldShared : "");
    }
    internal static string DateText(DateOnly? day, bool full = false) => day is { } value
        ? value.ToString(full ? "yyyy-MM-dd" : "M/d", CultureInfo.InvariantCulture) + " (" + "日月火水木金土"[(int)value.DayOfWeek] + ")" : "";
    internal bool IsDateField(string field) => field is PlanField.Start or PlanField.End
        || PlanSheetColumns.IsExtra(field) && Session.Document.Baseline.Columns.SingleOrDefault(c => "Project:" + c.Id == field)?.DataType == "DATE";
    internal DateOnly? CellDate(string identity, string field) => PlanSheetColumns.IsExtra(field) && IsDateField(field)
        ? Rows.TryGetValue(identity,out var row) && PlanExtraEditing.Value(row,field) is string text && DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day) ? day : null : field switch {
        PlanField.Start => Schedule.GetValueOrDefault(identity)?.Start.Value,
        PlanField.End => Schedule.GetValueOrDefault(identity)?.End.Value,
        "ForecastEnd" => ForecastEndOf(identity), _ => null };
    // 見込終了 is a calculated read-only column: the date is secondary text and a later forecast adds a red
    // working-day slip (#236 decision 5).
    internal string ForecastAccessibleValue(string identity) => ForecastEndOf(identity) is { } day
        ? DateText(day, true) + (WorkingDaysLater(identity) is { } later ? " +" + later + "日（稼働日）" : "") : "";
    internal string EditForm(string identity, string field) => IsDateField(field)
        ? CellDate(identity, field)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""
        : field == PlanField.Assignees && Rows.TryGetValue(identity, out var row)
            ? string.Join(", ", row.Assignees.Select(id => Session.Document.Sync.PeopleNames.GetValueOrDefault(id)
                ?? peopleNames.GetValueOrDefault(id) ?? "担当者（未確認）")) : field is PlanField.Estimate or PlanField.Actual or PlanField.Remaining
                ? Display(identity, field).TrimEnd('h') : Display(identity, field);
    internal PlanTaskLateness? LatenessOf(string identity) => lateness.Tasks.GetValueOrDefault(identity);
    private string AssigneeName(string id) => !string.IsNullOrWhiteSpace(peopleNames.GetValueOrDefault(id)) ? peopleNames[id]
        : !string.IsNullOrWhiteSpace(Session.Document.Sync.PeopleNames.GetValueOrDefault(id)) ? Session.Document.Sync.PeopleNames[id] : "担当者（未確認）";
    internal string AssigneeTooltip(string identity) => Rows.TryGetValue(identity, out var row)
        ? string.Join(", ", row.Assignees.Select(AssigneeName)) : "";
    internal string Display(string identity, string field)
    {
        if (!Rows.TryGetValue(identity, out var row)) return "";
        if (PlanSheetColumns.IsExtra(field) && PlanExtraEditing.Read(row, field).Availability is not (ValueAvailability.Present or ValueAvailability.Empty)) return "取得できません";
        if (PlanSheetColumns.IsExtra(field) && IsDateField(field)) return DateText(CellDate(identity,field));
        var computed = Schedule[identity];
        string Date(DateOnly? day) => DateText(day);
        return field switch {
            PlanField.Labels => string.Join(", ", row.Labels),
            PlanField.Start => Date(computed.Start.Value), PlanField.End => Date(computed.End.Value),
            // 見込終了 is read-only and derived (#236 part B): the date plus the working days it is later.
            "ForecastEnd" => computed.ForecastEnd is { } forecast ? DateText(forecast)
                + (lateness.Tasks.GetValueOrDefault(identity)?.DaysLater is { } later ? " +" + later + "日" : "") : "",
            PlanField.Estimate => computed.Estimate is { } estimate ? estimate.ToString(CultureInfo.CurrentCulture) + "h" : "",
            PlanField.Remaining => computed.Remaining is { } remaining ? remaining.ToString(CultureInfo.CurrentCulture) + "h" : "",
            PlanField.Actual => computed.Actual is { } actual ? actual.ToString(CultureInfo.CurrentCulture) + "h" : "",
            PlanField.Phase => Session.Document.State.Settings.Columns.Any(c => c.Role == field) ? row.Phase ?? "" : "",
            PlanField.Predecessors => TaskNames.List(row.Predecessors),
            PlanField.Assignees => row.Assignees.Length == 0 ? "" : AssigneeName(row.Assignees[0])
                + (row.Assignees.Length > 1 ? $" +{row.Assignees.Length - 1}" : ""),
            _ => PlanOperations.Value(row, field)?.ToString() ?? ""
        };
    }
    internal bool IsCalculated(string identity, string field) => Schedule.TryGetValue(identity, out var result) &&
        (field == PlanField.Start && result.Start.Origin == DateOrigin.Calculated
        || field == PlanField.End && result.End.Origin == DateOrigin.Calculated
        // 見込終了 is always the scheduler's derived forecast (#236).
        || field == "ForecastEnd" && result.ForecastEnd is not null
        || field == PlanField.Remaining && (result.Input.Closed || result.Input.Remaining is null && result.Remaining is not null)
        || result.IsSummary && field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual);
    internal string? ReadOnlyReason(string identity, string field) => Rows.TryGetValue(identity, out var row)
        ? field == PlanField.Repository ? "この列は読み取り専用です。"
        : PlanSheetColumns.IsExtra(field) && PlanExtraEditing.ReadOnlyReason(Session.Document,row,field) is { } extraReason ? extraReason
        : field == PlanField.Phase && !Session.Document.State.Settings.Columns.Any(c => c.Role == field) ? "工程のフィールドが未設定です。"
            : PlanOperations.ReadOnlyReason(row, Schedule.GetValueOrDefault(identity)?.IsSummary == true, field) : null;
    internal bool ReadOnly(string identity, string field) => remoteBusy || ReadOnlyReason(identity, field) is not null;
    internal bool IsChanged(string identity, string field) => !(Schedule.GetValueOrDefault(identity)?.IsSummary == true
        && field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual) &&
        Unpublished.Fields.TryGetValue(identity, out var fields) && fields.Contains(field);
    private void RowContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // This template is populated from Item, with no DataContext bindings.
        if (args.Phase == 0) args.Handled = true;
        if (args.ItemContainer.ContentTemplateRoot is PlanSheetRow row)
            row.SetOwner(args.InRecycleQueue ? null : this, args.InRecycleQueue ? null : (string)args.Item);
        else if (!args.InRecycleQueue && args.Phase == 0)
            args.RegisterUpdateCallback(1, RowContentChanging);
    }
    internal void RefreshRealized(CellRange? previousSelection = null)
    {
        var rows = Realized.ToArray();
        metrics.Count("realizedRows", rows.Length);
        if (previousSelection is { } previous) {
            var current = Range;
            rows = rows.Where(row => {
                var index = RowIndexes.GetValueOrDefault(row.Identity, -1);
                return index >= previous.Row && index < previous.Row + previous.RowCount
                    || index >= current.Row && index < current.Row + current.RowCount;
            }).ToArray();
        }
        metrics.Count("refreshedRows", rows.Length); metrics.Mark("rows-start");
        var started = metrics.MeasureStart();
        try { foreach (var row in rows) row.Refresh(); }
        finally { metrics.AddDuration("rowRefresh", started); }
        metrics.Mark("rows"); UpdateInputProblem();
    }
    internal void UpdateInputProblem()
    {
        if (disposed || !IsLoaded || Problems.Count == 0) { inputProblem.Close(); return; }
        var cells = Realized.Where(r => r.IsLoaded && ReferenceEquals(r.Owner, this))
            .SelectMany(r => r.Cells.Where(c => c.IsLoaded && Problems.ContainsKey((r.Identity, c.Field)) && InputProblemCellIsVisible(c))
            .Select(c => (Cell: c, Problem: Problems[(r.Identity, c.Field)]))).ToArray();
        var target = cells.OrderByDescending(c => c.Cell.FocusState != FocusState.Unfocused).FirstOrDefault();
        if (target.Cell is null) { inputProblem.Close(); return; }
        inputProblem.Show(target.Cell, target.Problem);
    }
    private void RowViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate) pageOffset = null;
        if (Problems.Count > 0) UpdateInputProblem();
    }
    private bool InputProblemCellIsVisible(PlanSheetCell cell)
    {
        if (cell.Visibility != Visibility.Visible || cell.ActualWidth <= 0 || cell.ActualHeight <= 0) return false;
        // A native popup anchor can keep its editor prepared outside the viewport.
        // Loaded/owned alone must not leave that task's error over other rows.
        var bounds = cell.TransformToVisual(List).TransformBounds(new(0, 0, cell.ActualWidth, cell.ActualHeight));
        return bounds.Bottom > 0 && bounds.Top < List.ActualHeight && bounds.Right > 0 && bounds.Left < SheetViewport;
    }
    private void RefreshLayout(bool preserveDateViewport = true, bool columnResize = false)
    {
        if (disposed) return;
        var previousOffset = ChartOffset;
        var width = Math.Max(320, ActualWidth - 20);
        var minimumChartWidth = dividerWidth.HasValue ? 230 : 320;
        SheetViewport = Math.Clamp(dividerWidth ?? SheetWidth + 6, 160, Math.Max(160, width - minimumChartWidth));
        ChartViewport = width - SheetViewport;
        metrics.Mark("timeline-range-start");
        var offsetShift = columnResize ? 0 : UpdateTimelineRange();
        metrics.Mark("timeline-range");
        if (acceptedZoom == 3) DayWidth = (ChartViewport - 2 * ChartDateInset) / DayCount;
        if (divider is not null) divider.Margin = new(SheetViewport - 3, 0, 0, 0);
        RowHeight = 28;
        sheetClip.Width = SheetViewport;
        foreach (var grid in new[] { headers, scrollbars })
        { grid.ColumnDefinitions[0].Width = new(SheetViewport); grid.ColumnDefinitions[1].Width = new(ChartViewport); }
        ((FrameworkElement)sheetHorizontal.Content).Width = SheetWidth + 4;
        ((FrameworkElement)chartHorizontal.Content).Width = DayCount * DayWidth + 2 * ChartDateInset;
        sheetHorizontal.Width = SheetViewport; chartHorizontal.Width = ChartViewport;
        metrics.Mark("layout-properties");
        if (initialChartPositioned && preserveDateViewport && acceptedZoom != 3 && offsetShift != 0) {
            // Native extent changes can clamp the old offset; restore the date using the pre-layout offset.
            metrics.Mark("forced-layout-start");
            chartHorizontal.UpdateLayout();
            metrics.Mark("forced-layout");
            ChangeTimelineOffset(Math.Clamp(previousOffset + offsetShift, 0, chartHorizontal.ScrollableWidth));
        }
        RefreshHeaders(); RenderTimelineHeader();
        metrics.Mark("headers");
        RefreshRealized();
    }
    private void RefreshHeaders()
    {
        if (resizingColumn is not null) { LayoutColumnHeaders(); return; }
        var key = string.Join("|", VisibleColumns.Select(c => $"{c.Field}:{c.Indicator}:{c.Label}:{c.Width}:{HeaderHelp(c)}"));
        if (headerKey == key) { LayoutColumnHeaders(); return; }
        headerKey = key;
        sheetHead.Children.Clear();
        columnHeaders.Clear();
        foreach (var column in VisibleColumns)
        {
            var text = Id(new TextBlock { Text = column.Indicator ? "\uE946" : Header(column), Width = column.Width, Padding = new(8, 0, 4, 6), FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("TextFillColorSecondaryBrush") },
                "PlanHeader" + (column.Indicator ? "Indicator" : column.Field?.ToString() ?? "Id"));
            if (column.Indicator) text.FontFamily = new FontFamily("Segoe Fluent Icons");
            AutomationProperties.SetName(text, FullName(column)); AutomationProperties.SetHelpText(text, HeaderHelp(column));
            ToolTipService.SetToolTip(text, HeaderHelp(column));
            var cell = new Grid { Height = 48, Width = column.Width }; cell.Children.Add(text); sheetHead.Children.Add(cell);
            var grip = column.Indicator ? null : ColumnGrip(column);
            columnHeaders.Add((cell, text, grip));
        }
        foreach (var (_, _, grip) in columnHeaders) if (grip is not null) sheetHead.Children.Add(grip);
        LayoutColumnHeaders();
        sheetHead.RenderTransform = new TranslateTransform { X = -SheetOffset };
    }
    private PlanDocument? milestoneDocument;
    private DateOnly milestoneDay;
    private IReadOnlyList<SummaryMilestone> milestones = [];
    private static string MilestoneDescription(SummaryMilestone item)
    {
        var m = item.Milestone;
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        return $"{m.Title} 目標日 {m.DueDate?.ToString("M/d (ddd)", culture) ?? "—"} · 完了見込 {item.Forecast?.ToString("M/d (ddd)", culture) ?? "—"}"
            + (item.Forecast > m.DueDate ? $" · +{item.Difference}日" : "") + (m.Closed ? " · クローズ" : "")
            + (m.ConflictingDates ? " · 目標日がリポジトリで異なります" : "");
    }
    private Task SelectMilestone(string title) => Run(async () => {
        await CommitPending();
        var current = milestones.SingleOrDefault(value => value.Milestone.Title == title);
        if (current is null) return;
        if (current.LateTasks.Count > 0) FilterTasks(current.LateTasks.ToHashSet(), title);
        else {
            reason.Text = title + " · 目標日を過ぎるタスクはありません";
            AutomationProperties.SetName(reason, reason.Text); ToolTipService.SetToolTip(reason, reason.Text);
        }
    }, "Milestone");
    private void RenderMilestones(double height)
    {
        var labelRight = 0d;
        var groups = milestones.Select((item, index) => (Item: item, Index: index))
            .Where(value => value.Item.Milestone.DueDate.HasValue).GroupBy(value => value.Item.Milestone.DueDate!.Value).ToArray();
        for (var g = 0; g < groups.Length; g++) {
            var group = groups[g]; var due = group.Key;
            var first = group.First(); var i = first.Index;
            var x = X(due);
            if (x < 0 || x > ChartViewport) continue;
            var late = group.Any(value => value.Item.Forecast > due);
            var closed = group.All(value => value.Item.Milestone.Closed);
            var brush = Brush(closed ? "TextFillColorDisabledBrush" : late ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            var description = string.Join("\n", group.Select(value => MilestoneDescription(value.Item)));
            chartStatus.Children.Add(Id(new Microsoft.UI.Xaml.Shapes.Line { X1 = x, X2 = x, Y1 = 0, Y2 = height,
                Stroke = brush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } }, "PlanMilestoneLine_" + i));
            var marker = Id(new Button { Content = "◆", Width = 20, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new(0),
                Background = Brush("WorkspaceCardBrush"), BorderThickness = new(0), Foreground = brush }, "PlanMilestone_" + i);
            AutomationProperties.SetName(marker, description); ToolTipService.SetToolTip(marker, description);
            if (group.Count() == 1) marker.Click += async (_, _) => await SelectMilestone(first.Item.Milestone.Title);
            else {
                var menu = new MenuFlyout();
                foreach (var value in group) {
                    var choice = Id(new MenuFlyoutItem { Text = MilestoneDescription(value.Item) }, "PlanMilestoneChoice_" + value.Index);
                    AutomationProperties.SetName(choice, choice.Text);
                    choice.Click += async (_, _) => await SelectMilestone(value.Item.Milestone.Title);
                    menu.Items.Add(choice);
                }
                marker.Flyout = menu;
            }
            Canvas.SetLeft(marker, x - 10); Canvas.SetTop(marker, 48); chartHead.Children.Add(marker);
            var next = g + 1 < groups.Length ? X(groups[g + 1].Key) : ChartViewport;
            var difference = late ? $"+{group.Max(value => value.Item.Difference)}日" : "";
            var title = string.Join(" / ", group.Select(value => value.Item.Milestone.Title));
            var label = Id(new TextBlock { Text = (late ? difference + " " : "") + title, FontSize = 11, Foreground = brush,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false }, "PlanMilestoneLabel_" + i);
            // Measure in the header's inherited text context, then constrain the rendered extent.
            chartHead.Children.Add(label);
            var left = Math.Max(x + 12, labelRight);
            var available = Math.Max(0, Math.Min(next - 12, ChartViewport) - left);
            label.Measure(new Size(double.PositiveInfinity, 22));
            if (label.DesiredSize.Width > available) {
                label.Text = difference;
                label.Measure(new Size(double.PositiveInfinity, 22));
            }
            var width = label.DesiredSize.Width;
            if (label.Text.Length == 0 || width <= 0 || width > available) {
                chartHead.Children.Remove(label);
                continue;
            }
            label.Width = width;
            Canvas.SetLeft(label, left); Canvas.SetTop(label, 51);
            labelRight = left + width + 4;
        }
    }
    private void RenderTimelineHeader()
    {
        if (!ReferenceEquals(milestoneDocument, Session.Document) || milestoneDay != StatusDate) {
            milestoneDocument = Session.Document; milestoneDay = StatusDate;
            milestones = Session.Document.Baseline.Milestones.IsEmpty ? [] : PlanSummary.CalculateMilestones(Session.Document, Schedule.Values.ToArray());
            timelineKey = null;
        }
        var height = List.ActualHeight;
        var key = $"{FirstDay}:{DayCount}:{DayWidth}:{ChartViewport}:{ChartOffset}:{StatusDate}:{height}:{acceptedZoom}";
        chartHead.Height = milestones.Any(m => m.Milestone.DueDate.HasValue) ? 70 : 48;
        sheetClip.Height = chartHead.Height;
        var headerBounds = new Rect(0, 0, SheetViewport, sheetClip.Height);
        if (sheetClip.Clip is RectangleGeometry geometry) {
            if (geometry.Rect != headerBounds) geometry.Rect = headerBounds;
        } else sheetClip.Clip = new RectangleGeometry { Rect = headerBounds };
        if (timelineKey == key) return;
        timelineKey = key;
        chartHead.Children.Clear(); chartHead.Width = ChartViewport;
        chartHead.Clip = new RectangleGeometry { Rect = new(0, 0, ChartViewport, chartHead.Height) };
        foreach (var layer in new[] { chartBackground, chartStatus }) {
            layer.Children.Clear(); layer.Width = ChartViewport;
            layer.Margin = new(SheetViewport, 0, 0, 0);
            layer.Clip = new RectangleGeometry { Rect = new(0, 0, ChartViewport, height) };
        }
        var first = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, FirstDay.DayNumber + Math.Max(0, (int)((ChartOffset - ChartDateInset) / DayWidth))));
        var last = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber,
            FirstDay.DayNumber + Math.Min(DayCount - 1, (int)((ChartOffset + ChartViewport - ChartDateInset) / DayWidth))));
        var upperBounds = new List<(double Left, double Right)>();
        bool AddLabel(string label, double x, double end, bool upper, string id) {
            var text = Id(new TextBlock { Text = label, FontSize = 11,
                FontWeight = upper ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = Brush("TextFillColorSecondaryBrush") }, id);
            chartHead.Children.Add(text);
            text.Measure(new Size(double.PositiveInfinity, 24));
            var left = Math.Max(0, x) + 3;
            var availableEnd = Math.Min(ChartViewport, end);
            if (upper && upperBounds.Count == 0) { left = 3; availableEnd = ChartViewport; }
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
            if (AddLabel(label, X(period), ChartDateInset + (endDay - FirstDay.DayNumber) * DayWidth - ChartOffset, true, "PlanTimelineUpper" + period.DayNumber)) firstUpper = false;
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
                X(day), ChartDateInset + (endDay - FirstDay.DayNumber) * DayWidth - ChartOffset, false, "PlanTimelineLabel" + day.DayNumber);
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
        RenderMilestones(height);
        var statusX = X(StatusDate);
        chartStatus.Children.Add(Id(new Microsoft.UI.Xaml.Shapes.Line { X1 = statusX, X2 = statusX, Y1 = 0, Y2 = height,
            Stroke = Brush("SystemControlHighlightAccentBrush"), StrokeThickness = 2 }, "PlanStatusLine"));
        if (statusX < 0 || statusX > ChartViewport) return;
        var pill = Id(new Border { CornerRadius = new(9), Padding = new(7, 1, 7, 1), Background = Brush("SystemControlHighlightAccentBrush"),
            Child = new TextBlock { Text = $"基準日 {StatusDate:M/d}", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("TextOnAccentFillColorPrimaryBrush") } }, "PlanStatusDateLabel");
        AutomationProperties.SetName(pill, $"基準日 {StatusDate:M/d}");
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
        if (candidates.Length == 0 && acceptedZoom == 3 && upperBounds.Count > 0
            && ChartViewport - upperBounds[0].Right - 4 >= pillWidth) {
            // Exact full-period fitting can crowd year labels; keep the first year and the status pill.
            var left = Math.Clamp(statusX - pillWidth / 2, upperBounds[0].Right + 4, ChartViewport - pillWidth);
            foreach (var label in chartHead.Children.OfType<TextBlock>().Where(label =>
                AutomationProperties.GetAutomationId(label).StartsWith("PlanTimelineUpper", StringComparison.Ordinal)
                && Canvas.GetLeft(label) > upperBounds[0].Left
                && Canvas.GetLeft(label) < left + pillWidth + 4 && Canvas.GetLeft(label) + label.DesiredSize.Width > left - 4).ToArray())
                chartHead.Children.Remove(label);
            candidates = [left];
        }
        if (candidates.Length == 0) { chartHead.Children.Remove(pill); return; }
        Canvas.SetLeft(pill, candidates[0]); Canvas.SetTop(pill, 2);
    }
    internal double X(DateOnly day) => ChartDateInset + (day.DayNumber - FirstDay.DayNumber) * DayWidth - ChartOffset;
}
internal sealed class PlanRowItems : ObservableCollection<string>
{
    public PlanRowItems() { }
    public PlanRowItems(IEnumerable<string> values) : base(values) { }
    internal void Replace(IEnumerable<string> values)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

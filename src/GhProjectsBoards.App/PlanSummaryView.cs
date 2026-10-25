using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace GhProjectsBoards.App;

internal sealed class PlanSummaryView : UserControl
{
    internal PlanSession Session { get; }
    private readonly Action<IReadOnlySet<string>> filter;
    private readonly Action settings;
    private readonly StackPanel body = new() { Spacing = 12 };
    // #260 owns the verdict wording; the summary reserves its line above everything else.
    private readonly Grid verdictSlot = new();
    private readonly Grid columns = new() { ColumnSpacing = 16 };
    private readonly StackPanel leftColumn = new() { Spacing = 4 };
    private readonly StackPanel rightColumn = new() { Spacing = 6 };
    private readonly Grid matrixArea = new();
    private readonly ScrollView matrixScroll;
    private readonly StackPanel matrixStack = new();
    private readonly StackPanel matrixHeader = new();
    private readonly StackPanel matrixRows = new();
    private readonly ScrollViewer matrixBody;
    private readonly Border loadPanel;
    private readonly StackPanel loadPanelContent = new() { Spacing = 6 };
    private readonly TextBlock loadTitle;
    private readonly TextBlock loadPercent;
    private readonly TextBlock loadOverDays;
    private readonly TextBlock loadMax;
    private readonly Canvas loadDays;
    private readonly TextBlock loadDaysCaption;
    private readonly StackPanel loadTasks = new() { Spacing = 4 };
    private readonly TextBlock loadTasksMore;
    private readonly Button loadPlanOpen;
    private readonly TextBlock unallocatedNote;
    private readonly ScrollViewer scroll;
    private readonly ComboBox modeSelector;
    private readonly ComboBox measureSelector;
    private readonly Dictionary<string, Button> cells = [];
    private string? selectedCell;
    private readonly StackPanel detail = new() { Spacing = 6 };
    private PlanDocument? document;
    private DateOnly day;
    private SummaryPlan report = null!;
    private IReadOnlyDictionary<string, ScheduledTask> schedule = null!;
    private int generation, measure, mode;
    private double monthWidth = 92;
    private bool renderingMatrix;
    private DateOnly barStart, barEnd;
    private Dictionary<DateOnly, decimal?> barLoads = [];
    private IReadOnlySet<string> panelIdentities = new HashSet<string>();
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly string[] Measures = ["見込み残存工数 h", "見込工数 h", "進捗", "期限超過"];
    private const double NameColumn = 150, MatrixMaxHeight = 620;
    internal PlanSummaryView(PlanSession session, Action<IReadOnlySet<string>> filter, Action settings)
    {
        Session = session; this.filter = filter; this.settings = settings;
        AutomationProperties.SetAutomationId(this, "PlanSummary");
        AutomationProperties.SetAutomationId(verdictSlot, "SummaryVerdict");
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.Children.Add(leftColumn); columns.Children.Add(rightColumn); Grid.SetColumn(rightColumn, 1);
        // The person matrix scrolls its own rows under a header that stays on screen, so the month
        // labels never leave the numbers they describe. The outer horizontal viewport moves header and
        // rows together when a narrow window or the drill-down panel needs horizontal scrolling.
        matrixBody = new ScrollViewer { Content = matrixRows, VerticalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false };
        matrixStack.Children.Add(Id(matrixHeader, "SummaryMatrixHeader")); matrixStack.Children.Add(Id(matrixBody, "SummaryMatrixBody"));
        matrixScroll = Id(Wide(matrixStack), "SummaryMatrixScroll");
        matrixArea.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        matrixArea.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        matrixArea.Children.Add(matrixScroll);
        loadTitle = Id(TableText(""), "SummaryLoadTitle");
        loadPercent = Id(TableText(""), "SummaryLoadPercent");
        loadOverDays = Id(TableText(""), "SummaryLoadOverDays");
        loadMax = Id(TableText(""), "SummaryLoadMax");
        loadDays = Id(new Canvas { Height = 88, HorizontalAlignment = HorizontalAlignment.Stretch }, "SummaryLoadDays");
        loadDaysCaption = Id(Text(""), "SummaryLoadDaysCaption");
        loadDaysCaption.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        loadTasksMore = Id(Text(""), "SummaryLoadTaskMore");
        loadTasksMore.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        loadPanelContent.Children.Add(loadTitle); loadPanelContent.Children.Add(loadPercent); loadPanelContent.Children.Add(loadOverDays);
        loadPanelContent.Children.Add(loadMax); loadPanelContent.Children.Add(loadDays); loadPanelContent.Children.Add(loadDaysCaption);
        loadPanelContent.Children.Add(Id(Text("この月のタスク"), "SummaryLoadTasksHeading"));
        loadPanelContent.Children.Add(Id(loadTasks, "SummaryLoadTasks"));
        loadPanelContent.Children.Add(loadTasksMore);
        loadPlanOpen = Button("計画で表示", "SummaryPlanOpen", () => { if (panelIdentities.Count > 0) filter(panelIdentities); });
        loadPlanOpen.IsEnabled = false;
        loadPanelContent.Children.Add(loadPlanOpen);
        loadPanel = new Border { Width = 344, Padding = new(8), Margin = new(8, 0, 0, 0), Visibility = Visibility.Collapsed,
            BorderThickness = new(1), BorderBrush = PlanSheetView.Brush("WorkspaceCardStrokeBrush"), CornerRadius = new(4),
            Background = PlanSheetView.Brush("WorkspaceCardBrush"), Child = loadPanelContent };
        AutomationProperties.SetAutomationId(loadPanel, "SummaryLoadPanel");
        Grid.SetColumn(loadPanel, 1); matrixArea.Children.Add(loadPanel);
        unallocatedNote = Id(Text(""), "SummaryUnallocatedNote");
        unallocatedNote.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        modeSelector = Id(new ComboBox { ItemsSource = new[] { "工程別", "月別" }, SelectedIndex = mode, Width = 110 }, "SummaryMode");
        measureSelector = Id(new ComboBox { ItemsSource = Measures, SelectedIndex = measure, Width = 150, IsEnabled = mode == 0 }, "SummaryMeasure");
        AutomationProperties.SetName(modeSelector, "集計の表示期間"); AutomationProperties.SetName(measureSelector, "工程の集計指標");
        modeSelector.SelectionChanged += (_, _) => { mode = modeSelector.SelectedIndex; measureSelector.IsEnabled = mode == 0; ClearSelection(); RenderMatrix(); };
        measureSelector.SelectionChanged += (_, _) => { measure = measureSelector.SelectedIndex; RenderMatrix(); };
        var loadHeading = Id(Heading("担当者の負荷"), "SummaryLoadHeading");
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(modeSelector); controls.Children.Add(measureSelector);
        rightColumn.Children.Add(loadHeading); rightColumn.Children.Add(controls); rightColumn.Children.Add(matrixArea); rightColumn.Children.Add(unallocatedNote);
        body.Children.Add(verdictSlot); body.Children.Add(columns); body.Children.Add(detail);
        scroll = Id(new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(12) }, "SummaryScroll");
        Content = scroll;
        columns.SizeChanged += (_, args) => FitColumns(args.NewSize.Width);
        rightColumn.SizeChanged += (_, _) => UpdateMonthWidth();
        loadDays.SizeChanged += (_, _) => DrawDayBars();
        scroll.SizeChanged += (_, _) => FitMatrixHeight();
    }
    internal async void Refresh(IReadOnlyDictionary<string, ScheduledTask> scheduled, DateOnly statusDate)
    {
        var next = Session.Document;
        if (ReferenceEquals(document, next) && day == statusDate) return;
        document = next; day = statusDate; schedule = scheduled;
        var token = ++generation; var tasks = scheduled.Values.ToArray();
        body.Children.Clear(); body.Children.Add(Text("集計しています…", "SummaryLoading"));
        try {
            var result = await Task.Run(() => PlanSummary.Calculate(next, tasks, statusDate));
            if (token != generation) return;
            report = result; Render();
        } catch (Exception ex) {
            if (token != generation) return;
            document = null; body.Children.Clear(); body.Children.Add(Text("集計できません: " + ex.Message, "SummaryError"));
        }
    }
    private static T Id<T>(T control, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(control, id); return control; }
    private static string Number(decimal? number) => number?.ToString("#,0.##", Japanese) ?? "—";
    private static string Date(DateOnly? date) => date?.ToString("M/d (ddd)", Japanese) ?? "—";
    private static string Signed(decimal? value) => value is null ? "—" : value.Value.ToString("+#,0.##;-#,0.##;0", Japanese);
    internal const string CapacityFormula = "稼働可能 = 稼働率 × 残り稼働日 × 8h（上限工数がある場合は 上限工数 − 実績 まで）";
    private static string Days(int? value) => value is null ? "—" : Signed(value) + "日";
    private static string Percent(decimal? value) => PlanSummaryPresentation.Percent(value);
    private static string Progress(SummaryTasks tasks) => PlanSummaryPresentation.Progress(tasks.Progress.Value, tasks.Closed == tasks.Count)
        + (tasks.Progress.Excluded > 0 ? $"（工数未入力 {tasks.Progress.Excluded:N0} タスクを除く）" : "");
    private static string Value(SummaryValue value) => Number(value.Value) + (value.Excluded > 0 ? $"（工数未入力 {value.Excluded:N0} タスクを除く）" : "");
    private static string Finish(SummaryTasks tasks) => Date(tasks.Finish) + (tasks.FinishExcluded > 0 && tasks.Finish is not null ? $"（日程なし {tasks.FinishExcluded:N0} タスクを除く）" : "");
    private static string Phase(SummaryPhase phase) => phase.IsTotal ? "全体" : phase.Phase ?? "工程なし";
    private static TextBlock Text(string value, string id = "", bool numeric = false)
    {
        var text = Id(new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
            Margin = new(4, 2, 4, 2) }, id);
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular); return text;
    }
    private static TextBlock TableText(string value, string id = "", bool numeric = false)
    {
        var text = Text(value, id, numeric); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(text, value); return text;
    }
    private static Button Button(string value, string id, Action action)
    {
        var button = Id(new Button { Content = Text(value), FontSize = 13, Padding = new(6, 2, 6, 2), MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left }, id);
        AutomationProperties.SetName(button, value); button.Click += (_, _) => action(); return button;
    }
    private Button TableCell(string value, string id, Action action, bool left = false)
    {
        var button = Id(new Button { Content = TableText(value, numeric: !left), Style = (Style)Application.Current.Resources["SummaryCellButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right }, id);
        AutomationProperties.SetName(button, value); cells[id] = button;
        Highlight(button, selectedCell == id);
        button.Click += (_, _) => {
            if (selectedCell is not null && cells.TryGetValue(selectedCell, out var previous)) Highlight(previous, false);
            selectedCell = id; Highlight(button, true); action();
        };
        return button;
    }
    // #230 D1: a month cell shows the 負荷率 in secondary text and, when at least one day is above the
    // person's cap, the same rate and the day count in critical text (「131% 9日」). The number of days
    // is text, so the overload stays readable in greyscale.
    private Button MonthCell(SummaryMonthCell cell, string id, string description, Action action)
    {
        var content = Id(new TextBlock { FontSize = 13, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new(4, 2, 4, 2) }, id + "Text");
        Typography.SetNumeralAlignment(content, FontNumeralAlignment.Tabular);
        var critical = PlanSheetView.Brush("SystemFillColorCriticalBrush"); var secondary = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        if (cell.Kind == SummaryMonthKind.Finished) content.Inlines.Add(new Run { Text = "完了", Foreground = secondary });
        else if (cell.Kind == SummaryMonthKind.NoWork) content.Inlines.Add(new Run { Text = "—", Foreground = secondary });
        else {
            var rate = Percent(cell.Percent / 100);
            content.Inlines.Add(new Run { Text = rate, Foreground = cell.Overloaded ? critical : secondary });
            if (cell.Overloaded) content.Inlines.Add(new Run { Text = $" {cell.DaysOverCap:N0}日", Foreground = critical });
        }
        var button = Id(new Button { Content = content, Style = (Style)Application.Current.Resources["SummaryCellButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Right }, id);
        AutomationProperties.SetName(button, description); ToolTipService.SetToolTip(button, description);
        cells[id] = button;
        Highlight(button, selectedCell == id);
        button.Click += (_, _) => {
            if (selectedCell is not null && cells.TryGetValue(selectedCell, out var previous)) Highlight(previous, false);
            selectedCell = id; Highlight(button, true); action();
        };
        return button;
    }
    private static void Highlight(Button button, bool selected)
    {
        if (selected) {
            button.Background = PlanSheetView.Brush("SheetSelectionBrush"); button.BorderBrush = PlanSheetView.Brush("SheetSelectionStrokeBrush");
        } else { button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.BorderBrushProperty); }
        AutomationProperties.SetItemStatus(button, selected ? "選択中" : "");
    }
    private void ClearSelection()
    {
        if (selectedCell is not null && cells.TryGetValue(selectedCell, out var previous)) Highlight(previous, false);
        selectedCell = null; detail.Children.Clear(); HideLoadPanel();
    }
    private static ScrollView Wide(UIElement content) => new() { Content = content, ContentOrientation = ScrollingContentOrientation.Horizontal,
        HorizontalScrollMode = ScrollingScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollingScrollMode.Disabled, VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden,
        // Horizontal scrolling remains available through the scrollbar and keyboard.
        // The enclosing ScrollViewer owns vertical wheel input, including over cells.
        IgnoredInputKinds = ScrollingInputKinds.MouseWheel, IsTabStop = false };
    private static Grid Row(params double[] widths)
    {
        var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = new(width) }); return grid;
    }
    private static void Cell(Grid row, FrameworkElement cell, int column)
    {
        // Align the glyph-sized TextBlock inside an explicit cell, not by its reported text width.
        if (cell is TextBlock text && row.ColumnDefinitions[column].Width.IsAbsolute) {
            var width = row.ColumnDefinitions[column].Width.Value;
            text.MaxWidth = Math.Max(0, width - text.Margin.Left - text.Margin.Right);
            text.HorizontalAlignment = text.TextAlignment == TextAlignment.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            cell = new Border { Width = width, Child = text };
        }
        Grid.SetColumn(cell, column); row.Children.Add(cell);
    }
    private static TextBlock Heading(string title) => new() { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new(0, 2, 0, 4) };
    private void Render()
    {
        body.Children.Clear(); detail.Children.Clear(); cells.Clear(); selectedCell = null; HideLoadPanel();
        body.Children.Add(verdictSlot);
        var s = document!.State.Settings;
        // The 基準日 is the toolbar's own control; the header only names the period the figures cover.
        body.Children.Add(Text($"期間 {Date(s.ProjectStart)} – {Date(s.ProjectEnd)}", "SummaryStatusDate"));
        if (s.ProjectEnd is null) {
            body.Children.Add(Text("終了日が未設定です", "SummaryNoEnd"));
            body.Children.Add(Button("設定を開く", "SummaryPeriodSettings", settings));
        }
        body.Children.Add(Band());
        RenderColumns();
        body.Children.Add(columns); body.Children.Add(detail);
    }
    private Grid Band()
    {
        var band = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        for (var i = 0; i < 5; i++) band.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        // A card shows the figure; how it is computed is the card's tooltip.
        void Card(int index, string title, string value, string id, bool warning = false, string? caption = null, string? tooltip = null) {
            var panel = new StackPanel { Spacing = 4 }; panel.Children.Add(Text(title)); var text = Text(value, id);
            text.FontSize = 14; if (warning) text.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush"); panel.Children.Add(text);
            if (caption is not null) { var note = Text(caption, id + "Caption"); note.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush"); panel.Children.Add(note); }
            var border = new Border { Child = panel, Padding = new(8), BorderThickness = new(1), BorderBrush = PlanSheetView.Brush("WorkspaceCardStrokeBrush"), CornerRadius = new(4) };
            if (tooltip is not null) ToolTipService.SetToolTip(border, tooltip);
            AutomationProperties.SetAutomationId(border, id + "Card");
            Cell(band, border, index);
        }
        Card(0, "残り稼働日", $"{Number(report.RemainingDays)} 日\n{Number(report.ElapsedDays)} / {Number(report.WorkingDays)} 稼働日が経過", "SummaryRemainingDays");
        var difference = report.FinishDifference is null ? "—" : report.FinishDifference > 0 ? $"終了日より {report.FinishDifference} 稼働日遅れ" : report.FinishDifference < 0 ? $"終了日より {-report.FinishDifference} 稼働日前" : "終了日どおり";
        Card(1, "完了見込", $"{Finish(report.Total)}\n{difference}", "SummaryFinish", report.FinishDifference > 0, "担当者の負荷は含まない", "先行関係と見込み残存工数から計算（担当者の負荷は含まない）");
        Card(2, "終了日までに必要な稼働", $"{Percent(report.RequiredLoad)}{(report.RequiredLoad > 1 ? " 超過" : "")}", "SummaryRequiredLoad", report.RequiredLoad > 1,
            tooltip: $"残 {Value(report.Total.Remaining)}h ÷ 稼働可能 {Number(report.Capacity)}h（100% を超えると間に合わない）");
        Card(3, "進捗", $"{PlanSummaryPresentation.Progress(report.Total.Progress.Value, report.Total.Closed == report.Total.Count)}\n完了 {report.Total.Closed:N0} / {report.Total.Count:N0} タスク\n見積 {Number(report.Total.VarianceEstimate)}h より {Signed(report.Total.Variance.Value)}h" + (report.Total.Variance.Excluded > 0 ? $"（工数未入力 {report.Total.Variance.Excluded:N0} タスクを除く）" : ""), "SummaryProgress",
            tooltip: $"実績 {Number(report.Total.ProgressActual)}h ÷ 見込 {Value(report.Total.Forecast)}h");
        var attention = new StackPanel { Spacing = 3 }; attention.Children.Add(Text("要注意"));
        void Attention(string label, string id, IReadOnlyList<string> tasks, string glyph, string detail = "") {
            if (tasks.Count == 0) return;
            var button = Button($"{label} {tasks.Count:N0}{detail}", id, () => filter(tasks.ToHashSet()));
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 });
            content.Children.Add(TableText($"{label} {tasks.Count:N0}{detail}")); button.Content = content; attention.Children.Add(button);
        }
        Attention("期限超過", "SummaryOverdue", report.Attention.Overdue, "\uE814");
        Attention("遅れの原因", "SummaryCause", report.Attention.Cause, "\uE7BA");
        Attention("予定より遅れ", "SummaryLater", report.Attention.Later, "\uE7BA");
        Attention("見積超過", "SummaryOverEstimate", report.Attention.OverEstimate, "\uE7BA");
        Attention("残 0・未完了", "SummaryZeroRemaining", report.Attention.ZeroRemainingOpen, "\uE7BA");
        var unassignedRemaining = report.Attention.UnassignedRemaining;
        var unassignedDetail = unassignedRemaining.Value is null ? "" : $" · 残 {Number(unassignedRemaining.Value)}h"
            + (unassignedRemaining.Excluded > 0 ? $"（工数未入力 {unassignedRemaining.Excluded:N0} タスクを除く）" : "");
        Attention("担当者なし", "SummaryUnassigned", report.Attention.Unassigned, "\uE77B", unassignedDetail);
        Attention("工数未入力", "SummaryUnknown", report.Attention.UnknownEffort, "\uE7BA");
        Cell(band, attention, 4);
        return band;
    }
    // #230 After 1: 工程 and マイルストーン form the left column and 担当者の負荷 the right one, so the
    // PMO sees the people without scrolling past the project structure.
    private void RenderColumns()
    {
        leftColumn.Children.Clear();
        var s = document!.State.Settings;
        var mapped = s.Columns.Any(c => c.Role == PlanField.Phase && c.FieldId is not null);
        if (!mapped) {
            leftColumn.Children.Add(Text("工程のフィールドが未設定です。", "SummaryUnmapped"));
            leftColumn.Children.Add(Button("設定を開く", "SummaryPhaseSettings", settings));
        } else {
            var table = Id(new StackPanel(), "SummaryPhaseTable"); table.Children.Add(Heading("工程別"));
            double[] widths = [88, 112, 72, 72, 72, 72, 72, 64, 108, 108, 64, 76, 76];
            var rows = new StackPanel();
            var header = Row(widths); header.Height = 44; var names = new[] { "工程", "完了 / タスク", "見積 h", "実績 h", "残 h", "見込 h", "差異 h", "進捗", "目標日", "完了見込", "差", "期限超過", "見積超過" };
            for (var i = 0; i < names.Length; i++) Cell(header, TableText(names[i], numeric: i > 0), i); rows.Children.Add(header);
            for (var n = 0; n < report.Phases.Count; n++) {
                var phase = report.Phases[n]; var t = phase.Tasks; var row = Id(Row(widths), $"SummaryPhaseRow_{n}"); row.MinHeight = 28;
                Cell(row, TableCell(Phase(phase), phase.IsTotal ? "SummaryPhaseTotal" : $"SummaryPhase_{n}", () => Select(Phase(phase), t.Identities), left: true), 0);
                string[] values = [$"{t.Closed:N0} / {t.Count:N0}", Value(t.Estimate), Value(t.Actual), Value(t.Remaining), Value(t.Forecast), Signed(t.Variance.Value) + (t.Variance.Excluded > 0 ? $"（工数未入力 {t.Variance.Excluded:N0} タスクを除く）" : ""), Progress(t), Date(phase.Target), Finish(t), Days(phase.Difference), $"{t.Overdue:N0}", $"{t.OverEstimate:N0}"];
                for (var i = 0; i < values.Length; i++) { var text = TableText(values[i], numeric: true); ToolTipService.SetToolTip(text, names[i + 1] + " " + values[i]); AutomationProperties.SetName(text, Phase(phase) + " " + names[i + 1] + " " + values[i]); Cell(row, text, i + 1); }
                AutomationProperties.SetName(row, Phase(phase) + " " + string.Join(" · ", values.Select((value, i) => names[i + 1] + " " + value)));
                rows.Children.Add(row);
            }
            table.Children.Add(Wide(rows)); leftColumn.Children.Add(table);
            leftColumn.Children.Add(Timeline());
        }
        if (report.Milestones.Count > 0) {
            var table = Id(new StackPanel(), "SummaryMilestoneTable"); table.Children.Add(Heading("マイルストーン"));
            double[] widths = [200, 108, 108, 72, 112, 300];
            var header = Row(widths); header.Height = 28;
            string[] names = ["名前", "目標日", "完了見込", "差", "完了 / タスク", "状態"];
            for (var i = 0; i < names.Length; i++) {
                var text = TableText(names[i], numeric: i is > 0 and < 5);
                if (i == 5) text.Margin = new(16, 2, 4, 2);
                Cell(header, text, i);
            }
            table.Children.Add(header);
            for (var n = 0; n < report.Milestones.Count; n++) {
                var milestone = report.Milestones[n]; var m = milestone.Milestone;
                var row = Id(Row(widths), "SummaryMilestoneRow_" + n); row.MinHeight = 28;
                var title = TableCell(m.Title, "SummaryMilestone_" + n, () => Select(m.Title + " · 目標日を過ぎるタスク", milestone.LateTasks), left: true);
                var description = m.Title + (m.ConflictingDates ? " · 目標日がリポジトリで異なります" : "");
                ToolTipService.SetToolTip(title, description); AutomationProperties.SetName(title, description);
                Cell(row, title, 0);
                string[] values = [Date(m.DueDate), Date(milestone.Forecast), Days(milestone.Difference), $"{milestone.Closed:N0} / {milestone.Tasks.Count:N0}", string.Join(" · ", new[] { m.Closed ? "クローズ" : "", m.ConflictingDates ? "目標日がリポジトリで異なります" : "" }.Where(value => value.Length > 0))];
                for (var i = 0; i < values.Length; i++) {
                    var text = TableText(values[i], i == 4 ? "SummaryMilestoneState_" + n : i == 3 ? "SummaryMilestoneCount_" + n : "", numeric: i < 4);
                    if (i == 4) text.Margin = new(16, 2, 4, 2);
                    Cell(row, text, i + 1);
                }
                table.Children.Add(row);
            }
            leftColumn.Children.Add(Wide(table));
        }
        RenderMatrix();
        FitColumns(columns.ActualWidth);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitMatrixHeight);
    }
    /// <summary>At 1920 the phase table fits its column exactly; narrower windows let it scroll inside the column.</summary>
    private void FitColumns(double width)
    {
        if (width <= 0) return;
        var left = Math.Min(1058, Math.Max(340, width - 420));
        leftColumn.Width = left;
        columns.ColumnDefinitions[0].Width = new(left);
    }
    private void FitMatrixHeight()
    {
        if (scroll.ActualHeight <= 0 || matrixArea.ActualHeight <= 0) return;
        var top = matrixArea.TransformToVisual(body).TransformPoint(new()).Y - scroll.VerticalOffset;
        var height = Math.Clamp(scroll.ActualHeight - top - 12, 240, MatrixMaxHeight);
        if (Math.Abs(matrixBody.MaxHeight - height) > 1) matrixBody.MaxHeight = height;
    }
    private void UpdateMonthWidth()
    {
        if (report?.Months is null || renderingMatrix) return;
        var count = report.Months.Periods.Count;
        var available = rightColumn.ActualWidth;
        if (count == 0 || available < 240) return;
        var width = Math.Clamp((available - NameColumn - 22) / count, 62, 92);
        if (Math.Abs(width - monthWidth) < 0.5) return;
        monthWidth = width; if (mode == 1) RenderMatrix();
    }
    private FrameworkElement Timeline()
    {
        const double axisHeight = 44, rowHeight = 28;
        var panel = Id(new StackPanel(), "SummaryTimeline"); panel.Children.Add(Heading("工程の日程"));
        var start = document!.State.Settings.ProjectStart; var end = document.State.Settings.ProjectEnd;
        if (start is null || end is null) { panel.Children.Add(Text("期間が未設定です")); return panel; }
        var until = report.ForecastFinish > end ? report.ForecastFinish.Value : end.Value;
        var phases = report.Phases.Where(p => !p.IsTotal).ToArray();
        if (phases.Where(p => p.Target.HasValue).Select(p => p.Target!.Value).DefaultIfEmpty(until).Max() is var latest && latest > until) until = latest;
        if (phases.Where(p => p.Target.HasValue).Select(p => p.Target!.Value).DefaultIfEmpty(start.Value).Min() is var earliest && earliest < start) start = earliest;
        var chart = new Canvas { Height = axisHeight + report.Phases.Count * rowHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
        void Draw()
        {
            chart.Children.Clear();
            if (chart.ActualWidth <= 0) return;
            double X(int number) => Math.Clamp((number - start.Value.DayNumber) / (double)Math.Max(1, until.DayNumber - start.Value.DayNumber + 1), 0, 1) * chart.ActualWidth;
            void Line(double x, double top, double height, string brush) {
                var line = new Border { Width = 1, Height = height, Background = PlanSheetView.Brush(brush) };
                Canvas.SetLeft(line, x); Canvas.SetTop(line, top); chart.Children.Add(line);
            }
            var month = new DateOnly(start.Value.Year, start.Value.Month, 1); double previousRight = -6;
            while (month <= until) {
                var next = month.DayNumber + DateTime.DaysInMonth(month.Year, month.Month);
                var x = X(month.DayNumber);
                Line(x, 0, axisHeight + phases.Length * rowHeight, "SheetSeparatorBrush");
                var label = Id(new TextBlock { Text = previousRight < 0 || month.Month == 1 ? $"{month.Year}年{month.Month}月" : $"{month.Month}月",
                    FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush") }, "SummaryMonthLabel_" + month.DayNumber);
                label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 20));
                if (x + 3 >= previousRight + 6 && x + 3 + label.DesiredSize.Width <= X(Math.Min(next, until.DayNumber + 1))) {
                    Canvas.SetLeft(label, x + 3); Canvas.SetTop(label, 3); chart.Children.Add(label); previousRight = x + 3 + label.DesiredSize.Width;
                }
                if (next > DateOnly.MaxValue.DayNumber) break;
                month = DateOnly.FromDayNumber(next);
            }
            for (var i = 0; i < phases.Length; i++) {
                var phase = phases[i]; var top = axisHeight + i * rowHeight;
                var separator = new Border { Width = chart.ActualWidth, Height = 1, Background = PlanSheetView.Brush("SheetSeparatorBrush") };
                Canvas.SetTop(separator, top + rowHeight - 1); chart.Children.Add(separator);
                if (phase.Target is { } target) {
                    var marker = Id(new TextBlock { Text = "◆", FontSize = 12, Foreground = PlanSheetView.Brush(phase.Tasks.Finish > target ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush") }, "SummaryPhaseTarget_" + i);
                    var description = $"{Phase(phase)} 目標日 {Date(target)} · 差 {Days(phase.Difference)}";
                    AutomationProperties.SetName(marker, description); ToolTipService.SetToolTip(marker, description);
                    Canvas.SetZIndex(marker, 1); Canvas.SetLeft(marker, X(target.DayNumber) - 6); Canvas.SetTop(marker, top + 6); chart.Children.Add(marker);
                }
                if (phase.Tasks.Start is not { } first || phase.Tasks.Finish is not { } last) continue;
                var width = Math.Max(2, X(last.DayNumber + 1) - X(first.DayNumber));
                var bar = new Border { Width = width, Height = 12, Background = PlanSheetView.Brush("GanttTaskTintBrush") };
                Canvas.SetLeft(bar, X(first.DayNumber)); Canvas.SetTop(bar, top + 8); chart.Children.Add(bar);
                var done = new Border { Width = width * (double)Math.Clamp(phase.Tasks.Progress.Value ?? 0, 0, 1), Height = 12, Background = PlanSheetView.Brush("GanttTaskBrush") };
                Canvas.SetLeft(done, X(first.DayNumber)); Canvas.SetTop(done, top + 8); chart.Children.Add(done);
                AutomationProperties.SetName(bar, $"{Phase(phase)} {Date(first)} – {Date(last)} 進捗 {Percent(phase.Tasks.Progress.Value)}");
                ToolTipService.SetToolTip(bar, AutomationProperties.GetName(bar));
            }
            void Marker(DateOnly date, string name, string id, double labelTop, string brush) {
                var x = Math.Min(chart.ActualWidth - 1, X(date.DayNumber));
                Line(x, labelTop, axisHeight + phases.Length * rowHeight - labelTop, brush);
                var label = Id(new TextBlock { Text = $"{name} {Date(date)}" + (date < start || date > until ? "（期間外）" : ""),
                    FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PlanSheetView.Brush(brush) }, id);
                label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 18));
                var background = new Border { Child = label, Background = PlanSheetView.Brush("WorkspaceCardBrush") };
                Canvas.SetLeft(background, Math.Clamp(x + 3, 0, Math.Max(0, chart.ActualWidth - label.DesiredSize.Width))); Canvas.SetTop(background, labelTop); chart.Children.Add(background);
            }
            Marker(day, "基準日", "SummaryTimelineStatusDate", 20, "AccentTextFillColorPrimaryBrush");
            Marker(end.Value, "終了日", "SummaryTimelineEndDate", 37, "SystemFillColorCriticalBrush");
        }
        chart.SizeChanged += (_, _) => Draw(); panel.Children.Add(chart); return panel;
    }
    private void RenderMatrix()
    {
        if (renderingMatrix || report is null) return;
        renderingMatrix = true;
        try {
            matrixHeader.Children.Clear(); matrixRows.Children.Clear();
            Id(matrixHeader, "SummaryMatrixHeader"); Id(matrixRows, "SummaryMatrixRows");
            // The name column must be visible after a mode switch or a resize; a previous drill-down may
            // have scrolled the matrix sideways to keep its cell in view.
            matrixScroll.ScrollTo(0, 0);
            foreach (var id in cells.Keys.Where(k => k.StartsWith("SummaryCell_") || k.StartsWith("SummaryMonth_")).ToArray()) cells.Remove(id);
            var phases = report.Phases.Where(p => !p.IsTotal).ToArray(); var months = report.Months;
            if (mode == 1 && months is null) { matrixRows.Children.Add(Text("月別を表示するには期間を設定してください。")); return; }
            var count = mode == 0 ? phases.Length : months!.Periods.Count;
            var widths = new[] { NameColumn }.Concat(Enumerable.Repeat(mode == 0 ? 100d : monthWidth, count)).Concat(mode == 0 ? new[] { 90d, 90d, 100d } : []).ToArray();
            var header = Row(widths); Cell(header, TableText("担当者", "SummaryLoadNameHeader"), 0);
            for (var i = 0; i < count; i++) Cell(header, TableText(mode == 0 ? Phase(phases[i]) : months!.Periods[i].Start.ToString("yyyy/M", Japanese), mode == 0 ? $"SummaryPhaseHeader_{i}" : $"SummaryMonthHeader_{i}", numeric: true), i + 1);
            if (mode == 0) {
                Cell(header, TableText("残 合計 h", numeric: true), count + 1);
                var capacityHeader = TableText("稼働可能 h", numeric: true);
                ToolTipService.SetToolTip(capacityHeader, CapacityFormula);
                Cell(header, capacityHeader, count + 2); Cell(header, TableText("必要稼働率", numeric: true), count + 3);
            }
            matrixHeader.Children.Add(header);
            var buttons = new List<Button[]>();
            var people = report.People.Append(report.PeopleTotal).ToArray();
            for (var p = 0; p < people.Length; p++) {
                var person = people[p];
                if (mode == 1 && person == report.PeopleTotal) continue;
                var row = Row(widths);
                Cell(row, PersonCell(person), 0);
                var rowButtons = new List<Button>();
                var load = months?.People.SingleOrDefault(pl => pl.Identity == person.Identity);
                var monthCells = report.MonthCells?.SingleOrDefault(c => c.Identity == person.Identity)?.Cells;
                for (var i = 0; i < count; i++) {
                    if (mode == 0) {
                        var t = person.Phases[i].Tasks;
                        var value = MeasureText(t);
                        var description = $"{person.Name} · {Phase(phases[i])} · {Measures[measure]} {value}" + DetailSuffix(t);
                        var button = TableCell(value, $"SummaryCell_{person.Identity}_{i}", () => Select($"{person.Name} · {Phase(phases[i])}", t.Identities));
                        AutomationProperties.SetName(button, description); ToolTipService.SetToolTip(button, description);
                        if (measure == 3 && t.Overdue > 0) button.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush");
                        if (t.Finished || t.Count == 0) button.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
                        Cell(row, button, i + 1); rowButtons.Add(button);
                    } else {
                        var column = i;
                        var id = $"SummaryMonth_{person.Identity}_{column}";
                        if (monthCells is null || monthCells[column] is not { } cell) { var empty = TableCell("—", id, () => { }); Cell(row, empty, column + 1); rowButtons.Add(empty); continue; }
                        var period = months!.Periods[column];
                        var description = MonthDescription(person, period, cell, load);
                        var button = MonthCell(cell, id, description, () => SelectMonth(person, column, cell, load));
                        Cell(row, button, column + 1); rowButtons.Add(button);
                    }
                }
                if (mode == 0) {
                    Cell(row, TableText(Value(person.Tasks.Remaining), numeric: true), count + 1); Cell(row, TableText(Number(person.Capacity), numeric: true), count + 2);
                    var required = TableText(Percent(person.RequiredLoad) + (person.RequiredLoad > 1 ? " 超過" : ""), numeric: true);
                    if (person.RequiredLoad > 1) required.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush"); Cell(row, required, count + 3);
                }
                buttons.Add(rowButtons.ToArray()); matrixRows.Children.Add(row);
            }
            for (var r = 0; r < buttons.Count; r++) for (var c = 0; c < buttons[r].Length; c++) {
                var y = r; var x = c;
                buttons[r][c].KeyDown += (_, e) => {
                    var nextY = y + (e.Key == VirtualKey.Down ? 1 : e.Key == VirtualKey.Up ? -1 : 0);
                    var nextX = x + (e.Key == VirtualKey.Right ? 1 : e.Key == VirtualKey.Left ? -1 : 0);
                    if (nextY == y && nextX == x) return;
                    if (nextY >= 0 && nextY < buttons.Count && nextX >= 0 && nextX < buttons[nextY].Length) { buttons[nextY][nextX].Focus(FocusState.Keyboard); e.Handled = true; }
                };
            }
        } finally { renderingMatrix = false; }
    }
    // A person name opens 計画 filtered to that person through the summary's exact-set route. The shared
    // person filter of #260/#227 replaces this once its model is on main.
    private FrameworkElement PersonCell(SummaryPerson person)
    {
        if (person.Identity == "total") return TableText(person.Name);
        var link = Id(new HyperlinkButton { Content = Text(person.Name), FontSize = 13, Padding = new(4, 0, 4, 0), MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Left }, $"SummaryPerson_{person.Identity}");
        AutomationProperties.SetName(link, person.Name);
        if (person.Identity is PlanPeople.Unassigned or PlanPeople.Multiple) link.IsEnabled = false;
        else {
            var ids = person.Tasks.Identities.ToHashSet();
            ToolTipService.SetToolTip(link, $"計画で {person.Name} のタスクを表示");
            link.Click += (_, _) => filter(ids);
        }
        return link;
    }
    private string MonthDescription(SummaryPerson person, PlanPeriod period, SummaryMonthCell cell, PersonLoad? load)
    {
        var title = $"{person.Name} · {period.Start:yyyy/M}";
        var text = cell.Kind switch {
            SummaryMonthKind.Finished => $"{title} · 完了",
            SummaryMonthKind.NoWork => $"{title} · —",
            _ => $"{title} · 負荷率 {Percent(cell.Percent / 100)} · 上限を超える日 {cell.DaysOverCap:N0}日"
                + (cell.MaxDailyPercent is { } max ? $" · 最大日次負荷 {Percent(max / 100)}（{Date(cell.MaxDate)}）" : "")
        };
        return text + (load is { Unallocated.Count: > 0 } ? $" · 未配分 {load.Unallocated.Count:N0}" : "");
    }
    private string MeasureText(SummaryTasks tasks)
    {
        if (tasks.Count == 0 || tasks.Finished) return "—";
        return measure switch {
            0 => tasks.Remaining.Value is null ? "—" : Number(tasks.Remaining.Value) + " h",
            1 => tasks.Forecast.Value is null ? "—" : Number(tasks.Forecast.Value) + " h",
            2 => PlanSummaryPresentation.Progress(tasks.Progress.Value, tasks.Finished),
            _ => $"{tasks.Overdue:N0} 件"
        };
    }
    private string DetailSuffix(SummaryTasks tasks)
    {
        var parts = new List<string>();
        if (tasks.Finished) parts.Add("完了");
        if (measure != 3 && tasks.Overdue > 0) parts.Add($"期限超過 {tasks.Overdue:N0}");
        if (tasks.Remaining.Excluded > 0) parts.Add($"工数未入力 {tasks.Remaining.Excluded:N0} タスクを除く");
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
    }
    private void Select(string title, IReadOnlyList<string> identities, IReadOnlySet<string>? unallocated = null)
    {
        HideLoadPanel();
        detail.Children.Clear(); var ids = identities.ToHashSet(); var tasks = schedule.Values.Where(t => ids.Contains(t.Input.Identity)).ToArray();
        var remaining = tasks.Where(t => t.Remaining.HasValue).Select(t => t.Remaining!.Value).ToArray();
        var overdue = identities.Count(report.Attention.Overdue.Contains); var over = tasks.Count(t => t.IsOverEstimate); var zero = tasks.Count(t => t.IsZeroRemainingOpen);
        var caption = $"{title}　{ids.Count:N0} タスク · 残 {Number(remaining.Length == 0 ? null : remaining.Sum())}h";
        if (remaining.Length < tasks.Length) caption += $"（工数未入力 {tasks.Length - remaining.Length:N0} タスクを除く）";
        if (overdue > 0) caption += $" · 期限超過 {overdue}"; if (over > 0) caption += $" · 見積超過 {over}"; if (zero > 0) caption += $" · 残 0・未完了 {zero}";
        detail.Children.Add(Text(caption, "SummarySelection"));
        detail.Children.Add(Button($"この {ids.Count:N0} タスクで計画を絞り込む", "SummaryFilter", () => filter(ids)));
        double[] widths = [70, 260, 90, 90, 90, 110, 110, 380];
        // A bounded preview shares one horizontal origin and the page's vertical viewport.
        var table = new StackPanel { Width = widths.Sum(), HorizontalAlignment = HorizontalAlignment.Left };
        var list = Id(new StackPanel(), "SummaryTasks");
        var header = Row(widths); string[] labels = ["ID", "タスク名", "見積 h", "実績 h", "残 h", "開始日", "終了日", "状態"];
        for (var i = 0; i < labels.Length; i++) Cell(header, TableText(labels[i], "SummaryTaskHeader_" + i, numeric: i is >= 2 and <= 6), i);
        table.Children.Add(header);
        var lateness = PlanLateness.Classify(schedule.Values.ToArray(), Calendar(), day);
        var names = PlanTaskNames.Of(document!);
        var selectedRows = document.State.Rows.Where(r => ids.Contains(r.Identity)).ToArray();
        foreach (var row in selectedRows.Take(200)) {
            var task = schedule[row.Identity]; var states = States(row, task, lateness, unallocated?.Contains(row.Identity) == true);
            var item = Row(widths);
            string[] values = [names[row.Identity], row.Title, Number(task.Estimate), Number(task.Actual), Number(task.Remaining), Date(task.Start.Value), Date(task.End.Value), string.Join("、", states)];
            for (var i = 0; i < values.Length; i++) Cell(item, TableText(values[i], i == 7 ? "SummaryTask_" + row.Identity : i == 0 ? "SummaryTaskId_" + row.Identity : $"SummaryTaskValue_{row.Identity}_{i}", i is >= 2 and <= 6), i);
            AutomationProperties.SetName(item, string.Join(" · ", values)); list.Children.Add(item);
        }
        table.Children.Add(list); detail.Children.Add(Wide(table));
        if (selectedRows.Length > 200) detail.Children.Add(Text($"ほか {selectedRows.Length - 200:N0} タスク。計画で絞り込むとすべて表示します", "SummaryTasksMore"));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (!detail.IsLoaded) return;
            // The new details may not be arranged yet; revealing the old, shorter panel
            // clamps the scroll above the command that has just been added.
            detail.UpdateLayout();
            detail.StartBringIntoView(new() { AnimationDesired = false, VerticalAlignmentRatio = 0,
                TargetRect = new Windows.Foundation.Rect(0, 0, detail.ActualWidth, 80) });
        });
    }
    private PlanCalendar Calendar() => new() {
        CompanyDaysOff = document!.State.Settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = document.State.Settings.ImportedHolidays?.ToPreset() };
    private static List<string> States(PlanRow row, ScheduledTask task, PlanLatenessResult lateness, bool unallocated)
    {
        var states = new List<string>(); var late = lateness.Tasks[row.Identity];
        if (late.MissedDate is { } missed) {
            var remainingText = task.Remaining is { } work ? $"（見込み残存工数 {work.ToString("0.############################", CultureInfo.InvariantCulture)}h）" : "";
            states.Add(late.OverdueKind == PlanOverdueKind.Finish
                ? $"期限超過: 終了日 {missed.ToString("M/d", CultureInfo.InvariantCulture)} を過ぎて未完了{remainingText}"
                : $"期限超過: 開始日 {missed.ToString("M/d", CultureInfo.InvariantCulture)} を過ぎて未着手");
        }
        // 予定より遅れ is the not-overdue set (#236); an overdue task is named once, as on the sheet.
        if (late.DaysLater is { } days && late.Level == PlanLatenessLevel.Later && task.End.Value is { } planned)
            states.Add($"予定より遅れ: {planned.ToString("M/d", CultureInfo.InvariantCulture)} から +{days}日"
                + (task.ForecastEnd is { } forecast ? $"（見込終了 {forecast.ToString("M/d", CultureInfo.InvariantCulture)}）" : ""));
        if (task.IsOverEstimate) states.Add(task.Forecast is null ? "見積超過: 見込工数・差異が数値の範囲外です" : $"見積超過: 見込工数 {task.Forecast:0.############################}h（見積工数 {task.Estimate:0.############################}h、差異 {task.Variance:+0.############################;-0.############################;0}h）");
        if (task.IsZeroRemainingOpen) states.Add("残 0・未完了: 見込み残存工数が 0 ですが Issue は閉じていません");
        if (task.Input.Closed) states.Add("完了");
        if (unallocated) states.Add("未配分: " + (task.Remaining is null ? "見込み残存工数が未入力" : task.StartReason == "入力エラー" || task.Remaining < 0 ? "入力エラー" : "日程が未入力"));
        return states;
    }
    // #230 After 5 / D3: the month drill-down is an in-page side panel beside the matrix, not a popup
    // and not the old bottom-of-page detail, so the clicked cell stays where the PMO clicked it.
    private void SelectMonth(SummaryPerson person, int index, SummaryMonthCell cell, PersonLoad? load)
    {
        detail.Children.Clear();
        var lateness = PlanLateness.Classify(schedule.Values.ToArray(), Calendar(), day);
        var names = PlanTaskNames.Of(document!);
        var unallocated = load?.Unallocated.ToHashSet(StringComparer.Ordinal) ?? [];
        var ids = new List<string>();
        foreach (var identity in cell.Tasks) if (!ids.Contains(identity)) ids.Add(identity);
        if (load is not null) foreach (var identity in load.Unallocated) if (!ids.Contains(identity)) ids.Add(identity);
        panelIdentities = ids.ToHashSet();
        var period = report.Months!.Periods[index];
        loadTitle.Text = $"{person.Name} · {period.Start:yyyy/M}";
        loadPercent.Text = $"負荷 {Percent(cell.Percent / 100)}";
        loadOverDays.Text = $"上限を超える日 {cell.DaysOverCap:N0}日";
        loadOverDays.Foreground = cell.Overloaded ? PlanSheetView.Brush("SystemFillColorCriticalBrush") : PlanSheetView.Brush("TextFillColorPrimaryBrush");
        loadMax.Text = cell.MaxDailyPercent is { } max ? $"最大 {Percent(max / 100)}（{Date(cell.MaxDate)}）" : "最大日次負荷 —";
        AutomationProperties.SetName(loadPanel, $"{person.Name} · {period.Start:yyyy/M} の負荷");
        BuildDayBars(load, index);
        loadTasks.Children.Clear();
        var rows = document!.State.Rows.Where(r => panelIdentities.Contains(r.Identity)).ToArray();
        var shown = 0;
        foreach (var row in rows) {
            if (shown++ >= 200) break;
            var task = schedule[row.Identity]; var state = string.Join("、", States(row, task, lateness, unallocated.Contains(row.Identity)));
            var entry = new StackPanel { Spacing = 0 };
            entry.Children.Add(Id(TableText(names[row.Identity] + " " + row.Title), "SummaryLoadTask_" + row.Identity));
            var detailText = $"{Date(task.Start.Value)} – {Date(task.End.Value)} · 残 {Number(task.Remaining)}h" + (state.Length > 0 ? " · " + state : "");
            var line = Id(TableText(detailText), "SummaryLoadTaskState_" + row.Identity);
            line.FontSize = 12; line.Foreground = PlanSheetView.Brush(state.Length > 0 ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
            entry.Children.Add(line); loadTasks.Children.Add(entry);
        }
        loadTasksMore.Text = rows.Length > 200 ? $"ほか {rows.Length - 200:N0} タスク。計画で表示するとすべて表示します" : "";
        loadTasksMore.Visibility = rows.Length > 200 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(loadPlanOpen, $"この {ids.Count:N0} タスクを計画で表示");
        loadPlanOpen.IsEnabled = ids.Count > 0;
        loadPanel.Width = Math.Min(344, Math.Max(240, rightColumn.ActualWidth * 0.45));
        loadPanel.Visibility = Visibility.Visible;
        // The matrix shares its width with the panel now; keep the clicked cell wholly inside both
        // viewports instead of letting it sit half-clipped at the new edge.
        if (selectedCell is not null && cells.TryGetValue(selectedCell, out var button))
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
                button.UpdateLayout();
                var point = button.TransformToVisual(matrixRows).TransformPoint(new());
                var horizontal = Math.Clamp(point.X - (matrixScroll.ActualWidth - button.ActualWidth) / 2, 0, Math.Max(0, matrixScroll.ScrollableWidth));
                matrixScroll.ScrollTo(horizontal, matrixScroll.VerticalOffset);
                var vertical = Math.Clamp(point.Y - (matrixBody.ActualHeight - button.ActualHeight) / 2, 0, Math.Max(0, matrixBody.ScrollableHeight));
                matrixBody.ChangeView(null, vertical, null, true);
            });
    }
    private void HideLoadPanel()
    {
        loadPanel.Visibility = Visibility.Collapsed;
        loadPlanOpen.IsEnabled = false;
        loadTasks.Children.Clear();
        panelIdentities = new HashSet<string>();
        barStart = barEnd = default; barLoads = []; loadDays.Children.Clear();
    }
    private void BuildDayBars(PersonLoad? load, int index)
    {
        var period = report.Months!.Periods[index];
        barStart = period.Start; barEnd = period.End; barLoads = [];
        if (load is not null) foreach (var day in load.Periods[index].Days) barLoads[day.Date] = day.Percent;
        DrawDayBars();
        loadDaysCaption.Text = $"{period.Start:M/d} – {period.End:M/d} の日ごとの負荷（赤は上限超え）";
    }
    private void DrawDayBars()
    {
        loadDays.Children.Clear();
        var width = loadDays.ActualWidth;
        if (width <= 0 || barStart == default || barEnd == default) return;
        const double baseline = 78, capLine = 54;
        var span = barEnd.DayNumber - barStart.DayNumber + 1;
        var slot = width / span;
        var cap = new Border { Width = width, Height = 1, Background = PlanSheetView.Brush("TextFillColorSecondaryBrush") };
        Canvas.SetLeft(cap, 0); Canvas.SetTop(cap, capLine); AutomationProperties.SetAutomationId(cap, "SummaryLoadCapLine");
        AutomationProperties.SetName(cap, "上限 100%");
        loadDays.Children.Add(cap);
        var capLabel = Text("100%", ""); capLabel.FontSize = 10; capLabel.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        Canvas.SetLeft(capLabel, Math.Max(0, width - 36)); Canvas.SetTop(capLabel, capLine - 16); loadDays.Children.Add(capLabel);
        for (var i = 0; i < span; i++) {
            var date = barStart.AddDays(i);
            if (!barLoads.TryGetValue(date, out var percent) || percent is not > 0) continue;
            var height = Math.Min(baseline - 4, (double)percent.Value * 0.24);
            var bar = new Border { Width = Math.Max(1, slot - 2), Height = height,
                Background = PlanSheetView.Brush(percent > 100 ? "SystemFillColorCriticalBrush" : "GanttTaskBrush") };
            Canvas.SetLeft(bar, i * slot + 1); Canvas.SetTop(bar, baseline - height); loadDays.Children.Add(bar);
            var label = $"{date:M/d} {Percent(percent / 100)}（上限 100%）";
            AutomationProperties.SetAutomationId(bar, "SummaryLoadDay_" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            AutomationProperties.SetName(bar, label); ToolTipService.SetToolTip(bar, label);
        }
        var first = Text($"{barStart:M/d}", ""); first.FontSize = 10; first.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        Canvas.SetLeft(first, 0); Canvas.SetTop(first, baseline + 2); loadDays.Children.Add(first);
        var last = Text($"{barEnd:M/d}", ""); last.FontSize = 10; last.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        Canvas.SetLeft(last, Math.Max(0, width - 30)); Canvas.SetTop(last, baseline + 2); loadDays.Children.Add(last);
    }
}





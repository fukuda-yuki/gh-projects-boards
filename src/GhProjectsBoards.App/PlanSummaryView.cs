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
    private readonly StackPanel matrix = new() { Spacing = 4 };
    private readonly ScrollView matrixScroll;
    private readonly ScrollViewer scroll;
    private readonly Dictionary<string, Button> cells = [];
    private string? selectedCell;
    private readonly StackPanel detail = new() { Spacing = 6 };
    private PlanDocument? document;
    private DateOnly day;
    private SummaryPlan report = null!;
    private IReadOnlyDictionary<string, ScheduledTask> schedule = null!;
    private int generation, measure, mode;
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly string[] Measures = ["見込み残存工数 h", "見込工数 h", "進捗", "期限超過"];
    internal PlanSummaryView(PlanSession session, Action<IReadOnlySet<string>> filter, Action settings)
    {
        Session = session; this.filter = filter; this.settings = settings; matrixScroll = Wide(matrix);
        AutomationProperties.SetAutomationId(this, "PlanSummary");
        scroll = Id(new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(12) }, "SummaryScroll");
        Content = scroll;
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
    private static string Days(int? value) => value is null ? "—" : Signed(value) + "日";
    private static string Percent(decimal? value) => PlanSummaryPresentation.Percent(value);
    private static string Progress(SummaryValue value) => Percent(value.Value) + (value.Excluded > 0 ? $"（工数未入力 {value.Excluded:N0} タスクを除く）" : "");
    private static string Value(SummaryValue value) => Number(value.Value) + (value.Excluded > 0 ? $"（工数未入力 {value.Excluded:N0} タスクを除く）" : "");
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
    private static void Highlight(Button button, bool selected)
    {
        if (selected) {
            button.Background = PlanSheetView.Brush("SheetSelectionBrush"); button.BorderBrush = PlanSheetView.Brush("SheetSelectionStrokeBrush");
        } else { button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.BorderBrushProperty); }
        AutomationProperties.SetItemStatus(button, selected ? "選択中" : "");
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
    private static void Heading(Panel panel, string title) => panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new(0, 2, 0, 4) });
    private void Render()
    {
        body.Children.Clear(); detail.Children.Clear(); cells.Clear(); selectedCell = null;
        var s = document!.State.Settings;
        body.Children.Add(Text($"基準日 {day.ToString("yyyy/MM/dd (ddd)", Japanese)} 時点の状況　　プロジェクト期間 {Date(s.ProjectStart)} – {Date(s.ProjectEnd)}", "SummaryStatusDate"));
        if (s.ProjectEnd is null) {
            body.Children.Add(Text("終了日が未設定です", "SummaryNoEnd"));
            body.Children.Add(Button("設定を開く", "SummaryPeriodSettings", settings));
        }
        var band = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        for (var i = 0; i < 5; i++) band.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        void Band(int index, string title, string value, string id, bool warning = false, string? caption = null) {
            var panel = new StackPanel { Spacing = 4 }; panel.Children.Add(Text(title)); var text = Text(value, id);
            text.FontSize = 14; if (warning) text.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush"); panel.Children.Add(text);
            if (caption is not null) { var note = Text(caption, id + "Caption"); note.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush"); panel.Children.Add(note); }
            var border = new Border { Child = panel, Padding = new(8), BorderThickness = new(1), BorderBrush = PlanSheetView.Brush("WorkspaceCardStrokeBrush"), CornerRadius = new(4) };
            Cell(band, border, index);
        }
        Band(0, "残り稼働日", $"{Number(report.RemainingDays)} 日\n全 {Number(report.WorkingDays)} 稼働日のうち {Number(report.ElapsedDays)} 日が経過", "SummaryRemainingDays");
        var difference = report.FinishDifference is null ? "—" : report.FinishDifference > 0 ? $"終了日より {report.FinishDifference} 稼働日遅れ" : report.FinishDifference < 0 ? $"終了日より {-report.FinishDifference} 稼働日前" : "終了日どおり";
        Band(1, "完了見込", $"{Date(report.ForecastFinish)}\n{difference}", "SummaryFinish", report.FinishDifference > 0, "先行関係と見込み残存工数から計算（担当者の負荷は含まない）");
        Band(2, "終了日までに必要な稼働", $"{Percent(report.RequiredLoad)}{(report.RequiredLoad > 1 ? " 超過" : "")}\n残 {Value(report.Total.Remaining)}h ÷ 稼働可能 {Number(report.Capacity)}h", "SummaryRequiredLoad", report.RequiredLoad > 1);
        Band(3, "進捗", $"{Percent(report.Total.Progress.Value)}\n実績 {Number(report.Total.ProgressActual)}h ÷ 見込 {Value(report.Total.Forecast)}h\n完了 {report.Total.Closed:N0} / {report.Total.Count:N0} タスク\n見積 {Number(report.Total.VarianceEstimate)}h より {Signed(report.Total.Variance.Value)}h" + (report.Total.Variance.Excluded > 0 ? $"（工数未入力 {report.Total.Variance.Excluded:N0} タスクを除く）" : ""), "SummaryProgress");
        var attention = new StackPanel { Spacing = 3 }; attention.Children.Add(Text("要注意"));
        void Attention(string label, string id, IReadOnlyList<string> tasks, string glyph) {
            if (tasks.Count == 0) return;
            var button = Button($"{label} {tasks.Count:N0}", id, () => filter(tasks.ToHashSet()));
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 });
            content.Children.Add(TableText($"{label} {tasks.Count:N0}")); button.Content = content; attention.Children.Add(button);
        }
        Attention("期限超過", "SummaryOverdue", report.Attention.Overdue, "\uE814");
        Attention("遅れの原因", "SummaryCause", report.Attention.Cause, "\uE7BA");
        Attention("予定より遅れ", "SummaryLater", report.Attention.Later, "\uE7BA");
        Attention("見積超過", "SummaryOverEstimate", report.Attention.OverEstimate, "\uE7BA");
        Attention("残 0・未完了", "SummaryZeroRemaining", report.Attention.ZeroRemainingOpen, "\uE7BA");
        Attention($"担当者なし 残 {Value(report.Attention.UnassignedRemaining)}h", "SummaryUnassigned", report.Attention.Unassigned, "\uE77B");
        Attention("工数未入力", "SummaryUnknown", report.Attention.UnknownEffort, "\uE7BA");
        Cell(band, attention, 4); body.Children.Add(band);
        var mapped = s.Columns.Any(c => c.Role == PlanField.Phase && c.FieldId is not null);
        if (!mapped) {
            body.Children.Add(Text("工程のフィールドが未設定です。プロジェクトの単一選択フィールドを 工程 に割り当てると、サマリーで工程別に集計します。", "SummaryUnmapped"));
            body.Children.Add(Button("設定を開く", "SummaryPhaseSettings", settings));
        } else {
            var phases = new Grid { ColumnSpacing = 16 };
            phases.ColumnDefinitions.Add(new()); phases.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var table = Id(new StackPanel(), "SummaryPhaseTable"); Heading(table, "工程別");
            var rows = new StackPanel();
            double[] widths = [88, 112, 72, 72, 72, 72, 72, 64, 108, 108, 64, 76, 76];
            phases.SizeChanged += (_, args) => phases.ColumnDefinitions[0].Width = new(Math.Min(widths.Sum(), Math.Max(0, args.NewSize.Width - 276)));
            var header = Row(widths); header.Height = 54; var names = new[] { "工程", "完了 / タスク", "見積 h", "実績 h", "残 h", "見込 h", "差異 h", "進捗", "目標日", "完了見込", "差", "期限超過", "見積超過" };
            for (var i = 0; i < names.Length; i++) Cell(header, TableText(names[i], numeric: i > 0), i); rows.Children.Add(header);
            for (var n = 0; n < report.Phases.Count; n++) {
                var phase = report.Phases[n]; var t = phase.Tasks; var row = Id(Row(widths), $"SummaryPhaseRow_{n}"); row.MinHeight = 29;
                Cell(row, TableCell(Phase(phase), phase.IsTotal ? "SummaryPhaseTotal" : $"SummaryPhase_{n}", () => Select(Phase(phase), t.Identities), left: true), 0);
                string[] values = [$"{t.Closed:N0} / {t.Count:N0}", Value(t.Estimate), Value(t.Actual), Value(t.Remaining), Value(t.Forecast), Signed(t.Variance.Value) + (t.Variance.Excluded > 0 ? $"（工数未入力 {t.Variance.Excluded:N0} タスクを除く）" : ""), Progress(t.Progress), Date(phase.Target), Date(t.Finish), Days(phase.Difference), $"{t.Overdue:N0}", $"{t.OverEstimate:N0}"];
                for (var i = 0; i < values.Length; i++) { var text = TableText(values[i], numeric: true); ToolTipService.SetToolTip(text, names[i + 1] + " " + values[i]); AutomationProperties.SetName(text, Phase(phase) + " " + names[i + 1] + " " + values[i]); Cell(row, text, i + 1); }
                AutomationProperties.SetName(row, Phase(phase) + " " + string.Join(" · ", values.Select((value, i) => names[i + 1] + " " + value)));
                rows.Children.Add(row);
            }
            table.Children.Add(Wide(rows)); Cell(phases, table, 0); Cell(phases, Timeline(), 1); body.Children.Add(phases);
        }
        Heading(body, "担当者 × 工程");
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var toggle = Id(new ComboBox { ItemsSource = new[] { "工程別", "月別" }, SelectedIndex = mode, Width = 110 }, "SummaryMode");
        var measures = Id(new ComboBox { ItemsSource = Measures, SelectedIndex = measure, Width = 150, IsEnabled = mode == 0 }, "SummaryMeasure");
        AutomationProperties.SetName(toggle, "集計の表示期間"); AutomationProperties.SetName(measures, "工程の集計指標");
        toggle.SelectionChanged += (_, _) => { mode = toggle.SelectedIndex; measures.IsEnabled = mode == 0;
            if (selectedCell is not null && cells.TryGetValue(selectedCell, out var previous)) Highlight(previous, false);
            selectedCell = null; detail.Children.Clear(); RenderMatrix(); };
        measures.SelectionChanged += (_, _) => { measure = measures.SelectedIndex; RenderMatrix(); };
        controls.Children.Add(toggle); controls.Children.Add(measures); body.Children.Add(controls);
        body.Children.Add(Text("稼働可能 = 稼働率 × 残り稼働日 × 8h（上限工数がある場合は 上限工数 − 実績 まで）"));
        body.Children.Add(matrixScroll); RenderMatrix();
        if (report.Milestones.Count > 0) {
            var table = Id(new StackPanel(), "SummaryMilestoneTable"); Heading(table, "マイルストーン");
            double[] widths = [200, 108, 108, 72, 112, 300];
            var header = Row(widths); header.Height = 32;
            string[] names = ["名前", "期日", "完了見込", "差", "完了 / タスク", "状態"];
            for (var i = 0; i < names.Length; i++) {
                var text = TableText(names[i], numeric: i is > 0 and < 5);
                if (i == 5) text.Margin = new(16, 2, 4, 2);
                Cell(header, text, i);
            }
            table.Children.Add(header);
            for (var n = 0; n < report.Milestones.Count; n++) {
                var milestone = report.Milestones[n]; var m = milestone.Milestone;
                var row = Id(Row(widths), "SummaryMilestoneRow_" + n); row.MinHeight = 29;
                var title = TableCell(m.Title, "SummaryMilestone_" + n, () => Select(m.Title + " · 期日を過ぎるタスク", milestone.LateTasks), left: true);
                var description = m.Title + (m.ConflictingDates ? " · 期日がリポジトリで異なります" : "");
                ToolTipService.SetToolTip(title, description); AutomationProperties.SetName(title, description);
                Cell(row, title, 0);
                string[] values = [Date(m.DueDate), Date(milestone.Forecast), Days(milestone.Difference), $"{milestone.Closed:N0} / {milestone.Tasks.Count:N0}", string.Join(" · ", new[] { m.Closed ? "クローズ" : "", m.ConflictingDates ? "期日がリポジトリで異なります" : "" }.Where(value => value.Length > 0))];
                for (var i = 0; i < values.Length; i++) {
                    var text = TableText(values[i], i == 4 ? "SummaryMilestoneState_" + n : i == 3 ? "SummaryMilestoneCount_" + n : "", numeric: i < 4);
                    if (i == 4) text.Margin = new(16, 2, 4, 2);
                    Cell(row, text, i + 1);
                }
                table.Children.Add(row);
            }
            body.Children.Add(Wide(table));
        }
        body.Children.Add(detail);
    }
    private FrameworkElement Timeline()
    {
        const double axisHeight = 54, rowHeight = 29;
        var panel = Id(new StackPanel(), "SummaryTimeline"); Heading(panel, "工程の日程");
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
        matrix.Children.Clear();
        foreach (var id in cells.Keys.Where(k => k.StartsWith("SummaryCell_") || k.StartsWith("SummaryMonth_")).ToArray()) cells.Remove(id);
        var phases = report.Phases.Where(p => !p.IsTotal).ToArray(); var months = report.Months;
        if (mode == 1 && months is null) { matrix.Children.Add(Text("月別を表示するには期間を設定してください。")); return; }
        var count = mode == 0 ? phases.Length : months!.Periods.Count;
        var widths = new[] { 150d }.Concat(Enumerable.Repeat(mode == 0 ? 100d : 270d, count)).Concat(mode == 0 ? new[] { 90d, 90d, 100d } : []).ToArray();
        var header = Row(widths); Cell(header, TableText("担当者"), 0);
        for (var i = 0; i < count; i++) Cell(header, TableText(mode == 0 ? Phase(phases[i]) : months!.Periods[i].Start.ToString("yyyy/M"), numeric: true), i + 1);
        if (mode == 0) { Cell(header, TableText("残 合計 h", numeric: true), count + 1); Cell(header, TableText("稼働可能 h", numeric: true), count + 2); Cell(header, TableText("必要稼働率", numeric: true), count + 3); }
        matrix.Children.Add(header);
        var buttons = new List<Button[]>();
        foreach (var person in report.People.Append(report.PeopleTotal)) {
            if (mode == 1 && person == report.PeopleTotal) continue;
            var row = Row(widths); Cell(row, TableText(person.Name), 0); var rowButtons = new List<Button>();
            var load = months?.People.SingleOrDefault(p => p.Identity == person.Identity);
            for (var i = 0; i < count; i++) {
                string value, label; IReadOnlyList<string> tasks; IReadOnlySet<string>? unallocated = null; bool warning;
                if (mode == 0) {
                    var t = person.Phases[i].Tasks;
                    value = t.Count == 0 ? "–" : measure switch { 0 => Value(t.Remaining), 1 => Value(t.Forecast), 2 => Progress(t.Progress), _ => $"{t.Overdue:N0}" };
                    warning = t.Overdue > 0; if (warning) value += " ⚠ 期限超過";
                    label = $"{person.Name} · {Phase(phases[i])} · {Measures[measure]} {value}"; tasks = t.Identities;
                } else {
                    var cell = load?.Periods[i];
                    value = Percent(cell?.Percent / 100); warning = cell?.DailyOverloads.Count > 0 || cell?.Overloaded == true;
                    value += $"{(warning ? " 超過" : "")} / 日超過 {cell?.DailyOverloads.Count ?? 0}日・最大 {Percent(cell?.MaxDailyPercent / 100)}";
                    if (cell?.Tasks.Count is null or 0) value = "–";
                    if (load?.Unallocated.Count > 0) value += $" 未配分 {load.Unallocated.Count}";
                    label = $"{person.Name} · {months!.Periods[i].Start:yyyy/M} · 負荷 {value}";
                    unallocated = load?.Unallocated.ToHashSet(); tasks = (cell?.Tasks ?? []).Concat(load?.Unallocated ?? []).Distinct().ToArray();
                }
                var title = $"{person.Name} · {(mode == 0 ? Phase(phases[i]) : months!.Periods[i].Start.ToString("yyyy/M"))}"; var ids = tasks; var unknown = unallocated;
                var button = TableCell(value, mode == 0 ? $"SummaryCell_{person.Identity}_{i}" : $"SummaryMonth_{person.Identity}_{i}", () => Select(title, ids, unknown));
                AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
                if (warning) button.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush");
                Cell(row, button, i + 1); rowButtons.Add(button);
            }
            if (mode == 0) {
                Cell(row, TableText(Value(person.Tasks.Remaining), numeric: true), count + 1); Cell(row, TableText(Number(person.Capacity), numeric: true), count + 2);
                var required = TableText(Percent(person.RequiredLoad) + (person.RequiredLoad > 1 ? " 超過" : ""), numeric: true);
                if (person.RequiredLoad > 1) required.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush"); Cell(row, required, count + 3);
            }
            buttons.Add(rowButtons.ToArray()); matrix.Children.Add(row);
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
    }
    private void Select(string title, IReadOnlyList<string> identities, IReadOnlySet<string>? unallocated = null)
    {
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
        var calendar = new PlanCalendar { CompanyDaysOff = document!.State.Settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = document.State.Settings.ImportedHolidays?.ToPreset() };
        var lateness = PlanLateness.Classify(schedule.Values.ToArray(), calendar, day);
        var rowIds = document!.State.Rows.Select((row, index) => (row.Identity, Id: index + 1)).ToDictionary(r => r.Identity, r => r.Id);
        var selectedRows = document.State.Rows.Where(r => ids.Contains(r.Identity)).ToArray();
        foreach (var row in selectedRows.Take(200)) {
            var task = schedule[row.Identity]; var states = new List<string>();
            var late = lateness.Tasks[row.Identity];
            if (late.MissedDate is { } missed) {
                var remainingText = task.Remaining is { } work ? $"（見込み残存工数 {work.ToString("0.############################", CultureInfo.InvariantCulture)}h）" : "";
                states.Add(late.OverdueKind == PlanOverdueKind.Finish
                    ? $"期限超過: 完了予定 {missed.ToString("M/d", CultureInfo.InvariantCulture)} を過ぎて未完了{remainingText}"
                    : $"期限超過: 開始予定 {missed.ToString("M/d", CultureInfo.InvariantCulture)} を過ぎて未着手");
            }
            if (late.DaysLater is { } days && task.End.Value is { } planned)
                states.Add($"予定より遅れ: {planned.ToString("M/d", CultureInfo.InvariantCulture)} から +{days} 日"
                    + (task.ForecastEnd is { } forecast ? $"（見込終了 {forecast.ToString("M/d", CultureInfo.InvariantCulture)}）" : ""));
            if (task.IsOverEstimate) states.Add(task.Forecast is null ? "見積超過: 見込工数・差異が数値の範囲外です" : $"見積超過: 見込工数 {task.Forecast:0.############################}h（見積工数 {task.Estimate:0.############################}h、差異 {task.Variance:+0.############################;-0.############################;0}h）");
            if (task.IsZeroRemainingOpen) states.Add("残 0・未完了: 見込み残存工数が 0 ですが Issue は閉じていません");
            if (task.Input.Closed) states.Add("完了");
            if (unallocated?.Contains(row.Identity) == true) states.Add("未配分: " + (task.Remaining is null ? "見込み残存工数が未入力" : task.StartReason == "入力エラー" || task.Remaining < 0 ? "入力エラー" : task.Start.Value is null || task.End.Value is null ? "日程が未入力" : "期間内に稼働日なし"));
            var item = Row(widths);
            string[] values = [rowIds[row.Identity].ToString(Japanese), row.Title, Number(task.Estimate), Number(task.Actual), Number(task.Remaining), Date(task.Start.Value), Date(task.End.Value), string.Join("、", states)];
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
}


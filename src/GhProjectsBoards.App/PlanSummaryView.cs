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
    private readonly ScrollViewer matrixScroll;
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
        Content = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(12) };
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
    private static string Percent(decimal? value) => value is null ? "—" : Number(value * 100) + "%";
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
    private static Button Button(string value, string id, Action action)
    {
        var button = Id(new Button { Content = Text(value, numeric: true), FontSize = 13, Padding = new(6, 2, 6, 2), MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Right }, id);
        Typography.SetNumeralAlignment(button, FontNumeralAlignment.Tabular);
        AutomationProperties.SetName(button, value); button.Click += (_, _) => action(); return button;
    }
    private static ScrollViewer Wide(UIElement content) => new() { Content = content, HorizontalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static Grid Row(params double[] widths)
    {
        var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = new(width) }); return grid;
    }
    private static void Cell(Grid row, FrameworkElement cell, int column) { Grid.SetColumn(cell, column); row.Children.Add(cell); }
    private static void Heading(Panel panel, string title) => panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new(0, 2, 0, 4) });
    private void Render()
    {
        body.Children.Clear(); detail.Children.Clear();
        var s = document!.State.Settings;
        body.Children.Add(Text($"基準日 {day.ToString("yyyy/MM/dd (ddd)", Japanese)} 時点の状況　　プロジェクト期間 {Date(s.ProjectStart)} – {Date(s.ProjectEnd)}", "SummaryStatusDate"));
        if (s.ProjectEnd is null) {
            body.Children.Add(Text("終了日が未設定です", "SummaryNoEnd"));
            body.Children.Add(Button("設定を開く", "SummaryPeriodSettings", settings));
        }
        var band = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        for (var i = 0; i < 5; i++) band.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        void Band(int index, string title, string value, string id, bool warning = false) {
            var panel = new StackPanel { Spacing = 4 }; panel.Children.Add(Text(title)); var text = Text(value, id);
            text.FontSize = 14; if (warning) text.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush"); panel.Children.Add(text);
            var border = new Border { Child = panel, Padding = new(8), BorderThickness = new(1), BorderBrush = PlanSheetView.Brush("WorkspaceCardStrokeBrush"), CornerRadius = new(4) };
            Cell(band, border, index);
        }
        Band(0, "残り稼働日", $"{Number(report.RemainingDays)} 日\n全 {Number(report.WorkingDays)} 稼働日のうち {Number(report.ElapsedDays)} 日が経過", "SummaryRemainingDays");
        var difference = report.FinishDifference is null ? "—" : report.FinishDifference > 0 ? $"終了日より {report.FinishDifference} 稼働日遅れ" : report.FinishDifference < 0 ? $"終了日より {-report.FinishDifference} 稼働日前" : "終了日どおり";
        Band(1, "完了見込", $"{Date(report.ForecastFinish)}\n{difference}\n先行関係と見込み残存工数から計算（担当者の負荷は含まない）", "SummaryFinish", report.FinishDifference > 0);
        Band(2, "終了日までに必要な稼働", $"{Percent(report.RequiredLoad)}{(report.RequiredLoad > 1 ? " 超過" : "")}\n残 {Value(report.Total.Remaining)}h ÷ 稼働可能 {Number(report.Capacity)}h", "SummaryRequiredLoad", report.RequiredLoad > 1);
        Band(3, "進捗", $"{Percent(report.Total.Progress.Value)}\n実績 {Number(report.Total.ProgressActual)}h ÷ 見込 {Value(report.Total.Forecast)}h\n完了 {report.Total.Closed:N0} / {report.Total.Count:N0} タスク\n見積 {Number(report.Total.VarianceEstimate)}h より {Signed(report.Total.Variance.Value)}h" + (report.Total.Variance.Excluded > 0 ? $"（工数未入力 {report.Total.Variance.Excluded:N0} タスクを除く）" : ""), "SummaryProgress");
        var attention = new StackPanel { Spacing = 3 }; attention.Children.Add(Text("要注意"));
        void Attention(string label, string id, IReadOnlyList<string> tasks) { if (tasks.Count > 0) attention.Children.Add(Button($"{label} {tasks.Count:N0}", id, () => filter(tasks.ToHashSet()))); }
        Attention("期限超過", "SummaryOverdue", report.Attention.Overdue);
        Attention("見積超過", "SummaryOverEstimate", report.Attention.OverEstimate);
        Attention("残 0・未完了", "SummaryZeroRemaining", report.Attention.ZeroRemainingOpen);
        Attention($"担当者なし 残 {Value(report.Attention.UnassignedRemaining)}h", "SummaryUnassigned", report.Attention.Unassigned);
        Attention("工数未入力", "SummaryUnknown", report.Attention.UnknownEffort);
        Cell(band, attention, 4); body.Children.Add(band);
        var mapped = s.Columns.Any(c => c.Role == PlanField.Phase && c.FieldId is not null);
        if (!mapped) {
            body.Children.Add(Text("工程のフィールドが未設定です。プロジェクトの単一選択フィールドを 工程 に割り当てると、サマリーで工程別に集計します。", "SummaryUnmapped"));
            body.Children.Add(Button("設定を開く", "SummaryPhaseSettings", settings));
        } else {
            var phases = new Grid { ColumnSpacing = 16 };
            phases.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) }); phases.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var table = new StackPanel(); Heading(table, "工程別");
            var rows = new StackPanel();
            double[] widths = [90, 65, 68, 68, 68, 68, 68, 65, 110, 70, 70];
            var header = Row(widths); header.Height = 40; var names = new[] { "工程", "完了 / タスク", "見積 h", "実績 h", "残 h", "見込 h", "差異 h", "進捗", "完了見込", "期限超過", "見積超過" };
            for (var i = 0; i < names.Length; i++) Cell(header, Text(names[i], numeric: i > 0), i); rows.Children.Add(header);
            for (var n = 0; n < report.Phases.Count; n++) {
                var phase = report.Phases[n]; var t = phase.Tasks; var row = Id(Row(widths), $"SummaryPhaseRow_{n}"); row.MinHeight = 29;
                Cell(row, Button(Phase(phase), phase.IsTotal ? "SummaryPhaseTotal" : $"SummaryPhase_{n}", () => Select(Phase(phase), t.Identities)), 0);
                string[] values = [$"{t.Closed:N0} / {t.Count:N0}", Value(t.Estimate), Value(t.Actual), Value(t.Remaining), Value(t.Forecast), Signed(t.Variance.Value) + (t.Variance.Excluded > 0 ? $"（工数未入力 {t.Variance.Excluded:N0} タスクを除く）" : ""), Progress(t.Progress), Date(t.Finish), $"{t.Overdue:N0}", $"{t.OverEstimate:N0}"];
                for (var i = 0; i < values.Length; i++) { var text = Text(values[i], numeric: true); ToolTipService.SetToolTip(text, names[i + 1] + " " + values[i]); AutomationProperties.SetName(text, Phase(phase) + " " + names[i + 1] + " " + values[i]); Cell(row, text, i + 1); }
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
        toggle.SelectionChanged += (_, _) => { mode = toggle.SelectedIndex; measures.IsEnabled = mode == 0; RenderMatrix(); };
        measures.SelectionChanged += (_, _) => { measure = measures.SelectedIndex; RenderMatrix(); };
        controls.Children.Add(toggle); controls.Children.Add(measures); body.Children.Add(controls);
        body.Children.Add(Text("稼働可能 = 稼働率 × 残り稼働日 × 8h（上限工数がある場合は 上限工数 − 実績 まで）"));
        body.Children.Add(matrixScroll); RenderMatrix(); body.Children.Add(detail);
    }
    private FrameworkElement Timeline()
    {
        var panel = new StackPanel(); Heading(panel, "工程の日程");
        var start = document!.State.Settings.ProjectStart; var end = document.State.Settings.ProjectEnd;
        if (start is null || end is null) { panel.Children.Add(Text("期間が未設定です")); return panel; }
        var until = report.ForecastFinish > end ? report.ForecastFinish.Value : end.Value;
        var axis = Text($"{Date(start)} – {Date(until)}\n基準日 {Date(day)} / 終了日 {Date(end)}"); axis.Height = 40; panel.Children.Add(axis);
        foreach (var phase in report.Phases.Where(p => !p.IsTotal)) {
            var row = new Grid { Height = 29 }; row.ColumnDefinitions.Add(new() { Width = new(55) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Cell(row, Text(Phase(phase)), 0);
            var chart = new Canvas { Height = 22, HorizontalAlignment = HorizontalAlignment.Stretch };
            void Draw() {
                chart.Children.Clear(); double X(DateOnly date) => Math.Clamp((date.DayNumber - start.Value.DayNumber) / (double)Math.Max(1, until.DayNumber - start.Value.DayNumber + 1), 0, 1) * chart.ActualWidth;
                if (phase.Tasks.Start is { } a && phase.Tasks.Finish is { } b) {
                    var width = Math.Max(2, X(b.AddDays(1)) - X(a));
                    var bar = new Border { Width = width, Height = 12, Background = PlanSheetView.Brush("GanttTaskTintBrush") };
                    Canvas.SetLeft(bar, X(a)); Canvas.SetTop(bar, 5); chart.Children.Add(bar);
                    var done = new Border { Width = width * (double)Math.Clamp(phase.Tasks.Progress.Value ?? 0, 0, 1), Height = 12, Background = PlanSheetView.Brush("GanttTaskBrush") };
                    Canvas.SetLeft(done, X(a)); Canvas.SetTop(done, 5); chart.Children.Add(done);
                }
                foreach (var (date, brush) in new[] { (day, "AccentFillColorDefaultBrush"), (end.Value, "SystemFillColorCriticalBrush") }) {
                    if (date < start || date > until) continue;
                    var line = new Border { Width = 1, Height = 22, Background = PlanSheetView.Brush(brush) }; Canvas.SetLeft(line, X(date)); chart.Children.Add(line);
                }
            }
            chart.SizeChanged += (_, _) => Draw();
            AutomationProperties.SetName(chart, $"{Phase(phase)} {Date(phase.Tasks.Start)} – {Date(phase.Tasks.Finish)} 進捗 {Percent(phase.Tasks.Progress.Value)}");
            ToolTipService.SetToolTip(chart, AutomationProperties.GetName(chart)); Cell(row, chart, 1); panel.Children.Add(row);
        }
        return panel;
    }
    private void RenderMatrix()
    {
        matrix.Children.Clear(); detail.Children.Clear();
        var phases = report.Phases.Where(p => !p.IsTotal).ToArray(); var months = report.Months;
        if (mode == 1 && months is null) { matrix.Children.Add(Text("月別を表示するには期間を設定してください。")); return; }
        var count = mode == 0 ? phases.Length : months!.Periods.Count;
        var widths = new[] { 150d }.Concat(Enumerable.Repeat(mode == 0 ? 100d : 180d, count)).Concat(mode == 0 ? new[] { 90d, 90d, 100d } : []).ToArray();
        var header = Row(widths); Cell(header, Text("担当者"), 0);
        for (var i = 0; i < count; i++) Cell(header, Text(mode == 0 ? Phase(phases[i]) : months!.Periods[i].Start.ToString("yyyy/M"), numeric: true), i + 1);
        if (mode == 0) { Cell(header, Text("残 合計 h", numeric: true), count + 1); Cell(header, Text("稼働可能 h", numeric: true), count + 2); Cell(header, Text("必要稼働率", numeric: true), count + 3); }
        matrix.Children.Add(header);
        var buttons = new List<Button[]>();
        foreach (var person in report.People.Append(report.PeopleTotal)) {
            if (mode == 1 && person == report.PeopleTotal) continue;
            var row = Row(widths); Cell(row, Text(person.Name), 0); var rowButtons = new List<Button>();
            var load = months?.People.SingleOrDefault(p => p.Identity == person.Identity);
            for (var i = 0; i < count; i++) {
                string value, label; IReadOnlyList<string> tasks; IReadOnlySet<string>? unallocated = null; bool warning;
                if (mode == 0) {
                    var t = person.Phases[i].Tasks;
                    value = measure switch { 0 => Value(t.Remaining), 1 => Value(t.Forecast), 2 => Progress(t.Progress), _ => $"{t.Overdue:N0}" };
                    warning = t.Overdue > 0; if (warning) value += " ⚠ 期限超過";
                    label = $"{person.Name} · {Phase(phases[i])} · {Measures[measure]} {value}"; tasks = t.Identities;
                } else {
                    var cell = load?.Periods[i];
                    value = cell?.Percent is { } percent ? Number(percent) + "%" : "—"; warning = cell?.DailyOverloads.Count > 0 || cell?.Overloaded == true;
                    value += $"{(warning ? " 超過" : "")} / 日超過 {cell?.DailyOverloads.Count ?? 0}日・最大 {(cell?.MaxDailyPercent is { } peak ? Number(peak) + "%" : "—")}";
                    if (load?.Unallocated.Count > 0) value += $" 未配分 {load.Unallocated.Count}";
                    label = $"{person.Name} · {months!.Periods[i].Start:yyyy/M} · 負荷 {value}";
                    unallocated = load?.Unallocated.ToHashSet(); tasks = (cell?.Tasks ?? []).Concat(load?.Unallocated ?? []).Distinct().ToArray();
                }
                var title = label; var ids = tasks; var unknown = unallocated;
                var button = Button(value, mode == 0 ? $"SummaryCell_{person.Identity}_{i}" : $"SummaryMonth_{person.Identity}_{i}", () => Select(title, ids, unknown));
                AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
                if (warning) button.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush");
                Cell(row, button, i + 1); rowButtons.Add(button);
            }
            if (mode == 0) {
                Cell(row, Text(Value(person.Tasks.Remaining), numeric: true), count + 1); Cell(row, Text(Number(person.Capacity), numeric: true), count + 2);
                var required = Text(Percent(person.RequiredLoad) + (person.RequiredLoad > 1 ? " 超過" : ""), numeric: true);
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
        var list = Id(new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 320, Width = 1230, Padding = new(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch }, "SummaryTasks");
        var table = new StackPanel(); double[] widths = [70, 260, 90, 90, 90, 110, 110, 380];
        var header = Row(widths); string[] labels = ["ID", "タスク名", "見積 h", "実績 h", "残 h", "開始日", "終了日", "状態"];
        for (var i = 0; i < labels.Length; i++) Cell(header, Text(labels[i], numeric: i is >= 2 and <= 6), i);
        table.Children.Add(header);
        var calendar = new PlanCalendar { CompanyDaysOff = document!.State.Settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = document.State.Settings.ImportedHolidays?.ToPreset() };
        var lateness = PlanLateness.Classify(schedule.Values.ToArray(), document.Baseline, calendar, day);
        foreach (var row in document!.State.Rows.Where(r => ids.Contains(r.Identity))) {
            var task = schedule[row.Identity]; var state = task.Input.Closed ? "完了" : "未完了";
            var late = lateness.Tasks[row.Identity];
            if (late.MissedPublishedDate is { } missed) state += late.OverdueKind == PlanOverdueKind.Finish
                ? $" · 期限超過: 完了予定 {missed:M/d} を過ぎて未完了（見込み残存工数 {Number(task.Remaining)}h）"
                : $" · 期限超過: 開始予定 {missed:M/d} を過ぎて未着手";
            if (task.IsOverEstimate) state += $" · 見積超過: 見込工数 {Number(task.Forecast)}h（見積工数 {Number(task.Estimate)}h、差異 {Signed(task.Variance)}h）"; if (task.IsZeroRemainingOpen) state += " · 残 0・未完了: 見込み残存工数が 0 ですが Issue は閉じていません";
            if (unallocated?.Contains(row.Identity) == true) state += " · 未配分: " + (task.Remaining is null ? "見込み残存工数が未入力" : task.StartReason == "入力エラー" || task.Remaining < 0 ? "入力エラー" : task.Start.Value is null || task.End.Value is null ? "日程が未入力" : "期間内に稼働日なし");
            var item = Row(widths);
            string[] values = [row.Identity, row.Title, Number(task.Estimate), Number(task.Actual), Number(task.Remaining), Date(task.Start.Value), Date(task.End.Value), state];
            for (var i = 0; i < values.Length; i++) Cell(item, Text(values[i], i == 7 ? "SummaryTask_" + row.Identity : "", i is >= 2 and <= 6), i);
            AutomationProperties.SetName(item, string.Join(" · ", values)); list.Items.Add(item);
        }
        table.Children.Add(list); detail.Children.Add(Wide(table));
    }
}


using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GhProjectsBoards.App;

internal static class SummaryText
{
    internal static string Number(decimal n) => n.ToString("0.###########", CultureInfo.CurrentCulture);
    internal static string Value(EffortValue v) => v.Known == 0 && v.Unknown > 0 ? "不明"
        : Number(v.Days) + (v.Complete ? "" : "（未完）");
    internal static string Exact(EffortValue v) => Value(v) + " 人日 / "
        + (v.Known == 0 && v.Unknown > 0 ? "不明" : Number(v.Hours) + (v.Complete ? "" : "（小計）")) + " 人時"
        + (v.Unknown > 0 ? $" · 未入力/未確認 {v.Unknown}件" : "") + (v.Stale > 0 ? $" · 古い報告 {v.Stale}件" : "");
    internal static string Comparison(BaselineComparison c) => c.State + "\n" + (c.Baseline is { } b
        ? $"基準: 見積 {Hours(b.Estimate)} / {b.Mode?.ToString() ?? "不明"} / {Date(b.Start)} → {Date(b.Finish)}" : "基準: 未設定")
        + (c.Current is { } r ? $"\n現在: 見積 {Hours(r.Input?.Estimate)} / {r.Plan?.Mode.ToString() ?? "不明"} / {Date(r.Plan?.Start)} → {Date(r.Plan?.Finish)}" : "\n現在: " + c.State);
    private static string Hours(decimal? value) => value is { } v ? Number(v) + "人時" : "不明";
    private static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "不明";
}

// Native ListView owns virtualization, focus and scrolling; this presenter only lays out a comparison row.
public sealed class SummaryPersonPresenter : ContentControl
{
    public SummaryPersonPresenter() { DataContextChanged += (_, _) => Present(); }
    internal static Grid Columns() {
        var grid = new Grid { Width = 940, ColumnSpacing = 12, Padding = new(4, 8, 4, 8) };
        foreach (var width in new[] { 160d, 160, 120, 160, 140, 120 }) grid.ColumnDefinitions.Add(new() { Width = new(width) });
        return grid;
    }
    internal static void Cell(Grid grid, string text, int column) {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(block, column); grid.Children.Add(block);
    }
    private void Present()
    {
        if (DataContext is not PersonSummary p) return;
        var grid = Columns();
        var name = p.Id.Length == 0 ? p.Name : p.Name + "\n" + p.Id;
        var comparison = p.Headroom is { } h ? (h < 0 ? "超過 " : "余裕 ") + SummaryText.Number(Math.Abs(h) / 8m) : "比較未完";
        var values = new[] { name, p.Allowance is { } a ? SummaryText.Number(a / 8m) : "未設定", SummaryText.Value(p.Estimate), SummaryText.Value(p.Actual), SummaryText.Value(p.Forecast), comparison };
        for (var i = 0; i < values.Length; i++) Cell(grid, values[i], i);
        AutomationProperties.SetName(this, string.Join(" / ", values)); Content = grid;
    }
}

internal sealed class SummaryView : Grid
{
    private readonly ListView people = new() { SelectionMode = ListViewSelectionMode.Single, SingleSelectionFollowsFocus = true,
        HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(4, 0, 4, 0) };
    private readonly ListView tasks = new() { SelectionMode = ListViewSelectionMode.Single, SingleSelectionFollowsFocus = true,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = nameof(TaskLine.Label) };
    private readonly TextBlock totals = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new(12, 4, 12, 4) };
    private readonly TextBlock context = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new(12, 4, 12, 4) };
    private readonly TextBlock actualHeader = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock personDetail = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBox filter = new() { Header = "内訳を絞り込み", PlaceholderText = "タイトル・番号", Width = 220 };
    private readonly InfoBar status = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly AppBarButton allowance, establish, replace, board, gantt, edit, baseline;
    private SummaryProjection? projection;
    private bool presenting;
    internal event Action<string>? AllowanceRequested;
    internal event Action<bool>? BaselineRequested;
    internal event Action<string, ProjectView>? TaskRequested;
    internal event Action<string>? EditRequested;
    internal event Action? UndoRequested, SaveRequested, SettingsRequested;
    internal string? SelectedPersonId => (people.SelectedItem as PersonSummary)?.Id;
    internal string? SelectedRowId => (tasks.SelectedItem as TaskLine)?.RowId;
    internal SummaryProjection? AdoptedSummary => projection;
    private sealed record TaskLine(SummaryContribution Contribution, string? RowId)
    {
        public string Label => $"{Contribution.Identity} · {Contribution.Title}\n見積 {SummaryText.Value(Contribution.Estimate)} / 実績 {SummaryText.Value(Contribution.Actual)} / 残り {SummaryText.Value(Contribution.Remaining)} / 見込み {SummaryText.Value(Contribution.Forecast)} 人日"
            + $"\n報告対象: {Contribution.ReportedThrough?.ToString("yyyy-MM-dd") ?? "未入力"}" + (Contribution.Problem is { } problem ? " · " + problem : "");
    }
    internal SummaryView()
    {
        AutomationProperties.SetAutomationId(this, "SummaryView"); Style = (Style)Application.Current.Resources["GanttSurfaceStyle"];
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            RowDefinitions.Add(new() { Height = height });
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true };
        AppBarButton Command(string text, string id, Symbol symbol, Action action, bool secondary = false) {
            var b = new AppBarButton { Label = text, Icon = new SymbolIcon(symbol) }; AutomationProperties.SetAutomationId(b, id); AutomationProperties.SetName(b, text);
            b.Click += (_, _) => action(); if (secondary) commands.SecondaryCommands.Add(b); else commands.PrimaryCommands.Add(b); return b;
        }
        allowance = Command("投入可能工数", "SummaryAllowance", Symbol.Edit, () => { if (SelectedPersonId is { Length: > 0 } id) AllowanceRequested?.Invoke(id); });
        baseline = Command("基準と比較", "SummaryCompare", Symbol.List, ShowBaseline);
        Command("元に戻す", "SummaryUndo", Symbol.Undo, () => UndoRequested?.Invoke());
        establish = Command("基準を確立…", "SummaryEstablish", Symbol.Save, () => BaselineRequested?.Invoke(false), true);
        replace = Command("基準を置換…", "SummaryReplace", Symbol.Refresh, () => BaselineRequested?.Invoke(true), true);
        Command("計画設定", "SummarySettings", Symbol.Setting, () => SettingsRequested?.Invoke(), true);
        Children.Add(commands);
        SetRow(totals, 1); Children.Add(totals); AutomationProperties.SetAutomationId(totals, "SummaryTotals");
        SetRow(context, 2); Children.Add(context); AutomationProperties.SetAutomationId(context, "SummaryContext");
        people.ItemTemplate = (DataTemplate)Application.Current.Resources["SummaryPersonTemplate"];
        var header = SummaryPersonPresenter.Columns(); header.HorizontalAlignment = HorizontalAlignment.Left; header.Margin = new(12, 0, 0, 0);
        var labels = new[] { "担当者", "投入可能工数\n（設定値）", "見積合計", "実績", "完了見込み", "余裕 / 超過" };
        for (var i = 0; i < labels.Length; i++)
            if (i == 3) { SetColumn(actualHeader, i); header.Children.Add(actualHeader); }
            else SummaryPersonPresenter.Cell(header, labels[i], i);
        people.Header = header;
        ScrollViewer.SetHorizontalScrollMode(people, ScrollMode.Enabled); ScrollViewer.SetHorizontalScrollBarVisibility(people, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(people, "SummaryPeople"); AutomationProperties.SetName(people, "担当者別の工数比較（人日）");
        SetRow(people, 3); Children.Add(people);
        var details = new Grid { Padding = new(12, 4, 12, 4), RowSpacing = 4 };
        details.RowDefinitions.Add(new() { Height = GridLength.Auto }); details.RowDefinitions.Add(new() { Height = GridLength.Auto });
        details.ColumnDefinitions.Add(new()); details.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        details.Children.Add(personDetail); SetColumn(filter, 1); details.Children.Add(filter);
        AutomationProperties.SetAutomationId(personDetail, "SummaryPersonDetail"); AutomationProperties.SetAutomationId(filter, "SummaryFilter");
        var taskCommands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true };
        AppBarButton TaskCommand(string label, string id, Action action) { var b = new AppBarButton { Label = label, Icon = new SymbolIcon(Symbol.OpenFile) };
            AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => action(); taskCommands.PrimaryCommands.Add(b); return b; }
        board = TaskCommand("Boardsで開く", "SummaryBoards", () => { if (SelectedRowId is { } id) TaskRequested?.Invoke(id, ProjectView.Boards); });
        gantt = TaskCommand("Ganttで開く", "SummaryGantt", () => { if (SelectedRowId is { } id) TaskRequested?.Invoke(id, ProjectView.Gantt); });
        edit = TaskCommand("工数を編集", "SummaryEdit", () => { if (SelectedRowId is { } id) EditRequested?.Invoke(id); });
        TaskCommand("内訳の詳細", "SummaryTaskDetails", ShowTask);
        SetRow(taskCommands, 1); SetColumnSpan(taskCommands, 2); details.Children.Add(taskCommands);
        SetRow(details, 4); Children.Add(details);
        AutomationProperties.SetAutomationId(tasks, "SummaryTasks"); AutomationProperties.SetName(tasks, "選択した担当者の工数内訳");
        SetRow(tasks, 5); Children.Add(tasks);
        AutomationProperties.SetAutomationId(status, "SummaryOperationStatus");
        var retry = new Button { Content = "保存を再試行" }; AutomationProperties.SetAutomationId(retry, "SummaryRetrySave"); retry.Click += (_, _) => SaveRequested?.Invoke(); status.ActionButton = retry;
        SetRow(status, 6); Children.Add(status);
        people.SelectionChanged += (_, _) => { if (!presenting) { tasks.SelectedItem = null; ShowPerson(); } }; filter.TextChanged += (_, _) => { if (!presenting) FilterTasks(); };
        tasks.SelectionChanged += (_, _) => { board.IsEnabled = gantt.IsEnabled = edit.IsEnabled = SelectedRowId is not null; };
    }
    internal void Present(SummaryProjection value, string? selectedPerson = null, string? selectedRow = null)
    {
        var person = selectedPerson ?? SelectedPersonId; var row = selectedRow ?? SelectedRowId;
        presenting = true;
        if (row is not null)
        {
            var taskId = value.TaskIdsByRow?.GetValueOrDefault(row);
            var candidates = value.Contributions.Where(c => taskId is null ? c.RowId == row : c.TaskId == taskId).ToArray();
            person = candidates.FirstOrDefault(c => c.PersonId == person)?.PersonId ?? candidates.FirstOrDefault()?.PersonId;
            if (selectedRow is not null || person != SelectedPersonId) filter.Text = "";
        }
        projection = value;
        actualHeader.Text = $"実績\n（{value.Cutoff:yyyy-MM-dd} 時点）";
        totals.Text = $"Project 合計 · 見積 {SummaryText.Value(value.Estimate)} / 実績 {SummaryText.Value(value.Actual)} 人日";
        context.Text = $"{value.ProjectTitle} · 1人日 = 8人時 · 本日 {value.Today:yyyy-MM-dd} / 報告基準 {value.Cutoff:yyyy-MM-dd} · {value.TaskCount}タスク"
            + (value.UnpublishedCount > 0 ? $"（未公開 {value.UnpublishedCount}件を含む）" : "")
            + (!value.Estimate.Complete || !value.Actual.Complete || value.People.Any(p => !p.Forecast.Complete) ? "\n未完: 入力・確認不足または古い報告" : "");
        people.ItemsSource = value.People; people.SelectedItem = value.People.FirstOrDefault(p => p.Id == person) ?? value.People.FirstOrDefault();
        establish.IsEnabled = value.Baseline is null; replace.IsEnabled = baseline.IsEnabled = value.Baseline is not null;
        presenting = false; ShowPerson(row);
    }
    private void ShowPerson(string? selectedRow = null)
    {
        allowance.IsEnabled = SelectedPersonId is { Length: > 0 };
        if (people.SelectedItem is PersonSummary p) personDetail.Text = $"{p.Name} · 内訳\n独立した残り: {SummaryText.Exact(p.Remaining)}";
        else personDetail.Text = "担当者を選択";
        FilterTasks(selectedRow);
    }
    private void FilterTasks(string? selectedRow = null)
    {
        var id = selectedRow ?? SelectedRowId;
        var taskId = id is null ? null : projection?.TaskIdsByRow?.GetValueOrDefault(id);
        var lines = (projection?.Contributions ?? []).Where(c => c.PersonId == SelectedPersonId)
            .Where(c => (c.Title + " " + c.Identity).Contains(filter.Text, StringComparison.OrdinalIgnoreCase))
            .Select(c => new TaskLine(c, taskId == c.TaskId ? id : c.RowId)).ToArray();
        tasks.ItemsSource = lines; tasks.SelectedItem = id is null ? lines.FirstOrDefault() : lines.FirstOrDefault(l => l.RowId == id);
    }
    internal void ShowOperationStatus(string? problem, string saveStatus)
    {
        var failed = saveStatus.Contains("失敗", StringComparison.Ordinal);
        status.Message = (failed ? saveStatus + " " : "") + problem; status.IsOpen = failed || problem is not null;
        status.ActionButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowTask()
    {
        if (tasks.SelectedItem is not TaskLine line) return;
        var c = line.Contribution;
        var text = $"{c.Identity}\n{c.Title}\n{c.TaskId}\n見積: {SummaryText.Exact(c.Estimate)}\n実績: {SummaryText.Exact(c.Actual)}\n独立した残り: {SummaryText.Exact(c.Remaining)}\n完了見込み: {SummaryText.Exact(c.Forecast)}\n報告対象: {c.ReportedThrough?.ToString("yyyy-MM-dd") ?? "未入力"}\n{c.Problem}";
        if (projection?.Comparisons.FirstOrDefault(b => b.TaskId == c.TaskId) is { } comparison) text += "\n" + SummaryText.Comparison(comparison);
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 560 };
        AutomationProperties.SetAutomationId(block, "SummaryFullTask"); new Flyout { Content = new ScrollViewer { Content = block, MaxHeight = 440 } }.ShowAt(tasks);
    }
    private void ShowBaseline()
    {
        if (projection?.Baseline is not { } b) return;
        var panel = new Grid { Width = 600, Height = 460, RowSpacing = 8 };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new());
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            Text = $"基準 {b.CapturedAt.LocalDateTime:g} · {b.Tasks.Length}タスク\nカレンダー {b.Calendar.Revision} / 祝日 {b.Calendar.Holidays.Version}\n配賦: " + string.Join("、", b.People.Select(p => $"{p.Name} {p.WeightPercent}%")) });
        var list = new ListView { ItemsSource = projection.Comparisons.Select(c => (c.Current?.Identity ?? c.Baseline!.Identity) + " · " + (c.Current?.Title ?? c.Baseline!.Title) + "\n" + SummaryText.Comparison(c)).ToArray() };
        AutomationProperties.SetAutomationId(list, "SummaryBaselineTasks"); SetRow(list, 1); panel.Children.Add(list);
        new Flyout { Content = panel }.ShowAt(baseline);
    }
    internal void CycleFocus(bool backwards)
    {
        var controls = new Control[] { people, filter, tasks, board, allowance };
        var focused = FocusManager.GetFocusedElement(XamlRoot); var index = Array.IndexOf(controls, focused);
        for (var n = 1; n <= controls.Length; n++) { var c = controls[(index + (backwards ? controls.Length - n : n) + controls.Length) % controls.Length]; if (c.IsEnabled && c.Focus(FocusState.Keyboard)) return; }
    }
}

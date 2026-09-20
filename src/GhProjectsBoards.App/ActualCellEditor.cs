using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly StackPanel actualInputPane = new() { Spacing = 4, Visibility = Visibility.Collapsed, Margin = new(0, 4, 0, 4) };
    private readonly TextBlock actualInputHeading = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CalendarDatePicker actualThrough = new() { Header = "報告対象最終日", PlaceholderText = "日付を確認", Width = 168 };
    private readonly ComboBox actualWorker = new() { Header = "実績担当者", Width = 184, DisplayMemberPath = nameof(ActualWorkerChoice.Label) };
    private readonly Button actualUpdate = new() { Content = "更新", VerticalAlignment = VerticalAlignment.Bottom };
    private DateOnly? confirmedActualThrough;
    private bool updatingActualContext;
    private (EditingWorkspace Work, string Row, long Stamp)? actualContext;
    private sealed record ActualWorkerChoice(string? Id, string Label);

    private void InitializeActualInput(StackPanel footer)
    {
        AutomationProperties.SetAutomationId(actualInputPane, "ActualCellEditor");
        AutomationProperties.SetAutomationId(actualInputHeading, "ActualInputHeading");
        AutomationProperties.SetAutomationId(actualThrough, "ActualReportedThrough");
        AutomationProperties.SetAutomationId(actualWorker, "ActualWorker");
        AutomationProperties.SetAutomationId(actualUpdate, "ActualUpdate");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(actualThrough); actions.Children.Add(actualWorker); actions.Children.Add(actualUpdate);
        var cancel = new Button { Content = "取消", VerticalAlignment = VerticalAlignment.Bottom };
        var remove = new Button { Content = "実績を削除", VerticalAlignment = VerticalAlignment.Bottom };
        var details = new Button { Content = "内訳", VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(cancel, "ActualCancel"); AutomationProperties.SetAutomationId(remove, "ActualRemove");
        AutomationProperties.SetAutomationId(details, "ActualDetails");
        actions.Children.Add(details); actions.Children.Add(cancel); actions.Children.Add(remove);
        actualInputPane.Children.Add(actualInputHeading);
        actualInputPane.Children.Add(new ScrollViewer { Content = actions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled });
        footer.Children.Add(actualInputPane);
        actualThrough.DateChanged += (_, _) => { if (!updatingActualContext) confirmedActualThrough = null; };
        actualUpdate.Click += (_, _) => CommitActualCell(confirmContext: true);
        details.Click += (_, _) => ShowActualReports(details);
        cancel.Click += (_, _) => Run(() => {
            if (!active || !CanRefresh) return;
            session.Workspace.SetPlanningBuffer(rows[currentRow].Cells[currentColumn], null);
            UpdateCell(currentRow, currentColumn); RestoreWorkspaceFocus();
        });
        remove.Click += (_, _) => Run(() => {
            if (!active || !CanRefresh) return;
            session.Workspace.RemoveActualInput(registration, rows[currentRow].ItemId, session.Workspace.Revision);
            UpdateCell(currentRow, currentColumn); RestoreWorkspaceFocus();
        });
    }
    private bool TypedActual(EditCell cell) => session.Workspace.PlanningInputRole(cell) == "Actual";
    private void SetCellBuffer(EditCell cell, string? value)
    {
        if (TypedPlanning(cell)) session.Workspace.SetPlanningBuffer(cell, value);
        else session.Workspace.SetBuffer(cell, value);
    }
    private void UpdateActualInput()
    {
        if (!active || currentRow >= rows.Length || ShowingGantt || !TypedActual(rows[currentRow].Cells[currentColumn]))
        { actualInputPane.Visibility = Visibility.Collapsed; actualContext = null; return; }
        actualInputPane.Visibility = Visibility.Visible;
        var work = session.Workspace; var row = rows[currentRow]; var plan = work.Planning(projectId)!;
        if (actualContext == (work, row.ItemId, plan.Stamp)) return;
        actualContext = (work, row.ItemId, plan.Stamp);
        var context = work.ActualInput(registration, row.ItemId);
        actualInputHeading.Text = $"{RowIdentity(row)} · 累計実績（人時）" + (context.Historical ? " · 過去の報告担当者を保持" : "")
            + (context.MultipleReports ? " · 複数人の実績は内訳で更新" : "");
        var choices = context.Historical
            ? new[] { new ActualWorkerChoice(context.PersonId, PersonName(context.PersonId)) }
            : plan.People.Select(p => new ActualWorkerChoice(p.Id, p.Name))
                .Concat((registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, context.TaskId))?.Native?.Assignees ?? []).Select(a => new ActualWorkerChoice(a.Id.NodeId, a.Login)))
                .DistinctBy(p => p.Id).Append(new(null, "未割当")).ToArray();
        actualWorker.ItemsSource = choices;
        actualWorker.SelectedItem = context.HasPerson ? choices.SingleOrDefault(p => p.Id == context.PersonId) : null;
        actualWorker.IsEnabled = !context.Historical && !context.MultipleReports;
        actualUpdate.IsEnabled = !context.MultipleReports;
        updatingActualContext = true;
        var proposed = confirmedActualThrough ?? (plan.Cutoff is { } cutoff ? DateOnly.FromDateTime(cutoff) : (DateOnly?)null);
        actualThrough.Date = proposed is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)) : null;
        updatingActualContext = false;
        string PersonName(string? id) => id is null ? "未割当" : plan.People.SingleOrDefault(p => p.Id == id)?.Name
            ?? registration.Snapshot.Issues.Values.SelectMany(i => i.Native?.Assignees ?? []).FirstOrDefault(a => a.Id.NodeId == id)?.Login ?? id;
    }
    private bool CommitActualCell(bool confirmContext)
    {
        if (!active || !CanRefresh || !TypedActual(rows[currentRow].Cells[currentColumn])) return false;
        UpdateActualInput();
        if (!confirmContext && confirmedActualThrough is null)
        {
            ShowOperationProblem("報告対象日と実績担当者を確認して更新してください。"); actualUpdate.Focus(FocusState.Keyboard); return false;
        }
        try
        {
            if (actualThrough.Date is not { } date) throw new InvalidOperationException("報告対象最終日を選んでください。");
            if (actualWorker.SelectedItem is not ActualWorkerChoice worker) throw new InvalidOperationException("実績担当者または未割当を選んでください。");
            var through = DateOnly.FromDateTime(date.DateTime);
            var cell = rows[currentRow].Cells[currentColumn];
            var text = session.Workspace.Buffer(cell) ?? session.Workspace.Value(cell) ?? "";
            session.Workspace.CommitActualInput(registration, rows[currentRow].ItemId, text, through, worker.Id, session.Workspace.Revision);
            confirmedActualThrough = through; operationProblem = null;
            UpdateCell(currentRow, currentColumn);
            Select(Math.Min(currentRow + 1, rows.Length - 1), currentColumn, false);
            _ = FlushDraftsAsync("actual-cell-commit");
            return true;
        }
        catch (InvalidOperationException error) { ShowOperationProblem(error.Message); return false; }
    }

    private void ShowActualReports(Button anchor)
    {
        if (!active || !CanRefresh || !TypedActual(rows[currentRow].Cells[currentColumn])) return;
        var work = session.Workspace; var revision = work.Revision; var request = generation;
        var row = rows[currentRow]; var cell = row.Cells[currentColumn];
        var plan = work.Planning(projectId)!; var taskId = work.TaskId(registration, row.ItemId);
        var task = plan.Tasks.SingleOrDefault(t => t.Id == taskId) ?? new(taskId);
        var content = new StackPanel { Spacing = 8, Width = Math.Max(280, Math.Min(520, ActualWidth - 64)) };
        AutomationProperties.SetAutomationId(content, "ActualReportsEditor");
        content.Children.Add(new TextBlock { Text = RowIdentity(row) + " · 累計実績の内訳（人時）", TextWrapping = TextWrapping.Wrap });
        var reportRows = new StackPanel { Spacing = 8 };
        content.Children.Add(new ScrollViewer { Content = reportRows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var entries = new List<(string? Person, TextBox Hours, CalendarDatePicker Day, StackPanel View)>();
        var dirty = false; var closingExplicitly = false;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(error, "ActualReportsError");
        var total = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.Children.Add(total);
        void UpdateTotal()
        {
            try { total.Text = "内訳合計: " + PlanningContract.CanonicalHours(entries.Sum(e => PlanningContract.ParseHours(e.Hours.Text))) + "人時"
                + (work.Buffer(cell) is { } target ? " / 入力中: " + target + "人時" : ""); }
            catch (InvalidOperationException) { total.Text = "内訳に未入力・無効な工数があります。"; }
        }
        void Add(string? person, string label, decimal? hours, DateOnly? day)
        {
            if (entries.Any(e => e.Person == person)) { error.Text = "この担当者は既に内訳にあります。"; return; }
            var line = new StackPanel { Spacing = 4 };
            var input = new TextBox { Header = label + "（累計人時）", Text = hours is { } h ? PlanningContract.CanonicalHours(h) : "" };
            TrackContextInput(input);
            var date = new CalendarDatePicker { Header = "報告対象最終日", Date = day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)) : null };
            var remove = new Button { Content = "内訳を削除" };
            AutomationProperties.SetAutomationId(input, "ActualReportHours-" + (person ?? "Unattributed"));
            AutomationProperties.SetAutomationId(date, "ActualReportDate-" + (person ?? "Unattributed"));
            AutomationProperties.SetAutomationId(remove, "ActualReportRemove-" + (person ?? "Unattributed"));
            line.Children.Add(input); line.Children.Add(date); line.Children.Add(remove); reportRows.Children.Add(line);
            entries.Add((person, input, date, line));
            input.TextChanged += (_, _) => { dirty = true; UpdateTotal(); };
            date.DateChanged += (_, _) => dirty = true;
            remove.Click += (_, _) => { dirty = true; entries.RemoveAll(e => e.View == line); reportRows.Children.Remove(line); UpdateTotal(); };
        }
        foreach (var report in task.Actuals ?? []) Add(report.PersonId,
            report.PersonId is null ? "未割当" : plan.People.SingleOrDefault(p => p.Id == report.PersonId)?.Name ?? report.PersonId, report.Hours, report.ReportedThrough);
        var people = plan.People.Select(p => new ActualWorkerChoice(p.Id, p.Name))
            .Concat((registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, taskId))?.Native?.Assignees ?? []).Select(a => new ActualWorkerChoice(a.Id.NodeId, a.Login)))
            .DistinctBy(p => p.Id).Append(new(null, "未割当")).ToArray();
        var choose = new ComboBox { Header = "担当者を追加", ItemsSource = people, DisplayMemberPath = nameof(ActualWorkerChoice.Label) };
        var add = new Button { Content = "追加" }; AutomationProperties.SetAutomationId(choose, "ActualReportAddWorker"); AutomationProperties.SetAutomationId(add, "ActualReportAdd");
        add.Click += (_, _) => { if (choose.SelectedItem is ActualWorkerChoice person) { dirty = true; Add(person.Id, person.Label, null, confirmedActualThrough); } };
        content.Children.Add(choose); content.Children.Add(add); content.Children.Add(error);
        var save = new Button { Content = "更新" }; AutomationProperties.SetAutomationId(save, "ActualReportsUpdate"); content.Children.Add(save);
        var flyout = new Flyout { Content = content, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        var cancel = new Button { Content = "取消" }; AutomationProperties.SetAutomationId(cancel, "ActualReportsCancel"); content.Children.Add(cancel);
        cancel.Click += (_, _) => { if (!CanRefresh) return; closingExplicitly = true; flyout.Hide(); };
        flyout.Closing += (_, args) => { if (!closingExplicitly && dirty) { args.Cancel = true; error.Text = "内訳は未保存です。更新または取消してください。表の入力は保持しています。"; } };
        save.Click += (_, _) => {
            try
            {
                if (!IsLoaded || request != generation || session.Workspace != work || work.Revision != revision || !CanRefresh)
                    throw new InvalidOperationException("入力対象が変わりました。現在の内訳を開き直してください。");
                var reports = entries.Select(e => new ActualContribution(e.Person, PlanningContract.ParseHours(e.Hours.Text),
                    e.Day.Date is { } date ? DateOnly.FromDateTime(date.DateTime) : throw new InvalidOperationException("各内訳の報告対象最終日を確認してください。"))).ToArray();
                work.CommitActualReports(registration, row.ItemId, reports, revision);
                operationProblem = null; closingExplicitly = true; flyout.Hide(); Update("actual-reports"); RestoreWorkspaceFocus(); _ = FlushDraftsAsync("actual-reports");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { error.Text = e.Message; }
        };
        UpdateTotal(); flyout.ShowAt(anchor);
    }
}

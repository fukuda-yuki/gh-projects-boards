using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly StackPanel actualInputPane = new() { Spacing = 4, Visibility = Visibility.Collapsed, Margin = new(0, 4, 0, 4) };
    private readonly TextBlock actualInputHeading = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CalendarDatePicker actualThrough = new() { PlaceholderText = "報告日", Width = 152 };
    private readonly Button actualContextButton = new() { Content = "実績の詳細", Visibility = Visibility.Collapsed };
    private readonly ComboBox actualWorker = new FormComboBox() { Header = "実績担当者", Width = 184, DisplayMemberPath = nameof(ActualWorkerChoice.Label) };
    private readonly Button actualUpdate = new() { Content = "更新", VerticalAlignment = VerticalAlignment.Bottom };
    private DateOnly? confirmedActualThrough;
    internal DateOnly? ReportingDay
    {
        get => confirmedActualThrough;
        set
        {
            confirmedActualThrough = value;
            actualThrough.Date = value is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)) : null;
        }
    }
    private bool actualDetailsRequested;
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
        actions.Children.Add(actualWorker); actions.Children.Add(actualUpdate);
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
        confirmedActualThrough = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9));
        actualThrough.Date = new DateTimeOffset(confirmedActualThrough.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9));
        AutomationProperties.SetName(actualThrough, "実績の報告基準日");
        ToolTipService.SetToolTip(actualThrough, "このProjectで続けて入力する実績の報告日");
        actualThrough.DateChanged += (_, _) => {
            confirmedActualThrough = actualThrough.Date is { } date ? DateOnly.FromDateTime(date.DateTime) : null;
            UpdateSummary(true);
        };
        AutomationProperties.SetAutomationId(actualContextButton, "ActualContext");
        actualContextButton.Click += (_, _) => { actualDetailsRequested = !actualDetailsRequested; UpdateActualInput(); };
        actualUpdate.Click += (_, _) => CommitActualCell();
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
        if (!active || currentRow >= rows.Length || ShowingGantt || ShowingSummary || !TypedActual(rows[currentRow].Cells[currentColumn]))
        { actualInputPane.Visibility = actualContextButton.Visibility = Visibility.Collapsed; actualContext = null; actualDetailsRequested = false; return; }
        actualContextButton.Visibility = Visibility.Visible;
        var work = session.Workspace; var row = rows[currentRow]; var plan = work.Planning(projectId)!;
        var context = work.ActualInput(registration, row.ItemId);
        if (actualContext?.Row != row.ItemId) actualDetailsRequested = false;
        actualInputPane.Visibility = actualDetailsRequested || !context.HasPerson || context.MultipleReports || context.Problem is not null
            ? Visibility.Visible : Visibility.Collapsed;
        actualInputHeading.Text = $"{(HasSelectedRange ? "現在のセル · " : "")}{RowIdentity(row)} · 累計実績（人時）" + (context.Historical ? " · 過去の報告担当者を保持" : "")
            + (context.MultipleReports ? " · 複数人の実績は内訳で更新" : "")
            + (context.Problem is { } problem ? "\n" + problem : "");
        if (actualContext == (work, row.ItemId, plan.Stamp)) return;
        actualContext = (work, row.ItemId, plan.Stamp);
        var choices = context.Historical
            ? new[] { new ActualWorkerChoice(context.PersonId, PersonName(context.PersonId)) }
            : plan.People.Select(p => new ActualWorkerChoice(p.Id, p.Name))
                .Concat((registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, context.TaskId))?.Native?.Assignees ?? []).Select(a => new ActualWorkerChoice(a.Id.NodeId, a.Login)))
                .DistinctBy(p => p.Id).Append(new(null, "未割当")).ToArray();
        actualWorker.ItemsSource = choices;
        actualWorker.SelectedItem = context.HasPerson ? choices.SingleOrDefault(p => p.Id == context.PersonId) : null;
        actualWorker.IsEnabled = !context.Historical && !context.MultipleReports;
        actualUpdate.IsEnabled = !context.MultipleReports;
        string PersonName(string? id) => id is null ? "未割当" : plan.People.SingleOrDefault(p => p.Id == id)?.Name
            ?? registration.Snapshot.Issues.Values.SelectMany(i => i.Native?.Assignees ?? []).FirstOrDefault(a => a.Id.NodeId == id)?.Login ?? "以前の担当者";
    }
    private bool CommitActualCell(bool moveNext = true)
    {
        if (!active || !CanRefresh || !TypedActual(rows[currentRow].Cells[currentColumn])) return false;
        UpdateActualInput();
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
            if (moveNext) Select(Math.Min(currentRow + 1, rows.Length - 1), currentColumn, false);
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
        var remainingId = plan.Fields.SingleOrDefault(f => f.Role == "Remaining")?.FieldId;
        var remainingCell = remainingId is null ? null : row.Cells.SingleOrDefault(c => c.Key?.FieldId == remainingId);
        var canAllocateRemaining = remainingCell is { Editable: true } && work.Field(remainingCell) is { Conflict: false };
        var content = new StackPanel { Spacing = 8, Width = Math.Max(280, Math.Min(520, ActualWidth - 64)) };
        AutomationProperties.SetAutomationId(content, "ActualReportsEditor");
        content.Children.Add(new TextBlock { Text = RowIdentity(row) + " · 工数の内訳（人時）", TextWrapping = TextWrapping.Wrap });
        var remainingTotal = new TextBox { Header = "タスクの残工数", PlaceholderText = "未入力", IsReadOnly = !canAllocateRemaining,
            Text = remainingCell is null ? "" : work.Buffer(remainingCell) ?? work.Value(remainingCell) ?? "" };
        AutomationProperties.SetAutomationId(remainingTotal, "ActualReportsRemainingTotal"); TrackContextInput(remainingTotal);
        if (canAllocateRemaining) content.Children.Add(remainingTotal);
        var reportRows = new StackPanel { Spacing = 8 };
        var reportScroll = new ScrollViewer { Content = reportRows, MaxHeight = Math.Max(112, Math.Min(240, ActualHeight - 380)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(reportScroll, "ActualReportsRows"); content.Children.Add(reportScroll);
        var entries = new List<(string? Person, TextBox Hours, TextBox Remaining, CalendarDatePicker Day, StackPanel View)>();
        var dirty = false; var closingExplicitly = false;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(error, "ActualReportsError");
        var total = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(total, "ActualReportsTotal");
        content.Children.Add(total);
        void UpdateTotal()
        {
            try
            {
                var actuals = entries.Where(e => !string.IsNullOrWhiteSpace(e.Hours.Text)).ToArray();
                total.Text = actuals.Length == 0 ? "実績 未入力" : "実績合計 " + PlanningContract.CanonicalHours(actuals.Sum(e => PlanningContract.ParseHours(e.Hours.Text))) + "人時";
                if (work.Buffer(cell) is { } target) total.Text += " / 入力中 " + target + "人時";
                if (canAllocateRemaining)
                {
                    var assigned = entries.Where(e => e.Person is not null && !string.IsNullOrWhiteSpace(e.Remaining.Text)).Sum(e => PlanningContract.ParseHours(e.Remaining.Text));
                    total.Text += "\n残工数 配分済み " + PlanningContract.CanonicalHours(assigned) + "人時";
                    if (string.IsNullOrWhiteSpace(remainingTotal.Text)) total.Text += " / 合計 未入力";
                    else
                    {
                        var balance = PlanningContract.ParseHours(remainingTotal.Text) - assigned;
                        total.Text += balance < 0 ? " / 合計を超えています" : " / 未配分 " + PlanningContract.CanonicalHours(balance) + "人時";
                    }
                }
            }
            catch (InvalidOperationException) { total.Text = "工数を確認してください。"; }
        }
        void Changed() { dirty = true; error.Text = ""; UpdateTotal(); }
        void Add(string? person, string label, decimal? hours, DateOnly? day, decimal? remaining = null)
        {
            if (entries.Any(e => e.Person == person)) { error.Text = "この担当者は既に内訳にあります。"; return; }
            var line = new StackPanel { Spacing = 4 };
            line.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            var input = new TextBox { Header = "累計実績", PlaceholderText = "未入力", Text = hours is { } h ? PlanningContract.CanonicalHours(h) : "" };
            var share = new TextBox { Header = "残工数", PlaceholderText = person is null ? "差額で保持" : "未入力", IsReadOnly = person is null || !canAllocateRemaining,
                Text = remaining is { } r ? PlanningContract.CanonicalHours(r) : "" };
            TrackContextInput(input); TrackContextInput(share);
            var date = new CalendarDatePicker { Header = "報告日", Date = day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)) : null };
            var remove = new Button { Content = "内訳を削除", VerticalAlignment = VerticalAlignment.Bottom };
            AutomationProperties.SetAutomationId(input, "ActualReportHours-" + (person ?? "Unattributed"));
            AutomationProperties.SetAutomationId(share, "ActualReportRemaining-" + (person ?? "Unattributed"));
            AutomationProperties.SetAutomationId(date, "ActualReportDate-" + (person ?? "Unattributed"));
            AutomationProperties.SetAutomationId(remove, "ActualReportRemove-" + (person ?? "Unattributed"));
            AutomationProperties.SetName(input, label + " 累計実績（人時）"); AutomationProperties.SetName(share, label + " 残工数（人時）");
            AutomationProperties.SetName(date, label + " 報告対象最終日"); AutomationProperties.SetName(remove, label + "の内訳を削除");
            var hoursLine = new Grid { ColumnSpacing = 8 };
            hoursLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            hoursLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            hoursLine.Children.Add(input);
            if (canAllocateRemaining) { Grid.SetColumn(share, 1); hoursLine.Children.Add(share); }
            var dateLine = new Grid { ColumnSpacing = 8 };
            dateLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            dateLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            dateLine.Children.Add(date); Grid.SetColumn(remove, 1); dateLine.Children.Add(remove);
            line.Children.Add(hoursLine); line.Children.Add(dateLine); reportRows.Children.Add(line);
            entries.Add((person, input, share, date, line));
            input.TextChanged += (_, _) => Changed(); share.TextChanged += (_, _) => Changed();
            date.DateChanged += (_, _) => Changed();
            remove.Click += (_, _) => { if (!CanRefresh) return; entries.RemoveAll(e => e.View == line); reportRows.Children.Remove(line); Changed(); };
        }
        var people = plan.People.Select(p => new ActualWorkerChoice(p.Id, p.Name))
            .Concat((registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, taskId))?.Native?.Assignees ?? []).Select(a => new ActualWorkerChoice(a.Id.NodeId, a.Login)))
            .Concat((task.Actuals ?? []).Where(a => a.PersonId is not null).Select(a => new ActualWorkerChoice(a.PersonId, a.PersonId!)))
            .Concat((task.Contributions ?? []).Select(a => new ActualWorkerChoice(a.PersonId, a.PersonId)))
            .DistinctBy(p => p.Id).Append(new(null, "未割当")).ToArray();
        foreach (var person in (task.Actuals ?? []).Select(a => a.PersonId).Concat((task.Contributions ?? []).Select(c => c.PersonId)).Distinct())
        {
            var report = task.Actuals?.SingleOrDefault(a => a.PersonId == person);
            Add(person, people.First(p => p.Id == person).Label, report?.Hours, report?.ReportedThrough ?? confirmedActualThrough,
                task.Contributions?.SingleOrDefault(c => c.PersonId == person)?.RemainingHours);
        }
        var choose = new FormComboBox { Header = "担当者を追加", ItemsSource = people, DisplayMemberPath = nameof(ActualWorkerChoice.Label) };
        var add = new Button { Content = "追加", VerticalAlignment = VerticalAlignment.Bottom }; AutomationProperties.SetAutomationId(choose, "ActualReportAddWorker"); AutomationProperties.SetAutomationId(add, "ActualReportAdd");
        add.Click += (_, _) => {
            if (!CanRefresh || choose.SelectedItem is not ActualWorkerChoice person) return;
            if (entries.Any(e => e.Person == person.Id)) { error.Text = "この担当者は既に内訳にあります。"; return; }
            Add(person.Id, person.Label, null, confirmedActualThrough); Changed();
            var input = entries.Single(e => e.Person == person.Id).Hours;
            reportRows.UpdateLayout(); input.Focus(FocusState.Programmatic); input.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        };
        var addLine = new Grid { ColumnSpacing = 8 };
        addLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); addLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        addLine.Children.Add(choose); Grid.SetColumn(add, 1); addLine.Children.Add(add);
        content.Children.Add(addLine); content.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "更新" }; AutomationProperties.SetAutomationId(save, "ActualReportsUpdate"); actions.Children.Add(save);
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(MaxWidthProperty, Math.Min(560, XamlRoot.Size.Width - 32)));
        presenter.Setters.Add(new Setter(MinWidthProperty, 0d));
        var flyout = new Flyout { Content = content, FlyoutPresenterStyle = presenter,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        var cancel = new Button { Content = "取消" }; AutomationProperties.SetAutomationId(cancel, "ActualReportsCancel"); actions.Children.Add(cancel); content.Children.Add(actions);
        remainingTotal.TextChanged += (_, _) => Changed();
        cancel.Click += (_, _) => { if (!CanRefresh) return; closingExplicitly = true; flyout.Hide(); };
        flyout.Closing += (_, args) => { if (!closingExplicitly && dirty) { args.Cancel = true; error.Text = "内訳は未保存です。更新または取消してください。表の入力は保持しています。"; } };
        save.Click += (_, _) => {
            try
            {
                if (!IsLoaded || request != generation || session.Workspace != work || work.Revision != revision || !CanRefresh)
                    throw new InvalidOperationException("入力対象が変わりました。現在の内訳を開き直してください。");
                foreach (var entry in entries.Where(e => string.IsNullOrWhiteSpace(e.Hours.Text)))
                    if (task.Actuals?.Any(a => a.PersonId == entry.Person) == true)
                        throw new InvalidOperationException("実績を消すには対象の「内訳を削除」を選んでください。");
                var reports = entries.Where(e => !string.IsNullOrWhiteSpace(e.Hours.Text)).Select(e => new ActualContribution(e.Person, PlanningContract.ParseHours(e.Hours.Text),
                    e.Day.Date is { } date ? DateOnly.FromDateTime(date.DateTime) : throw new InvalidOperationException("各内訳の報告対象最終日を確認してください。"))).ToArray();
                if (canAllocateRemaining)
                    work.CommitWorkAllocation(registration, row.ItemId, reports,
                        string.IsNullOrWhiteSpace(remainingTotal.Text) ? null : remainingTotal.Text,
                        entries.Where(e => e.Person is not null).Select(e => new RemainingContribution(e.Person!,
                            string.IsNullOrWhiteSpace(e.Remaining.Text) ? null : PlanningContract.ParseHours(e.Remaining.Text))).ToArray(), revision);
                else work.CommitActualReports(registration, row.ItemId, reports, revision);
                operationProblem = null; closingExplicitly = true; flyout.Hide(); Update("actual-reports"); RestoreWorkspaceFocus(); _ = FlushDraftsAsync("actual-reports");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { error.Text = e.Message; }
        };
        void CloseOnUnload(object sender, RoutedEventArgs args) { closingExplicitly = true; flyout.Hide(); }
        Unloaded += CloseOnUnload; flyout.Closed += (_, _) => Unloaded -= CloseOnUnload;
        UpdateTotal(); flyout.ShowAt(anchor);
    }
}

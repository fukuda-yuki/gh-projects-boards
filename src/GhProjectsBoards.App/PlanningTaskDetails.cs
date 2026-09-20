using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private sealed record PredecessorChoice(string Id, string Label);
    private sealed record PlanningDetails(Func<PlanningTask> Task, Func<PlanningValueEdit[]> Values,
        Func<PlanningDependencyEdit[]> Dependencies, Func<PlanningProjectionDecision[]> Decisions);
    private PlanningDetails PlanningTaskDetails(StackPanel parent, ProjectPlanning plan, PlanningTask task, EditRow row)
    {
        var work = session.Workspace;
        var issue = registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, task.Id));
        var native = issue?.Native;
        if (issue is not null) parent.Children.Add(new TextBlock { Text = $"GitHub: {issue.State.Value} / 担当: {string.Join("、", native?.Assignees.Select(a => a.Login) ?? [])}"
            + (native?.Parent.Value is { } p ? $" / 親: {p.NodeId}（先行関係とは別）" : ""), TextWrapping = TextWrapping.Wrap });
        var effort = PlanningSection(parent, "工数・進捗・実績");
        var laborKind = new ComboBox { Header = "親タスクの工数区分", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "未指定（子を持つ場合は要確認）", "このタスクの直接工数", "子の集計（合計へ加算しない）" }, SelectedIndex = (int)task.LaborKind };
        AutomationProperties.SetAutomationId(laborKind, "PlanLaborKind"); effort.Children.Add(laborKind);
        var numbers = new List<(string Role, TextBox Input, string Initial)>();
        foreach (var role in new[] { "Estimate", "Remaining" })
        {
            var binding = plan.Fields.SingleOrDefault(b => b.Role == role);
            if (binding is null) continue;
            var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId);
            if (cell is null) continue;
            var initial = work.Value(cell) ?? "";
            var input = PlanningText(effort, role == "Estimate" ? "総見積（人時）" : "独立した残時間（人時）", "PlanWork-" + role, initial);
            input.IsReadOnly = work.Buffer(cell) is not null;
            if (input.IsReadOnly) effort.Children.Add(new TextBlock { Text = "表の未確定文字を保持中。確定・取消後に工数を変更できます。", TextWrapping = TextWrapping.Wrap });
            numbers.Add((role, input, initial));
        }
        if (task.Progress == PlanningProgress.Unstarted && numbers.Any(n => n.Role == "Estimate") && numbers.Any(n => n.Role == "Remaining"))
        {
            var seed = new Button { Content = "見積を残時間へコピー" }; AutomationProperties.SetAutomationId(seed, "PlanSeedRemaining");
            seed.Click += (_, _) => { var remaining = numbers.Single(n => n.Role == "Remaining"); if (!remaining.Input.IsReadOnly) remaining.Input.Text = numbers.Single(n => n.Role == "Estimate").Input.Text; };
            effort.Children.Add(seed);
        }
        var progress = new ComboBox { Header = "採用する進捗", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(progress, "PlanProgress");
        foreach (var value in Enum.GetValues<PlanningProgress>()) progress.Items.Add(new ComboBoxItem { Content = value switch {
            PlanningProgress.Unstarted => "未着手", PlanningProgress.InProgress => "進行中", PlanningProgress.Completed => "完了", _ => "再開" }, Tag = value });
        progress.SelectedIndex = (int)task.Progress; effort.Children.Add(progress);
        var confirmRemaining = new CheckBox { Content = "再開時の残時間を確認", Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(confirmRemaining, "PlanConfirmRemaining"); effort.Children.Add(confirmRemaining);
        progress.SelectionChanged += (_, _) => confirmRemaining.Visibility = progress.SelectedIndex == (int)PlanningProgress.Reopened ? Visibility.Visible : Visibility.Collapsed;
        if (task.Progress == PlanningProgress.Reopened) confirmRemaining.Visibility = Visibility.Visible;
        var actualStart = PlanningText(effort, "実績開始（日本時間）", "PlanActualStart", DateText(task.ActualStart));
        var actualFinish = PlanningText(effort, "実績終了（日本時間）", "PlanActualFinish", DateText(task.ActualFinish));
        effort.Children.Add(new TextBlock { Text = "進行中・再開は基準日時から残時間を計画。完了は残時間0と実績日時を入力します。GitHubのOpen/Closedは変更しません。", TextWrapping = TextWrapping.Wrap });
        var reports = PlanningSection(effort, "累積実績・担当者別内訳");
        var removeActuals = false;
        var removeReports = new Button { Content = "実績を削除" };
        AutomationProperties.SetAutomationId(removeReports, "PlanRemoveReports"); reports.Children.Add(removeReports);
        reports.Children.Add(new TextBlock { Text = "累計人時と報告対象最終日。残時間は独立した見積です。", TextWrapping = TextWrapping.Wrap });
        var reportRows = new List<(string? Id, TextBox Hours, TextBox Day)>();
        var shareRows = new List<(string Id, TextBox Estimate, TextBox Remaining)>();
        var people = plan.People.Select(p => (Id: p.Id, Name: p.Name)).Concat((task.Actuals ?? []).Where(a => a.PersonId is not null).Select(a => (a.PersonId!, a.PersonId!)))
            .Concat((task.Contributions ?? []).Select(a => (a.PersonId, a.PersonId))).DistinctBy(p => p.Item1).ToArray();
        foreach (var person in new[] { (Id: (string?)null, Name: "未割当") }.Concat(people.Select(p => (Id: (string?)p.Item1, Name: p.Item2))))
        {
            var saved = task.Actuals?.SingleOrDefault(a => a.PersonId == person.Id);
            var hours = PlanningText(reports, person.Name + " 累積実績（人時）", "PlanActualHours-" + (person.Id ?? "Unattributed"), saved is null ? "" : PlanningContract.CanonicalHours(saved.Hours));
            var day = PlanningText(reports, "報告対象最終日 yyyy-MM-dd", "PlanReportedThrough-" + (person.Id ?? "Unattributed"), saved?.ReportedThrough.ToString("yyyy-MM-dd"));
            reportRows.Add((person.Id, hours, day));
            hours.TextChanged += (_, _) => { if (hours.Text.Length != 0) removeActuals = false; };
            day.TextChanged += (_, _) => { if (day.Text.Length != 0) removeActuals = false; };
            if (person.Id is null) continue;
            var share = task.Contributions?.SingleOrDefault(c => c.PersonId == person.Id);
            shareRows.Add((person.Id, PlanningText(reports, person.Name + " 見積内訳（人時、空欄は不明）", "PlanShareEstimate-" + person.Id, share?.EstimateHours?.ToString(CultureInfo.InvariantCulture)),
                PlanningText(reports, person.Name + " 残時間内訳（人時、空欄は不明）", "PlanShareRemaining-" + person.Id, share?.RemainingHours?.ToString(CultureInfo.InvariantCulture))));
        }
        removeReports.Click += (_, _) => { removeActuals = true; foreach (var r in reportRows) { r.Hours.Text = ""; r.Day.Text = ""; } };
        reports.Children.Add(new TextBlock { Text = "内訳の合計をタスク工数以下にします。差額は未割当のまま保持します。", TextWrapping = TextWrapping.Wrap });
        var constraints = PlanningSection(parent, "日程の制約");
        var earliest = PlanningText(constraints, "最早開始", "PlanEarliest", DateText(task.EarliestStart));
        var fixedStart = PlanningText(constraints, "固定開始", "PlanFixedStart", DateText(task.FixedStart));
        var fixedFinish = PlanningText(constraints, "固定終了", "PlanFixedFinish", DateText(task.FixedFinish));
        var deadline = PlanningText(constraints, "期限（警告のみ）", "PlanDeadline", DateText(task.Deadline));
        var links = PlanningSection(parent, "先行Issue（終了→開始）");
        var adopted = work.PlanFor(registration).Inputs!.Single(i => i.Task.Id == task.Id).Predecessors;
        var selected = adopted.Where(l => l.Kind == "FS" && l.ExternalFinish is null).Select(l => l.PredecessorId).ToHashSet();
        var choices = registration.Snapshot.Issues.Values.Where(i => i.Id.NodeId != task.Id)
            .Select(i => new PredecessorChoice(i.Id.NodeId, $"{i.Repository.NameWithOwner} #{i.Number} {i.Title.Value}"))
            .Concat(work.LocalRows.Where(r => r.ProjectId == projectId && r.Id != task.Id).Select(r => new PredecessorChoice(r.Id, "新規: " + r.Title)))
            .Concat(selected.Where(id => !registration.Snapshot.Issues.ContainsKey(new(work.Scope, id)) && !work.LocalRows.Any(r => r.Id == id)).Select(id => new PredecessorChoice(id, id + "（外部・未確認）"))).ToArray();
        var search = PlanningText(links, "先行Issueを検索", "PlanPredecessorSearch", "");
        var list = new ListView { ItemsSource = choices, DisplayMemberPath = nameof(PredecessorChoice.Label), SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = true, Height = 180 };
        AutomationProperties.SetAutomationId(list, "PlanPredecessors"); links.Children.Add(list);
        bool changing = true;
        foreach (var choice in choices.Where(c => selected.Contains(c.Id))) list.SelectedItems.Add(choice); changing = false;
        list.SelectionChanged += (_, args) => { if (changing) return; foreach (PredecessorChoice p in args.AddedItems) selected.Add(p.Id); foreach (PredecessorChoice p in args.RemovedItems) selected.Remove(p.Id); };
        search.TextChanged += (_, _) => {
            changing = true; var shown = choices.Where(c => c.Label.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray(); list.ItemsSource = shown;
            foreach (var choice in shown.Where(c => selected.Contains(c.Id))) list.SelectedItems.Add(choice); changing = false;
        };
        foreach (var unsupported in adopted.Where(l => l.Kind != "FS" || l.ExternalFinish is not null))
            links.Children.Add(new TextBlock { Text = $"保持: {unsupported.PredecessorId} / {unsupported.Kind} / {DateText(unsupported.ExternalFinish)}", TextWrapping = TextWrapping.Wrap });
        var decisions = new List<(DraftField Field, ComboBox Choice)>();
        foreach (var field in work.PlanningDecisions(projectId, row.ItemId))
        {
            var role = plan.Fields.Single(f => f.FieldId == field.Key.FieldId).Role;
            var choice = new ComboBox { Header = $"{role}: 基準 {field.Baseline ?? "空"} / ローカル {work.Value(row.Cells.Single(c => c.Key == field.Key)) ?? "空"} / GitHub {field.Observation!.Value ?? "空"}", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(choice, "PlanReconcile-" + role);
            foreach (var label in new[] { "判断を保留", "ローカルを採用", "GitHubを採用（正確な日時・内訳を入力）" }) choice.Items.Add(label);
            choice.SelectedIndex = 0; parent.Children.Add(choice); decisions.Add((field, choice));
        }
        return new(() => {
            decimal? Hours(string value) => string.IsNullOrWhiteSpace(value) ? null : PlanningContract.ParseHours(value);
            var editedReports = reportRows.Any(r => r.Hours.Text != (task.Actuals?.SingleOrDefault(a => a.PersonId == r.Id) is { } saved ? PlanningContract.CanonicalHours(saved.Hours) : "")
                || r.Day.Text != (task.Actuals?.SingleOrDefault(a => a.PersonId == r.Id)?.ReportedThrough.ToString("yyyy-MM-dd") ?? ""));
            if (!removeActuals && editedReports && reportRows.Any(r => task.Actuals?.Any(a => a.PersonId == r.Id) == true
                && string.IsNullOrWhiteSpace(r.Hours.Text) && string.IsNullOrWhiteSpace(r.Day.Text)))
                throw new InvalidOperationException("担当者別の実績を消すには表の「内訳」、全実績を消すには「実績を削除」を選んでください。");
            var actuals = removeActuals ? [] : !editedReports ? task.Actuals : reportRows.Where(r => !string.IsNullOrWhiteSpace(r.Hours.Text) || !string.IsNullOrWhiteSpace(r.Day.Text)).Select(r => {
                if (!DateOnly.TryParseExact(r.Day.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) throw new InvalidOperationException("実績の報告対象最終日を入力してください。");
                return new ActualContribution(r.Id, PlanningContract.ParseHours(r.Hours.Text), day); }).ToArray();
            var shares = shareRows.Where(r => r.Estimate.Text.Length != 0 || r.Remaining.Text.Length != 0
                || task.Contributions?.Any(c => c.PersonId == r.Id) == true)
                .Select(r => new WorkContribution(r.Id, Hours(r.Estimate.Text), Hours(r.Remaining.Text))).ToArray();
            return task with { LaborKind = (TaskLaborKind)laborKind.SelectedIndex, Progress = (PlanningProgress)((ComboBoxItem)progress.SelectedItem).Tag, ActualStart = PlanningDate(actualStart.Text), ActualFinish = PlanningDate(actualFinish.Text),
                EarliestStart = PlanningDate(earliest.Text), FixedStart = PlanningDate(fixedStart.Text), FixedFinish = PlanningDate(fixedFinish.Text), Deadline = PlanningDate(deadline.Text), Actuals = actuals,
                Contributions = task.Contributions is null && shares.Length == 0 ? null : shares };
        }, () => numbers.Where(n => n.Input.Text != n.Initial || n.Role == "Remaining" && confirmRemaining.IsChecked == true && !n.Input.IsReadOnly)
            .Select(n => new PlanningValueEdit(row.ItemId, n.Role, n.Input.Text.Length == 0 ? null : n.Input.Text)).ToArray(),
            () => selected.SetEquals(adopted.Where(l => l.Kind == "FS" && l.ExternalFinish is null).Select(l => l.PredecessorId)) ? [] : [new(task.Id, selected.ToArray())],
            () => decisions.Where(d => d.Choice.SelectedIndex != 0).Select(d => new PlanningProjectionDecision(d.Field.Key, d.Field.Observation!.Id, d.Choice.SelectedIndex == 2)).ToArray());
    }
}

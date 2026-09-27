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
        Func<PlanningDependencyEdit[]> Dependencies, Func<PlanningProjectionDecision[]> Decisions,
        Func<PlanningInputException[]> Problems, Func<bool> Dirty, Control Progress, Action<Control> Reveal);
    private PlanningDetails PlanningTaskDetails(StackPanel parent, ProjectPlanning plan, PlanningTask task, EditRow row,
        Action changed, Action<FieldKey> goToCell)
    {
        var work = session.Workspace;
        var rules = new List<PlanningInputException>();
        var edits = new List<Func<bool>>();
        var sections = new Dictionary<StackPanel, Expander[]>();
        var inputSections = new Dictionary<Control, Expander[]>();
        StackPanel Section(StackPanel container, string title)
        {
            var panel = PlanningSection(container, title);
            sections[panel] = [.. sections.GetValueOrDefault(container, []), (Expander)panel.Tag];
            return panel;
        }
        void Check(Control target, string message, Func<bool> valid) => rules.Add(new(target, message, valid));
        TextBox Text(StackPanel panel, string label, string id, string? initial)
        {
            var input = PlanningText(panel, label, id, initial);
            inputSections[input] = sections.GetValueOrDefault(panel, []);
            edits.Add(() => input.Text != (initial ?? "")); input.TextChanged += (_, _) => changed();
            return input;
        }
        decimal? Hours(TextBox input) => string.IsNullOrWhiteSpace(input.Text) ? null : PlanningContract.ParseHours(input.Text);
        void HoursRule(TextBox input, string label, bool whitespaceIsEmpty = false) => Check(input,
            label + "：0以上10億以下の人時を小数8桁以内で入力してください。",
            () => input.Text.Length == 0 || whitespaceIsEmpty && string.IsNullOrWhiteSpace(input.Text) || ValidPlanningInput(() => PlanningContract.ParseHours(input.Text)));
        MinuteEditor Date(StackPanel panel, string label, string id, DateTime? initial)
        {
            var editor = new MinuteEditor(label + "（日本時間）", id, DateText(initial)); panel.Children.Add(editor);
            inputSections[editor.Input] = sections.GetValueOrDefault(panel, []);
            TrackContextInput(editor.Input); edits.Add(() => editor.Text != DateText(initial)); editor.Edited += changed;
            Check(editor.Input, label + "：日付と時刻を選択するか、yyyy-MM-dd HH:mmで入力してください。",
                () => ValidPlanningInput(() => PlanningDate(editor.Text)));
            return editor;
        }
        var issue = registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, task.Id));
        var native = issue?.Native;
        if (issue is not null) parent.Children.Add(new TextBlock { Text = $"GitHub: {issue.State.Value} / 担当: {string.Join("、", native?.Assignees.Select(a => a.Login) ?? [])}"
            + (native?.Parent.Value is { } p ? $" / 親: {p.NodeId}（先行関係とは別）" : ""), TextWrapping = TextWrapping.Wrap });
        var effort = Section(parent, "工数・進捗・実績");
        var laborKind = new ComboBox { Header = "親タスクの工数区分", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "未指定（子を持つ場合は要確認）", "このタスクの直接工数", "子の集計（合計へ加算しない）" }, SelectedIndex = (int)task.LaborKind };
        AutomationProperties.SetAutomationId(laborKind, "PlanLaborKind"); effort.Children.Add(laborKind);
        edits.Add(() => laborKind.SelectedIndex != (int)task.LaborKind); laborKind.SelectionChanged += (_, _) => changed();
        var numbers = new List<(string Role, TextBox Input, string Initial)>();
        foreach (var role in new[] { "Estimate", "Remaining" })
        {
            var binding = plan.Fields.SingleOrDefault(b => b.Role == role);
            if (binding is null) continue;
            var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId);
            if (cell is null) continue;
            var initial = work.Value(cell) ?? "";
            var label = role == "Estimate" ? "見積" : "残時間";
            var input = Text(effort, role == "Estimate" ? "総見積（人時）" : "残時間（人時）", "PlanWork-" + role, initial);
            input.IsReadOnly = work.Buffer(cell) is not null;
            if (work.Buffer(cell) is { } pending)
            {
                var retained = new TextBlock { Text = $"{label}の確定値：{(initial.Length == 0 ? "未入力" : initial + "人時")}\n表の入力「{pending}」は未確定・計算対象外です。", TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetAutomationId(retained, "PlanPending-" + role); effort.Children.Add(retained);
                var jump = new Button { Content = label + "の入力へ" }; AutomationProperties.SetAutomationId(jump, "PlanPendingEdit-" + role);
                jump.Click += (_, _) => goToCell(cell.Key!); effort.Children.Add(jump);
            }
            else HoursRule(input, label);
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
        inputSections[progress] = sections[effort];
        edits.Add(() => progress.SelectedIndex != (int)task.Progress);
        var confirmRemaining = new CheckBox { Content = "再開時の残時間を確認", Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(confirmRemaining, "PlanConfirmRemaining"); effort.Children.Add(confirmRemaining);
        inputSections[confirmRemaining] = sections[effort];
        progress.SelectionChanged += (_, _) => confirmRemaining.Visibility = progress.SelectedIndex == (int)PlanningProgress.Reopened ? Visibility.Visible : Visibility.Collapsed;
        if (task.Progress == PlanningProgress.Reopened) confirmRemaining.Visibility = Visibility.Visible;
        confirmRemaining.Checked += (_, _) => changed(); confirmRemaining.Unchecked += (_, _) => changed();
        edits.Add(() => confirmRemaining.IsChecked == true);
        var actualStart = Date(effort, "実績開始", "PlanActualStart", task.ActualStart);
        var actualFinish = Date(effort, "実績終了", "PlanActualFinish", task.ActualFinish);
        var effect = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(effect, "PlanProgressEffect");
        void ExplainProgress()
        {
            var selectedProgress = (PlanningProgress)progress.SelectedIndex;
            var role = selectedProgress is PlanningProgress.InProgress or PlanningProgress.Reopened ? "Remaining" : "Estimate";
            var number = numbers.SingleOrDefault(n => n.Role == role); var text = number.Input?.Text;
            var amount = string.IsNullOrEmpty(text) ? "未入力" : ValidPlanningInput(() => PlanningContract.ParseHours(text)) ? text + "人時" : "入力を確認";
            effect.Text = task.Mode == PlanningMode.Manual ? "指定した日程を保持します。進捗の変更だけでは自動計算に切り替えません。"
                : task.Mode == PlanningMode.Unplanned ? "日程は未設定です。日程を作るには「自動計算」か「日時を指定」を選んでください。"
                : selectedProgress == PlanningProgress.Completed ? "保存後の日程には実績開始・終了を採用します。"
                : $"保存後の計算：{(number.Input?.IsReadOnly == true ? "確定済みの" : "")}{(role == "Remaining" ? "残時間" : "見積")} {amount}"
                    + (role == "Remaining" ? $"\nProject共通の再計画基準：{(plan.Cutoff is null ? "未設定" : DateText(plan.Cutoff))}" : "");
            if (selectedProgress == PlanningProgress.Completed) effect.Text += "\n完了には残時間0と実績開始・終了を入力してください。";
        }
        progress.SelectionChanged += (_, _) => { ExplainProgress(); changed(); };
        foreach (var number in numbers) number.Input.TextChanged += (_, _) => ExplainProgress();
        ExplainProgress();
        // Progress correction must not start with unrelated parent/effort decisions.
        foreach (var element in new UIElement[] { progress, effect, confirmRemaining, actualStart, actualFinish }.Reverse())
        { effort.Children.Remove(element); effort.Children.Insert(0, element); }
        Check(actualFinish.Input, "実績終了：実績開始以降の日時を入力してください。", () =>
            !ValidPlanningInput(() => PlanningDate(actualStart.Text)) || !ValidPlanningInput(() => PlanningDate(actualFinish.Text))
            || !(PlanningDate(actualFinish.Text) < PlanningDate(actualStart.Text)));
        bool Completed() => progress.SelectedIndex == (int)PlanningProgress.Completed;
        Check(actualStart.Input, "実績開始：完了にするには実際の開始日時を入力してください。", () => !Completed()
            || !ValidPlanningInput(() => PlanningDate(actualStart.Text)) || PlanningDate(actualStart.Text) is not null);
        Check(actualFinish.Input, "実績終了：完了にするには実際の終了日時を入力してください。", () => !Completed()
            || !ValidPlanningInput(() => PlanningDate(actualFinish.Text)) || PlanningDate(actualFinish.Text) is not null);
        var remainingNumber = numbers.SingleOrDefault(n => n.Role == "Remaining");
        Check(remainingNumber.Input ?? (Control)progress, "残時間：完了にするには0を入力してください。", () => !Completed()
            || remainingNumber.Input is { } remaining && ValidPlanningInput(() => PlanningContract.ParseHours(remaining.Text)) && PlanningContract.ParseHours(remaining.Text) == 0);
        Check(progress, "進捗：完了したタスクは「再開」を選んでください。", () => task.Progress != PlanningProgress.Completed
            || progress.SelectedIndex is (int)PlanningProgress.Completed or (int)PlanningProgress.Reopened);
        Check(confirmRemaining, "残時間：再開時の値を明示的に入力・確認してください。", () => task.Mode != PlanningMode.Auto || task.Progress == PlanningProgress.Reopened
            || progress.SelectedIndex != (int)PlanningProgress.Reopened || remainingNumber.Input is { IsReadOnly: false } remaining
                && remaining.Text.Length > 0 && (remaining.Text != remainingNumber.Initial || confirmRemaining.IsChecked == true));
        var reports = Section(effort, "累積実績・担当者別内訳");
        var removeActuals = false;
        edits.Add(() => removeActuals && task.Actuals is not { Length: 0 });
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
            var hours = Text(reports, person.Name + " 累積実績（人時）", "PlanActualHours-" + (person.Id ?? "Unattributed"), saved is null ? "" : PlanningContract.CanonicalHours(saved.Hours));
            var day = Text(reports, person.Name + " 報告対象最終日 yyyy-MM-dd", "PlanReportedThrough-" + (person.Id ?? "Unattributed"), saved?.ReportedThrough.ToString("yyyy-MM-dd"));
            bool EmptyReport() => string.IsNullOrWhiteSpace(hours.Text) && string.IsNullOrWhiteSpace(day.Text);
            Check(hours, person.Name + " 累積実績：0以上10億以下の人時を小数8桁以内で入力してください。", () => EmptyReport() || ValidPlanningInput(() => PlanningContract.ParseHours(hours.Text)));
            Check(day, person.Name + " 報告対象最終日：yyyy-MM-ddで入力してください。", () => EmptyReport()
                || DateOnly.TryParseExact(day.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && value != default);
            if (saved is not null) Check(hours, person.Name + "の実績：担当者別の削除は表の「内訳」、全実績の削除は「実績を削除」を選んでください。", () => removeActuals || !EmptyReport());
            reportRows.Add((person.Id, hours, day));
            hours.TextChanged += (_, _) => { if (hours.Text.Length != 0) removeActuals = false; };
            day.TextChanged += (_, _) => { if (day.Text.Length != 0) removeActuals = false; };
            if (person.Id is null) continue;
            var share = task.Contributions?.SingleOrDefault(c => c.PersonId == person.Id);
            var estimateShare = Text(reports, person.Name + " 見積内訳（人時、空欄は不明）", "PlanShareEstimate-" + person.Id, share?.EstimateHours?.ToString(CultureInfo.InvariantCulture));
            var remainingShare = Text(reports, person.Name + " 残時間内訳（人時、空欄は不明）", "PlanShareRemaining-" + person.Id, share?.RemainingHours?.ToString(CultureInfo.InvariantCulture));
            HoursRule(estimateShare, person.Name + " 見積内訳", whitespaceIsEmpty: true); HoursRule(remainingShare, person.Name + " 残時間内訳", whitespaceIsEmpty: true);
            shareRows.Add((person.Id, estimateShare, remainingShare));
        }
        removeReports.Click += (_, _) => { removeActuals = true; foreach (var r in reportRows) { r.Hours.Text = ""; r.Day.Text = ""; } changed(); };
        foreach (var number in numbers)
        {
            var parts = shareRows.Select(r => number.Role == "Estimate" ? r.Estimate : r.Remaining).ToArray();
            if (parts.Length == 0) continue;
            Check(parts[0], (number.Role == "Estimate" ? "見積" : "残時間") + "内訳：合計をタスクの工数以下にしてください。", () =>
                !ValidPlanningInput(() => Hours(number.Input)) || parts.Any(p => !ValidPlanningInput(() => Hours(p)))
                || Hours(number.Input) is not { } total || parts.Sum(p => Hours(p) ?? 0) <= total);
        }
        reports.Children.Add(new TextBlock { Text = "内訳の合計をタスク工数以下にします。差額は未割当のまま保持します。", TextWrapping = TextWrapping.Wrap });
        var constraints = Section(parent, "日程の制約");
        var earliest = Date(constraints, "最早開始", "PlanEarliest", task.EarliestStart);
        var fixedStart = Date(constraints, "固定開始", "PlanFixedStart", task.FixedStart);
        var fixedFinish = Date(constraints, "固定終了", "PlanFixedFinish", task.FixedFinish);
        var deadline = Date(constraints, "期限（警告のみ）", "PlanDeadline", task.Deadline);
        var links = Section(parent, "先行Issue（終了→開始）");
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
        edits.Add(() => !selected.SetEquals(adopted.Where(l => l.Kind == "FS" && l.ExternalFinish is null).Select(l => l.PredecessorId)));
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
            edits.Add(() => choice.SelectedIndex != 0);
        }
        return new(() => {
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
                .Select(r => new WorkContribution(r.Id, Hours(r.Estimate), Hours(r.Remaining))).ToArray();
            return task with { LaborKind = (TaskLaborKind)laborKind.SelectedIndex, Progress = (PlanningProgress)((ComboBoxItem)progress.SelectedItem).Tag, ActualStart = PlanningDate(actualStart.Text), ActualFinish = PlanningDate(actualFinish.Text),
                EarliestStart = PlanningDate(earliest.Text), FixedStart = PlanningDate(fixedStart.Text), FixedFinish = PlanningDate(fixedFinish.Text), Deadline = PlanningDate(deadline.Text), Actuals = actuals,
                Contributions = task.Contributions is null && shares.Length == 0 ? null : shares };
        }, () => numbers.Where(n => n.Input.Text != n.Initial || n.Role == "Remaining" && confirmRemaining.IsChecked == true && !n.Input.IsReadOnly)
            .Select(n => new PlanningValueEdit(row.ItemId, n.Role, n.Input.Text.Length == 0 ? null : n.Input.Text)).ToArray(),
            () => selected.SetEquals(adopted.Where(l => l.Kind == "FS" && l.ExternalFinish is null).Select(l => l.PredecessorId)) ? [] : [new(task.Id, selected.ToArray())],
            () => decisions.Where(d => d.Choice.SelectedIndex != 0).Select(d => new PlanningProjectionDecision(d.Field.Key, d.Field.Observation!.Id, d.Choice.SelectedIndex == 2)).ToArray(),
            () => rules.Where(r => !r.Corrected()).ToArray(), () => edits.Any(edited => edited()), progress,
            target => { foreach (var section in inputSections.GetValueOrDefault(target, [])) section.IsExpanded = true; });
    }
}

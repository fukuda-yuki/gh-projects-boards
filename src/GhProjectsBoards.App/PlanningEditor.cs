using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private static TextBox PlanningText(StackPanel panel, string label, string id, string? value)
    {
        var input = new TextBox { Header = label, Text = value ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(input, id); panel.Children.Add(input); return input;
    }
    private static string DateText(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
    private static DateTime? PlanningDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException("日時は yyyy-MM-dd HH:mm（日本時間）で入力してください。");
        return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
    }
    private async Task PlanningDialogAsync(bool settings)
    {
        if (!CanRefresh) { ShowOperationProblem("IME入力を確定・取消してから計画を開いてください。"); return; }
        var request = generation;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var work = session.Workspace;
        var saved = work.Planning(projectId);
        if (saved is null) settings = true;
        if (!settings && (!active || currentRow >= rows.Length)) { ShowOperationProblem("計画する行を選択してください。"); return; }
        if (!settings && !rows[currentRow].IsLocal && !registration.Snapshot.Items.Any(i => i.Id.NodeId == rows[currentRow].ItemId
            && i.Kind == ProjectItemKind.Issue && i.ContentId is not null)) { ShowOperationProblem("計画はIssueまたは新規行で設定してください。"); return; }
        var plan = saved ?? new(1, projectId, 0, null, null, [], new("official-2025-2027", PlanningContract.BundledHolidays(), false, []), [], []);
        var expected = work.Revision;
        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "PlanningStatus");
        Func<ProjectPlanning> candidate;
        if (settings)
        {
            var start = PlanningText(content, "Project開始（日本時間）", "PlanProjectStart", DateText(plan.Start));
            var cutoff = PlanningText(content, "再計画の基準日時（日本時間）", "PlanCutoff", DateText(plan.Cutoff));
            var mappings = new Dictionary<string, ComboBox>();
            foreach (var role in PlanningContract.Roles)
            {
                var name = role switch { "Estimate" => "見積時間", "Remaining" => "残時間", "Actual" => "実績合計", "Start" => "開始日", _ => "終了日" };
                var box = new ComboBox { Header = name + " のGitHubフィールド", HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetAutomationId(box, "PlanField-" + role);
                box.Items.Add(new ComboBoxItem { Content = "未設定", Tag = "" });
                foreach (var f in registration.Snapshot.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem
                    && f.DataType == (role is "Start" or "Finish" ? "DATE" : "NUMBER") && f.Availability == ValueAvailability.Present))
                    box.Items.Add(new ComboBoxItem { Content = f.Name + " [" + f.Id.NodeId + "]", Tag = f.Id.NodeId });
                var id = plan.Fields.SingleOrDefault(f => f.Role == role)?.FieldId ?? "";
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == id) ?? box.Items[0];
                content.Children.Add(box); mappings.Add(role, box);
            }
            content.Children.Add(new TextBlock { Text = $"採用祝日: {plan.Calendar.Holidays.FirstYear}–{plan.Calendar.Holidays.LastYear} / {plan.Calendar.Holidays.Dates.Length}日。平日9–13時・14–18時。", TextWrapping = TextWrapping.Wrap });
            candidate = () => plan with { Start = PlanningDate(start.Text), Cutoff = PlanningDate(cutoff.Text),
                Fields = mappings.Where(x => (string)((ComboBoxItem)x.Value.SelectedItem).Tag != "").Select(x =>
                    new PlanningFieldBinding(x.Key, (string)((ComboBoxItem)x.Value.SelectedItem).Tag, x.Key is "Start" or "Finish" ? "DATE" : "NUMBER")).ToArray() };
        }
        else
        {
            var id = work.TaskId(registration, rows[currentRow].ItemId);
            var task = plan.Tasks.SingleOrDefault(t => t.Id == id) ?? new PlanningTask(id);
            var calculated = work.PlanFor(registration).Tasks.Single(t => t.Id == id);
            content.Children.Add(new TextBlock { Text = rows[currentRow].Cells[0].Key is null ? id : work.Value(rows[currentRow].Cells[0]), TextWrapping = TextWrapping.Wrap });
            var mode = new ComboBox { Header = "日程の決め方", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(mode, "PlanMode");
            foreach (var value in Enum.GetValues<PlanningMode>()) mode.Items.Add(new ComboBoxItem { Content = value.ToString(), Tag = value });
            mode.SelectedIndex = (int)task.Mode; content.Children.Add(mode);
            var owner = new ComboBox { Header = "計画担当者", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(owner, "PlanOwner");
            owner.Items.Add(new ComboBoxItem { Content = "未設定（共通・暫定）", Tag = "" });
            foreach (var person in plan.People) owner.Items.Add(new ComboBoxItem { Content = $"{person.Name} / {person.WeightPercent}%", Tag = person.Id });
            owner.SelectedItem = owner.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (task.OwnerId ?? "")) ?? owner.Items[0]; content.Children.Add(owner);
            var start = PlanningText(content, "採用開始（日本時間、空欄可）", "PlanTaskStart", DateText(calculated.Start));
            var finish = PlanningText(content, "採用終了（日本時間、空欄可）", "PlanTaskFinish", DateText(calculated.Finish));
            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(preview, "PlanSuggestion"); content.Children.Add(preview);
            void Preview() => preview.Text = $"自動案: {DateText(calculated.SuggestedStart)} → {DateText(calculated.SuggestedFinish)}\n{calculated.Problem ?? string.Join(" / ", calculated.Warnings)}\nAutoを選ぶと採用日時をこの自動案で置き換えます。";
            mode.SelectionChanged += (_, _) => Preview(); Preview();
            candidate = () =>
            {
                var selectedMode = (PlanningMode)((ComboBoxItem)mode.SelectedItem).Tag;
                var first = PlanningDate(start.Text); var last = PlanningDate(finish.Text);
                if (start.Text != DateText(calculated.Start) || finish.Text != DateText(calculated.Finish)) selectedMode = PlanningMode.Manual;
                var chosen = task with { Mode = selectedMode, OwnerId = (string)((ComboBoxItem)owner.SelectedItem).Tag is { Length: > 0 } personId ? personId : null,
                    ManualStart = selectedMode == PlanningMode.Manual ? first : null, ManualFinish = selectedMode == PlanningMode.Manual ? last : null };
                return plan with { Tasks = plan.Tasks.Where(t => t.Id != id).Append(chosen).ToArray() };
            };
        }
        content.Children.Add(status);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = settings ? "計画設定" : "タスクの計画", PrimaryButtonText = "保存", CloseButtonText = "キャンセル",
            Content = new ScrollViewer { Content = content, MaxHeight = 560, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        AutomationProperties.SetAutomationId(dialog, "PlanningDialog");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { work.CommitPlanning(registration, candidate(), expected); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { status.Text = e.Message; args.Cancel = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            layout = work.Columns(registration); RebuildRows(); Update(); await FlushDraftsAsync("planning");
        }
    }
    private string PlanningSummary(EditRow row)
    {
        if (session.Workspace.Planning(projectId) is null) return "";
        if (!row.IsLocal && !registration.Snapshot.Items.Any(i => i.Id.NodeId == row.ItemId && i.Kind == ProjectItemKind.Issue && i.ContentId is not null)) return "";
        var id = session.Workspace.TaskId(registration, row.ItemId);
        var result = session.Workspace.PlanFor(registration).Tasks.SingleOrDefault(t => t.Id == id);
        return result is null ? "" : $"\n{result.Mode}: {DateText(result.Start)} → {DateText(result.Finish)}\n{result.Problem ?? result.Controller}\n{string.Join(" / ", result.Warnings)}";
    }
}

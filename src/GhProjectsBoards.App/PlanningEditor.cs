using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private TextBox PlanningText(StackPanel panel, string label, string id, string? value)
    {
        var input = new TextBox { Header = label, Text = value ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
        TrackContextInput(input);
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
        if (settings || session.Workspace.Planning(projectId) is null) { await ShowPlanningSettingsAsync(); return; }
        if (!CanRefresh) { ShowOperationProblem("IME入力を確定・取消してから計画を開いてください。"); return; }
        var request = generation;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var work = session.Workspace;
        var saved = work.Planning(projectId);
        if (!active || currentRow >= rows.Length) { ShowOperationProblem("計画する行を選択してください。"); return; }
        if (!rows[currentRow].IsLocal && !registration.Snapshot.Items.Any(i => i.Id.NodeId == rows[currentRow].ItemId
            && i.Kind == ProjectItemKind.Issue && i.ContentId is not null)) { ShowOperationProblem("計画はIssueまたは新規行で設定してください。"); return; }
        var plan = saved ?? new(3, projectId, 0, null, null, [], new("official-2025-2027", PlanningContract.BundledHolidays(), false, []), [], []);
        var expected = work.Revision;
        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "PlanningStatus");
        Func<ProjectPlanning> candidate;
        Func<PlanningValueEdit[]> values = () => [];
        Func<PlanningDependencyEdit[]> dependencies = () => [];
        Func<PlanningProjectionDecision[]> decisions = () => [];
        {
            var id = work.TaskId(registration, rows[currentRow].ItemId);
            var task = plan.Tasks.SingleOrDefault(t => t.Id == id) ?? (plan.Version >= 3 ? EditingWorkspace.WithObservedAssignment(registration, new(id)) : new PlanningTask(id));
            content.Children.Add(new TextBlock { Text = RowIdentity(rows[currentRow]), TextWrapping = TextWrapping.Wrap });
            var details = PlanningTaskDetails(content, plan, task, rows[currentRow]);
            values = details.Values; dependencies = details.Dependencies; decisions = details.Decisions;
            candidate = () => plan with { Tasks = plan.Tasks.Where(t => t.Id != id).Append(details.Task()).ToArray() };
        }
        content.Children.Add(status);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "タスクの詳細", PrimaryButtonText = "保存", CloseButtonText = "キャンセル",
            Content = new ScrollViewer { Content = content, MaxHeight = 560, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        AutomationProperties.SetAutomationId(dialog, "PlanningDialog");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            using var operation = diagnostics?.Span("planning-save-handler");
            if (!CanRefresh) { status.Text = "IME変換を確定または取消してから保存してください。"; args.Cancel = true; return; }
            try {
                ProjectPlanning next; PlanningValueEdit[] edits; PlanningDependencyEdit[] links; PlanningProjectionDecision[] choices;
                using (diagnostics?.Span("planning-candidate")) { next = candidate(); edits = values(); links = dependencies(); choices = decisions(); }
                using var coreTrace = diagnostics is null ? null : new PerformanceTrace();
                using (diagnostics?.Span("planning-commit")) work.CommitPlanning(registration, next, expected, edits, links, choices);
                if (coreTrace is not null) diagnostics!.Record("planning-core", new { samples = coreTrace.Samples.ToArray() });
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { status.Text = e.Message; args.Cancel = true; return; }
            // A task-value edit preserves the existing controls and pending native input.
            using (diagnostics?.Span("planning-view-refresh"))
            {
                Update();
            }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await FlushDraftsAsync("planning");
        }
    }
    private string PlanningSummary(EditRow row)
    {
        if (session.Workspace.Planning(projectId) is null) return "";
        if (!row.IsLocal && !registration.Snapshot.Items.Any(i => i.Id.NodeId == row.ItemId && i.Kind == ProjectItemKind.Issue && i.ContentId is not null)) return "";
        var id = session.Workspace.TaskId(registration, row.ItemId);
        var result = session.Workspace.PlanFor(registration).Tasks.SingleOrDefault(t => t.Id == id);
        return result is null ? "" : $"\n{result.Mode}: {DateText(result.Start)} → {DateText(result.Finish)}\n{result.Problem ?? result.Controller}"
            + (result.Mode == PlanningMode.Auto && !result.Resolved ? "\n表の以前の日付は今回の計算結果ではありません。" : "")
            + $"\n{string.Join(" / ", result.Warnings)}";
    }
}

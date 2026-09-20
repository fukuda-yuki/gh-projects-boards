using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private async Task InitializeSelectedPlansAsync()
    {
        if (!CanRefresh || !active) { ShowOperationProblem("計画する行を選択してください。"); return; }
        var request = generation; var selected = SelectedRows();
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var work = session.Workspace; var plan = work.Planning(projectId);
        if (plan is null) { ShowOperationProblem("先に計画設定で工数フィールドと開始日時を指定してください。"); return; }
        var adopted = work.PlanFor(registration).Tasks.ToDictionary(t => t.Id);
        var tasks = plan.Tasks.ToDictionary(t => t.Id);
        var ids = selected.Where(r => r.IsLocal || registration.Snapshot.Items.Any(i => i.Id.NodeId == r.ItemId && i.Kind == ProjectItemKind.Issue && i.ContentId is not null))
            .Select(r => work.TaskId(registration, r.ItemId)).Where(id => adopted.GetValueOrDefault(id)?.Mode == PlanningMode.Unplanned).ToArray();
        if (ids.Length == 0) { ShowOperationProblem("選択内に未設定の計画はありません。"); return; }
        var expected = work.Revision;
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = $"選択した未設定 {ids.Length} 件を自動計算にします。GitHub担当者とProject配賦を使います。既存の日程は保持します。", TextWrapping = TextWrapping.Wrap });
        var preview = new ListView { Height = 240, SelectionMode = ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(preview, "PlanBatchPreview"); panel.Children.Add(preview);
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(notice);
        ProjectPlanning Candidate() => EditingWorkspace.UpgradeAssignmentContract(plan) with { Tasks = EditingWorkspace.UpgradeAssignmentContract(plan).Tasks.Where(t => !ids.Contains(t.Id)).Concat(ids.Select(id =>
            EditingWorkspace.WithObservedAssignment(registration, (tasks.GetValueOrDefault(id) ?? new(id)) with { Mode = PlanningMode.Auto }))).ToArray() };
        bool Preview()
        {
            try
            {
                var staged = EditingWorkspace.Restore(work.Snapshot()); staged.CommitPlanning(registration, Candidate(), expected);
                var results = staged.PlanFor(registration).Tasks.Where(t => ids.Contains(t.Id)).ToArray();
                preview.ItemsSource = results.Select(t => $"{t.Id}: {DateText(t.Start)} → {DateText(t.Finish)} / {t.Problem ?? t.Controller}").ToArray();
                notice.Text = $"計算済み {results.Count(t => t.Resolved)} / 未解決 {results.Count(t => !t.Resolved)}。保存はローカルのみです。"; return true;
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { notice.Text = e.Message; return false; }
        }
        Preview();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "自動計算を設定", Content = panel, PrimaryButtonText = "設定", CloseButtonText = "キャンセル" };
        AutomationProperties.SetAutomationId(dialog, "PlanningBatchDialog");
        dialog.PrimaryButtonClick += (_, args) => {
            try { work.CommitPlanning(registration, Candidate(), expected); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { notice.Text = e.Message; args.Cancel = true; return; }
            // No row replacement may run after the modal releases native input.
            RebuildRows(); Update();
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        { await FlushDraftsAsync("planning-batch"); }
    }
}

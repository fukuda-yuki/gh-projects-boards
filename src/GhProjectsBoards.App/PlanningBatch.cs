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
        panel.Children.Add(new TextBlock { Text = $"選択した未設定 {ids.Length} 件をAutoにします。既存のAuto・Manualは保持します。", TextWrapping = TextWrapping.Wrap });
        var owner = new ComboBox { Header = "計画担当者", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(owner, "PlanBatchOwner");
        owner.Items.Add(new ComboBoxItem { Content = "未設定（担当なしは共通・暫定）", Tag = "" });
        foreach (var person in plan.People) owner.Items.Add(new ComboBoxItem { Content = $"{person.Name} / {person.WeightPercent}%", Tag = person.Id });
        owner.SelectedIndex = 0; panel.Children.Add(owner);
        var preview = new ListView { Height = 240, SelectionMode = ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(preview, "PlanBatchPreview"); panel.Children.Add(preview);
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(notice);
        ProjectPlanning Candidate() => plan with { Tasks = plan.Tasks.Where(t => !ids.Contains(t.Id)).Concat(ids.Select(id =>
            (tasks.GetValueOrDefault(id) ?? new(id)) with { Mode = PlanningMode.Auto, OwnerId = (string)((ComboBoxItem)owner.SelectedItem).Tag is { Length: > 0 } person ? person : null })).ToArray() };
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
        owner.SelectionChanged += (_, _) => Preview(); Preview();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Autoを設定", Content = panel, PrimaryButtonText = "設定", CloseButtonText = "キャンセル" };
        AutomationProperties.SetAutomationId(dialog, "PlanningBatchDialog");
        dialog.PrimaryButtonClick += (_, args) => {
            try { work.CommitPlanning(registration, Candidate(), expected); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { notice.Text = e.Message; args.Cancel = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        { RebuildRows(); Update(); await FlushDraftsAsync("planning-batch"); }
    }
}

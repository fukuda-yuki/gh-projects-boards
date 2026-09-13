using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private bool applyDialog;
    private async void ReviewApply(object sender, RoutedEventArgs e)
    {
        if (applyDialog || Workspace.Selected is not { } selected) return;
        applyDialog = true;
        try
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, Height = 280 };
            AutomationProperties.SetAutomationId(list, "ApplyTargetRows");
            var targets = selected.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null)
                .Select(i => new ApplyTarget(i.Id.NodeId, selected.Snapshot.Issues[i.ContentId!])).ToArray();
            list.ItemsSource = targets;
            string Excluded() => $"新規ローカル行 {Workspace.Drafts?.Workspace.LocalRows.Count(r => r.ProjectId == selected.Snapshot.Id.NodeId) ?? 0} 件は作成未対応のためApply対象外です。ローカルに保持します。";
            var pickContent = new StackPanel { Spacing = 8 };
            pickContent.Children.Add(new TextBlock { Text = $"既存Issue {targets.Length} 件から選択。\n" + Excluded(), TextWrapping = TextWrapping.Wrap }); pickContent.Children.Add(list);
            var pick = new ContentDialog { XamlRoot = XamlRoot, Title = $"{selected.Snapshot.Title} のApply対象を選択", Content = pickContent,
                PrimaryButtonText = "選択行を照合", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(pick, "ApplySelectionDialog");
            if (await pick.ShowAsync() != ContentDialogResult.Primary || list.SelectedItems.Count == 0) return;
            await Workspace.PrepareApplyAsync(list.SelectedItems.Cast<ApplyTarget>().Select(t => t.Id).ToHashSet());
            if (Workspace.ApplyReview is not { } review) return;
            var text = $"{review.Batch.Project.Scope.Host} / {Workspace.ProfileLogin} / ID {review.Batch.Project.Scope.ViewerId}\n{review.Batch.ProjectName} / {review.Batch.Project.NodeId}\n選択行 {review.SelectedRows} / フィールド・操作 {review.Batch.Operations.Length} / 作成 0\n未確定文字 {review.PendingBuffers} 件は除外（自動確定しません）\n";
            text += string.Join("\n\n", review.Batch.Operations.Select(o => $"{o.Identity}\n{o.FieldName} / 項目 {o.ItemId} / フィールド {o.Key.FieldId ?? "Issue title"}\nGitHub: {o.Expected ?? "明示的な空値"}\n適用値: {(o.Intended.Clear ? "明示的にクリア" : o.Intended.Value)}"));
            text += "\n" + string.Join("\n", review.Blocked);
            text += "\n" + Excluded();
            text += "\n直前に再照合します。APIに条件付き更新ロックはなく、照合と更新の間の競合は完全には排除できません。";
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "GitHubへ反映する差分", Content = new ScrollViewer { MaxHeight = 380,
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = "明示的にApply", IsPrimaryButtonEnabled = review.Blocked.Length == 0,
                CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(dialog, "ApplyReviewDialog");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Workspace.ConfirmApplyAsync(review);
        }
        finally { applyDialog = false; rendered = null; Update(); }
    }
    private async void ShowApplyHistory(object sender, RoutedEventArgs e)
    {
        if (applyDialog || Workspace.Drafts is not { } session) return;
        applyDialog = true;
        try
        {
            var batch = session.Workspace.Journal.LastOrDefault();
            var text = string.Join("\n\n", session.Workspace.Journal.SelectMany(b => b.Operations.Select(o =>
                $"{b.ProjectName} / {o.Identity}\n{o.FieldName}: {o.State} / {o.Reason}\n試行 {o.Attempts.Length} / {o.Id}\n読み戻し: {o.Verification?.Value ?? (o.Verification is null ? "未確認" : "明示的な空値")}")));
            text += $"\n新規ローカル行 {session.Workspace.LocalRows.Count} 件は作成未対応・Apply対象外です。適用済みには含めません。";
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Applyの実行履歴", Content = new ScrollViewer { MaxHeight = 380,
                Content = new TextBlock { Text = text.Length == 0 ? "実行履歴なし" : text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = "明示的に照合・再開", IsPrimaryButtonEnabled = batch is not null && Workspace.CanRead,
                SecondaryButtonText = batch is null ? "" : "旧承認を撤回して再レビュー",
                CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(dialog, "ApplyHistoryDialog");
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary && batch is not null) await Workspace.ResumeApplyAsync(batch.Id);
            if (choice == ContentDialogResult.Secondary && batch is not null) await Workspace.SupersedeApplyAsync(batch.Id);
        }
        finally { applyDialog = false; rendered = null; Update(); }
    }
    private sealed record ApplyTarget(string Id, IssueReadModel Issue)
    {
        public override string ToString() => $"{Issue.Repository.NameWithOwner} #{Issue.Number} / {Issue.Title.Value} / {Id}";
    }
}

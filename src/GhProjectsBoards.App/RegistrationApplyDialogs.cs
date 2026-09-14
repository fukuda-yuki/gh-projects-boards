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
                .Select(i => new ApplyTarget(i.Id.NodeId, selected.Snapshot.Issues[i.ContentId!].Repository.NameWithOwner + " #" + selected.Snapshot.Issues[i.ContentId!].Number + " / " + selected.Snapshot.Issues[i.ContentId!].Title.Value))
                .Concat((Workspace.Drafts?.Workspace.LocalRows ?? []).Where(r => r.ProjectId == selected.Snapshot.Id.NodeId)
                    .Select(r => new ApplyTarget(r.Id, $"新規作成 / {r.Repository} / {r.Title}"))).ToArray();
            list.ItemsSource = targets;
            string Excluded() => "選択行だけを検証します。未選択の未完成行は送信しません。新規作成と既存更新は別操作です。";
            var pickContent = new StackPanel { Spacing = 8 };
            pickContent.Children.Add(new TextBlock { Text = $"選択候補 {targets.Length} 件（既存Issue・新規作成）。\n" + Excluded(), TextWrapping = TextWrapping.Wrap }); pickContent.Children.Add(list);
            var pick = new ContentDialog { XamlRoot = XamlRoot, Title = $"{selected.Snapshot.Title} のApply対象を選択", Content = pickContent,
                PrimaryButtonText = "選択行を照合", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(pick, "ApplySelectionDialog");
            if (await pick.ShowAsync() != ContentDialogResult.Primary || list.SelectedItems.Count == 0) return;
            await Workspace.PrepareApplyAsync(list.SelectedItems.Cast<ApplyTarget>().Select(t => t.Id).ToHashSet());
            if (Workspace.ApplyReview is not { } review) return;
            var text = $"{review.Batch.Project.Scope.Host} / {Workspace.ProfileLogin} / ID {review.Batch.Project.Scope.ViewerId}\n{review.Batch.ProjectName} / {review.Batch.Project.NodeId}\n選択行 {review.SelectedRows} / 更新 {review.Batch.Operations.Length} / 作成 {review.Batch.Creations?.Length ?? 0}\n未確定文字 {review.PendingBuffers} 件は除外（自動確定しません）\n";
            text += string.Join("\n\n", (review.Batch.Creations ?? []).Select(CreationReviewText)) + "\n";
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
            text += "\n" + string.Join("\n\n", session.Workspace.Creations.Select(c => $"作成 {c.LocalId} / 試行 {c.Id}\n{c.Reason}\n受信ID: {c.ReceivedId ?? c.Received?.Id ?? "未確認"}\n検証済みIssue: {c.Verified?.Url ?? "未確認"}\nProject項目: {c.ItemId ?? "未確認"}\n以前の試行不確定: {c.EarlierUncertain}"));
            var historyContent = new StackPanel { Spacing = 8 };
            historyContent.Children.Add(new TextBlock { Text = text.Length == 0 ? "実行履歴なし" : text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            string? resolutionBatch = null; string? resolutionOperation = null; bool setupReview = false; string? resumeBatch = null;
            foreach (var b in session.Workspace.Journal.Where(b => (b.Creations ?? []).Any(c => !c.Completed)))
            {
                var resume = new Button { Content = $"この実行を照合・再開: {b.Id}", IsEnabled = Workspace.CanRead };
                AutomationProperties.SetAutomationId(resume, "ResumeCreationBatch-" + b.Id);
                resume.Click += (_, _) => resumeBatch = b.Id; historyContent.Children.Add(resume);
            }
            foreach (var b in session.Workspace.Journal)
            foreach (var c in b.Creations ?? [])
            {
                if (!c.Dispatched || c.Completed || session.Workspace.Creations.Last(x => x.LocalId == c.LocalId).Id != c.Id) continue;
                var resolve = new Button { Content = c.Verified is null ? $"作成の不確定結果を解決: {c.Title}" : $"既知Issueの設定を再比較: {c.Title}", IsEnabled = Workspace.CanRead };
                AutomationProperties.SetAutomationId(resolve, "ResolveCreation-" + c.Id);
                resolve.Click += (_, _) => { resolutionBatch = b.Id; resolutionOperation = c.Id; setupReview = c.Verified is not null; };
                historyContent.Children.Add(resolve);
            }
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Applyの実行履歴", Content = new ScrollViewer { MaxHeight = 380,
                Content = historyContent },
                PrimaryButtonText = "明示的に照合・再開", IsPrimaryButtonEnabled = batch is not null && Workspace.CanRead,
                SecondaryButtonText = batch is null ? "" : "旧承認を撤回して再レビュー",
                CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(dialog, "ApplyHistoryDialog");
            foreach (var button in historyContent.Children.OfType<Button>()) button.Click += (_, _) => dialog.Hide();
            var choice = await dialog.ShowAsync();
            if (resumeBatch is not null) { await Workspace.ResumeApplyAsync(resumeBatch); return; }
            if (resolutionBatch is not null && resolutionOperation is not null)
            { if (setupReview) await ReviewCreationSetupAsync(resolutionBatch, resolutionOperation); else await ResolveCreationAsync(resolutionBatch, resolutionOperation); return; }
            if (choice == ContentDialogResult.Primary && batch is not null) await Workspace.ResumeApplyAsync(batch.Id);
            if (choice == ContentDialogResult.Secondary && batch is not null) await Workspace.SupersedeApplyAsync(batch.Id);
        }
        finally { applyDialog = false; rendered = null; Update(); }
    }
    private static string CreationReviewText(CreationOperation c) => $"新規Issue作成 / {c.LocalId}\n宛先 {c.Repository.Name} / Repository ID {c.Repository.Id}\nタイトル: {c.Title}\n"
        + string.Join("\n", c.Selects.Select(s => $"{s.FieldName} [{s.FieldId}]: {s.Intent} {s.OptionName} [{s.OptionId}]"));
    private async Task ReviewCreationSetupAsync(string batchId, string id)
    {
        await Workspace.PrepareCreationSetupAsync(batchId, id);
        if (Workspace.CreationSetupReview is not { } review) return;
        var text = $"既知Issue: {review.Issue.Url}\n現在のタイトル: {review.Issue.Title}\nIssueを再作成せず、この実行のProject設定を再承認します。以前の送信結果は保持されます。\n";
        var batch = Workspace.Drafts!.Workspace.Journal.Single(b => b.Id == batchId);
        var c = batch.Creations!.Single(c => c.Id == id);
        text += $"Project: {batch.ProjectName} / {batch.Project.NodeId}\n";
        text += "現在のローカル値から承認する設定:\n" + string.Join("\n", review.Intents.Select(s => $"{s.FieldName} [{s.FieldId}]: {s.Intent} [{s.OptionId}]"));
        if (review.Fields is null) text += "\n所属後に初期値を観測します。";
        text += "\n" + string.Join("\n", review.Withdrawn.Select(s => $"削除・型変更されたフィールドの意図を撤回: {s.FieldName} [{s.FieldId}] / 以前の意図は実行履歴に保持"));
        text += string.Join("\n", (review.Fields ?? []).Select(f => $"{f.FieldName}: 現在 {f.Expected ?? "空値"} → {f.Intended.Value ?? "明示クリア"}"));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "既知Issueの設定を再承認", Content = new ScrollViewer { MaxHeight = 380,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } }, PrimaryButtonText = "この設定を承認して再開",
            CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "CreationSetupReviewDialog");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Workspace.ConfirmCreationSetupAsync(review);
    }
    private async Task ResolveCreationAsync(string batchId, string id)
    {
        var c = Workspace.Drafts!.Workspace.Creations.Single(c => c.Id == id);
        var url = new TextBox { Header = "関連付けるIssue URL" }; AutomationProperties.SetAutomationId(url, "CreationBindUrl");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"作成試行 {c.Id}\n{c.Repository.Name}: {c.Title}\n作成済みの可能性があります。保留は何も送信しません。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(url);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "不確定なIssue作成", Content = panel,
            PrimaryButtonText = "URLを独立確認", SecondaryButtonText = "新規試行を別承認", CloseButtonText = "保留を続ける", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "CreationResolutionDialog");
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await Workspace.InspectCreationBindingAsync(batchId, id, url.Text);
            if (Workspace.CreationBindingPreview is not { } issue) return;
            var revision = Workspace.CreationBindingRevision;
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "実際のIssueを確認", Content = new TextBlock {
                Text = $"{issue.Url}\n{issue.Title}\nRepository ID: {issue.RepositoryId}\nIssue ID: {issue.Id}\nこの行に関連付けます。元の作成成功の証明ではなく、GitHub変更も行いません。", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "このIssueに関連付ける", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(confirm, "CreationBindingConfirmDialog");
            if (await confirm.ShowAsync() == ContentDialogResult.Primary) await Workspace.ConfirmCreationBindingAsync(batchId, id, issue, revision);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await Workspace.PrepareCreationRetryAsync(batchId, id);
            if (Workspace.ApplyReview is not { } review) return;
            var acknowledge = new CheckBox { Content = new TextBlock { Text = "以前の試行でIssueが作成済みの可能性と、重複作成のリスクを理解しました。", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 } };
            AutomationProperties.SetAutomationId(acknowledge, "CreationDuplicateAcknowledgement");
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"不確定な以前の試行: {id}\n" + CreationReviewText(review.Batch.Creations!.Single()), TextWrapping = TextWrapping.Wrap }); content.Children.Add(acknowledge);
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "別の作成試行を承認", Content = content,
                PrimaryButtonText = "重複リスクで新規作成", IsPrimaryButtonEnabled = false, CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
            acknowledge.Checked += (_, _) => confirm.IsPrimaryButtonEnabled = true;
            acknowledge.Unchecked += (_, _) => confirm.IsPrimaryButtonEnabled = false;
            AutomationProperties.SetAutomationId(confirm, "CreationRetryConfirmDialog");
            if (await confirm.ShowAsync() == ContentDialogResult.Primary) await Workspace.ConfirmCreationRetryAsync(review);
        }
    }
    private sealed record ApplyTarget(string Id, string Description)
    {
        public override string ToString() => $"{Description} / {Id}";
    }
}

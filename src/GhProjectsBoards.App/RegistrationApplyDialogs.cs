using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private bool applyDialog;
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (!IsLoaded) return ContentDialogResult.None;
        var expected = lifetime;
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        activeDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            return expected == lifetime && IsLoaded ? result : ContentDialogResult.None;
        }
        finally { if (ReferenceEquals(activeDialog, dialog)) activeDialog = null; }
    }
    private async void ReviewApply(object sender, RoutedEventArgs e)
    {
        if (applyDialog || Workspace.Selected is not { } selected) return;
        var owner = Workspace; var expected = lifetime;
        applyDialog = true; ApplyHistory.IsEnabled = false;
        try
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, Height = 280 };
            AutomationProperties.SetAutomationId(list, "ApplyTargetRows");
            var canonicalIds = Workspace.Drafts!.Workspace.Open(selected).Select(r => r.ItemId).ToHashSet();
            var titles = Workspace.Drafts.Workspace.Fields.Where(f => f.Key.Kind == "Title").ToDictionary(f => f.Key);
            var targets = selected.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null)
                .Select(i =>
                {
                    var issue = selected.Snapshot.Issues[i.ContentId!];
                    titles.TryGetValue(new FieldKey("Title", issue.Id.NodeId), out var title);
                    var committed = title?.Change?.Value ?? title?.Baseline ?? issue.Title.Value;
                    return new ApplyTarget(i.Id.NodeId, $"{issue.Repository.NameWithOwner} #{issue.Number} / {committed}");
                })
                .Concat((Workspace.Drafts?.Workspace.LocalRows ?? []).Where(r => r.ProjectId == selected.Snapshot.Id.NodeId)
                    .Select(r => new ApplyTarget(r.Id, $"新規作成 / {r.Repository} / {r.Title}"))).Where(t => canonicalIds.Contains(t.Id)).ToArray();
            var visible = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.DisplayedRowIds ?? [];
            var includeHidden = new CheckBox { Content = "非表示行も候補に含める", IsChecked = false }; AutomationProperties.SetAutomationId(includeHidden, "ApplyIncludeHidden");
            var counts = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(counts, "ApplyTargetCounts");
            void Counts() => counts.Text = $"全候補 {targets.Length} / 表示 {targets.Count(t => visible.Contains(t.Id))} / 選択 {list.SelectedItems.Count} / 非表示の作業 {Workspace.Drafts!.Workspace.Open(selected).Count(r => !visible.Contains(r.ItemId) && Workspace.Drafts.Workspace.RowHasWork(r))}";
            void Populate() { list.ItemsSource = targets.Where(t => includeHidden.IsChecked == true || visible.Contains(t.Id)).ToArray(); Counts(); }
            includeHidden.Checked += (_, _) => Populate(); includeHidden.Unchecked += (_, _) => Populate(); list.SelectionChanged += (_, _) => Counts(); Populate();
            string Excluded() => "選択行だけを検証します。未選択の未完成行は送信しません。新規作成と既存更新は別操作です。";
            var pickContent = new StackPanel { Spacing = 8 };
            pickContent.Children.Add(new TextBlock { Text = $"選択候補 {targets.Length} 件（既存Issue・新規作成）。\n" + Excluded(), TextWrapping = TextWrapping.Wrap }); pickContent.Children.Add(counts); pickContent.Children.Add(includeHidden); pickContent.Children.Add(list);
            var pick = new ContentDialog { XamlRoot = XamlRoot, Title = $"{selected.Snapshot.Title} のApply対象を選択", Content = pickContent,
                PrimaryButtonText = "選択行を照合", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(pick, "ApplySelectionDialog");
            if (await ShowDialogAsync(pick) != ContentDialogResult.Primary || list.SelectedItems.Count == 0) return;
            if (owner.Selected != selected) return;
            var selectedIds = list.SelectedItems.Cast<ApplyTarget>().Select(t => t.Id).ToArray();
            await owner.PrepareApplyAsync(selectedIds.ToHashSet(), new(selected.Snapshot.Id, visible, selectedIds, includeHidden.IsChecked == true));
            if (!IsCurrent(owner, expected)) return;
            if (Workspace.ApplyReview is not { } review) return;
            var content = ApplyPanel();
            content.Children.Add(ApplyText($"{review.Batch.ProjectName} / {review.Batch.Project.Scope.Host} / {Workspace.ProfileLogin}", emphasis: true));
            content.Children.Add(ApplyText($"選択行 {review.SelectedRows} / 更新 {review.Batch.Operations.Length} / 作成 {review.Batch.Creations?.Length ?? 0}"));
            content.Children.Add(ApplyText($"未確定文字 {review.PendingBuffers} 件は除外（自動確定しません）"));
            if (review.Blocked.Length > 0)
                content.Children.Add(ApplyMessage("反映できない項目があります", string.Join("\n", review.Blocked), InfoBarSeverity.Error));
            var changes = ApplyPanel(16);
            foreach (var operation in review.Batch.Operations) changes.Children.Add(ApplyChange(operation, includeHistory: false));
            foreach (var creation in review.Batch.Creations ?? []) changes.Children.Add(CreationReview(creation));
            content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = changes, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            content.Children.Add(ApplyText(Excluded()));
            content.Children.Add(ApplyDetails("対象の識別情報と照合について", ApplyText(
                $"アカウント ID {review.Batch.Project.Scope.ViewerId}\nProject ID {review.Batch.Project.NodeId}\n"
                + "直前に再照合します。APIに条件付き更新ロックはなく、照合と更新の間の競合は完全には排除できません。"), "ApplyReviewIdentity"));
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "GitHubへ反映する差分", Content = content,
                PrimaryButtonText = "明示的にApply", IsPrimaryButtonEnabled = review.Blocked.Length == 0,
                CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(dialog, "ApplyReviewDialog");
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) await owner.ConfirmApplyAsync(review);
        }
        finally { applyDialog = false; if (IsLoaded) { if (expected == lifetime) rendered = null; Update(); } }
    }
    private async void ShowApplyHistory(object sender, RoutedEventArgs e)
    {
        if (applyDialog || Workspace.Drafts is not { } session) return;
        var owner = Workspace; var expected = lifetime;
        applyDialog = true; ApplyHistory.IsEnabled = false;
        try
        {
            var batch = session.Workspace.Journal.LastOrDefault();
            var historyContent = ApplyPanel(16);
            var recoveryActions = new List<Button>();
            string? resolutionBatch = null; string? resolutionOperation = null; bool setupReview = false; string? resumeBatch = null;
            if (batch is null) historyContent.Children.Add(ApplyText("実行履歴なし"));
            foreach (var b in session.Workspace.Journal.Reverse())
            {
                var entry = ApplyPanel(12);
                entry.Children.Add(ApplyText($"{b.ProjectName} / {b.ReviewedAt.LocalDateTime:g}", emphasis: true));
                entry.Children.Add(ApplyText($"更新 {b.Operations.Length} / 作成 {b.Creations?.Length ?? 0} / {b.Project.Scope.Host}"));
                if ((b.Creations ?? []).Any(c => !c.Completed))
                {
                    var resume = new Button { Content = "この実行を照合・再開", IsEnabled = Workspace.CanRead };
                    AutomationProperties.SetAutomationId(resume, "ResumeCreationBatch-" + b.Id);
                    ToolTipService.SetToolTip(resume, $"{b.ProjectName} / 実行 {b.Id}");
                    resume.Click += (_, _) => resumeBatch = b.Id; entry.Children.Add(resume); recoveryActions.Add(resume);
                }
                foreach (var operation in b.Operations) entry.Children.Add(ApplyChange(operation, includeHistory: true));
                foreach (var c in b.Creations ?? [])
                {
                    var creation = CreationHistory(c);
                    if (c.Dispatched && !c.Completed && session.Workspace.Creations.Last(x => x.LocalId == c.LocalId).Id == c.Id)
                    {
                        var resolve = new Button { Content = c.Verified is null ? "作成の不確定結果を解決" : "既知Issueの設定を再比較", IsEnabled = Workspace.CanRead };
                        AutomationProperties.SetAutomationId(resolve, "ResolveCreation-" + c.Id);
                        AutomationProperties.SetName(resolve, $"{resolve.Content}: {c.Repository.Name} / {c.Title}");
                        resolve.Click += (_, _) => { resolutionBatch = b.Id; resolutionOperation = c.Id; setupReview = c.Verified is not null; };
                        creation.Children.Add(resolve); recoveryActions.Add(resolve);
                    }
                    entry.Children.Add(creation);
                }
                entry.Children.Add(ApplyDetails("実行とProjectの識別情報", ApplyText($"実行 {b.Id}\nProject {b.Project.NodeId}\nアカウント ID {b.Project.Scope.ViewerId}"), "ApplyBatchIdentity-" + b.Id));
                historyContent.Children.Add(entry);
            }
            var content = ApplyPanel();
            content.Children.Add(ApplyText("各実行の結果と次の操作を確認できます。履歴を開くだけでは送信しません。"));
            if (batch is not null) content.Children.Add(ApplyText($"下の操作対象: 最新の実行 / {batch.ProjectName} / {batch.ReviewedAt.LocalDateTime:g}"));
            content.Children.Add(new ScrollViewer { MaxHeight = 380, Content = historyContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Applyの実行履歴", Content = content,
                PrimaryButtonText = "明示的に照合・再開", IsPrimaryButtonEnabled = batch is not null && Workspace.CanRead,
                SecondaryButtonText = batch is null ? "" : "旧承認を撤回して再レビュー",
                CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(dialog, "ApplyHistoryDialog");
            foreach (var button in recoveryActions) button.Click += (_, _) => dialog.Hide();
            var choice = await ShowDialogAsync(dialog);
            if (!IsCurrent(owner, expected) || !ReferenceEquals(owner.Drafts, session)) return;
            if (resumeBatch is not null) { await Workspace.ResumeApplyAsync(resumeBatch); return; }
            if (resolutionBatch is not null && resolutionOperation is not null)
            { if (setupReview) await ReviewCreationSetupAsync(resolutionBatch, resolutionOperation); else await ResolveCreationAsync(resolutionBatch, resolutionOperation); return; }
            if (choice == ContentDialogResult.Primary && batch is not null) await Workspace.ResumeApplyAsync(batch.Id);
            if (choice == ContentDialogResult.Secondary && batch is not null) await Workspace.SupersedeApplyAsync(batch.Id);
        }
        finally { applyDialog = false; if (IsLoaded) { if (expected == lifetime) rendered = null; Update(); } }
    }
    private string HiddenColumnNote(string? fieldId, string? projectId = null) => Workspace.Selected is { } p
        && (projectId is null || p.Snapshot.Id.NodeId == projectId) && Workspace.Drafts?.Workspace.Columns(p).Hidden(fieldId) == true ? "（グリッドでは非表示）" : "";
    private static StackPanel ApplyPanel(double spacing = 8) => new() { Spacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static TextBlock ApplyText(string text, bool emphasis = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = emphasis ? FontWeights.SemiBold : FontWeights.Normal
    };
    private static InfoBar ApplyMessage(string title, string message, InfoBarSeverity severity) => new()
    {
        Title = title, Message = message, Severity = severity, IsOpen = true, IsClosable = false
    };
    private static Expander ApplyDetails(string title, UIElement content, string id)
    {
        var details = new Expander { Header = title, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(details, id);
        return details;
    }
    private string ApplyValue(ApplyOperation operation, string? value)
    {
        if (value is null) return "明示的な空値";
        if (operation.Key.Kind == "Title") return value;
        var name = Workspace.Registrations.Where(p => p.Snapshot.Id.Scope == Workspace.Profile && p.Snapshot.Id.NodeId == operation.Key.ProjectId)
            .SelectMany(p => p.Snapshot.Fields).SingleOrDefault(f => f.Id.NodeId == operation.Key.FieldId)?.Options.SingleOrDefault(o => o.Id == value)?.Name;
        return name is null ? value + "（選択肢名は未確認）" : $"{value}（{name}）";
    }
    private StackPanel ApplyChange(ApplyOperation operation, bool includeHistory)
    {
        var panel = ApplyPanel(4);
        panel.Children.Add(ApplyText($"{operation.Identity} / {operation.FieldName}{HiddenColumnNote(operation.Key.FieldId, operation.Key.ProjectId)}", emphasis: true));
        var values = ApplyPanel(4);
        var comparison = new Grid { ColumnSpacing = 16 };
        comparison.ColumnDefinitions.Add(new()); comparison.ColumnDefinitions.Add(new());
        comparison.Children.Add(ApplyText($"GitHub: {ApplyValue(operation, operation.Expected)}"));
        var intended = ApplyText($"適用値: {(operation.Intended.Clear ? "明示的にクリア" : ApplyValue(operation, operation.Intended.Value))}", emphasis: true);
        Grid.SetColumn(intended, 1); comparison.Children.Add(intended); values.Children.Add(comparison);
        values.Children.Add(ApplyText($"所有: {(operation.Key.Kind == "Title" ? "Issue共通のタイトル" : "このProjectの項目フィールド")} / フィールド {operation.Key.FieldId ?? "Issue title"}"));
        if (includeHistory)
        {
            panel.Children.Add(ApplyText($"{ApplyStateText(operation.State)} / {operation.Reason}"));
            if (operation.NotBefore is { } wait) panel.Children.Add(ApplyText($"再開可能時刻: {wait.LocalDateTime:g}"));
            var verification = operation.Verification;
            values.Children.Add(ApplyText($"読み戻し: {(verification is null ? "未確認" : verification.Availability is ValueAvailability.Present or ValueAvailability.Empty ? ApplyValue(operation, verification.Value) : $"未確認（{verification.Availability}）")}"));
            values.Children.Add(ApplyText($"試行 {operation.Attempts.Length} / {operation.Id}\nIssue {operation.IssueId} / 項目 {operation.ItemId}"));
            foreach (var attempt in operation.Attempts)
                values.Children.Add(ApplyText($"{attempt.Number}: {attempt.At.LocalDateTime:g} / {ApplyStateText(attempt.State)} / {attempt.Reason}"));
            panel.Children.Add(ApplyDetails("値・読み戻し・試行の詳細", values, "ApplyOperationDetails-" + operation.Id));
        }
        else
        {
            panel.Children.Add(values);
            panel.Children.Add(ApplyDetails("Issueと項目の識別情報", ApplyText($"Issue {operation.IssueId}\n項目 {operation.ItemId}"), "ApplyOperationIdentity-" + operation.Id));
        }
        return panel;
    }
    private StackPanel CreationReview(CreationOperation creation)
    {
        var panel = ApplyPanel(4);
        panel.Children.Add(ApplyText("新規Issue作成 / " + creation.Title, emphasis: true));
        panel.Children.Add(ApplyText($"宛先 {creation.Repository.Name} / Repository ID {creation.Repository.Id}"));
        foreach (var select in creation.Selects)
            panel.Children.Add(ApplyText($"{select.FieldName}{HiddenColumnNote(select.FieldId)} [{select.FieldId}]: {SelectIntentText(select)}"));
        if (creation.Selects.Length == 0) panel.Children.Add(ApplyText("Projectフィールドの指定なし"));
        panel.Children.Add(ApplyDetails("ローカル行と作成試行", ApplyText($"ローカル行 {creation.LocalId}\n作成試行 {creation.Id}"), "CreationReviewIdentity-" + creation.Id));
        return panel;
    }
    private static string SelectIntentText(LocalSelect select) => select.Intent switch
    {
        "Set" => $"設定 / Set {select.OptionName} [{select.OptionId}]",
        "ExplicitClear" => "明示的にクリア / ExplicitClear",
        _ => "未指定（送信しません） / Unspecified"
    };
    private StackPanel CreationHistory(CreationOperation creation)
    {
        var panel = ApplyPanel(4);
        panel.Children.Add(ApplyText($"{creation.Repository.Name} / {creation.Title}", emphasis: true));
        panel.Children.Add(ApplyText($"作成 {creation.LocalId}"));
        var state = creation.Completed ? "完了" : creation.Verified is not null ? "既知IssueのProject設定を確認" : creation.Dispatched ? "作成結果が不確定" : "未送信";
        panel.Children.Add(ApplyText($"{state} / {creation.Reason}"));
        panel.Children.Add(ApplyText($"Issue確認: {(creation.Verified is null ? "未確認" : "確認済み")} / Project所属: {(creation.ItemId is null ? "未確認" : "確認済み")} / フィールド: {(creation.Fields is null ? "未観測" : $"確認済み {creation.Fields.Count(f => f.State == ApplyState.Succeeded)} / {creation.Fields.Length}")}"));
        if (creation.Verified is { } issue) panel.Children.Add(ApplyText("検証済みIssue: " + issue.Url));
        if (creation.EarlierUncertain)
            panel.Children.Add(ApplyMessage("以前の試行に不確定な結果があります", "現在の結果から、以前の試行でIssueが作成されなかったとは判断できません。", InfoBarSeverity.Warning));
        var details = ApplyPanel(4);
        details.Children.Add(ApplyText($"試行 {creation.Id}\nRepository ID {creation.Repository.Id}\n受信ID: {creation.ReceivedId ?? creation.Received?.Id ?? "未確認"}\nIssue ID: {creation.Verified?.Id ?? "未確認"}\nProject項目: {creation.ItemId ?? "未確認"}\n以前の試行不確定: {creation.EarlierUncertain}"));
        if (creation.PreviousAttempt is { } previous) details.Children.Add(ApplyText("以前の試行: " + previous));
        if (creation.UserBound) details.Children.Add(ApplyText("利用者が確認したURLを関連付けました。元の作成成功の証明ではありません。"));
        foreach (var select in creation.SetupIntents ?? creation.Selects.ToArray())
            details.Children.Add(ApplyText($"{select.FieldName} [{select.FieldId}]: {SelectIntentText(select)}"));
        foreach (var intents in creation.EarlierSetupIntents ?? [])
        {
            details.Children.Add(ApplyText("以前に承認した設定（現在の承認には含みません）", emphasis: true));
            foreach (var select in intents)
                details.Children.Add(ApplyText($"{select.FieldName} [{select.FieldId}]: {SelectIntentText(select)}"));
        }
        foreach (var field in creation.Fields ?? []) details.Children.Add(ApplyChange(field, includeHistory: true));
        foreach (var field in creation.EarlierFields ?? [])
        {
            details.Children.Add(ApplyText("以前に承認したフィールド操作", emphasis: true));
            details.Children.Add(ApplyChange(field, includeHistory: true));
        }
        panel.Children.Add(ApplyDetails("作成・Project設定の詳細", details, "CreationHistoryDetails-" + creation.Id));
        return panel;
    }
    private static string ApplyStateText(ApplyState state) => state switch
    {
        ApplyState.Pending => "未送信", ApplyState.Running => "実行中", ApplyState.Succeeded => "読み戻し確認済み",
        ApplyState.Failed => "失敗", ApplyState.Unknown => "結果が不確定", ApplyState.Waiting => "待機中",
        ApplyState.Blocked => "保留", ApplyState.Cancelled => "キャンセル", ApplyState.Superseded => "承認を撤回済み", _ => state.ToString()
    };
    private async Task ReviewCreationSetupAsync(string batchId, string id)
    {
        var owner = Workspace; var expected = lifetime;
        await owner.PrepareCreationSetupAsync(batchId, id);
        if (!IsCurrent(owner, expected)) return;
        if (Workspace.CreationSetupReview is not { } review) return;
        var batch = Workspace.Drafts!.Workspace.Journal.Single(b => b.Id == batchId);
        var content = ApplyPanel();
        content.Children.Add(ApplyText(review.Issue.Title, emphasis: true));
        content.Children.Add(ApplyText($"既知Issue: {review.Issue.Url}\nProject: {batch.ProjectName} / {batch.Project.NodeId}"));
        content.Children.Add(ApplyText("Issueを再作成せず、この実行のProject設定を再承認します。以前の送信結果は保持されます。"));
        var values = ApplyPanel(12);
        values.Children.Add(ApplyText("現在のローカル値から承認する設定", emphasis: true));
        foreach (var intent in review.Intents)
            values.Children.Add(ApplyText($"{intent.FieldName} [{intent.FieldId}]: {SelectIntentText(intent)}"));
        if (review.Fields is null) values.Children.Add(ApplyText("所属後に初期値を観測します。"));
        foreach (var withdrawn in review.Withdrawn)
            values.Children.Add(ApplyMessage("削除・型変更されたフィールドの意図を撤回", $"{withdrawn.FieldName} [{withdrawn.FieldId}] / 以前の意図は実行履歴に保持", InfoBarSeverity.Warning));
        foreach (var field in review.Fields ?? []) values.Children.Add(ApplyChange(field, includeHistory: false));
        content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = values, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "既知Issueの設定を再承認", Content = content, PrimaryButtonText = "この設定を承認して再開",
            CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "CreationSetupReviewDialog");
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) await Workspace.ConfirmCreationSetupAsync(review);
    }
    private async Task ResolveCreationAsync(string batchId, string id)
    {
        var owner = Workspace; var expected = lifetime;
        var c = Workspace.Drafts!.Workspace.Creations.Single(c => c.Id == id);
        var url = new TextBox { Header = "関連付けるIssue URL" }; AutomationProperties.SetAutomationId(url, "CreationBindUrl");
        var panel = ApplyPanel();
        panel.Children.Add(ApplyText($"{c.Repository.Name}: {c.Title}", emphasis: true));
        panel.Children.Add(ApplyMessage("作成済みの可能性があります", "保留は何も送信しません。既存IssueのURLを確認するか、重複リスクを別途承認して新しい作成を行います。", InfoBarSeverity.Warning));
        panel.Children.Add(ApplyText($"作成試行 {c.Id}"));
        panel.Children.Add(url);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "不確定なIssue作成", Content = panel,
            PrimaryButtonText = "URLを独立確認", SecondaryButtonText = "新規試行を別承認", CloseButtonText = "保留を続ける", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "CreationResolutionDialog");
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await owner.InspectCreationBindingAsync(batchId, id, url.Text);
            if (!IsCurrent(owner, expected)) return;
            if (Workspace.CreationBindingPreview is not { } issue) return;
            var revision = Workspace.CreationBindingRevision;
            var content = ApplyPanel();
            content.Children.Add(ApplyText(issue.Title, emphasis: true));
            content.Children.Add(ApplyText(issue.Url));
            content.Children.Add(ApplyText($"Repository ID: {issue.RepositoryId}\nIssue ID: {issue.Id}"));
            content.Children.Add(ApplyText("この行に関連付けます。元の作成成功の証明ではなく、GitHub変更も行いません。"));
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "実際のIssueを確認", Content = content,
                PrimaryButtonText = "このIssueに関連付ける", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(confirm, "CreationBindingConfirmDialog");
            if (await ShowDialogAsync(confirm) == ContentDialogResult.Primary) await Workspace.ConfirmCreationBindingAsync(batchId, id, issue, revision);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await owner.PrepareCreationRetryAsync(batchId, id);
            if (!IsCurrent(owner, expected)) return;
            if (Workspace.ApplyReview is not { } review) return;
            var acknowledge = new CheckBox { Content = new TextBlock { Text = "以前の試行でIssueが作成済みの可能性と、重複作成のリスクを理解しました。", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 } };
            AutomationProperties.SetAutomationId(acknowledge, "CreationDuplicateAcknowledgement");
            var content = ApplyPanel();
            content.Children.Add(ApplyText($"不確定な以前の試行: {id}"));
            content.Children.Add(new ScrollViewer { MaxHeight = 280, Content = CreationReview(review.Batch.Creations!.Single()), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            content.Children.Add(acknowledge);
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "別の作成試行を承認", Content = content,
                PrimaryButtonText = "重複リスクで新規作成", IsPrimaryButtonEnabled = false, CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
            acknowledge.Checked += (_, _) => confirm.IsPrimaryButtonEnabled = true;
            acknowledge.Unchecked += (_, _) => confirm.IsPrimaryButtonEnabled = false;
            AutomationProperties.SetAutomationId(confirm, "CreationRetryConfirmDialog");
            if (await ShowDialogAsync(confirm) == ContentDialogResult.Primary) await Workspace.ConfirmCreationRetryAsync(review);
        }
    }
    private sealed record ApplyTarget(string Id, string Description)
    {
        public override string ToString() => $"{Description} / {Id}";
    }
}

using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private bool applyDialog;
    private static string ApplyIdentity(ApplyOperation operation) => ApplyResultsPresentation.Identity(operation);
    private void UpdateApplyProgress()
    {
        var batch = Workspace.Drafts?.Workspace.Journal.SingleOrDefault(b => b.Id == Workspace.ExecutingBatchId);
        ApplyProgressPanel.Visibility = batch is null ? Visibility.Collapsed : Visibility.Visible;
        if (batch is null) { ApplyProgressRows.ItemsSource = null; return; }
        string Field(ApplyOperation operation) => $"{ApplyIdentity(operation)} / {(operation.Key.Kind == "Title" ? "タイトル（Issue共通）" : operation.FieldName)}: {ApplyStateText(operation.State)}";
        ApplyProgressRows.ItemsSource = batch.Operations.Select(Field).Concat((batch.Creations ?? []).SelectMany(c =>
            new[] { $"新規作成 / {c.Repository.Name} / {c.Title}: {(c.Completed ? "反映済み" : c.Verified is not null ? "Issue確認済み・Project設定が未完了" : c.Dispatched ? "作成結果の確認が必要" : "未送信")}" }
                .Concat((c.Fields ?? []).Select(Field)))).ToArray();
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (!IsLoaded) return ContentDialogResult.None;
        var expected = lifetime;
        if (!dialog.Resources.ContainsKey("ContentDialogMaxWidth")) dialog.Resources["ContentDialogMaxWidth"] = 760d;
        activeDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            return expected == lifetime && IsLoaded ? result : ContentDialogResult.None;
        }
        finally { if (ReferenceEquals(activeDialog, dialog)) activeDialog = null; }
    }
    private async void ShowApplyHistory(object sender, RoutedEventArgs e)
    {
        if (applyDialog || Workspace.Drafts is not { } session) return;
        ProjectSettingsFlyout.Hide();
        var owner = Workspace; var expected = lifetime;
        applyDialog = true; ApplyHistory.IsEnabled = false;
        string? resolutionBatch = null; string? resolutionOperation = null; bool setupReview = false;
        string? resumeBatch = null; string? withdrawBatch = null;
        try
        {
            var historyContent = new ListView { SelectionMode = ListViewSelectionMode.None,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 380 };
            AutomationProperties.SetAutomationId(historyContent, "ApplyResultBatches");
            var allHistory = new CheckBox { Content = "完了分を含む保存履歴" };
            AutomationProperties.SetAutomationId(allHistory, "ApplyShowAllHistory");
            var attention = ApplyResultsPresentation.Attention(session.Workspace.Journal);
            var content = ApplyPanel();
            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.Children.Add(ApplyText(ApplyResultsPresentation.Summary(attention)));
            var help = ApplyHelp("履歴を開くだけでは送信しません。「確認して再開」は未完了の結果を照合してから続けます。不確定な送信は自動で繰り返しません。「承認を撤回」は試行を保存したまま承認を取り消します。再度反映するには新しいレビューが必要です。", "ApplyHistoryHelp");
            Grid.SetColumn(help, 1); heading.Children.Add(help); content.Children.Add(heading);
            content.Children.Add(allHistory); content.Children.Add(historyContent);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "反映結果・履歴", Content = content,
                CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(dialog, "ApplyHistoryDialog");
            void Populate()
            {
                historyContent.Items.Clear();
                foreach (var b in session.Workspace.Journal.Reverse())
                {
                var pending = attention.Where(a => a.BatchId == b.Id).ToArray();
                if (allHistory.IsChecked != true && pending.Length == 0) continue;
                var entry = ApplyPanel(12);
                entry.Children.Add(ApplyText($"{b.ProjectName} / {b.ReviewedAt.LocalDateTime:g}", emphasis: true));
                if (b.Operations.Any(o => o.State is not (ApplyState.Succeeded or ApplyState.Superseded))
                    || (b.Creations ?? []).Any(c => !c.Completed && c.Authorized))
                {
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var resume = new Button { Content = "確認して再開", IsEnabled = Workspace.CanRead };
                    AutomationProperties.SetAutomationId(resume, ((b.Creations ?? []).Any() ? "ResumeCreationBatch-" : "ResumeApplyBatch-") + b.Id);
                    AutomationProperties.SetName(resume, $"確認して再開：{b.ProjectName} / {b.ReviewedAt.LocalDateTime:g}");
                    resume.Click += (_, _) => { resumeBatch = b.Id; dialog.Hide(); };
                    var withdraw = new Button { Content = "承認を撤回" };
                    AutomationProperties.SetAutomationId(withdraw, "WithdrawApplyBatch-" + b.Id);
                    withdraw.Click += (_, _) => { withdrawBatch = b.Id; dialog.Hide(); };
                    actions.Children.Add(resume); actions.Children.Add(withdraw); entry.Children.Add(actions);
                }
                foreach (var operation in b.Operations.Where(o => allHistory.IsChecked == true || pending.Any(a => a.OperationId == o.Id)))
                    entry.Children.Add(ApplyChange(operation, includeHistory: true));
                foreach (var c in (b.Creations ?? []).Where(c => allHistory.IsChecked == true || pending.Any(a => a.CreationId == c.Id)))
                {
                    var creation = CreationHistory(c);
                    if (c.Dispatched && !c.Completed && session.Workspace.Creations.Last(x => x.LocalId == c.LocalId).Id == c.Id)
                    {
                        var resolve = new Button { Content = c.Verified is null ? "作成の不確定結果を解決" : "既知Issueの設定を再比較", IsEnabled = Workspace.CanRead };
                        AutomationProperties.SetAutomationId(resolve, "ResolveCreation-" + c.Id);
                        AutomationProperties.SetName(resolve, $"{resolve.Content}: {c.Repository.Name} / {c.Title}");
                        resolve.Click += (_, _) => { resolutionBatch = b.Id; resolutionOperation = c.Id; setupReview = c.Verified is not null; dialog.Hide(); };
                        creation.Children.Add(resolve);
                    }
                    entry.Children.Add(creation);
                }
                entry.Children.Add(ApplyDetails("実行とProjectの識別情報", ApplyText($"実行 {b.Id}\nProject {b.Project.NodeId}\nアカウント ID {b.Project.Scope.ViewerId}"), "ApplyBatchIdentity-" + b.Id));
                historyContent.Items.Add(entry);
                }
                if (session.Workspace.Journal.Count == 0) historyContent.Items.Add(ApplyText("実行履歴なし"));
                historyContent.Visibility = historyContent.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            allHistory.Checked += (_, _) => Populate(); allHistory.Unchecked += (_, _) => Populate(); Populate();
            await ShowDialogAsync(dialog);
        }
        finally { applyDialog = false; if (IsLoaded) Update(); }
        if (!IsCurrent(owner, expected) || !ReferenceEquals(owner.Drafts, session)) return;
        if (resumeBatch is not null)
        {
            var generation = applyViewGeneration;
            await owner.ResumeApplyAsync(resumeBatch);
            if (IsCurrent(owner, expected) && generation == applyViewGeneration) QueueApplyOutcome(resumeBatch);
        }
        else if (withdrawBatch is not null) await owner.SupersedeApplyAsync(withdrawBatch);
        else if (resolutionBatch is not null && resolutionOperation is not null)
        { if (setupReview) await ReviewCreationSetupAsync(resolutionBatch, resolutionOperation); else await ResolveCreationAsync(resolutionBatch, resolutionOperation); }
    }
    private static Button ApplyHelp(string text, string id)
    {
        var help = new Button { Content = new FontIcon { Glyph = "\uE946" }, Padding = new(6), MinWidth = 28, MinHeight = 28,
            Flyout = new Flyout { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 } } };
        AutomationProperties.SetAutomationId(help, id); AutomationProperties.SetName(help, "反映結果の操作について");
        ToolTipService.SetToolTip(help, text);
        return help;
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
        return name ?? value + "（選択肢名は未確認）";
    }
    private StackPanel ApplyChange(ApplyOperation operation, bool includeHistory)
    {
        var panel = ApplyPanel(4);
        var identity = ApplyIdentity(operation);
        panel.Children.Add(ApplyText($"{identity} / {(operation.Key.Kind == "Title" ? "タイトル（Issue共通）" : operation.FieldName)}{HiddenColumnNote(operation.Key.FieldId, operation.Key.ProjectId)}", emphasis: true));
        var values = ApplyPanel(4);
        var comparison = new Grid { ColumnSpacing = 16 };
        comparison.ColumnDefinitions.Add(new()); comparison.ColumnDefinitions.Add(new());
        comparison.Children.Add(ApplyText($"GitHub: {ApplyValue(operation, operation.Expected)}"));
        var intended = ApplyText($"反映する値: {(operation.Intended.Clear ? "明示的にクリア" : ApplyValue(operation, operation.Intended.Value))}", emphasis: true);
        Grid.SetColumn(intended, 1); comparison.Children.Add(intended); values.Children.Add(comparison);
        values.Children.Add(ApplyText($"所有: {(operation.Key.Kind == "Title" ? "Issue共通のタイトル" : "このProjectの項目フィールド")} / フィールド {operation.Key.FieldId ?? "Issue title"}"));
        if (includeHistory)
        {
            var uncertain = ApplyResultsPresentation.Kind(operation) == ApplyAttentionKind.Uncertain;
            panel.Children.Add(ApplyText($"{(uncertain ? "結果の確認が必要" : ApplyStateText(operation.State))} / {operation.Reason}"));
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
        panel.Children.Add(ApplyText($"宛先 {creation.Repository.Name}"));
        foreach (var select in creation.Selects)
            panel.Children.Add(ApplyText($"{select.FieldName}{HiddenColumnNote(select.FieldId)}: {SelectIntentText(select)}"));
        if (creation.Selects.Length == 0) panel.Children.Add(ApplyText("Projectフィールドの指定なし"));
        panel.Children.Add(ApplyDetails("ローカル行と作成試行", ApplyText($"Repository ID {creation.Repository.Id}\nローカル行 {creation.LocalId}\n作成試行 {creation.Id}"), "CreationReviewIdentity-" + creation.Id));
        return panel;
    }
    private static string SelectIntentText(LocalSelect select) => select.Intent switch
    {
        "Set" => $"{select.OptionName ?? "選択肢名は未確認"} に設定",
        "ExplicitClear" => "空にする",
        _ => "未指定（送信しません）"
    };
    private StackPanel CreationHistory(CreationOperation creation)
    {
        var panel = ApplyPanel(4);
        panel.Children.Add(ApplyText($"{creation.Repository.Name} / {creation.Title}", emphasis: true));
        var state = creation.Completed ? "完了" : creation.Verified is not null ? "既知IssueのProject設定を確認" : creation.Dispatched ? "作成結果が不確定" : "未送信";
        panel.Children.Add(ApplyText($"{state} / {creation.Reason}"));
        panel.Children.Add(ApplyText($"Issue確認: {(creation.Verified is null ? "未確認" : "確認済み")} / Project所属: {(creation.ItemId is null ? "未確認" : "確認済み")} / フィールド: {(creation.Fields is null ? "未観測" : $"確認済み {creation.Fields.Count(f => f.State == ApplyState.Succeeded)} / {creation.Fields.Length}")}"));
        if (creation.Verified is { } issue) panel.Children.Add(ApplyText("検証済みIssue: " + issue.Url));
        if (creation.EarlierUncertain)
            panel.Children.Add(ApplyMessage("以前の試行に不確定な結果があります", "現在の結果から、以前の試行でIssueが作成されなかったとは判断できません。", InfoBarSeverity.Warning));
        var details = ApplyPanel(4);
        details.Children.Add(ApplyText($"ローカル行 {creation.LocalId}\n試行 {creation.Id}\nRepository ID {creation.Repository.Id}\n受信ID: {creation.ReceivedId ?? creation.Received?.Id ?? "未確認"}\nIssue ID: {creation.Verified?.Id ?? "未確認"}\nProject項目: {creation.ItemId ?? "未確認"}\n以前の試行不確定: {creation.EarlierUncertain}"));
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
        ApplyState.Pending => "未送信", ApplyState.Running => "実行中（結果未確認）", ApplyState.Succeeded => "反映済み・読み戻し確認済み",
        ApplyState.Failed => "失敗", ApplyState.Unknown => "結果が不確定", ApplyState.Waiting => "待機中",
        ApplyState.Blocked => "保留", ApplyState.Cancelled => "未送信（キャンセル）", ApplyState.Superseded => "承認を撤回済み", _ => state.ToString()
    };
    private async Task ReviewCreationSetupAsync(string batchId, string id)
    {
        var owner = Workspace; var expected = lifetime;
        var generation = applyViewGeneration;
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
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && IsCurrent(owner, expected) && generation == applyViewGeneration)
        {
            await owner.ConfirmCreationSetupAsync(review);
            if (IsCurrent(owner, expected) && generation == applyViewGeneration) QueueApplyOutcome(batchId);
        }
    }
    private async Task ResolveCreationAsync(string batchId, string id)
    {
        var owner = Workspace; var expected = lifetime;
        var generation = applyViewGeneration;
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
            if (await ShowDialogAsync(confirm) == ContentDialogResult.Primary && IsCurrent(owner, expected) && generation == applyViewGeneration)
            {
                await owner.ConfirmCreationRetryAsync(review);
                if (IsCurrent(owner, expected) && generation == applyViewGeneration) QueueApplyOutcome(review.Batch.Id);
            }
        }
    }
    private sealed record ApplyTarget(string Id, string Description)
    {
        public override string ToString() => $"{Description} / {Id}";
    }
}

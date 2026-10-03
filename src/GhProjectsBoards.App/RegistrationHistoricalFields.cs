using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private void AddHistoricalHandling(StackPanel panel, HistoricalFieldTarget target, EditingWorkspace work,
        List<Button> remoteActions, bool readOnly, Action<HistoricalFieldTarget> inspect, Action<string> continueChange,
        bool includeDetails = true, bool includeActions = true)
    {
        if (HistoricalFieldHandling.Resolve(work.Journal, target) is not { } source) return;
        var decisions = work.HistoricalDispositions.Where(d => d.Target == target).ToArray();
        var settled = HistoricalFieldHandling.IsSettled(work.Journal, work.HistoricalDispositions, target);
        var section = ApplyPanel(4);
        AutomationProperties.SetAutomationId(section, "HistoricalHandling-" + target.OperationId + (includeActions ? "" : "-Evidence"));
        if (target.CreationId is not null) section.Children.Add(ApplyText($"以前の {source.Operation.FieldName} への送信：結果不明", true));
        if (decisions.LastOrDefault() is { } last)
        {
            section.Children.Add(ApplyText(last.Kind switch {
                HistoricalFieldDecisionKind.AcceptCurrent => "現在の状態を受け入れ、この処理への対応は完了しました。",
                HistoricalFieldDecisionKind.FieldNotApplicable => "フィールドが存在しないことを確認し、この処理への対応は完了しました。",
                _ => settled ? "新しい変更の反映を確認し、この処理への対応は完了しました。" : "変更を続ける判断を保存済みです。新しい反映はまだ完了していません。"
            }, true));
            section.Children.Add(ApplyText("元の送信結果は不明のまま保持しています。"));
            if (includeDetails)
            {
                section.Children.Add(ApplyText($"{last.Observation.At.LocalDateTime:g} 確認時の状態：{HistoricalValue(last.Observation)}"));
                var details = ApplyPanel(4);
                foreach (var decision in decisions)
                    details.Children.Add(ApplyText($"{decision.At.LocalDateTime:g} / {HistoricalDecisionText(decision.Kind)} / {HistoricalValue(decision.Observation)}"
                        + (decision.FollowUp is { } link ? $"\n新しい実行 {link.BatchId} / 操作 {link.OperationId}" : "")));
                section.Children.Add(ApplyDetails("その後の確認と判断", details, "HistoricalDecisionDetails-" + target.OperationId));
            }
            if (includeActions && !settled && last.Kind == HistoricalFieldDecisionKind.ContinueChange && last.FollowUp is null)
            {
                var next = new Button { Content = "現在の変更をレビュー", IsEnabled = Workspace.CanRead && !readOnly };
                AutomationProperties.SetAutomationId(next, "HistoricalContinue-" + target.OperationId);
                next.Click += (_, _) => continueChange(last.Id);
                remoteActions.Add(next); section.Children.Add(next);
            }
        }
        if (includeActions && !settled)
        {
            var check = new Button { Content = "現在の状態を確認", IsEnabled = Workspace.CanRead && !readOnly };
            AutomationProperties.SetAutomationId(check, "HistoricalInspect-" + target.OperationId);
            AutomationProperties.SetName(check, $"現在の状態を確認：{ApplyIdentity(source.Operation)} / {source.Operation.FieldName}");
            check.Click += (_, _) => inspect(target);
            remoteActions.Add(check); section.Children.Add(check);
        }
        panel.Children.Add(section);
    }

    private static string HistoricalDecisionText(HistoricalFieldDecisionKind kind) => kind switch {
        HistoricalFieldDecisionKind.AcceptCurrent => "現在の状態で完了",
        HistoricalFieldDecisionKind.FieldNotApplicable => "対象外として完了",
        _ => "変更を続ける"
    };

    private static string HistoricalValue(HistoricalFieldObservation observation)
    {
        if (observation.Kind == HistoricalFieldEvidenceKind.ProjectFieldAbsent) return "このProjectにフィールドはありません";
        var current = observation.Current!;
        if (current.Value is null) return "空";
        if (observation.Key.Kind == "Dependency") return "依存関係あり";
        return observation.Key.Kind == "Select"
            ? current.Options.SingleOrDefault(option => option.Id == current.Value)?.Name ?? current.Value
            : current.Value;
    }

    private async Task ReviewHistoricalFieldAsync(HistoricalFieldTarget target)
    {
        if (applyDialog || Workspace.Drafts is not { } session || !CanRefreshEditors()) return;
        var owner = Workspace; var expected = lifetime; var profile = owner.Profile; var project = owner.Selected?.Snapshot.Id;
        bool Current() => IsCurrent(owner, expected) && owner.Profile == profile
            && owner.Selected?.Snapshot.Id == project && ReferenceEquals(owner.Drafts, session);
        string? continuation = null;
        string? feedback = null;
        HistoricalFieldDecision? savedDecision = null;
        applyDialog = true; ApplyHistory.IsEnabled = false;
        try
        {
            await owner.PrepareHistoricalFieldAsync(target);
            while (Current())
            {
                var review = owner.HistoricalFieldReview;
                var content = ApplyPanel(8);
                if (savedDecision is not null)
                {
                    content.Children.Add(ApplyText("判断は保存済みです。追加のローカル入力を保存できませんでした。", true));
                    content.Children.Add(ApplyText(feedback ?? owner.Status));
                    var retrySave = new ContentDialog { XamlRoot = XamlRoot, Title = "ローカル保存を再試行", Content = content,
                        PrimaryButtonText = "保存を再試行", CloseButtonText = "履歴へ戻る", DefaultButton = ContentDialogButton.Close };
                    AutomationProperties.SetAutomationId(retrySave, "HistoricalSaveRetryDialog");
                    if (await ShowDialogAsync(retrySave) != ContentDialogResult.Primary || !Current()) break;
                    if (!await owner.FlushDraftsAsync()) { feedback = owner.Status; continue; }
                    if (savedDecision.Kind == HistoricalFieldDecisionKind.ContinueChange) continuation = savedDecision.Id;
                    break;
                }
                if (review is null)
                {
                    content.Children.Add(ApplyText(owner.Status));
                    content.Children.Add(ApplyText("元の送信履歴と編集内容は保持しています。"));
                    var failed = new ContentDialog { XamlRoot = XamlRoot, Title = "現在の状態を確認できません", Content = content,
                        PrimaryButtonText = "再確認", IsPrimaryButtonEnabled = owner.CanRead,
                        CloseButtonText = "履歴へ戻る", DefaultButton = ContentDialogButton.Close };
                    AutomationProperties.SetAutomationId(failed, "HistoricalReadFailureDialog");
                    if (await ShowDialogAsync(failed) != ContentDialogResult.Primary || !Current()) break;
                    await owner.PrepareHistoricalFieldAsync(target); continue;
                }
                var operation = review.Operation;
                var absent = review.Observation.Kind == HistoricalFieldEvidenceKind.ProjectFieldAbsent;
                var identity = ApplyPanel(4);
                identity.Children.Add(ApplyText($"{review.Batch.ProjectName} / {ApplyIdentity(operation)}", true));
                identity.Children.Add(ApplyText(operation.Key.Kind == "Title" ? "タイトル（Issue共通）" : operation.FieldName));
                content.Children.Add(identity);
                if (feedback is not null)
                {
                    var message = ApplyText(feedback);
                    AutomationProperties.SetAutomationId(message, "HistoricalReviewFeedback");
                    content.Children.Add(message);
                }
                var facts = ApplyPanel(8);
                facts.Children.Add(ApplyText("当時の送信結果：不明（この確認では書き換えません）"));
                facts.Children.Add(ApplyText($"当時の反映予定値：{(operation.Intended.Clear ? "空にする" : HistoricalStoredValue(operation, operation.Intended.Value))}"));
                facts.Children.Add(ApplyText($"今回確認したGitHubの値：{HistoricalValue(review.Observation)}", true));
                facts.Children.Add(ApplyText($"確認日時 {review.Observation.At.LocalDateTime:g}"));
                if (review.LocalField is { } local)
                {
                    facts.Children.Add(ApplyText($"ローカルの確定値：{ApplyValue(operation, local.Change is { } change ? change.Value : local.Baseline)}"));
                    if (local.Buffer is not null) facts.Children.Add(ApplyText($"入力途中：{local.Buffer}（反映しません）"));
                }
                facts.Children.Add(ApplyText(absent
                    ? "対象のIssueとProjectを確認し、このフィールドが存在しないことを確認しました。"
                    : "「現在の状態で完了」は、この過去の処理への対応を終えます。ローカルの変更・入力途中・Undoは保持します。"));
                if (!absent) facts.Children.Add(ApplyText("変更を続ける場合は、現在の確定済み変更を新しくレビューします。ここではGitHubに送信しません。"));
                var registration = owner.Registrations.SingleOrDefault(r => r.Snapshot.Id == review.Batch.Project);
                facts.Children.Add(ApplyDetails("対象の識別情報", ApplyText($"{registration?.Snapshot.Url ?? review.Batch.Project.NodeId}\nIssue {operation.IssueId}\n項目 {operation.ItemId}\nフィールド {operation.Key.FieldId ?? "タイトル"}"), "HistoricalTargetIdentity"));
                content.Children.Add(new ScrollViewer { Content = facts, MaxHeight = Math.Max(100, Math.Min(340, XamlRoot.Size.Height - 360)),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "過去の送信への対応", Content = content,
                    PrimaryButtonText = absent ? "対象外として完了" : "現在の状態で完了",
                    SecondaryButtonText = absent ? "" : "変更を続ける", CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
                AutomationProperties.SetAutomationId(dialog, "HistoricalFieldReviewDialog");
                var recheck = new Button { Content = "再確認", HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetAutomationId(recheck, "HistoricalReviewCheckAgain");
                bool checkAgain = false;
                recheck.Click += (_, _) => { checkAgain = true; dialog.Hide(); };
                content.Children.Add(recheck);
                var choice = await ShowDialogAsync(dialog);
                if (checkAgain && Current()) { await owner.PrepareHistoricalFieldAsync(target); feedback = null; continue; }
                if (!Current() || choice == ContentDialogResult.None) break;
                var decision = choice == ContentDialogResult.Secondary ? HistoricalFieldDecisionKind.ContinueChange
                    : absent ? HistoricalFieldDecisionKind.FieldNotApplicable : HistoricalFieldDecisionKind.AcceptCurrent;
                var result = await owner.ConfirmHistoricalFieldAsync(review, decision);
                if (!Current()) break;
                if (result.Decision is not null)
                {
                    if (!result.AllLocalWorkSaved) { savedDecision = result.Decision; feedback = result.Problem; continue; }
                    if (decision == HistoricalFieldDecisionKind.ContinueChange) continuation = result.Decision.Id;
                    break;
                }
                feedback = result.Problem ?? owner.Status;
            }
        }
        finally { applyDialog = false; if (IsLoaded) Update(); }
        if (!Current()) return;
        if (continuation is not null) await ContinueHistoricalFieldAsync(continuation);
        else ShowApplyHistory(this, new RoutedEventArgs());
    }

    private async Task ContinueHistoricalFieldAsync(string decisionId)
    {
        if (applyDialog || Workspace.Drafts is not { } session || !CanRefreshEditors()) return;
        var owner = Workspace; var expected = lifetime;
        var decision = session.Workspace.HistoricalDispositions.SingleOrDefault(d => d.Id == decisionId);
        if (decision is null || decision.Kind != HistoricalFieldDecisionKind.ContinueChange || decision.FollowUp is not null) return;
        if (owner.Selected?.Snapshot.Id != decision.Observation.Project) await owner.SelectAsync(decision.Observation.Project);
        if (!IsCurrent(owner, expected) || owner.Drafts != session || owner.Selected?.Snapshot.Id != decision.Observation.Project) return;
        await ReviewApplyAsync(decisionId);
    }
}

using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private long applyViewGeneration;
    private DispatcherTimer? outcomeTimer;
    private (string Batch, ScopedId Project, DraftSession Session, long Generation)? pendingOutcome;

    private void CancelApplyOutcome()
    {
        applyViewGeneration++; pendingOutcome = null;
        outcomeTimer?.Stop(); outcomeTimer = null;
    }

    private void QueueApplyOutcome(string batchId, bool newlyApproved = false)
    {
        if (Workspace.Drafts is not { } session || session.Workspace.Journal.SingleOrDefault(b => b.Id == batchId) is not { } batch
            || Workspace.Selected?.Snapshot.Id != batch.Project) return;
        // Establish the destination before the deferred notification; later browsing wins over that notification.
        if (newlyApproved)
        {
            if (historyPlace is not { } place || place.Owner != Workspace || place.Session != session || place.Project != batch.Project.NodeId)
                historyPlace = new(Workspace, session, batch.Project.NodeId);
            var target = ApplyResultsPresentation.OutcomeTarget(batch, ApplyResultsPresentation.Attention(session.Workspace));
            historyPlace!.Selected = historyPlace.Anchor = target is null ? null : new(batch.Id, target.CreationId, target.OperationId);
            historyPlace.EvidenceOffset = 0;
        }
        pendingOutcome = (batchId, batch.Project, session, applyViewGeneration);
        outcomeTimer?.Stop();
        outcomeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        outcomeTimer.Tick += PresentApplyOutcome;
        outcomeTimer.Start();
    }

    private async void PresentApplyOutcome(object? sender, object e)
    {
        if (pendingOutcome is not { } outcome) return;
        if (!IsLoaded || outcome.Generation != applyViewGeneration || !ReferenceEquals(Workspace.Drafts, outcome.Session)
            || Workspace.Selected?.Snapshot.Id != outcome.Project) { CancelApplyOutcome(); return; }
        if (Workspace.IsBusy || applyDialog || activeDialog is not null || !CanRefreshEditors()) return;
        outcomeTimer?.Stop(); outcomeTimer = null; pendingOutcome = null;
        var attention = ApplyResultsPresentation.Attention(outcome.Session.Workspace).Where(a => a.BatchId == outcome.Batch).ToArray();
        var batch = outcome.Session.Workspace.Journal.Single(b => b.Id == outcome.Batch);
        var result = ApplyResultsPresentation.ExecutionOutcome(batch, attention);
        var peer = FrameworkElementAutomationPeer.FromElement(Status) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Status);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            attention.Length == 0 ? ApplyResultsPresentation.CompletionAnnouncement(batch) : result.Title + "。" + result.Summary, "ApplyOutcome");
        if (attention.Length == 0) return;
        var content = ApplyPanel();
        content.Children.Add(ApplyText(result.Summary, emphasis: true));
        var rows = ApplyPanel(12);
        var groups = batch.Operations.GroupBy(operation => operation.IssueId)
            .OrderByDescending(group => group.Any(operation => attention.Any(entry => entry.OperationId == operation.Id))).ToArray();
        foreach (var group in groups.Take(3)) rows.Children.Add(ExecutionTarget(batch, group.ToArray()));
        if (groups.Length > 3)
        {
            var remaining = ApplyPanel(12);
            foreach (var group in groups.Skip(3)) remaining.Children.Add(ExecutionTarget(batch, group.ToArray()));
            rows.Children.Add(ApplyDetails($"ほかの結果（{groups.Length - 3}件のIssue）", remaining, "ApplyOutcomeOtherTargets"));
        }
        foreach (var creation in batch.Creations ?? []) rows.Children.Add(CreationHistory(batch, creation, includeDetails: false));
        var reviewRemaining = CanReviewRemaining(batch, outcome.Session.Workspace);
        rows.Children.Add(ApplyText(reviewRemaining
            ? "残る変更は「未反映の変更を確認…」で最新状態を確認し、新しいレビューで承認してください。"
            : "未送信・未完了の処理は「反映結果・履歴」で確認できます。結果が不明な送信は自動で繰り返しません。"));
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = Math.Max(140, XamlRoot.Size.Height - 310),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = result.Title, Content = content,
            CloseButtonText = "編集に戻る", DefaultButton = ContentDialogButton.Close, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (reviewRemaining)
        { dialog.SecondaryButtonText = "未反映の変更を確認…"; dialog.IsSecondaryButtonEnabled = Workspace.CanRestartApplyReview; }
        AutomationProperties.SetAutomationId(dialog, "ApplyOutcomeWarning");
        var owner = Workspace; var expected = lifetime;
        ContentDialogResult choice;
        applyDialog = true;
        try { Update(); choice = await ShowDialogAsync(dialog); }
        finally { applyDialog = false; if (IsLoaded) Update(); }
        if (!IsCurrent(owner, expected) || outcome.Generation != applyViewGeneration || Workspace.Selected?.Snapshot.Id != outcome.Project
            || !ReferenceEquals(Workspace.Drafts, outcome.Session)) return;
        if (choice == ContentDialogResult.Secondary && Workspace.CanRestartApplyReview && CanReviewRemaining(batch, outcome.Session.Workspace))
        { await ReviewApplyAsync(restartRemaining: true); return; }
        EditorHost.Children.OfType<EditingGrid>().SingleOrDefault()?.GoToApplyProblem(attention[0]);
    }

    private bool CanReviewRemaining(ApplyBatch batch, EditingWorkspace work) => Workspace.Selected?.Snapshot.Id == batch.Project
        && ApplyResultsPresentation.RequiresFreshReview(batch)
        && work.Journal.Where(candidate => candidate.Operations.Any(operation => operation.State is not (ApplyState.Succeeded or ApplyState.Superseded)))
            .All(candidate => candidate.Project == batch.Project && ApplyResultsPresentation.RequiresFreshReview(candidate));

    private IssueReadModel? ExecutionIssue(ApplyBatch batch, ApplyOperation operation) => Workspace.Registrations
        .Where(registration => registration.Snapshot.Id.Scope == batch.Project.Scope)
        .Select(registration => registration.Snapshot.Issues.GetValueOrDefault(new(batch.Project.Scope, operation.IssueId)))
        .FirstOrDefault(issue => issue is not null);

    private StackPanel ExecutionTarget(ApplyBatch batch, ApplyOperation[] operations)
    {
        var panel = ApplyPanel(4);
        var first = operations[0];
        panel.Children.Add(ApplyText(ApplyIdentity(first), true));
        if (operations.Any(operation => !ApplyResultsPresentation.IsVerified(batch, operation)))
            panel.Children.Add(ApplyText("比較に使用したGitHubの値 → 今回の反映予定値"));
        foreach (var operation in operations.OrderBy(operation => ApplyResultsPresentation.IsVerified(batch, operation)))
        {
            if (ApplyResultsPresentation.IsVerified(batch, operation))
                panel.Children.Add(ApplyText($"{ApplyResultsPresentation.FieldName(operation)} → {ApplyResultsPresentation.VerifiedValue(operation)}：反映を確認しました。", true));
            else
            {
                panel.Children.Add(ApplyText($"{ApplyResultsPresentation.FieldName(operation)}：{HistoricalStoredValue(operation, operation.Expected)} → "
                    + (operation.Intended.Clear ? "空にする" : HistoricalStoredValue(operation, operation.Intended.Value)), true));
                panel.Children.Add(ApplyText(operation.State == ApplyState.Failed && operation.Reason == "PermissionDenied"
                    ? ApplyResultsPresentation.OutcomeReason(operation)
                    : $"{ApplyResultsPresentation.FieldName(operation)}：{ApplyStateText(operation.State)}。{ApplyResultsPresentation.OutcomeReason(operation)}"));
            }
        }
        if (operations.Any(operation => operation.State == ApplyState.Failed && operation.Reason == "PermissionDenied"
            && !ApplyJournal.HasUnresolvedDispatch(operation))) AddPermissionTarget(panel, batch, first);
        return panel;
    }

    private void AddPermissionTarget(StackPanel panel, ApplyBatch batch, ApplyOperation operation)
    {
        var registration = Workspace.Registrations.FirstOrDefault(registration => registration.Snapshot.Id.Scope == batch.Project.Scope);
        var login = registration is null ? "" : $"（保存済み名：{registration.ViewerLogin}）";
        panel.Children.Add(ApplyText($"送信時のアカウント：{batch.Project.Scope.Host} / ID {batch.Project.Scope.ViewerId}{login}"));
        var url = ExecutionIssue(batch, operation)?.Url;
        panel.Children.Add(ApplyText(url is null ? "対象IssueのURLは保存済み情報で確認できません。" : "対象Issue：" + url));
        panel.Children.Add(ApplyText("対象へのアクセス権・認証状態を確認してから、残る変更を再確認してください。"));
    }
}

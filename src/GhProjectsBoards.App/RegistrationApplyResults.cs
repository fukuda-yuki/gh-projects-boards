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

    private void QueueApplyOutcome(string batchId)
    {
        if (Workspace.Drafts is not { } session || session.Workspace.Journal.SingleOrDefault(b => b.Id == batchId) is not { } batch
            || Workspace.Selected?.Snapshot.Id != batch.Project) return;
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
        var attention = ApplyResultsPresentation.Attention(outcome.Session.Workspace.Journal).Where(a => a.BatchId == outcome.Batch).ToArray();
        var peer = FrameworkElementAutomationPeer.FromElement(Status) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Status);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            attention.Length == 0 ? "GitHubへの反映が完了しました。" : ApplyResultsPresentation.Summary(attention), "ApplyOutcome");
        if (attention.Length == 0) return;
        var content = ApplyPanel();
        content.Children.Add(ApplyText(ApplyResultsPresentation.Summary(attention), emphasis: true));
        content.Children.Add(ApplyText("編集表の印を確認してください。「次の問題へ」で順に移動できます。"));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "反映が完了していません", Content = content,
            CloseButtonText = "編集に戻る", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "ApplyOutcomeWarning");
        var owner = Workspace; var expected = lifetime;
        applyDialog = true;
        try { Update(); await ShowDialogAsync(dialog); }
        finally { applyDialog = false; if (IsLoaded) Update(); }
        if (!IsCurrent(owner, expected) || outcome.Generation != applyViewGeneration || Workspace.Selected?.Snapshot.Id != outcome.Project
            || !ReferenceEquals(Workspace.Drafts, outcome.Session)) return;
        EditorHost.Children.OfType<EditingGrid>().SingleOrDefault()?.GoToApplyProblem(attention[0]);
    }
}

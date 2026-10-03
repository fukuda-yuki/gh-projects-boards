using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private void ShowProblemHistory(ApplyAttention? attention)
    {
        if (attention is { } target && Workspace.Drafts is { } session && Workspace.Selected?.Snapshot.Id == target.Project
            && session.Workspace.Journal.SingleOrDefault(batch => batch.Id == target.BatchId) is { } batch)
        {
            if (historyPlace is not { } place || place.Owner != Workspace || place.Session != session || place.Project != target.Project.NodeId)
                historyPlace = new(Workspace, session, target.Project.NodeId);
            var record = ApplyResultsPresentation.OutcomeTarget(batch, [target]);
            historyPlace!.Selected = historyPlace.Anchor = record is null ? null : new(batch.Id, record.CreationId, record.OperationId);
            historyPlace.EvidenceOffset = 0;
        }
        ShowApplyHistory(this, new RoutedEventArgs());
    }

    private void ShowCreationComparisonFailure(RegistrationWorkspace owner, DraftSession? session, string batchId, string id)
    {
        if (session is null || owner.Selected?.Snapshot.Id is not { } project
            || session.Workspace.Journal.SingleOrDefault(batch => batch.Id == batchId && batch.Project == project) is not { } batch
            || batch.Creations?.SingleOrDefault(creation => creation.Id == id) is not { Verified: not null, Completed: false }) return;
        if (historyPlace is not { } place || place.Owner != owner || place.Session != session || place.Project != project.NodeId)
            historyPlace = new(owner, session, project.NodeId);
        var key = new HistoryKey(batchId, id, null);
        historyPlace!.Selected = historyPlace.Anchor = key;
        historyPlace.SetupCheckFailure = (key, owner.Status);
        historyPlace.EvidenceOffset = 0;
        ShowApplyHistory(this, new RoutedEventArgs());
    }

    private void AddCreationComparisonContext(StackPanel panel, ApplyBatch batch, CreationOperation creation, HistoryPlace place)
    {
        var stages = ApplyResultsPresentation.CreationStages(batch, creation, Workspace.Drafts?.Workspace.Journal);
        panel.Children.Add(ApplyText(stages.Membership));
        var unfinished = (creation.Fields ?? []).Where(field => ApplyResultsPresentation.Kind(field) is not null).ToArray();
        // Keep the immediate decision in the fixed context; the full set and its
        // evidence remain in the existing scroller for large multi-field setups.
        foreach (var field in unfinished.Take(1))
        {
            var savedOption = (creation.SetupIntents ?? creation.Selects.ToArray()).SingleOrDefault(intent =>
                intent.FieldId == field.Key.FieldId && intent.OptionId == field.Intended.Value && intent.ExplicitClear == field.Intended.Clear);
            var value = field.Intended.Clear ? "空にする" : savedOption?.OptionName
                ?? ApplyResultsPresentation.HistoricalValue(field, field.Intended.Value, batch.Project);
            panel.Children.Add(ApplyText($"{field.FieldName} → {value}：{ApplyStateText(field.State)}", true));
            if (field.Verification is { Reason: null, Availability: ValueAvailability.Present or ValueAvailability.Empty } observed)
                panel.Children.Add(ApplyText($"前回確認した値：{ApplyResultsPresentation.VerifiedValue(field)} / {observed.At.LocalDateTime:g}"));
        }
        if (unfinished.Length > 1) panel.Children.Add(ApplyText($"ほか {unfinished.Length - 1} 件の設定は下の詳細で確認できます。"));
        var failure = place.SetupCheckFailure is { } check && check.Target == new HistoryKey(batch.Id, creation.Id, null)
            ? check.Reason : null;
        if (failure is null && !creation.Reason.Contains("再照合に失敗", StringComparison.Ordinal)) return;
        var text = ApplyText("今回の比較：未確認。" +
            (unfinished.Length > 0 && unfinished.All(field => field.Attempts.Length == 0) ? "設定は送信していません。" : "") +
            "設定内容を再確認してください。");
        AutomationProperties.SetAutomationId(text, "CreationComparisonFailure-" + creation.Id);
        AutomationProperties.SetLiveSetting(text, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(text);
    }
}

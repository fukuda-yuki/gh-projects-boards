using GhProjectsBoards.App;

namespace GhProjectsBoards.Core.Projects;

internal sealed partial class RegistrationWorkspace
{
    public HistoricalFieldReview? HistoricalFieldReview { get; private set; }
    public Task PrepareHistoricalFollowUpAsync(string decisionId, IReadOnlySet<string> items,
        RowTargetSelection? viewSelection = null) => PrepareApplyCoreAsync(items, viewSelection, restart: false, historicalDecisionId: decisionId);
    public Task PrepareHistoricalFieldAsync(HistoricalFieldTarget target) => RunAsync(async token =>
    {
        HistoricalFieldReview = null;
        RequireConnection();
        if (Drafts is not { } session || HistoricalFieldHandling.Resolve(session.Workspace.Journal, target) is not { } source
            || HistoricalFieldHandling.IsSettled(session.Workspace.Journal, session.Workspace.HistoricalDispositions, target))
        { Status = "この履歴はフィールドの確認対象ではありません。"; return; }
        var connection = ConnectionRevision; var request = generation;
        if (!await session.FlushAsync()) { Status = session.Status; return; }
        var revision = session.Workspace.Revision;
        var result = await new ApplyRemote(service!, context!).ObserveHistoricalFieldAsync(source.Batch, source.Operation, token);
        if (token.IsCancellationRequested || connection != ConnectionRevision || request != generation || Drafts != session || !CanRead) return;
        if (result.Observation is null) { Status = "現在の状態を確認できません。" + ConnectionViewModel.FailureText(result.Result.Failure); return; }
        if (revision != session.Workspace.Revision) { Status = "確認中にローカルの作業が変わりました。再確認してください。"; return; }
        HistoricalFieldHandling.ValidateObservation(source, result.Observation);
        HistoricalFieldReview = MakeHistoricalReview(target, source, result.Observation, session, connection);
        Status = "現在の観測と以前の送信履歴を確認してください。GitHubには書き込みません。";
    });

    private static HistoricalFieldReview MakeHistoricalReview(HistoricalFieldTarget target, HistoricalFieldSource source,
        HistoricalFieldObservation observation, DraftSession session, int connection) =>
        new(target, source.Batch, source.Operation, observation, session.Workspace.Revision, connection,
            session.Workspace.Snapshot().Fields.SingleOrDefault(f => f.Key == source.Operation.Key));

    public async Task<HistoricalFieldDecisionResult> ConfirmHistoricalFieldAsync(HistoricalFieldReview review,
        HistoricalFieldDecisionKind decision)
    {
        HistoricalFieldDecisionResult outcome = new(null, false, "判断を保存できませんでした。現在の状態を再確認してください。", review);
        await RunAsync(async token =>
        {
            RequireConnection();
            if (Drafts is not { } session || !ReferenceEquals(review, HistoricalFieldReview)
                || review.ConnectionRevision != ConnectionRevision || review.Revision != session.Workspace.Revision
                || HistoricalFieldHandling.Resolve(session.Workspace.Journal, review.Target) is not { } source) return;
            var request = generation;
            bool Current() => !token.IsCancellationRequested && request == generation && CanRead && Drafts == session
                && review.ConnectionRevision == ConnectionRevision && ReferenceEquals(review, HistoricalFieldReview);
            var result = await new ApplyRemote(service!, context!).ObserveHistoricalFieldAsync(source.Batch, source.Operation, token);
            if (!Current()) return;
            if (result.Observation is null)
            { Status = "現在の状態を確認できません。" + ConnectionViewModel.FailureText(result.Result.Failure); outcome = outcome with { Problem = Status }; return; }
            HistoricalFieldHandling.ValidateObservation(source, result.Observation);
            if (review.Revision != session.Workspace.Revision || !HistoricalFieldHandling.SameEvidence(review.Observation, result.Observation))
            {
                HistoricalFieldReview = MakeHistoricalReview(review.Target, source, result.Observation, session, ConnectionRevision);
                Status = "確認後に状態が変わりました。新しい観測を確認して、もう一度判断してください。";
                outcome = new(null, false, Status, HistoricalFieldReview); return;
            }
            HistoricalFieldDecision? saved = null;
            var currentReview = review with { Observation = result.Observation };
            var allSaved = await session.CommitAsync(w => { saved = w.DecideHistoricalField(currentReview, decision); return w; },
                () => Current() && session.Workspace.Revision == review.Revision);
            var durable = saved is null ? null : session.Workspace.HistoricalDispositions.SingleOrDefault(d => d.Id == saved.Id && d.Revision <= session.DurableRevision);
            if (durable is not null && allSaved)
            {
                HistoricalFieldReview = null;
                Status = decision == HistoricalFieldDecisionKind.ContinueChange
                    ? "変更を続ける判断を保存しました。現在の確定済み変更を新しくレビューしてください。"
                    : "以前の処理への対応を完了しました。元の送信結果とローカル編集は保持しています。";
            }
            else Status = durable is null ? session.Status : "判断は保存済みですが、追加のローカル入力を保存できませんでした。保存を再試行してください。";
            outcome = new(durable, allSaved, allSaved ? null : Status, HistoricalFieldReview);
        });
        return outcome;
    }
}

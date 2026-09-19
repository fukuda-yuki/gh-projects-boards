using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed partial class RegistrationWorkspace
{
    public ApplyReview? ApplyReview { get; private set; }
    public FailureKind[] ApplyCheckFailures { get; private set; } = [];
    public bool ApplySelectionInvalidated { get; private set; }
    public bool ApplyNeedsConnectionRecovery => !CanRead || ApplyCheckFailures.Any(f => f is
        FailureKind.NotLoggedIn or FailureKind.AuthenticationExpired or FailureKind.IdentityChanged
        or FailureKind.MissingExecutable or FailureKind.StartFailed);
    public string? ExecutingBatchId { get; private set; }
    private int applyConnectionRevision;
    private ApplyBatch[] UnfinishedApplyBatches => Drafts?.Workspace.Journal.Where(b =>
        b.Operations.Any(o => o.State is not (ApplyState.Succeeded or ApplyState.Superseded))).ToArray() ?? [];
    public bool CanRestartApplyReview => CanRead && Selected is { } selected && UnfinishedApplyBatches is { Length: > 0 } batches
        && batches.All(b => b.Project == selected.Snapshot.Id && (b.Creations ?? []).Length == 0);
    public Task RestartApplyReviewAsync(IReadOnlySet<string> items, RowTargetSelection? viewSelection = null)
        => PrepareApplyCoreAsync(items, viewSelection, restart: true);
    public string? ApplyBlockReason(ApplyReview? review)
    {
        if (!CanRead) return "接続を確認してください。保存済みの変更は保持しています。";
        if (review is null || !ReferenceEquals(review, ApplyReview) || applyConnectionRevision != ConnectionRevision
            || Selected?.Snapshot.Id != review.Batch.Project) return "GitHubの最新状態の確認が必要です。";
        if (Drafts?.Workspace.Revision != review.Batch.ReviewedRevision) return "確認後に変更がありました。最新状態を再確認してください。";
        if (UnfinishedApplyBatches is { Length: > 0 } unfinished)
            return "前回の反映が未完了です。" + ApplyResultsPresentation.Summary(ApplyResultsPresentation.Attention(unfinished))
                + (CanRestartApplyReview ? "。"
                    : "。「反映結果・履歴」で対象のProjectと結果を確認してください。");
        if (review.SelectedRows == 0) return "反映する行を選択してください。";
        if (review.Blocked.Length > 0) return $"選択対象の{review.Problems.Select(p => p.RowId).Distinct().Count()}件に要対応の項目があります。解決するか、その行の選択を解除してください。";
        if (review.IssueCount == 0) return "選択した行に反映できる変更がありません。未確定入力は送信しません。";
        return null;
    }
    public Task PrepareApplyAsync(IReadOnlySet<string> items, RowTargetSelection? viewSelection = null)
        => PrepareApplyCoreAsync(items, viewSelection, restart: false);
    private Task PrepareApplyCoreAsync(IReadOnlySet<string> items, RowTargetSelection? viewSelection, bool restart) => RunAsync(async token =>
    {
        ApplyReview = null; ApplyCheckFailures = []; ApplySelectionInvalidated = false; RequireConnection();
        if (Selected is not { } selected || Drafts is not { } session) return;
        if (restart && !CanRestartApplyReview)
        { Status = "このProjectの既存フィールドの反映だけをやり直せます。「反映結果・履歴」で対象を確認してください。"; return; }
        var previousApprovals = restart ? UnfinishedApplyBatches.Select(b => b.Id).ToArray() : [];
        var connection = ConnectionRevision; var requestGeneration = generation;
        bool Current() => connection == ConnectionRevision && requestGeneration == generation && CanRead && !token.IsCancellationRequested;
        if (CanRefresh?.Invoke() == false) { Status = "IME変換中です。自然に確定・取消した後で反映内容の確認を開いてください。"; return; }
        if (!await session.FlushAsync()) { Status = session.Status; return; }
        Status = "GitHubの最新状態を確認中… 書き込みは開始していません。"; Changed?.Invoke();
        var result = await new ProjectReader(service!).ReadAsync(context!, selected.Snapshot.Id, token);
        if (!Current()) return;
        if (result.Outcome != ProjectReadOutcome.Complete || result.Project is null)
        { ApplyCheckFailures = result.Problems.Select(p => p.Failure).Distinct().ToArray();
            Status = $"最新状態を確認できません（{AttemptText(result.Outcome == ProjectReadOutcome.Partial ? RegistrationAttempt.Partial : RegistrationAttempt.Failed)}）。"
            + string.Join(" / ", result.Problems.Select(p => GhProjectsBoards.App.ConnectionViewModel.FailureText(p.Failure)))
            + $" 保存済み情報: {selected.RetrievedAt.LocalDateTime:g}。未確認のまま送信しません。"; return; }
        var fetched = selected with { Snapshot = result.Project, RetrievedAt = DateTimeOffset.UtcNow };
        if (!await session.CommitAsync(w =>
        {
            w.Reconcile(selected, fetched);
            // Withdraw only after a complete, same-context observation, in the same durable checkpoint.
            // Attempts and verified successes survive; this action never executes the old or new payload.
            foreach (var id in previousApprovals) w.SupersedeApply(id);
            w.SetRegistrations(registrations.Select(r => r == selected ? fetched : r)); return w;
        }, () => Selected == selected && Current()
            && (session.Workspace.HasCheckpoint || store.MatchesLegacy(selected.Snapshot.Id.Scope, registrations))))
        { Status = session.Status; return; }
        registrations[registrations.IndexOf(selected)] = fetched; Selected = fetched; AcceptedRefreshGeneration++;
        if (viewSelection is not null)
        {
            var viewProblem = session.Workspace.ViewProblem(fetched, session.Workspace.RowView(fetched));
            if (viewSelection.Project != fetched.Snapshot.Id || !items.SetEquals(viewSelection.Selected)
                || viewProblem is not null || viewSelection.NeedsConfirmation(session.Workspace.EvaluateRows(fetched).Select(r => r.ItemId)))
            { ApplySelectionInvalidated = true; Status = "再取得で表示対象が変わりました。反映する行を選び直してください。"; return; }
            if (!viewSelection.IncludeHidden && items.Any(id => !viewSelection.Visible.Contains(id)))
            { Status = "非表示行を候補に追加してから、反映する行を選び直してください。"; return; }
        }
        var destinations = new Dictionary<string, CreationRepository>();
        var remote = new ApplyRemote(service!, context!);
        foreach (var row in session.Workspace.LocalRows.Where(r => items.Contains(r.Id) && r.ProjectId == fetched.Snapshot.Id.NodeId))
        {
            var destination = await remote.ResolveCreationRepositoryAsync(fetched.Snapshot.Id, row.Repository, token);
            if (destination is not null) destinations[row.Id] = destination;
        }
        if (!Current() || Selected != fetched) return;
        ApplyReview = session.Workspace.ReviewApply(fetched, items, destinations);
        applyConnectionRevision = connection;
        Status = ApplyBlockReason(ApplyReview) ?? "反映する値と送信先を確認して「GitHubに反映」を押してください。未確定入力は送信しません。";
    });
    public Task ConfirmApplyAsync(ApplyReview review) => RunAsync(async token =>
    {
        RequireConnection();
        if (ApplyBlockReason(review) is { } reason) { Status = reason; return; }
        if (Drafts is not { } session || Selected?.Snapshot.Id != review.Batch.Project) return;
        if (!await session.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => CanRead && applyConnectionRevision == ConnectionRevision
            && ReferenceEquals(review, ApplyReview) && Selected?.Snapshot.Id == review.Batch.Project && !token.IsCancellationRequested))
        { Status = session.Status; return; }
        ApplyReview = null;
        await ExecuteApplyAsync(session, review.Batch.Id, token);
    });
    public Task ResumeApplyAsync(string batchId) => RunAsync(async token =>
    {
        RequireConnection();
        if (Drafts is not { } session) return;
        await ExecuteApplyAsync(session, batchId, token);
    });
    public async Task SupersedeApplyAsync(string batchId)
    {
        if (IsBusy || Drafts is not { } session) return;
        if (await session.CommitAsync(w => { w.SupersedeApply(batchId); return w; }, () => !IsBusy))
            Status = "以前の承認を撤回し、試行履歴を保持しました。改めて取得・競合解決・レビューしてください。";
        else Status = session.Status;
        Changed?.Invoke();
    }
    private async Task ExecuteApplyAsync(DraftSession session, string batchId, CancellationToken token)
    {
        var executor = new ApplyExecutor(draftStore, session, new(service!, context!), CanRefresh);
        executor.Progress += message => { Status = message; Changed?.Invoke(); };
        ExecutingBatchId = batchId; Changed?.Invoke();
        try { await executor.ExecuteAsync(batchId, token); }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }
        catch (IOException) { Status = "別プロセスが実行中、または保存先に問題があります。追加送信していません。"; return; }
        finally
        {
            ExecutingBatchId = null;
            var selectedId = Selected?.Snapshot.Id;
            registrations.RemoveAll(r => r.Snapshot.Id.Scope == session.Workspace.Scope);
            registrations.AddRange(session.Workspace.CheckpointRegistrations);
            Selected = registrations.SingleOrDefault(r => r.Snapshot.Id == selectedId);
        }
        var attention = ApplyResultsPresentation.Attention(session.Workspace.Journal).Where(a => a.BatchId == batchId).ToArray();
        Status = attention.Length == 0 ? "反映完了" : ApplyResultsPresentation.Summary(attention);
    }
}

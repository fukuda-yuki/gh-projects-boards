namespace GhProjectsBoards.Core.Projects;

internal sealed partial class RegistrationWorkspace
{
    public ApplyReview? ApplyReview { get; private set; }
    public Task PrepareApplyAsync(IReadOnlySet<string> items) => RunAsync(async token =>
    {
        ApplyReview = null; RequireConnection();
        if (Selected is not { } selected || Drafts is not { } session) return;
        if (CanRefresh?.Invoke() == false) { Status = "IME変換中です。自然に確定・取消した後でApplyを再試行してください。"; return; }
        if (!await session.FlushAsync()) { Status = session.Status; return; }
        var result = await new ProjectReader(service!).ReadAsync(context!, selected.Snapshot.Id, token);
        if (result.Outcome != ProjectReadOutcome.Complete || result.Project is null)
        { Status = "Apply前の取得が不完全です。書き込みは開始していません。"; return; }
        var fetched = selected with { Snapshot = result.Project, RetrievedAt = DateTimeOffset.UtcNow };
        if (!await session.CommitAsync(w =>
        {
            w.Reconcile(selected, fetched);
            w.SetRegistrations(registrations.Select(r => r == selected ? fetched : r)); return w;
        }, () => Selected == selected && CanRead && !token.IsCancellationRequested
            && (session.Workspace.HasCheckpoint || store.MatchesLegacy(selected.Snapshot.Id.Scope, registrations))))
        { Status = session.Status; return; }
        registrations[registrations.IndexOf(selected)] = fetched; Selected = fetched;
        var destinations = new Dictionary<string, CreationRepository>();
        var remote = new ApplyRemote(service!, context!);
        foreach (var row in session.Workspace.LocalRows.Where(r => items.Contains(r.Id) && r.ProjectId == fetched.Snapshot.Id.NodeId))
        {
            var destination = await remote.ResolveCreationRepositoryAsync(fetched.Snapshot.Id, row.Repository, token);
            if (destination is not null) destinations[row.Id] = destination;
        }
        ApplyReview = session.Workspace.ReviewApply(fetched, items, destinations);
        Status = ApplyReview.Blocked.Length == 0 ? "フィールド差分を確認し、明示的にApplyしてください。未確定文字は送信対象外です。" : string.Join(" / ", ApplyReview.Blocked);
    });
    public Task ConfirmApplyAsync(ApplyReview review) => RunAsync(async token =>
    {
        RequireConnection();
        if (Drafts is not { } session || Selected?.Snapshot.Id != review.Batch.Project) return;
        if (!await session.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => CanRead && Selected?.Snapshot.Id == review.Batch.Project && !token.IsCancellationRequested))
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
        try { await executor.ExecuteAsync(batchId, token); }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }
        catch (IOException) { Status = "別プロセスが実行中、または保存先に問題があります。追加送信していません。"; return; }
        finally
        {
            var selectedId = Selected?.Snapshot.Id;
            registrations.RemoveAll(r => r.Snapshot.Id.Scope == session.Workspace.Scope);
            registrations.AddRange(session.Workspace.CheckpointRegistrations);
            Selected = registrations.SingleOrDefault(r => r.Snapshot.Id == selectedId);
        }
        Status = "Apply処理を停止しました。成功・保留・不確定のフィールド別履歴を確認してください。";
    }
}

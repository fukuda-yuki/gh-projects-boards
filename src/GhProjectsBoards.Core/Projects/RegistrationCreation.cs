namespace GhProjectsBoards.Core.Projects;

internal sealed partial class RegistrationWorkspace
{
    public CreationSetupReview? CreationSetupReview { get; private set; }
    public Task PrepareCreationSetupAsync(string batchId, string id) => RunAsync(async token =>
    {
        CreationSetupReview = null; RequireConnection(); if (Drafts is not { } session) return;
        var batch = session.Workspace.Journal.Single(b => b.Id == batchId); var c = batch.Creations!.Single(c => c.Id == id);
        if (c.Verified is null) { Status = "既知Issueを確認してから設定を再レビューしてください。"; return; }
        var remote = new ApplyRemote(service!, context!);
        var issue = await remote.ObserveCreatedIssueAsync(batch, c, null, token);
        if (issue is null) { Status = "既知Issueの同一性を確認できません。再作成しません。"; return; }
        var project = await remote.ObserveCreationProjectAsync(batch, token);
        var proposed = session.Workspace.LocalRows.SingleOrDefault(r => r.Id == c.LocalId)?.Selects.ToArray() ?? c.SetupIntents ?? c.Selects.ToArray();
        var withdrawn = proposed.Where(s => project is not null && !project.Fields.Any(f => f.Id.NodeId == s.FieldId && f.DataType == "SINGLE_SELECT" && f.ValueOwner == FieldOwner.ProjectItem)).ToArray();
        proposed = proposed.Except(withdrawn).ToArray();
        if (project is null || proposed.Any(s => (s.OptionId is not null || s.ExplicitClear) && !project.Fields.Any(f => f.Id.NodeId == s.FieldId
            && f.DataType == "SINGLE_SELECT" && f.ValueOwner == FieldOwner.ProjectItem && f.Availability == ValueAvailability.Present
            && (s.OptionId is null || f.Options.Any(o => o.Id == s.OptionId)))))
        { Status = "Projectの権限・フィールド・選択肢を確認できません。"; return; }
        var fields = new List<ApplyOperation>();
        foreach (var intent in proposed.Where(s => s.OptionId is not null || s.ExplicitClear))
        {
            if (c.ItemId is null) break;
            var f = new ApplyOperation(Guid.NewGuid().ToString("N"), new("Select", c.ItemId, batch.Project.NodeId, intent.FieldId),
                c.ItemId, c.Verified.Id, c.Verified.Url, intent.FieldName, null, new(intent.OptionId, intent.ExplicitClear), c.Stamp,
                ApplyState.Pending, [], "現在値と新しい設定を再比較");
            var read = await remote.ObserveAsync(batch, f, token);
            if (read.Observation is not { } observed) { Status = "設定対象の現在値・選択肢・権限が未確認です。"; return; }
            fields.Add(f with { Id = Guid.NewGuid().ToString("N"), Expected = observed.Value, Verification = observed,
                State = ApplyState.Pending, Attempts = [], Reason = "現在値からの設定を別途再レビュー" });
        }
        var registration = session.Workspace.CheckpointRegistrations.Single(p => p.Snapshot.Id == batch.Project);
        foreach (var field in session.Workspace.Fields.Where(f => f.Key.NodeId == c.LocalId && f.Key.ProjectId == batch.Project.NodeId))
            if (session.Workspace.PlanningPublicationProblem(registration, c.LocalId, field) is { } stale)
            { Status = stale; return; }
        var planning = session.Workspace.LocalRows.Any(r => r.Id == c.LocalId) ? session.Workspace.CreationPlanningFor(registration, c.LocalId)
            : c.SetupPlanningIntents ?? c.PlanningIntents ?? [];
        var withdrawnPlanning = planning.Where(s => s.Kind != "Dependency" && !project.Fields.Any(f => f.Id.NodeId == s.FieldId
            && f.DataType == PlanningScalars.DataType(s.Kind) && f.ValueOwner == FieldOwner.ProjectItem)).ToArray();
        planning = planning.Except(withdrawnPlanning).ToArray();
        foreach (var intent in planning)
        {
            if (!PlanningScalars.Publishable(intent.Kind, intent.Value)) { Status = "計画フィールドを正確に反映できません。ローカル値を確認してください。"; return; }
            if (c.ItemId is null) continue;
            var target = intent.Kind == "Dependency" ? session.Workspace.VerifiedPredecessor(intent.FieldId) : intent.FieldId;
            if (target is null) { Status = "先行する新規行のIssue ID検証待ちです。"; return; }
            var operation = new ApplyOperation(Guid.NewGuid().ToString("N"), new(intent.Kind, intent.Kind == "Dependency" ? c.Verified.Id : c.ItemId, batch.Project.NodeId, target),
                c.ItemId, c.Verified.Id, c.Verified.Url, intent.FieldName, null, intent.Value, c.Stamp, ApplyState.Pending, [], "計画フィールドの設定を再比較");
            var read = await remote.ObserveAsync(batch, operation, token);
            if (read.Observation is not { } observed) { Status = "計画フィールド・先行関係を確認できません。"; return; }
            fields.Add(operation with { Expected = observed.Value, Verification = observed });
        }
        CreationSetupReview = new(batchId, id, session.Workspace.Revision, issue, c.ItemId is null ? null : fields.ToArray(), proposed, withdrawn, planning, withdrawnPlanning);
    });
    public Task ConfirmCreationSetupAsync(CreationSetupReview review) => RunAsync(async token =>
    {
        RequireConnection(); if (Drafts is not { } session || CreationSetupReview != review) return;
        if (!await session.CommitAsync(w => { w.ConfirmCreationSetup(review); return w; }, () => !token.IsCancellationRequested))
        { Status = session.Status; return; }
        CreationSetupReview = null; await ExecuteApplyAsync(session, review.BatchId, token);
    });
    public CreatedIssue? CreationBindingPreview { get; private set; }
    public long CreationBindingRevision { get; private set; }
    public Task InspectCreationBindingAsync(string batchId, string operationId, string url) => RunAsync(async token =>
    {
        CreationBindingPreview = null; RequireConnection();
        if (Drafts is not { } session) return;
        var batch = session.Workspace.Journal.Single(b => b.Id == batchId);
        var creation = batch.Creations!.Single(c => c.Id == operationId);
        var issue = await new ApplyRemote(service!, context!).ObserveCreatedIssueAsync(batch, creation, url, token);
        if (issue is null || session.Workspace.Creations.Any(c => c.LocalId != creation.LocalId && c.Verified?.Id == issue.Id))
        { Status = "URLのhost・Repository・Issue型・IDを検証できないか、別の作成行に関連付け済みです。"; return; }
        CreationBindingPreview = issue; CreationBindingRevision = session.Workspace.Revision;
        Status = "実際のIssueを確認して関連付けを承認してください。関連付け自体はGitHubを変更しません。";
    });
    public Task ConfirmCreationBindingAsync(string batchId, string operationId, CreatedIssue issue, long revision) => RunAsync(async token =>
    {
        RequireConnection();
        if (Drafts is not { } session || CreationBindingPreview != issue || CreationBindingRevision != revision) return;
        using var lease = draftStore.AcquireExecution(session.Workspace.Scope);
        var batch = session.Workspace.Journal.Single(b => b.Id == batchId); var c = batch.Creations!.Single(c => c.Id == operationId);
        var actual = await new ApplyRemote(service!, context!).ObserveCreatedIssueAsync(batch, c, issue.Url, token);
        if (actual is null || actual.Id != issue.Id || actual.Title != issue.Title || actual.RepositoryId != issue.RepositoryId)
        { Status = "確認後にIssueが変更されました。URLをもう一度確認してください。"; return; }
        if (!await session.CommitAsync(w => { w.BindCreation(batchId, operationId, actual, revision); return w; }, () => !token.IsCancellationRequested))
        { Status = session.Status; return; }
        CreationBindingPreview = null; Status = "関連付けを保存しました。履歴の再開からProject設定を進められます。元の試行は不確定のままです。";
    });
    public Task PrepareCreationRetryAsync(string batchId, string operationId) => RunAsync(async token =>
    {
        ApplyReview = null; RequireConnection();
        if (Drafts is not { } session) return;
        var batch = session.Workspace.Journal.Single(b => b.Id == batchId); var c = batch.Creations!.Single(c => c.Id == operationId);
        var repository = await new ApplyRemote(service!, context!).ResolveCreationRepositoryAsync(batch.Project, c.Repository.Name, token);
        if (repository is null) { Status = "宛先を確認できません。"; return; }
        ApplyReview = session.Workspace.ReviewCreationRetry(batchId, operationId, repository);
    });
    public Task ConfirmCreationRetryAsync(ApplyReview review) => RunAsync(async token =>
    {
        RequireConnection(); if (Drafts is not { } session) return;
        if (!await session.CommitAsync(w => { w.ConfirmCreationRetry(review); return w; }, () => !token.IsCancellationRequested))
        { Status = session.Status; return; }
        ApplyReview = null; await ExecuteApplyAsync(session, review.Batch.Id, token);
    });
}

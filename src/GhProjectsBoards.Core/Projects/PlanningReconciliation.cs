namespace GhProjectsBoards.Core.Projects;

internal sealed record PlanningProjectionDecision(FieldKey Key, string ObservationId, bool AdoptRemote);

internal sealed partial class EditingWorkspace
{
    public void CancelUnavailablePlanningDraft(FieldKey key, long expectedRevision)
    {
        if (Revision != expectedRevision || !fields.TryGetValue(key, out var old) || key.Kind is not ("Number" or "Date")
            || old.Observation is not { Availability: ValueAvailability.Unavailable or ValueAvailability.NotLoaded or ValueAvailability.Unsupported }
            || old.Change is null && old.Buffer is null)
            throw new InvalidOperationException("保持した下書きが変更されました。比較を開き直してください。");
        var next = old with { Change = null, Buffer = null, Conflict = false, Stamp = Revision + 1 };
        fields[key] = next; Revision++;
        var changes = new Dictionary<FieldKey, FieldChange> { [key] = new(key, old, next) };
        ProjectCommittedPlan(key.ProjectId!, changes);
        history.Add(new(Guid.NewGuid().ToString("N"), key.ProjectId!, changes.Values.ToArray()));
    }
    internal const string ProjectionDecisionReason = "計画の照合が必要です。日時・実績を確認してください。";
    private string? ProjectionRole(FieldKey key) => key.Kind is "Date" or "Number"
        ? Planning(key.ProjectId!)?.Fields.SingleOrDefault(b => b.FieldId == key.FieldId && b.Role is "Start" or "Finish" or "Actual")?.Role : null;
    public DraftField[] PlanningDecisions(string projectId, string rowId) => fields.Values.Where(f => f.Key.ProjectId == projectId
        && f.Key.NodeId == rowId && f.Observation?.Reason == ProjectionDecisionReason).ToArray();

    private bool ProjectionNeedsDecision(DraftField old, string? remote)
    {
        var role = ProjectionRole(old.Key); if (role is null || remote == old.Baseline) return false;
        // A compatible date-only read is evidence for the projection, never the exact time.
        return remote != (old.Change is null ? old.Baseline : old.Change.Value);
    }
    private void AcceptProjectionBaselines(ProjectRegistration registration, ProjectPlanning candidate, PlanningProjectionDecision[] decisions)
    {
        if (decisions.Select(d => d.Key).Distinct().Count() != decisions.Length) throw new InvalidOperationException("重複した計画の照合です。");
        foreach (var decision in decisions)
        {
            if (!fields.TryGetValue(decision.Key, out var old) || old.Observation is not { } observation
                || observation.Id != decision.ObservationId || observation.Reason != ProjectionDecisionReason
                || old.Key.ProjectId != candidate.ProjectId || observation.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                throw new InvalidOperationException("取得結果が変わりました。計画の照合を開き直してください。");
            var role = ProjectionRole(old.Key) ?? throw new InvalidOperationException("計画フィールドではありません。");
            var id = TaskId(registration, old.Key.NodeId);
            var task = candidate.Tasks.SingleOrDefault(t => t.Id == id) ?? new PlanningTask(id);
            if (role == "Actual" && task.Actuals is null)
                throw new InvalidOperationException("実績の照合には明示的な報告内訳・報告対象日を入力してください。未割当の報告も指定できます。");
            if (decision.AdoptRemote)
            {
                if (role is "Start" or "Finish")
                {
                    var endpoint = role == "Start" ? task.ManualStart : task.ManualFinish;
                    if (task.Mode != PlanningMode.Manual || PlanningContract.ProjectDate(endpoint) != observation.Value)
                        throw new InvalidOperationException("GitHubの日付に合わせて正確な採用日時を入力し、Manualで保存してください。空値は日時を空欄にしてください。");
                }
                else
                {
                    var value = task.Actuals is null ? null : task.Actuals.Length == 0 ? null : PlanningContract.CanonicalHours(task.Actuals.Sum(a => a.Hours));
                    if (task.Actuals is null || value != observation.Value)
                        throw new InvalidOperationException("GitHubの実績合計と一致する報告内訳・報告日を入力してください。空値は明示的に実績を削除してください。");
                }
            }
            var local = old.Change is null ? old.Baseline : old.Change.Value;
            fields[old.Key] = old with { Baseline = observation.Value,
                Change = local == observation.Value ? null : new(local, local is null), Conflict = false,
                Observation = observation with { Reason = null }, RetrievedAt = observation.At };
            for (var i = 0; i < history.Count; i++)
                if (history[i].Changes.Any(c => c.Key == old.Key)) InvalidateRemoteUndo(i, "計画の照合で取得基準が変わりました。");
        }
    }
    private void InvalidatePlanningForRemoteInputs(ProjectRegistration before, ProjectRegistration after)
    {
        var plan = Planning(after.Snapshot.Id.NodeId); if (plan is null) return;
        var selected = plan.Fields.Select(f => f.FieldId).ToHashSet();
        string Fingerprint(ProjectReadModel p) => System.Text.Json.JsonSerializer.Serialize(new {
            Fields = p.Fields.Where(f => selected.Contains(f.Id.NodeId)).OrderBy(f => f.Id.NodeId).Select(f => new { f.Id, f.DataType, f.ValueOwner, f.Availability }),
            Items = p.Items.OrderBy(i => i.Id.NodeId).Select(i => new { i.Id, i.ContentId, i.Kind, i.IsArchived,
                Values = i.Values.Where(v => v.FieldId is not null && selected.Contains(v.FieldId.NodeId)).OrderBy(v => v.FieldId!.NodeId).Select(v => new { v.FieldId, v.Availability, v.Scalar }) }),
            Native = p.Issues.OrderBy(i => i.Key.NodeId).Select(i => new { i.Key, i.Value.Native?.Complete,
                Assignees = i.Value.Native?.Assignees.Select(a => a.Id.NodeId).Order().ToArray(), Predecessors = i.Value.Native?.Predecessors.Select(a => a.NodeId).Order().ToArray() })
        });
        if (Fingerprint(before.Snapshot) == Fingerprint(after.Snapshot)) return;
        foreach (var transaction in history.Where(t => t.ProjectId == plan.ProjectId && t.InvalidReason is null
            && (t.Plan is not null || t.Changes.Any(c => c.Key.Kind == "Dependency" || c.Key.FieldId is { } id && selected.Contains(id)))).ToArray())
            InvalidateRemoteUndo(history.IndexOf(transaction), "計画の外部入力が変わったため、以前の日程を復元するUndoを無効化しました。");
    }
}

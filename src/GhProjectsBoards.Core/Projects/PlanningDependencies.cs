namespace GhProjectsBoards.Core.Projects;

internal sealed record PlanningDependencyEdit(string TaskId, string[] Predecessors);
internal sealed record PlanningValueEdit(string RowId, string Role, string? Value);

internal sealed partial class EditingWorkspace
{
    private EditCell[] DependencyCells(ProjectRegistration registration, ProjectItemReadModel item, IEnumerable<FieldKey>? draftKeys = null)
    {
        var p = registration.Snapshot;
        var issue = item.Kind == ProjectItemKind.Issue && item.ContentId is { } id ? p.Issues.GetValueOrDefault(id) : null;
        if (issue is null || Planning(p.Id.NodeId) is null) return [];
        var native = issue.Native;
        var observed = native?.Predecessors.Select(i => i.NodeId).ToHashSet() ?? [];
        var keys = observed.Concat((draftKeys ?? fields.Keys.Where(k => k.Kind == "Dependency" && k.ProjectId == p.Id.NodeId && k.NodeId == issue.Id.NodeId))
            .Select(k => k.FieldId!)).Distinct();
        var reason = native?.Complete != true ? "先行関係が未取得です。再取得してください。"
            : item.IsArchived ? "アーカイブ項目は編集対象外"
            : issue.Capability is not { CanUpdate: true } c || c.ObservedAt == default ? "Issueの更新権限未確認" : null;
        return keys.Select(pred => new EditCell(new("Dependency", issue.Id.NodeId, p.Id.NodeId, pred), "先行Issue / " + pred,
            observed.Contains(pred) ? "present" : null, reason, [], native?.Complete != true ? ValueAvailability.NotLoaded
                : observed.Contains(pred) ? ValueAvailability.Present : ValueAvailability.Empty, Scope)).ToArray();
    }
    private EditRow[] OperationRows(ProjectRegistration registration)
    {
        var rows = Open(registration);
        var items = registration.Snapshot.Items.ToDictionary(i => i.Id.NodeId);
        var keys = fields.Keys.Where(k => k.Kind == "Dependency" && k.ProjectId == registration.Snapshot.Id.NodeId).ToLookup(k => k.NodeId);
        return rows.Select(row => items.GetValueOrDefault(row.ItemId) is { } item
            ? row with { Cells = row.Cells.Concat(DependencyCells(registration, item, keys[item.ContentId?.NodeId ?? ""])).ToArray() } : row).ToArray();
    }

    private PlanningLink[] AdoptedLinks(ProjectRegistration registration, string taskId, PlanningTask task, IEnumerable<DraftField> dependencies)
    {
        var issue = registration.Snapshot.Issues.GetValueOrDefault(new(Scope, taskId));
        var native = issue?.Native?.Predecessors.Select(p => p.NodeId).ToHashSet() ?? [];
        foreach (var field in dependencies)
            if ((field.Change is null ? field.Baseline : field.Change.Value) == "present") native.Add(field.Key.FieldId!); else native.Remove(field.Key.FieldId!);
        return native.Select(id => new PlanningLink(id)).Concat(task.LocalLinks ?? []).Distinct().ToArray();
    }
    private void ChangeDependencies(ProjectRegistration registration, PlanningDependencyEdit[] edits, Dictionary<FieldKey, FieldChange> changes)
    {
        var p = registration.Snapshot;
        foreach (var edit in edits)
        {
            var local = localRows.Any(r => r.Id == edit.TaskId && r.ProjectId == p.Id.NodeId);
            var localTargets = edit.Predecessors.Where(id => localRows.Any(r => r.Id == id && r.ProjectId == p.Id.NodeId)).ToArray();
            if (local || localTargets.Length != 0 || Planning(p.Id.NodeId)!.Tasks.Any(t => t.Id == edit.TaskId && (t.LocalLinks ?? []).Any(l => l.PredecessorId.StartsWith("local-"))))
            {
                if (edit.Predecessors.Any(id => id == edit.TaskId || !p.Issues.ContainsKey(new(Scope, id)) && !localRows.Any(r => r.Id == id && r.ProjectId == p.Id.NodeId)))
                    throw new InvalidOperationException("先行Issue・新規行のIDを確認できません。");
                var plan = Planning(p.Id.NodeId)!;
                var task = plan.Tasks.SingleOrDefault(t => t.Id == edit.TaskId) ?? new PlanningTask(edit.TaskId);
                var retained = (task.LocalLinks ?? []).Where(l => l.Kind != "FS" || l.ExternalFinish is not null);
                var updated = task with { LocalLinks = retained.Concat((local ? edit.Predecessors : localTargets).Select(id => new PlanningLink(id))).ToArray() };
                planning[planning.FindIndex(p => p.ProjectId == plan.ProjectId)] = plan with { Tasks = plan.Tasks.Where(t => t.Id != edit.TaskId).Append(updated).ToArray() };
                if (local) continue;
            }
            if (!p.Issues.TryGetValue(new(Scope, edit.TaskId), out var issue) || issue.Native?.Complete != true
                || issue.Capability is not { CanUpdate: true } c || c.ObservedAt == default
                || !p.Items.Any(i => i.ContentId == issue.Id && !i.IsArchived))
                throw new InvalidOperationException("Issue・先行関係・更新権限を再取得してください。");
            if (edit.Predecessors.Any(id => string.IsNullOrWhiteSpace(id) || id == edit.TaskId)
                || edit.Predecessors.Distinct().Count() != edit.Predecessors.Length)
                throw new InvalidOperationException("先行IssueのIDが無効です。");
            var observed = issue.Native.Predecessors.Select(i => i.NodeId).ToHashSet();
            // Only registered Issues or an already observed external edge can be selected.
            if (edit.Predecessors.Any(id => !localTargets.Contains(id) && !observed.Contains(id) && !p.Issues.ContainsKey(new(Scope, id))))
                throw new InvalidOperationException("先行Issueの識別情報を確認できません。");
            var keys = observed.Concat(edit.Predecessors.Except(localTargets)).Concat(fields.Keys.Where(k => k.Kind == "Dependency" && k.NodeId == edit.TaskId && k.ProjectId == p.Id.NodeId).Select(k => k.FieldId!)).Distinct();
            foreach (var pred in keys)
            {
                var key = new FieldKey("Dependency", edit.TaskId, p.Id.NodeId, pred);
                var old = fields.GetValueOrDefault(key) ?? new(key, observed.Contains(pred) ? "present" : null, p.Id, registration.RetrievedAt, null, null, 0);
                if (old.Conflict || old.Observation?.Reason is not null) throw new InvalidOperationException("先行関係の取得結果を先に確認してください。");
                var value = edit.Predecessors.Contains(pred) ? "present" : null;
                if ((old.Change is null ? old.Baseline : old.Change.Value) == value) continue;
                var next = old with { Change = value == old.Baseline ? null : new(value, value is null), Stamp = Revision };
                fields[key] = next;
                changes[key] = new(key, old, next);
            }
        }
    }
    private void ReconcileAcknowledgedDependency(ApplyOperation operation)
    {
        foreach (var old in fields.Values.Where(f => f.Key.Kind == "Dependency" && f.Key.NodeId == operation.IssueId
            && f.Key.FieldId == operation.Key.FieldId && f.Key.ProjectId != operation.Key.ProjectId).ToArray())
        {
            var local = old.Change is null ? old.Baseline : old.Change.Value;
            var remote = operation.Intended.Value;
            fields[old.Key] = old with { Baseline = remote,
                Change = old.Change is null || local == remote ? null : new(local, local is null), Conflict = false,
                Observation = operation.Verification! with { Project = old.SourceProject }, RetrievedAt = operation.Verification!.At };
            for (var i = 0; i < history.Count; i++)
                if (history[i].Changes.Any(c => c.Key == old.Key)) InvalidateRemoteUndo(i, "別Projectで同じIssueの先行関係が反映されました。");
        }
        foreach (var registration in CheckpointRegistrations.Where(p => p.Snapshot.Issues.ContainsKey(new(Scope, operation.IssueId))))
            ProjectPlan(registration, []);
    }
}

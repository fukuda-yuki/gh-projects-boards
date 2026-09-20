namespace GhProjectsBoards.Core.Projects;

internal sealed partial class EditingWorkspace
{
    internal CreationPlanningIntent[] CreationPlanningFor(ProjectRegistration registration, string localId)
    {
        var intents = fields.Values.Where(f => f.Key.NodeId == localId && f.Key.ProjectId == registration.Snapshot.Id.NodeId
            && f.Key.Kind is "Number" or "Date" && (f.Change is not null || f.Baseline is not null))
            .Select(f => new CreationPlanningIntent(f.Key.Kind, f.Key.FieldId!,
                registration.Snapshot.Fields.SingleOrDefault(d => d.Id.NodeId == f.Key.FieldId)?.Name ?? f.Key.FieldId!,
                f.Change ?? new(f.Baseline, f.Baseline is null)));
        var links = Planning(registration.Snapshot.Id.NodeId)?.Tasks.SingleOrDefault(t => t.Id == localId)?.LocalLinks ?? [];
        return intents.Concat(links.Where(l => l.Kind == "FS" && l.ExternalFinish is null)
            .Select(l => new CreationPlanningIntent("Dependency", l.PredecessorId, "先行Issue / " + l.PredecessorId, new("present")))).ToArray();
    }
    internal string? VerifiedPredecessor(string id) => id.StartsWith("local-", StringComparison.Ordinal)
        ? Creations.LastOrDefault(c => c.LocalId == id && c.Verified is not null)?.Verified?.Id : id;

    private void PromotePlanning(ProjectRegistration registration, CreationOperation creation, ProjectItemReadModel item)
    {
        var projectId = registration.Snapshot.Id.NodeId;
        foreach (var old in fields.Values.Where(f => f.Key.NodeId == creation.LocalId && f.Key.ProjectId == projectId && f.Key.Kind is "Number" or "Date").ToArray())
        {
            var observed = item.Values.SingleOrDefault(v => v.FieldId?.NodeId == old.Key.FieldId);
            var available = observed?.Availability is ValueAvailability.Present or ValueAvailability.Empty
                && registration.Snapshot.Fields.Any(f => f.Id.NodeId == old.Key.FieldId && f.DataType == PlanningScalars.DataType(old.Key.Kind) && f.Availability == ValueAvailability.Present);
            if (!available && old.Change is null && old.Buffer is null) continue;
            var published = creation.Fields?.SingleOrDefault(f => f.Key.Kind == old.Key.Kind && f.Key.FieldId == old.Key.FieldId);
            var baseline = published is null ? available ? observed!.Scalar : old.Baseline : published.Intended.Value;
            var local = available && published is null && old.Change is null ? observed!.Scalar : old.Change is null ? old.Baseline : old.Change.Value;
            var key = old.Key with { NodeId = item.Id.NodeId };
            var observation = new FieldObservation(Guid.NewGuid().ToString("N"), registration.Snapshot.Id, registration.RetrievedAt,
                available ? observed!.Scalar : null, available ? observed!.Availability : ValueAvailability.Unavailable,
                available ? null : "フィールド未確認。保持した入力を確認してください。", []);
            var transferred = old with { Key = key, Baseline = baseline, Change = local == baseline ? null : new(local, local is null),
                Observation = observation, RetrievedAt = registration.RetrievedAt, Stamp = Revision + 1, Conflict = false };
            if (available)
            {
                if (ProjectionNeedsDecision(transferred, observed!.Scalar)) transferred = transferred with { Observation = observation with { Reason = ProjectionDecisionReason } };
                else if (local == baseline || local == observed!.Scalar) transferred = transferred with { Baseline = observed!.Scalar, Change = null };
                else if (baseline != observed!.Scalar) transferred = transferred with { Conflict = true };
            }
            fields[key] = transferred;
            // Retain the key required by immutable, now-invalid Undo evidence.
            fields[old.Key] = old with { Change = null, Buffer = null, Observation = null, Conflict = false };
            for (var i = 0; i < history.Count; i++)
                if (history[i].Changes.Any(c => c.Key == old.Key)) InvalidateRemoteUndo(i, "検証済みIssueへ移行したため以前の新規行のUndoは復元できません。");
        }
        var plan = Planning(projectId); if (plan is null) return;
        var tasks = plan.Tasks.Select(task => task with {
            Id = task.Id == creation.LocalId ? creation.Verified!.Id : task.Id,
            LocalLinks = task.LocalLinks?.Select(l => l.PredecessorId == creation.LocalId ? l with { PredecessorId = creation.Verified!.Id } : l).ToArray()
        }).ToArray();
        // The verified lineage promotes local FS intents into the same per-edge drafts.
        foreach (var task in tasks.Where(t => !t.Id.StartsWith("local-", StringComparison.Ordinal)))
        {
            var native = registration.Snapshot.Issues.GetValueOrDefault(new(Scope, task.Id))?.Native;
            if (native?.Complete != true) continue;
            var desired = (task.LocalLinks ?? []).Where(l => l.Kind == "FS" && l.ExternalFinish is null && !l.PredecessorId.StartsWith("local-", StringComparison.Ordinal)).Select(l => l.PredecessorId).ToHashSet();
            var publishedLinks = task.Id == creation.Verified!.Id ? (creation.Fields ?? []).Where(f => f.Key.Kind == "Dependency").ToArray() : [];
            foreach (var predecessor in desired.Concat(publishedLinks.Select(f => f.Key.FieldId!)).Distinct())
            {
                var key = new FieldKey("Dependency", task.Id, projectId, predecessor);
                var remote = native.Predecessors.Any(i => i.NodeId == predecessor) ? "present" : null;
                var published = publishedLinks.SingleOrDefault(f => f.Key.FieldId == predecessor);
                var baseline = published is null ? remote : published.Intended.Value;
                var local = desired.Contains(predecessor) ? "present" : null;
                var value = local == baseline ? remote : local;
                if (!fields.ContainsKey(key)) fields[key] = new(key, remote, registration.Snapshot.Id, registration.RetrievedAt,
                    value == remote ? null : new(value, value is null), null, Revision + 1);
            }
        }
        tasks = tasks.Select(t => registration.Snapshot.Issues.GetValueOrDefault(new(Scope, t.Id))?.Native?.Complete == true
            ? t with { LocalLinks = t.LocalLinks?.Where(l => l.Kind != "FS" || l.ExternalFinish is not null || l.PredecessorId.StartsWith("local-", StringComparison.Ordinal)).ToArray() } : t).ToArray();
        for (var i = 0; i < history.Count; i++)
            if (history[i].Plan is not null && history[i].ProjectId == projectId) InvalidateRemoteUndo(i, "作成identityの検証後は以前の計画identityをUndoで復元できません。");
        var summary = plan.Summary;
        if (summary?.Baseline is { } protectedBaseline) summary = summary with { Baseline = protectedBaseline with {
            Tasks = protectedBaseline.Tasks.Select(t => t.TaskId == creation.LocalId
                ? t with { TaskId = creation.Verified!.Id, RowId = item.Id.NodeId } : t).ToArray() } };
        planning[planning.FindIndex(p => p.ProjectId == projectId)] = plan with { Stamp = Revision + 1, Tasks = tasks, Summary = summary };
        InvalidatePlan(projectId);
    }
    private bool PlanningIdentityOccupied(string projectId, string issueId)
    {
        if (Planning(projectId)?.Tasks.Any(t => t.Id == issueId) == true) return true;
        var registration = CheckpointRegistrations.SingleOrDefault(p => p.Snapshot.Id.NodeId == projectId);
        var items = registration?.Snapshot.Items.Where(i => i.ContentId?.NodeId == issueId).Select(i => i.Id.NodeId).ToHashSet() ?? [];
        return fields.Values.Any(f => f.Key.ProjectId == projectId && (items.Contains(f.Key.NodeId) || f.Key.Kind == "Dependency" && f.Key.NodeId == issueId)
            && (f.Change is not null || f.Buffer is not null || f.Conflict || f.Observation?.Reason is not null));
    }
}

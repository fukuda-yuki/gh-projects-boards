namespace GhProjectsBoards.Core.Projects;

internal sealed record PlanningChange(ProjectPlanning? Before, ProjectPlanning After);

internal sealed partial class EditingWorkspace
{
    private readonly Dictionary<string, AdoptedPlan> calculatedPlans = [];
    private void InvalidatePlan(string projectId) => calculatedPlans.Remove(projectId);
    public string TaskId(ProjectRegistration registration, string rowId) => localRows.Any(r => r.Id == rowId && r.ProjectId == registration.Snapshot.Id.NodeId)
        ? rowId : registration.Snapshot.Items.Single(i => i.Id.NodeId == rowId).ContentId?.NodeId
            ?? throw new InvalidOperationException("Issueを選択してください。");
    public AdoptedPlan PlanFor(ProjectRegistration registration)
    {
        var id = registration.Snapshot.Id.NodeId;
        if (registration.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("別プロフィールの計画です。");
        if (calculatedPlans.TryGetValue(id, out var cached)) return cached;
        var plan = Planning(id); if (plan is null) return new(id, Revision, []);
        var metadata = plan.Tasks.ToDictionary(t => t.Id);
        var roles = plan.Fields.ToDictionary(f => f.Role, f => f.FieldId);
        var inputs = new List<PlanningInput>();
        var issueRows = registration.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null).ToDictionary(i => i.Id.NodeId, i => i.ContentId!.NodeId);
        foreach (var row in Open(registration).Where(r => r.IsLocal || issueRows.ContainsKey(r.ItemId)))
        {
            var taskId = TaskId(registration, row.ItemId); var task = metadata.GetValueOrDefault(taskId) ?? new(taskId);
            decimal? Work(string role)
            {
                if (!roles.TryGetValue(role, out var field)) return null;
                var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == field);
                if (cell is null || cell.Reason is not null || Field(cell)?.Conflict == true || Field(cell)?.Observation?.Reason is not null) return null;
                var value = Value(cell);
                try { return value is null ? null : PlanningContract.ParseHours(value); }
                catch (InvalidOperationException) { return null; }
            }
            inputs.Add(new(task, Work("Estimate"), Work("Remaining"), [], task.LocalLinks ?? []));
        }
        return calculatedPlans[id] = PlanningEngine.Calculate(plan, inputs.ToArray(), Revision);
    }
    public void CommitPlanning(ProjectRegistration registration, ProjectPlanning candidate, long expectedRevision)
    {
        if (!HasCheckpoint || registration.Snapshot.Id.Scope != Scope || registration.Snapshot.Id.NodeId != candidate.ProjectId || expectedRevision != Revision)
            throw new InvalidOperationException("計画を開いた後に変更がありました。現在の値を確認してください。");
        if (registration.Snapshot.Capability is not { CanUpdate: true } capability || capability.ObservedAt == default)
            throw new InvalidOperationException("Projectの更新権限を確認してください。");
        foreach (var binding in candidate.Fields)
            if (!registration.Snapshot.Fields.Any(f => f.Id.NodeId == binding.FieldId && f.DataType == binding.DataType && f.ValueOwner == FieldOwner.ProjectItem && f.Availability == ValueAvailability.Present))
                throw new InvalidOperationException("計画フィールドのID・型を確認できません。");
        var staged = Restore(Snapshot());
        staged.CommitPlanningCore(registration, candidate);
        fields.Clear(); foreach (var pair in staged.fields) fields.Add(pair.Key, pair.Value);
        planning.Clear(); planning.AddRange(staged.planning);
        history.Clear(); history.AddRange(staged.history);
        Revision = staged.Revision; InvalidatePlan(candidate.ProjectId);
    }
    private void CommitPlanningCore(ProjectRegistration registration, ProjectPlanning candidate)
    {
        var before = Planning(candidate.ProjectId);
        SetPlanning(candidate, before?.Stamp ?? 0);
        Open(registration);
        var changes = new Dictionary<FieldKey, FieldChange>(); ProjectPlan(registration, changes);
        history.Add(new(Guid.NewGuid().ToString("N"), candidate.ProjectId, changes.Values.ToArray(), Plan: new(before, Planning(candidate.ProjectId)!)));
    }
    private void ProjectPlan(ProjectRegistration registration, Dictionary<FieldKey, FieldChange> changes)
    {
        var id = registration.Snapshot.Id.NodeId; InvalidatePlan(id);
        var p = Planning(id); if (p is null) return;
        var result = PlanFor(registration); var byTask = result.Tasks.ToDictionary(t => t.Id);
        var metadata = p.Tasks.ToDictionary(t => t.Id);
        var issueRows = registration.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null).Select(i => i.Id.NodeId).ToHashSet();
        foreach (var row in Open(registration).Where(r => r.IsLocal || issueRows.Contains(r.ItemId)))
        {
            var taskId = TaskId(registration, row.ItemId); if (!byTask.TryGetValue(taskId, out var task) || task.Mode == PlanningMode.Unplanned) continue;
            foreach (var binding in p.Fields.Where(f => f.Role is "Start" or "Finish" or "Actual"))
            {
                // An unresolved Auto result must not erase previously published dates.
                if (binding.Role is "Start" or "Finish" && task.Mode == PlanningMode.Auto && !task.Resolved) continue;
                var reports = metadata.GetValueOrDefault(taskId)?.Actuals;
                if (binding.Role == "Actual" && reports is null) continue;
                var value = binding.Role switch { "Start" => PlanningContract.ProjectDate(task.Start), "Finish" => PlanningContract.ProjectDate(task.Finish),
                    _ => reports!.Length == 0 ? null : PlanningContract.CanonicalHours(reports.Sum(a => a.Hours)) };
                var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId);
                if (cell?.Key is not { } key || cell.Reason is not null || !fields.TryGetValue(key, out var old) || old.Conflict || old.Observation?.Reason is not null) continue;
                if ((old.Change is null ? old.Baseline : old.Change.Value) == value) continue;
                var next = old with { Change = value == old.Baseline ? null : new(value, value is null), Stamp = Revision };
                fields[key] = next;
                changes[key] = new(key, changes.TryGetValue(key, out var prior) ? prior.Before : old, next);
            }
        }
    }
    private void ProjectCommittedPlan(string projectId, Dictionary<FieldKey, FieldChange> changes)
    {
        var registration = CheckpointRegistrations.SingleOrDefault(p => p.Snapshot.Id.NodeId == projectId);
        if (registration is not null && Planning(projectId) is not null) ProjectPlan(registration, changes);
    }
}

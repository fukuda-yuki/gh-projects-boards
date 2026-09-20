namespace GhProjectsBoards.Core.Projects;

internal sealed partial class EditingWorkspace
{
    private void ValidateWorkContributions(string projectId, IReadOnlyDictionary<FieldKey, FieldChange> proposed)
    {
        var plan = Planning(projectId); if (plan is null) return;
        var registration = CheckpointRegistrations.SingleOrDefault(p => p.Snapshot.Id.NodeId == projectId);
        if (registration is null) return;
        var rows = registration.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null)
            .ToDictionary(i => i.ContentId!.NodeId, i => i.Id.NodeId);
        foreach (var task in plan.Tasks.Where(t => t.Contributions is { Length: > 0 }))
        {
            var row = task.Id.StartsWith("local-", StringComparison.Ordinal) ? task.Id : rows.GetValueOrDefault(task.Id);
            if (row is null) continue;
            foreach (var role in new[] { "Estimate", "Remaining" })
            {
                var fieldId = plan.Fields.SingleOrDefault(f => f.Role == role)?.FieldId;
                if (fieldId is null) continue;
                var key = new FieldKey("Number", row, projectId, fieldId);
                var field = proposed.GetValueOrDefault(key)?.After ?? fields.GetValueOrDefault(key);
                var raw = field?.Change is null ? field?.Baseline : field.Change.Value;
                if (raw is null) continue;
                decimal hours;
                try { hours = PlanningContract.ParseHours(raw); } catch (InvalidOperationException) { continue; }
                var sum = task.Contributions!.Sum(c => (role == "Estimate" ? c.EstimateHours : c.RemainingHours) ?? 0);
                if (sum > hours) throw new InvalidOperationException("担当者別の工数内訳がタスクの工数を超えています。内訳または工数を確認してください。");
            }
        }
    }
}

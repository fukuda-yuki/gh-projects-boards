namespace GhProjectsBoards.Core.PlanEditor;

// 先行タスクを追加 (#263): in a V-model WBS a task usually waits for its own requirement's task in the
// preceding 工程, so those come first. A requirement row is never offered: its dates are its children's.
internal static class PlanPredecessorChoices
{
    internal static IReadOnlyList<string> Rank(PlanDocument document, string target)
    {
        var rows = document.State.Rows;
        var byId = rows.ToDictionary(r => r.Identity);
        if (!byId.TryGetValue(target, out var task)) return [];
        var requirements = rows.Where(r => r.Parent is not null && byId.ContainsKey(r.Parent)).Select(r => r.Parent!).ToHashSet();
        var mapping = document.State.Settings.Columns.SingleOrDefault(c => c.Role == PlanField.Phase);
        var phases = (document.Baseline.Columns.SingleOrDefault(c => c.Id == mapping?.FieldId)?.Options ?? []).ToList();
        var phase = task.Phase is { } own ? phases.IndexOf(own) : -1;
        var previous = phase > 0 ? phases[phase - 1] : null;
        var requirement = Requirement(task);
        return rows.Where(r => r.Identity != target && !requirements.Contains(r.Identity))
            .Select((r, position) => (r.Identity, position, Group: requirement is null || Requirement(r) != requirement ? 2 : previous is not null && r.Phase == previous ? 0 : 1))
            .OrderBy(c => c.Group).ThenBy(c => c.position).Select(c => c.Identity).ToArray();

        // The top-level ancestor, as CSV export's 要求事項.
        string? Requirement(PlanRow row)
        {
            var top = row; var seen = new HashSet<string>(StringComparer.Ordinal);
            while (top.Parent is { } parent && seen.Add(parent) && byId.TryGetValue(parent, out var next)) top = next;
            return ReferenceEquals(top, row) ? null : top.Identity;
        }
    }
}

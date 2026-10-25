using System.Collections.Immutable;

namespace GhProjectsBoards.Core.PlanEditor;

// The tasks whose recalculated dates one edit caused. Cause is null when no edited task reaches them,
// which happens when a 基準日, settings or GitHub-side change, not a task edit, moved them.
internal sealed record PlanRecalculationGroup(string? Cause, ImmutableArray<string> CauseFields, ImmutableArray<string> Tasks);
// The part of a parent's child order a publish rewrites: which children caused it and their neighbourhoods
// in the new order. Added names a new task; any other cause is a moved child (#224).
internal sealed record PlanChildOrderExcerpt(ImmutableArray<(string Identity, bool Added)> Causes, ImmutableArray<ImmutableArray<string>> Windows);

internal static class PlanPublishGrouping
{
    // The entered fields that feed the scheduler: only an edit to one of these can move another task's dates.
    private static readonly string[] ScheduleInputs = [PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.Start,
        PlanField.End, PlanField.Predecessors, PlanField.Parent, PlanField.Closed, PlanField.Cancelled, PlanField.Removed, PlanField.Assignees];

    internal static ImmutableArray<string> CauseFields(PlanUnpublished changes, string identity)
        => !changes.Fields.TryGetValue(identity, out var fields) ? []
            : fields.Contains(PlanField.NewTask) ? [PlanField.NewTask]
            : fields.Where(f => ScheduleInputs.Contains(f) && changes.InputKind(identity, f) == PlanUnpublishedInputKind.Entered).ToImmutableArray();

    // Attributes each task with a recalculated value to the nearest edited task upstream of it in the
    // scheduler's own dependency graph: its predecessors, its ancestors' predecessors and, for a summary,
    // its children. The walk only passes through tasks whose dates differ from GitHub's, because a task
    // whose dates did not move cannot carry a change further. Equal distances go to the earlier plan row.
    internal static ImmutableArray<PlanRecalculationGroup> RecalculationGroups(PlanDocument document, PlanUnpublished changes, IReadOnlyList<ScheduledTask> schedule)
    {
        var rows = PlanOperations.EffectiveRows(document);
        var byId = rows.ToDictionary(r => r.Identity, StringComparer.Ordinal);
        var order = rows.Select((r, i) => (r.Identity, i)).ToDictionary(p => p.Identity, p => p.i, StringComparer.Ordinal);
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity, StringComparer.Ordinal);
        var moved = schedule.Where(t => !baseline.TryGetValue(t.Input.Identity, out var old) || old.Start != t.Start.Value || old.End != t.End.Value)
            .Select(t => t.Input.Identity).ToHashSet(StringComparer.Ordinal);
        var dependents = rows.ToDictionary(r => r.Identity, _ => new List<string>(), StringComparer.Ordinal);
        void Edge(string from, string to) { if (dependents.TryGetValue(from, out var list) && !list.Contains(to)) list.Add(to); }
        foreach (var row in rows)
        {
            foreach (var predecessor in row.Predecessors) Edge(predecessor, row.Identity);
            // A summary rolls up its children, and a task inherits its ancestors' predecessors.
            if (row.Parent is not null) Edge(row.Identity, row.Parent);
            var seen = new HashSet<string>(StringComparer.Ordinal) { row.Identity };
            for (var parent = row.Parent; parent is not null && seen.Add(parent) && byId.TryGetValue(parent, out var ancestor); parent = ancestor.Parent)
                foreach (var predecessor in ancestor.Predecessors) Edge(predecessor, row.Identity);
        }

        var cause = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var row in rows.Where(r => CauseFields(changes, r.Identity).Length > 0)) { cause[row.Identity] = row.Identity; queue.Enqueue(row.Identity); }
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            foreach (var dependent in dependents[next].OrderBy(id => order[id]))
                if (moved.Contains(dependent) && cause.TryAdd(dependent, cause[next])) queue.Enqueue(dependent);
        }
        var recalculated = changes.RecalculatedFields.Select(f => f.Identity).Where(order.ContainsKey).Distinct(StringComparer.Ordinal).OrderBy(id => order[id]);
        return recalculated.GroupBy(id => cause.GetValueOrDefault(id))
            .OrderBy(g => g.Key is null ? int.MaxValue : order[g.Key])
            .Select(g => new PlanRecalculationGroup(g.Key, g.Key is null ? [] : CauseFields(changes, g.Key), g.ToImmutableArray()))
            .ToImmutableArray();
    }

    internal static PlanChildOrderExcerpt ChildOrderExcerpt(PlanDocument document, ImmutableArray<string> before, ImmutableArray<string> after)
    {
        var published = document.Baseline.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        var previous = before.ToHashSet(StringComparer.Ordinal);
        var moved = PlanOrder.Moved(before, after);
        var changed = after.Select((id, index) => (id, index)).Where(p => !previous.Contains(p.id) || moved.Contains(p.id)).ToArray();
        var causes = changed.Select(p => (p.id, !published.Contains(p.id)))
            .Concat(before.Where(id => !after.Contains(id)).Select(id => (id, false))).ToImmutableArray();
        var windows = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        int start = -1, end = -1;
        foreach (var (_, index) in changed)
        {
            var (from, to) = (Math.Max(0, index - 1), Math.Min(after.Length - 1, index + 1));
            if (start >= 0 && from <= end + 1) { end = to; continue; }
            if (start >= 0) windows.Add(after[start..(end + 1)]);
            (start, end) = (from, to);
        }
        if (start >= 0) windows.Add(after[start..(end + 1)]);
        return new(causes, windows.ToImmutable());
    }
}

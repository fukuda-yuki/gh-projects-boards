using System.Collections.Immutable;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanTaskLinks
{
    internal static (EditPlanCells? Command, string? Reason) Prepare(PlanState state, IReadOnlyList<string> selected, bool unlink)
    {
        var outline = new PlanOutline(state.Rows);
        var ids = state.Rows.Select(r => r.Identity).ToHashSet();
        var expanded = selected.Where(ids.Contains).SelectMany(id => outline.Subtree(id)).Select(r => r.Identity).ToHashSet();
        var tasks = state.Rows.Where(r => outline.Subtree(r.Identity).Length == 1).ToArray();
        var changes = ImmutableArray.CreateBuilder<PlanCellChange>();
        if (unlink) {
            foreach (var row in tasks) {
                var predecessors = row.Predecessors.Where(id => expanded.Count == 1
                    ? expanded.Contains(row.Identity) ? !ids.Contains(id) : !expanded.Contains(id)
                    : !expanded.Contains(row.Identity) || !expanded.Contains(id)).ToImmutableArray();
                if (!predecessors.SequenceEqual(row.Predecessors)) changes.Add(new(row.Identity, PlanField.Predecessors, predecessors));
            }
        }
        else {
            var groups = tasks.Where(r => expanded.Contains(r.Identity)).GroupBy(r => r.Parent).Select(g => g.ToArray()).Where(g => g.Length > 1).ToArray();
            if (groups.Length == 0) return (null, "リンクできるタスクを2つ以上選択してください");
            foreach (var group in groups)
                for (var i = 1; i < group.Length; i++)
                    if (!group[i].Predecessors.Contains(group[i - 1].Identity))
                        changes.Add(new(group[i].Identity, PlanField.Predecessors, group[i].Predecessors.Add(group[i - 1].Identity)));
        }
        return changes.Count == 0 ? (null, unlink ? "解除するリンクがありません" : "選択したタスクはすでにリンクされています")
            : (new(PlanOperationKind.Cell, changes.ToImmutable()), null);
    }
}

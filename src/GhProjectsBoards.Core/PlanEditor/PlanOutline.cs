using System.Collections.Immutable;

namespace GhProjectsBoards.Core.PlanEditor;

// Built once per command/drag. DFS intervals include descendants regardless of display order or folding.
internal sealed class PlanOutline
{
    private readonly ImmutableArray<PlanRow> rows;
    private readonly Dictionary<string, PlanRow> byId;
    private readonly Dictionary<string, int> start = [], end = [];
    private readonly ImmutableArray<PlanRow> ordered;
    internal PlanOutline(ImmutableArray<PlanRow> rows)
    {
        this.rows = rows;
        byId = rows.ToDictionary(r => r.Identity);
        var children = rows.ToLookup(r => r.Parent is { } p && byId.ContainsKey(p) ? p : "");
        var result = ImmutableArray.CreateBuilder<PlanRow>();
        var stack = new Stack<(PlanRow Row, bool Exit)>();
        foreach (var root in children[""].Reverse()) stack.Push((root, false));
        while (stack.TryPop(out var entry)) {
            var id = entry.Row.Identity;
            if (entry.Exit) { end[id] = result.Count; continue; }
            start.Add(id, result.Count); result.Add(entry.Row);
            stack.Push((entry.Row, true));
            foreach (var child in children[id].Reverse()) stack.Push((child, false));
        }
        if (result.Count != rows.Length)
            throw new PlanCycleException("親が循環しています。", rows.Where(r => !start.ContainsKey(r.Identity)).Select(r => r.Identity).ToArray());
        ordered = result.ToImmutable();
    }
    internal ImmutableArray<PlanRow> Arrange() => ordered;
    internal bool IsContiguous => rows.Select(r => r.Identity).SequenceEqual(ordered.Select(r => r.Identity));
    internal ImmutableArray<PlanRow> Subtree(string id) => ordered[start[id]..end[id]];
    internal ImmutableArray<string> Roots(ImmutableArray<string> targets)
    {
        PlanOperations.Require(!targets.IsDefaultOrEmpty && targets.Distinct().Count() == targets.Length && targets.All(byId.ContainsKey), "移動する行を選択してください。");
        var selected = targets.ToHashSet();
        var roots = new HashSet<string>();
        var coveredUntil = -1;
        foreach (var row in ordered) {
            if (start[row.Identity] < coveredUntil || !selected.Contains(row.Identity)) continue;
            roots.Add(row.Identity); coveredUntil = end[row.Identity];
        }
        var result = rows.Where(r => roots.Contains(r.Identity)).Select(r => r.Identity).ToImmutableArray();
        var parent = byId[result[0]].Parent;
        PlanOperations.Require(result.All(id => byId[id].Parent == parent), "同じ親のタスクを選択してください。");
        return result;
    }
    private ImmutableArray<PlanRow> Siblings(ImmutableArray<string> roots)
        => rows.Where(r => r.Parent == byId[roots[0]].Parent).ToImmutableArray();
    internal ImmutableArray<PlanRow> Move(ImmutableArray<string> targets, string? before)
    {
        var roots = Roots(targets);
        var moving = roots.SelectMany(id => Subtree(id)).ToImmutableArray();
        var ids = moving.Select(r => r.Identity).ToHashSet();
        var siblings = Siblings(roots);
        PlanOperations.Require(before is null || siblings.Any(r => r.Identity == before) && !ids.Contains(before), "移動先には移動しない同じ親のタスクを指定してください。");
        var rest = rows.Where(r => !ids.Contains(r.Identity)).ToList();
        var group = siblings.SelectMany(r => Subtree(r.Identity)).Select(r => r.Identity).ToHashSet();
        var index = before is null ? rest.FindLastIndex(r => group.Contains(r.Identity)) + 1 : rest.FindIndex(r => r.Identity == before);
        // If the entire group moves, retain its original group location.
        if (before is null && index == 0 && !rest.Any(r => group.Contains(r.Identity)))
            index = rows.TakeWhile(r => !group.Contains(r.Identity)).Count(r => !ids.Contains(r.Identity));
        rest.InsertRange(index, moving);
        return rest.ToImmutableArray();
    }
    internal MovePlanSubtrees Step(ImmutableArray<string> targets, bool down)
    {
        var roots = Roots(targets); var siblings = Siblings(roots).Select(r => r.Identity).ToArray();
        var first = Array.IndexOf(siblings, roots[0]); var last = Array.IndexOf(siblings, roots[^1]);
        PlanOperations.Require(down ? last < siblings.Length - 1 : first > 0, down ? "最後のタスクです" : "先頭のタスクです");
        return new(targets, down ? last + 2 < siblings.Length ? siblings[last + 2] : null : siblings[first - 1]);
    }
    internal PlanOutlineGaps Gaps(ImmutableArray<string> targets, IReadOnlyList<string> visible)
    {
        var roots = Roots(targets); var moving = roots.ToHashSet(); var siblings = Siblings(roots);
        var positions = visible.Select((id, i) => (id, i)).ToDictionary(v => v.id, v => v.i);
        var group = siblings.SelectMany(r => Subtree(r.Identity)).Select(r => r.Identity).Where(positions.ContainsKey).Select(id => positions[id]).ToArray();
        var visibleSiblings = siblings.Where(r => positions.ContainsKey(r.Identity)).Select(r => positions[r.Identity]).ToArray();
        PlanOperations.Require(group.Length > 0 && visibleSiblings.Length > 0, "移動先が表示されていません");
        var min = visibleSiblings.Min(); var max = group.Max() + 1;
        var boundaries = siblings.Where(r => !moving.Contains(r.Identity) && positions.ContainsKey(r.Identity))
            .Select(r => new PlanOutlineGap(positions[r.Identity], r.Identity)).Append(new(max, null)).OrderBy(g => g.Index).ToImmutableArray();
        return new(min, max, boundaries);
    }
    internal ImmutableArray<PlanRow> Import(ImmutableArray<PlanRow> additions)
    {
        var imported = new PlanOutline(additions);
        var importedIds = additions.Select(r => r.Identity).ToHashSet();
        var result = rows.ToList();
        foreach (var root in imported.ordered.Where(r => r.Parent is null || !importedIds.Contains(r.Parent))) {
            var subtree = imported.Subtree(root.Identity);
            var index = result.Count;
            if (root.Parent is { } parent && byId.ContainsKey(parent)) {
                var descendants = Subtree(parent).Select(r => r.Identity).ToHashSet();
                // Earlier imported children belong to the same insertion tail.
                foreach (var row in result) if (row.Parent is { } p && descendants.Contains(p)) descendants.Add(row.Identity);
                index = result.FindLastIndex(r => descendants.Contains(r.Identity)) + 1;
            }
            result.InsertRange(index, subtree);
        }
        return result.ToImmutableArray();
    }
    internal ImmutableArray<PlanRow> Outdent(ImmutableArray<string> targets)
    {
        var roots = Roots(targets); var parent = byId[roots[0]].Parent;
        PlanOperations.Require(parent is not null && byId.ContainsKey(parent), "親タスクがありません。");
        var moving = roots.SelectMany(id => Subtree(id)).ToArray(); var ids = moving.Select(r => r.Identity).ToHashSet();
        var parentTree = Subtree(parent!).Select(r => r.Identity).ToHashSet();
        var rest = rows.Where(r => !ids.Contains(r.Identity)).ToList();
        rest.InsertRange(rest.FindLastIndex(r => parentTree.Contains(r.Identity)) + 1,
            moving.Select(r => roots.Contains(r.Identity) ? r with { Parent = byId[parent!].Parent } : r));
        return rest.ToImmutableArray();
    }
}
internal sealed record PlanOutlineGap(int Index, string? Before);
internal sealed record PlanOutlineGaps(int First, int Last, ImmutableArray<PlanOutlineGap> Boundaries)
{
    internal PlanOutlineGap? Snap(double gap) => gap < First || gap > Last ? null
        : Boundaries.MinBy(b => Math.Abs(b.Index - gap));
}

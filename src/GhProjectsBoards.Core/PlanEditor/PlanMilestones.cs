using System.Collections.Immutable;

namespace GhProjectsBoards.Core.PlanEditor;

// #298: one repository can run several versions in parallel, each a separate Project with its own
// milestones. The complete repository catalog stays for validating milestone-cell edits and detecting
// refresh conflicts; only the exact-title groups a current plan row belongs to are this Project's plan.
internal static class PlanMilestones
{
    internal static ImmutableArray<PlanMilestone> InScope(PlanDocument document) => InScope(document.State.Rows, document.Baseline.Milestones);

    internal static ImmutableArray<PlanMilestone> InScope(IEnumerable<PlanRow> rows, ImmutableArray<PlanMilestone> catalog)
    {
        var used = rows.Select(row => row.MilestoneTitle).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return catalog.Where(milestone => used.Contains(milestone.Title)).ToImmutableArray();
    }
}

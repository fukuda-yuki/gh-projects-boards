namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanInitialSettings
{
    internal static ProjectPlanSettings From(PlanBaseline baseline, ProjectPlanSettings settings)
    {
        var start = settings.ProjectStart ?? baseline.Rows.Min(r => r.Start);
        var end = settings.ProjectEnd ?? baseline.Milestones.Max(m => m.DueDate) ?? baseline.Rows.Max(r => r.End);
        if (start > end) { start = settings.ProjectStart; end = settings.ProjectEnd; }
        var mappings = settings.PhaseMilestones;
        var field = settings.Columns.SingleOrDefault(c => c.Role == PlanField.Phase);
        var options = baseline.Columns.SingleOrDefault(c => c.Id == field?.FieldId)?.Options ?? [];
        foreach (var option in options)
        {
            if (mappings.ContainsKey(option)) continue;
            var matches = baseline.Milestones.Where(m => m.Title.Equals(option, StringComparison.OrdinalIgnoreCase)
                || m.Title.StartsWith(option, StringComparison.OrdinalIgnoreCase) && m.Title.Length > option.Length
                    && !char.IsLetterOrDigit(m.Title, option.Length)).ToArray();
            if (matches.Length == 1) mappings = mappings.Add(option, matches[0].Title);
        }
        return settings with { ProjectStart = start, ProjectEnd = end, PhaseMilestones = mappings };
    }
}

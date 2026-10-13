namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanInitialSettings
{
    internal static ProjectPlanSettings From(PlanBaseline baseline, ProjectPlanSettings settings)
    {
        // One repository can run several versions in parallel, each a separate Project with its own milestones,
        // so only milestones that this Project's Issues belong to describe its period and phases.
        var used = baseline.Rows.Select(r => r.MilestoneTitle).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var milestones = baseline.Milestones.Where(m => used.Contains(m.Title)).ToArray();
        var start = settings.ProjectStart ?? baseline.Rows.Min(r => r.Start);
        var end = settings.ProjectEnd ?? milestones.Max(m => m.DueDate) ?? baseline.Rows.Max(r => r.End);
        if (start > end) { start = settings.ProjectStart; end = settings.ProjectEnd; }
        var mappings = settings.PhaseMilestones;
        var field = settings.Columns.SingleOrDefault(c => c.Role == PlanField.Phase);
        var options = baseline.Columns.SingleOrDefault(c => c.Id == field?.FieldId)?.Options ?? [];
        foreach (var option in options)
        {
            if (mappings.ContainsKey(option)) continue;
            var matches = milestones.Where(m => ContainsToken(m.Title, option)).ToArray();
            if (matches.Length == 1) mappings = mappings.Add(option, matches[0].Title);
        }
        return settings with { ProjectStart = start, ProjectEnd = end, PhaseMilestones = mappings };
    }

    // Titles are often version-prefixed (第2027.04版 SA 完了) or unspaced (SA完了); an ASCII boundary keeps P from matching PS.
    private static bool ContainsToken(string title, string option)
    {
        if (option.Length == 0) return false;
        for (var at = title.IndexOf(option, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = title.IndexOf(option, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + option.Length;
            var before = at == 0 || !char.IsAsciiLetterOrDigit(option[0]) || !char.IsAsciiLetterOrDigit(title[at - 1]);
            var after = end == title.Length || !char.IsAsciiLetterOrDigit(option[^1]) || !char.IsAsciiLetterOrDigit(title[end]);
            if (before && after) return true;
        }
        return false;
    }
}

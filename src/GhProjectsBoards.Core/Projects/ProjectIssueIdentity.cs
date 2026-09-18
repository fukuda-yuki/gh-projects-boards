namespace GhProjectsBoards.Core.Projects;

internal static class ProjectIssueIdentity
{
    // The accepted Project membership determines identity, never its current row filter.
    public static bool NeedsRepository(ProjectReadModel project) => project.Items
        .Where(item => item.Kind == ProjectItemKind.Issue && item.ContentId is not null)
        .Select(item => project.Issues.GetValueOrDefault(item.ContentId!)?.Repository.Id)
        .Where(id => id is not null).Distinct().Take(2).Count() > 1;
}

namespace GhProjectsBoards.Core.Projects;

internal enum ProjectRepositoryKind { All, Repository, Unlinked }
internal sealed record ProjectRepositoryChoice(ProjectRepositoryKind Kind, string? NameWithOwner, string DisplayText, int Count);
internal enum ProjectChooserEmptyReason { None, NoProjects, AllClosed, NoSearchMatch }
internal static class ProjectChooserFilter
{
    public static IReadOnlyList<ProjectRepositoryChoice> Repositories(IEnumerable<ProjectListing> listings, string owner, bool includeClosed)
    {
        var shown = listings.Where(p => includeClosed || !p.Closed).ToArray();
        var entries = new List<ProjectRepositoryChoice> { new(ProjectRepositoryKind.All, null, "すべてのリポジトリ", shown.Length) };
        entries.AddRange(shown.SelectMany(p => p.Repositories.Distinct(StringComparer.OrdinalIgnoreCase)).GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectRepositoryChoice(ProjectRepositoryKind.Repository, g.Key, RepositoryName(g.Key, owner), g.Count()))
            .OrderBy(r => r.DisplayText, StringComparer.OrdinalIgnoreCase));
        var unlinked = shown.Count(p => p.Repositories.IsEmpty);
        if (unlinked > 0) entries.Add(new(ProjectRepositoryKind.Unlinked, null, "リポジトリ未リンク", unlinked));
        return entries;
    }
    public static string RepositoryName(string name, string owner) => name.StartsWith(owner + "/", StringComparison.OrdinalIgnoreCase) ? name[(owner.Length + 1)..] : name;
    private static bool Matches(ProjectListing listing, ProjectRepositoryChoice repository) => repository.Kind switch {
        ProjectRepositoryKind.Repository => listing.Repositories.Contains(repository.NameWithOwner!, StringComparer.OrdinalIgnoreCase),
        ProjectRepositoryKind.Unlinked => listing.Repositories.IsEmpty, _ => true };
    public static IReadOnlyList<ProjectListing> Projects(IEnumerable<ProjectListing> listings, ProjectRepositoryChoice repository, bool includeClosed, string search)
    {
        var text = search.Trim();
        var numberText = text.StartsWith('#') ? text[1..] : text;
        var numeric = numberText.Length > 0 && numberText.All(char.IsAsciiDigit);
        var number = int.TryParse(numberText, out var parsed) ? parsed : -1;
        return listings.Where(p => Matches(p, repository) && (includeClosed || !p.Closed) &&
            (text.Length == 0 || numeric && p.Choice.Number == number || !text.StartsWith('#') && p.Choice.Title.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Choice.Number).ToArray();
    }
    public static int ClosedCount(IEnumerable<ProjectListing> listings, ProjectRepositoryChoice repository) => listings.Count(p => p.Closed && Matches(p, repository));
    public static ProjectChooserEmptyReason EmptyReason(IEnumerable<ProjectListing> listings, ProjectRepositoryChoice repository, bool includeClosed, string search)
    {
        var source = listings.ToArray();
        if (Projects(source, repository, includeClosed, search).Count > 0) return ProjectChooserEmptyReason.None;
        var selected = source.Where(p => Matches(p, repository)).ToArray();
        if (selected.Length == 0) return ProjectChooserEmptyReason.NoProjects;
        if (!includeClosed && selected.All(p => p.Closed)) return ProjectChooserEmptyReason.AllClosed;
        return ProjectChooserEmptyReason.NoSearchMatch;
    }
}

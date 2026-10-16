using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ProjectChooserFilterTests
{
    private static ProjectListing Listing(int number, string title, bool closed, int day, params string[] repositories)
        => new(new(new(new("github.com", 42), "P" + number), new(new("github.com", 42), "O1"), "acme", "Organization", number,
            $"https://github.com/orgs/acme/projects/{number}", title), closed, new(2026, 10, day, 0, 0, 0, TimeSpan.Zero), repositories.ToImmutableArray());
    private static readonly ProjectListing[] Listings = [Listing(12, "Release Alpha", false, 3, "acme/zeta", "other/beta"),
        Listing(3, "ALPHA maintenance", true, 5, "acme/zeta"), Listing(8, "Unlinked", false, 3), Listing(4, "Beta", false, 2, "acme/Alpha")];
    private static readonly ProjectRepositoryChoice All = new(ProjectRepositoryKind.All, null, "", 0);

    [TestCase(false), TestCase(true)]
    public void RepositoryEntriesCountShownProjectsAndKeepUnlinkedLast(bool closed)
    {
        var entries = ProjectChooserFilter.Repositories(Listings, "ACME", closed);
        Assert.That(entries.Select(e => e.DisplayText), Is.EqualTo(new[] { "すべてのリポジトリ", "Alpha", "other/beta", "zeta", "リポジトリ未リンク" }));
        Assert.That(entries.Select(e => e.Count), Is.EqualTo(new[] { closed ? 4 : 3, 1, 1, closed ? 2 : 1, 1 }));
        Assert.That(entries.Last().Kind, Is.EqualTo(ProjectRepositoryKind.Unlinked));
        Assert.That(ProjectChooserFilter.Repositories([Listings[0]], "acme", false).Any(e => e.Kind == ProjectRepositoryKind.Unlinked), Is.False);
        Assert.That(ProjectChooserFilter.Repositories([Listings[1]], "acme", false).Select(e => e.Count), Is.EqualTo(new[] { 0 }));
    }
    [Test]
    public void RepositoryCountsDeduplicateLinksAndOmitClosedOnlyEntriesUntilRequested()
    {
        ProjectListing[] listings = [Listing(1, "Open", false, 1, "acme/repo", "ACME/REPO"), Listing(2, "Closed", true, 1, "acme/closed")];
        Assert.That(ProjectChooserFilter.Repositories(listings, "acme", false).Select(r => (r.DisplayText, r.Count)), Is.EqualTo(new[] { ("すべてのリポジトリ", 1), ("repo", 1) }));
        Assert.That(ProjectChooserFilter.Repositories(listings, "acme", true).Select(r => (r.DisplayText, r.Count)), Is.EqualTo(new[] { ("すべてのリポジトリ", 2), ("closed", 1), ("repo", 1) }));
    }
    [TestCase("", 12, 8, 4), TestCase(" alpha ", 12), TestCase("12", 12), TestCase(" #12 ", 12), TestCase("2")]
    public void SearchTrimsAndMatchesTitleOrExactNumber(string search, params int[] expected)
        => Assert.That(ProjectChooserFilter.Projects(Listings, All, false, search).Select(p => p.Choice.Number), Is.EqualTo(expected));
    [Test]
    public void DigitsAlsoMatchTitlesWhileHashMatchesOnlyTheNumber()
    {
        ProjectListing[] listings = [Listing(5, "第2027.04版", false, 4), Listing(2027, "Other", false, 3)];
        Assert.That(ProjectChooserFilter.Projects(listings, All, false, "2027").Select(p => p.Choice.Number), Is.EqualTo(new[] { 5, 2027 }));
        Assert.That(ProjectChooserFilter.Projects(listings, All, false, "#2027").Select(p => p.Choice.Number), Is.EqualTo(new[] { 2027 }));
    }
    [Test]
    public void RepositoryChoicesClosedToggleAndOrderingAreIndependentOfSearch()
    {
        var repository = new ProjectRepositoryChoice(ProjectRepositoryKind.Repository, "ACME/ZETA", "", 0);
        Assert.That(ProjectChooserFilter.Projects(Listings, repository, true, "").Select(p => p.Choice.Number), Is.EqualTo(new[] { 3, 12 }));
        Assert.That(ProjectChooserFilter.Projects(Listings, repository, false, "").Select(p => p.Choice.Number), Is.EqualTo(new[] { 12 }));
        Assert.That(ProjectChooserFilter.Projects(Listings, All with { Kind = ProjectRepositoryKind.Unlinked }, false, "").Single().Choice.Number, Is.EqualTo(8));
        Assert.That(ProjectChooserFilter.ClosedCount(Listings, repository), Is.EqualTo(1));
        Assert.That(ProjectChooserFilter.ClosedCount(Listings, All with { Kind = ProjectRepositoryKind.Unlinked }), Is.Zero);
        Assert.That(ProjectChooserFilter.Projects(Listings, All, true, "").Select(p => p.Choice.Number), Is.EqualTo(new[] { 3, 12, 8, 4 }));
    }
    [Test]
    public void EmptyReasonsDistinguishOwnerClosedAndSearch()
    {
        Assert.That(ProjectChooserFilter.EmptyReason([], All, false, ""), Is.EqualTo(ProjectChooserEmptyReason.NoProjects));
        Assert.That(ProjectChooserFilter.EmptyReason([Listings[1]], All, false, "missing"), Is.EqualTo(ProjectChooserEmptyReason.AllClosed));
        Assert.That(ProjectChooserFilter.EmptyReason(Listings, All, false, "missing"), Is.EqualTo(ProjectChooserEmptyReason.NoSearchMatch));
        Assert.That(ProjectChooserFilter.EmptyReason(Listings, All, false, "Alpha"), Is.EqualTo(ProjectChooserEmptyReason.None));
        var repository = All with { Kind = ProjectRepositoryKind.Repository, NameWithOwner = "acme/zeta" };
        Assert.That(ProjectChooserFilter.EmptyReason([Listings[1]], repository, false, ""), Is.EqualTo(ProjectChooserEmptyReason.AllClosed));
        Assert.That(ProjectChooserFilter.EmptyReason([Listings[1]], repository, true, "missing"), Is.EqualTo(ProjectChooserEmptyReason.NoSearchMatch));
        Assert.That(ProjectChooserFilter.ClosedCount(Listings, All), Is.EqualTo(1));
    }
}

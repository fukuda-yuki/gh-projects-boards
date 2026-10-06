using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ProjectIssueIdentityTests
{
    [TestCase(false, false), TestCase(false, true), TestCase(true, true), TestCase(true, false)]
    public void AcceptedMembershipDeterminesRepositoryDisambiguation(bool secondRepository, bool secondMember)
    {
        var scope = new ConnectionScope("github.com", 42);
        var repository = new RepositoryReadModel(new(scope, "R1"), new(scope, "O1"), "owner/first");
        var rows = Enumerable.Range(1, 2).Select(i => new IssueReadModel(new(scope, "I" + i), repository, i,
            "https://github.com/owner/first/issues/" + i, new(ValueAvailability.Present, "Task " + i), new(ValueAvailability.Present, IssueState.Open))).ToArray();
        var project = new ProjectReadModel(new(scope, "P1"), new(scope, "O1"), "User", 1, "https://github.com/users/owner/projects/1", "Plan", [],
            rows.ToDictionary(r => r.Id), rows.Select(r => new ProjectItemReadModel(new(scope, "T" + r.Number), ProjectItemKind.Issue, "ISSUE", r.Id, false, [], true)).ToArray(), true, true);
        var issues = project.Issues.ToDictionary();
        var second = issues.Values.Last();
        if (secondRepository) issues[second.Id] = second with { Repository = second.Repository with {
            Id = new(project.Id.Scope, "R2"), NameWithOwner = "owner/second" } };
        project = project with { Issues = issues, Items = secondMember ? project.Items : project.Items.Take(1).ToArray() };
        Assert.That(ProjectIssueIdentity.NeedsRepository(project), Is.EqualTo(secondRepository && secondMember));
    }
}

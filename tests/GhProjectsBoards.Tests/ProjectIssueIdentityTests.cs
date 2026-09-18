using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ProjectIssueIdentityTests
{
    [TestCase(false, false), TestCase(false, true), TestCase(true, true), TestCase(true, false)]
    public void AcceptedMembershipDeterminesRepositoryDisambiguation(bool secondRepository, bool secondMember)
    {
        var project = EditingTests.Registration(count: 2).Snapshot;
        var issues = project.Issues.ToDictionary();
        var second = issues.Values.Last();
        if (secondRepository) issues[second.Id] = second with { Repository = second.Repository with {
            Id = new(project.Id.Scope, "R2"), NameWithOwner = "owner/second" } };
        project = project with { Issues = issues, Items = secondMember ? project.Items : project.Items.Take(1).ToArray() };
        Assert.That(ProjectIssueIdentity.NeedsRepository(project), Is.EqualTo(secondRepository && secondMember));
    }
}

using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #319: the organization Issue type catalog can only be exercised against a real organization Project.
// Read-only, opt-in, and confined to the organization Project named by the owning Issue (#319, ls-2026-class4).
[TestFixture]
[Category("LiveGitHub")]
[NonParallelizable]
internal sealed class OrgIssueTypeReadLiveTests
{
    [Test]
    public async Task ReadsAnOrganizationProjectTypeCatalogWithoutMutation()
    {
        var artifacts = Environment.GetEnvironmentVariable("GHPB_ORG_TYPE_READ_ARTIFACTS");
        if (Environment.GetEnvironmentVariable("GHPB_RUN_ORG_TYPE_READ") != "1" || artifacts is null)
            Assert.Ignore("Set GHPB_RUN_ORG_TYPE_READ=1 and GHPB_ORG_TYPE_READ_ARTIFACTS; read-only organization Project check.");
        Directory.CreateDirectory(artifacts);
        var service = new GhConnectionService(Environment.GetEnvironmentVariable("GHPB_ORG_TYPE_READ_GH")!, "github.com");
        var started = DateTimeOffset.UtcNow;
        var connection = await service.ConnectAsync();
        Assert.That(connection.IsConnected, Is.True, connection.Result.ToString());
        var scope = await service.SendAsync(connection.Context!, ApiRequest.GraphQl("""
            query OrganizationProjectScope {
              organization(login: "ls-2026-class4") { projectV2(number: 1) { id number } }
            }
            """));
        Assert.That(scope.IsSuccess, Is.True, scope.ToString());
        var projectNode = scope.Data!.Value.GetProperty("data").GetProperty("organization").GetProperty("projectV2");
        var projectId = projectNode.GetProperty("id").GetString()!;
        var read = await new ProjectReader(service).ReadAsync(connection.Context!, new(ConnectionScope.From(connection.Context!), projectId));
        var elapsed = DateTimeOffset.UtcNow - started;
        Assert.That(read.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), read.ToString());
        var project = read.Project!;
        Assert.That(project.IssueTypesComplete, Is.True);
        Assert.That(project.IssueTypes.Select(t => t.Name), Is.SupersetOf(new[] { "Task", "Bug", "Feature" }));
        Assert.That(project.IssueTypeRepositories, Is.Not.Empty, "The organization project's Issues belong to organization repositories.");
        var types = project.Issues.Values.Select(i => i.IssueType).ToArray();
        Assert.That(types.All(v => v.Availability is ValueAvailability.Present or ValueAvailability.Empty), Is.True);
        File.WriteAllText(Path.Combine(artifacts, "org-issue-type-read.json"), JsonSerializer.Serialize(new
        {
            verified = true, projectId, project.Number, items = project.Items.Count, issues = project.Issues.Count,
            catalogs = project.IssueTypes.Select(t => new { t.Id, t.Name }).ToArray(),
            repositories = project.IssueTypeRepositories, issuesWithType = types.Count(v => v.Value is not null),
            complete = project.IssueTypesComplete, elapsedMs = (long)elapsed.TotalMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

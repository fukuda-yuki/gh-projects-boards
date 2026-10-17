using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class ProjectListingDiscoveryTests
{
    private static JsonObject Node() => JsonNode.Parse("""{"id":"P1","number":12,"url":"https://github.com/orgs/acme/projects/12","title":"Release","owner":{"id":"O1","__typename":"Organization","login":"acme"},"closed":false,"updatedAt":"2026-10-17T01:00:00Z","repositories":{"nodes":[{"nameWithOwner":"acme/repo"}]}}""")!.AsObject();
    private static async Task<IReadOnlyList<ProjectListing>> Read(Func<JsonElement, object> response, Action<string>? queryCheck = null)
    {
        var runner = new ScriptedRunner(command => {
            if (command.Arguments[0] == "--version") return new(ProcessCompletion.Exited, true, 0, "gh version 2.80.0");
            if (command.Arguments[0] == "auth") return new(ProcessCompletion.Exited, true, 0, """[{"host":"github.com","login":"fixture-user","active":true,"state":"success","tokenSource":"keyring","scopes":"repo,project"}]""");
            if (command.Arguments.Contains("user")) return ScriptedRunner.Http("""{"id":42,"login":"fixture-user"}""");
            using var payload = JsonDocument.Parse(command.StandardInput!);
            queryCheck?.Invoke(payload.RootElement.GetProperty("query").GetString()!);
            return ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { repositoryOwner = new { projectsV2 = response(payload.RootElement.GetProperty("variables")) } } }));
        });
        var service = new GhConnectionService("fake-gh", "github.com", runner);
        var connection = await service.ConnectAsync();
        return await new ProjectDiscovery(service).ListingsAsync(connection.Context!, "acme", default);
    }
    [Test]
    public async Task ListingPagesReadClosedUpdatedAndLinkedRepositoriesInUpdatedOrder()
    {
        var calls = 0;
        var results = await Read(v => {
            var node = Node(); if (calls++ > 0) node["id"] = "P2";
            Assert.That(v.GetProperty("owner").GetString(), Is.EqualTo("acme"));
            return FakePlanEditor.Page([node], 2, calls == 1, calls == 1 ? "next" : null);
        }, query => {
            Assert.That(query, Does.Contain("RegistrationProjects").And.Contain("first:50").And.Contain("minPermissionLevel:READ")
                .And.Contain("orderBy:{field:UPDATED_AT, direction:DESC}").And.Contain("repositories(first:100)").And.Contain("closed updatedAt"));
        });
        Assert.That(calls, Is.EqualTo(2));
        Assert.That(results.Select(p => p.Choice.Id.NodeId), Is.EqualTo(new[] { "P1", "P2" }));
        Assert.That(results[0].Closed, Is.False);
        Assert.That(results[0].UpdatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-10-17T01:00:00Z")));
        Assert.That(results[0].Repositories, Is.EqualTo(new[] { "acme/repo" }));
    }
    [Test]
    public async Task UnreadableLinkedRepositoryIsOmittedWithoutHidingTheProject()
    {
        var results = await Read(_ => { var node = Node(); node["repositories"]!["nodes"]!.AsArray().Add(null); return FakePlanEditor.Page([node], 1); });
        Assert.That(results.Single().Repositories, Is.EqualTo(new[] { "acme/repo" }));
    }
    [TestCase("closed"), TestCase("updatedAt"), TestCase("repositories"), TestCase("invalid-date"), TestCase("invalid-closed"), TestCase("invalid-repository"), TestCase("duplicate")]
    [TestCase("null-closed"), TestCase("null-repositories"), TestCase("null-owner"), TestCase("invalid-number"), TestCase("missing-repository-name")]
    public void InvalidListingFailsWithoutReturningPartialData(string invalid)
    {
        var node = Node();
        if (invalid == "invalid-date") node["updatedAt"] = "tomorrow";
        else if (invalid.StartsWith("null-")) node[invalid[5..]] = null;
        else if (invalid == "invalid-number") node["number"] = "twelve";
        else if (invalid == "missing-repository-name") node["repositories"]!["nodes"]![0]!.AsObject().Remove("nameWithOwner");
        else if (invalid == "invalid-closed") node["closed"] = "false";
        else if (invalid == "invalid-repository") node["repositories"]!["nodes"]![0]!["nameWithOwner"] = "repo";
        else if (invalid != "duplicate") node.Remove(invalid);
        var error = Assert.ThrowsAsync<DiscoveryException>(async () => await Read(_ => FakePlanEditor.Page(invalid == "duplicate" ? [node, node] : [node], 1)));
        Assert.That(error!.Failure, Is.EqualTo(FailureKind.InvalidResponse));
    }
}

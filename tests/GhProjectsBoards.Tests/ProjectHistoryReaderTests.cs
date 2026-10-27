using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #308: the bounded read of the Issue timeline. The reader owns filtering to this Project, actor/automation,
// pagination and completeness. The continuation request is checked at the gh boundary: GitHub rejects a
// second argument list written as timelineItems(...)(after:).
[TestFixture]
internal sealed class ProjectHistoryReaderTests
{
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");

    private static (GhConnectionService Service, ScriptedRunner Runner) Boundary(Func<JsonElement, JsonNode?> history)
    {
        var runner = new ScriptedRunner(command =>
        {
            if (command.Arguments[0] == "--version") return new(ProcessCompletion.Exited, true, 0, "gh version 2.100.0");
            if (command.Arguments[0] == "auth") return GhConnectionTests.Auth();
            if (command.Arguments[1] == "user") return ScriptedRunner.Http(JsonSerializer.Serialize(new { id = 42, login = "sample-user" }));
            using var payload = JsonDocument.Parse(command.StandardInput!);
            var variables = payload.RootElement.GetProperty("variables");
            var node = history(variables);
            return ScriptedRunner.Http(new JsonObject { ["data"] = new JsonObject { ["viewer"] = new JsonObject { ["databaseId"] = 42 }, ["nodes"] = node } }.ToJsonString());
        });
        return (new GhConnectionService("gh.exe", "github.com", runner), runner);
    }

    private static JsonNode? Node(string id, string updatedAt, JsonArray events, string projectId = "P1")
        => new JsonObject { ["__typename"] = "Issue", ["id"] = id, ["updatedAt"] = updatedAt,
            ["timelineItems"] = new JsonObject { ["totalCount"] = events.Count, ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = null }, ["nodes"] = events } };

    private static JsonObject StatusEvent(string at, string previous, string next, string projectId = "P1",
        string actor = "fukuda-yuki", string type = "User")
        => new() { ["__typename"] = "ProjectV2ItemStatusChangedEvent", ["createdAt"] = at, ["actor"] = new JsonObject { ["__typename"] = type, ["login"] = actor },
            ["previousStatus"] = previous, ["status"] = next, ["project"] = new JsonObject { ["id"] = projectId } };
    private static JsonObject CloseEvent(string at, string reason)
        => new() { ["__typename"] = "ClosedEvent", ["createdAt"] = at, ["actor"] = new JsonObject { ["__typename"] = "User", ["login"] = "fukuda-yuki" }, ["stateReason"] = reason };
    private static JsonObject ReopenEvent(string at)
        => new() { ["__typename"] = "ReopenedEvent", ["createdAt"] = at, ["actor"] = new JsonObject { ["__typename"] = "User", ["login"] = "fukuda-yuki" } };

    private static async Task<(ProjectHistoryResult Result, ScriptedRunner Runner)> Read(Dictionary<string, JsonNode?> nodes, params string[] ids)
    {
        var (service, runner) = Boundary(variables =>
        {
            var array = new JsonArray();
            foreach (var id in variables.GetProperty("ids").EnumerateArray().Select(v => v.GetString()!)) array.Add(nodes.GetValueOrDefault(id));
            return array;
        });
        var report = await service.ConnectAsync();
        Assert.That(report.IsConnected, Is.True);
        var result = await new ProjectHistoryReader(service, null).ReadAsync(report.Context!, Project, ids);
        return (result, runner);
    }

    [Test]
    public async Task ReadsStatusChangesClosesAndReopensOfThisProject()
    {
        var events = new JsonArray(StatusEvent("2026-10-01T03:00:00Z", "Ready", "In progress"),
            StatusEvent("2026-10-02T03:00:00Z", "Other", "Done", projectId: "P9"),
            CloseEvent("2026-10-03T06:00:00Z", "COMPLETED"), ReopenEvent("2026-10-04T06:00:00Z"));
        var (result, _) = await Read(new() { ["I1"] = Node("I1", "2026-10-04T07:00:00Z", events) }, "I1");
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        var history = result.History!["I1"];
        Assert.That(history.IssueId, Is.EqualTo("I1"));
        Assert.That(history.UpdatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-10-04T07:00:00Z")));
        Assert.That(history.StatusChanges.Select(s => (s.Previous, s.New, s.Actor, s.Automated)), Is.EqualTo(new[] {
            (("Ready", "In progress", "fukuda-yuki", false)) }), "a change in another Project is ignored");
        Assert.That(history.Closes.Single().StateReason, Is.EqualTo("COMPLETED"));
        Assert.That(history.Reopens.Single().Actor, Is.EqualTo("fukuda-yuki"));
    }

    [Test]
    public async Task MarksGitHubAutomationAsAutomated()
    {
        var events = new JsonArray(StatusEvent("2026-10-01T03:00:00Z", "Ready", "In progress", actor: "github-project-automation", type: "Bot"));
        var (result, _) = await Read(new() { ["I1"] = Node("I1", "2026-10-01T03:00:00Z", events) }, "I1");
        Assert.That(result.History!["I1"].StatusChanges.Single().Automated, Is.True);
    }

    [Test]
    public async Task FollowsTheTimelinePagesWhenOnePageIsNotEnough()
    {
        var first = new JsonArray(StatusEvent("2026-10-01T03:00:00Z", "Ready", "In progress"));
        var second = new JsonArray(CloseEvent("2026-10-02T06:00:00Z", "COMPLETED"));
        var runner = new ScriptedRunner(command =>
        {
            if (command.Arguments[0] == "--version") return new(ProcessCompletion.Exited, true, 0, "gh version 2.100.0");
            if (command.Arguments[0] == "auth") return GhConnectionTests.Auth();
            if (command.Arguments[1] == "user") return ScriptedRunner.Http(JsonSerializer.Serialize(new { id = 42, login = "sample-user" }));
            using var payload = JsonDocument.Parse(command.StandardInput!);
            var query = payload.RootElement.GetProperty("query").GetString()!;
            var data = query.Contains("PlanIssueHistoryPage")
                ? new JsonObject { ["viewer"] = new JsonObject { ["databaseId"] = 42 }, ["node"] = new JsonObject { ["__typename"] = "Issue", ["id"] = "I1",
                    ["timelineItems"] = new JsonObject { ["totalCount"] = 2, ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = null }, ["nodes"] = second } } }
                : new JsonObject { ["viewer"] = new JsonObject { ["databaseId"] = 42 }, ["nodes"] = new JsonArray(new JsonObject {
                    ["__typename"] = "Issue", ["id"] = "I1", ["updatedAt"] = "2026-10-02T07:00:00Z",
                    ["timelineItems"] = new JsonObject { ["totalCount"] = 2, ["pageInfo"] = new JsonObject { ["hasNextPage"] = true, ["endCursor"] = "c1" }, ["nodes"] = first } }) };
            return ScriptedRunner.Http(new JsonObject { ["data"] = data }.ToJsonString());
        });
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var report = await service.ConnectAsync();
        var result = await new ProjectHistoryReader(service, null).ReadAsync(report.Context!, Project, ["I1"]);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", result.Problems));
        Assert.That(result.History!["I1"].StatusChanges.Count, Is.EqualTo(1));
        Assert.That(result.History!["I1"].Closes.Count, Is.EqualTo(1));
        var page = runner.Commands.Single(c => c.StandardInput?.Contains("PlanIssueHistoryPage") == true);
        using var sent = JsonDocument.Parse(page.StandardInput!);
        var query = sent.RootElement.GetProperty("query").GetString();
        Assert.That(query, Does.Contain("timelineItems(first: 100, itemTypes: [PROJECT_V2_ITEM_STATUS_CHANGED_EVENT, CLOSED_EVENT, REOPENED_EVENT], after:$after)"));
        Assert.That(query, Does.Not.Contain(")(after:"));
        Assert.That(sent.RootElement.GetProperty("variables").GetProperty("after").GetString(), Is.EqualTo("c1"));
    }

    [Test]
    public async Task AMissingIssueIsAConcurrentChangeAndNotACompleteRead()
    {
        var (result, _) = await Read(new() { ["I1"] = Node("I1", "2026-10-01T03:00:00Z", []) }, "I1", "I2");
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Any(p => p.Kind == ReadProblemKind.ConcurrentChange), Is.True);
    }

    [Test]
    public async Task RequestsAtMostTwentyFiveIssuesPerQuery()
    {
        var ids = Enumerable.Range(1, 30).Select(n => "I" + n).ToArray();
        var nodes = ids.ToDictionary(id => id, id => (JsonNode?)Node(id, "2026-10-01T03:00:00Z", []));
        var (result, runner) = await Read(nodes, ids);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        var batches = runner.Commands
            .Where(c => c.Arguments[0] == "api" && c.Arguments[1] == "graphql" && c.StandardInput?.Contains("PlanIssueHistories") == true)
            .Select(c => JsonDocument.Parse(c.StandardInput!).RootElement.GetProperty("variables").GetProperty("ids").GetArrayLength())
            .ToArray();
        Assert.That(batches, Is.EqualTo(new[] { 25, 5 }), "a batch is at most 25 Issues");
    }
}

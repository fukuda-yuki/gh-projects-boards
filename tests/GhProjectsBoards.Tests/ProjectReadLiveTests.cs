using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("LiveGitHub")]
[NonParallelizable]
internal sealed class ProjectReadLiveTests
{
    [Test]
    public async Task ReadsExistingSandboxProjectThroughProductionReaderWithoutMutation()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_PROJECT_READ") != "1")
            Assert.Ignore("Use scripts/Test-ProjectRead.ps1 after reading the sandbox scope Issue.");
        var directory = Environment.GetEnvironmentVariable("GHPB_PROJECT_READ_ARTIFACTS")!;
        Directory.CreateDirectory(directory);
        var runner = new ReadOnlyRunner();
        var service = new GhConnectionService(Environment.GetEnvironmentVariable("GHPB_PROJECT_READ_GH")!, "github.com", runner);
        ProjectReadResult? result = null;
        ConnectionReport? connection = null;
        var verified = false;
        try
        {
            connection = await service.ConnectAsync();
            Assert.That(connection.IsConnected, Is.True, connection.Result.ToString());
            Assert.That(connection.Context!.Login == "fukuda-yuki", Is.True, "The designated sandbox account is required.");
            var scope = await service.SendAsync(connection.Context, ApiRequest.GraphQl("""
                query SandboxReadScope {
                  user(login: "fukuda-yuki") { id projectV2(number: 3) { id number } }
                  repository(owner: "fukuda-yuki", name: "codex-sandbox") { id issue(number: 1) { id } }
                }
                """));
            Assert.That(scope.IsSuccess, Is.True, scope.ToString());
            var data = scope.Data!.Value.GetProperty("data");
            Assert.That(data.GetProperty("user").GetProperty("projectV2").GetProperty("id").GetString() == "PVT_kwHOBGPKL84BjFYc",
                Is.True, "Project identity must match the authorized scope.");
            Assert.That(data.GetProperty("repository").GetProperty("id").GetString() == "R_kgDOUVKgAw",
                Is.True, "Repository identity must match the authorized scope.");

            result = await new ProjectReader(service).ReadAsync(connection.Context,
                new(ConnectionScope.From(connection.Context), "PVT_kwHOBGPKL84BjFYc"));
            Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
            var project = result.Project!;
            Assert.That(project.Number, Is.EqualTo(3));
            Assert.That(project.OwnerId.NodeId == data.GetProperty("user").GetProperty("id").GetString(), Is.True);
            var scopeIssueId = data.GetProperty("repository").GetProperty("issue").GetProperty("id").GetString();
            var issue = project.Issues.Values.Single(i => i.Id.NodeId == scopeIssueId);
            Assert.That(issue.Repository.Id.NodeId == "R_kgDOUVKgAw", Is.True);
            var item = project.Items.Single(i => i.ContentId == issue.Id);
            var present = item.Values.Where(value => value.Availability == ValueAvailability.Present).ToArray();
            Assert.That(present.Length, Is.GreaterThan(0), "Existing sandbox data must exercise a single-select value.");

            // Independent ID-based query compares the implemented values without persisting raw content.
            var readback = await service.SendAsync(connection.Context, ApiRequest.GraphQl("""
                query SandboxReadback($issue: ID!, $item: ID!) {
                  issue: node(id: $issue) { ... on Issue { id title state } }
                  item: node(id: $item) { ... on ProjectV2Item {
                    id project { id }
                    fieldValues(first: 100) {
                      pageInfo { hasNextPage }
                      nodes { ... on ProjectV2ItemFieldSingleSelectValue {
                        optionId field { ... on ProjectV2FieldCommon { id } }
                      } }
                    }
                  } }
                }
                """, new { issue = issue.Id.NodeId, item = item.Id.NodeId }));
            Assert.That(readback.IsSuccess, Is.True, readback.ToString());
            var observed = readback.Data!.Value.GetProperty("data");
            Assert.That(observed.GetProperty("issue").GetProperty("title").GetString() == issue.Title.Value, Is.True, "Issue title readback.");
            Assert.That(observed.GetProperty("issue").GetProperty("state").GetString() ==
                (issue.State.Value == IssueState.Open ? "OPEN" : "CLOSED"), Is.True, "Issue state readback.");
            var values = observed.GetProperty("item").GetProperty("fieldValues");
            Assert.That(values.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False,
                "This independent smoke comparison requires at most 100 values; production pagination is tested separately.");
            foreach (var value in present)
                Assert.That(values.GetProperty("nodes").EnumerateArray().Any(node =>
                    node.TryGetProperty("field", out var field) && field.GetProperty("id").GetString() == value.FieldId!.NodeId
                    && node.GetProperty("optionId").GetString() == value.OptionId), Is.True, "Single-select ID/value readback.");
            verified = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "read-evidence.json"), JsonSerializer.Serialize(new
            {
                verified, source = Environment.GetEnvironmentVariable("GHPB_PROJECT_READ_SOURCE"),
                host = "github.com", repository = "fukuda-yuki/codex-sandbox", projectNumber = 3,
                connection?.Version, outcome = result?.Outcome.ToString(),
                fields = result?.Project?.Fields.Count, issues = result?.Project?.Issues.Count, items = result?.Project?.Items.Count,
                problems = result?.Problems, processCount = runner.Count, queries = runner.Queries,
                applicationDataMutations = 0, completedAt = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class ReadOnlyRunner : IGhProcessRunner
    {
        private readonly GhProcessRunner runner = new();
        public int Count { get; private set; }
        public int Queries { get; private set; }
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            if (command.Arguments[0] == "api")
            {
                Assert.That(command.Arguments[command.Arguments.ToList().IndexOf("--hostname") + 1], Is.EqualTo("github.com"));
                if (command.Arguments[1] == "graphql")
                {
                    using var payload = JsonDocument.Parse(command.StandardInput!);
                    var query = payload.RootElement.GetProperty("query").GetString()!;
                    Assert.That(query.TrimStart(), Does.StartWith("query"));
                    Assert.That(query, Does.Not.Contain("mutation"));
                    Queries++;
                }
                else
                {
                    Assert.That(command.Arguments[1], Is.EqualTo("user"));
                    Assert.That(command.Arguments, Does.Contain("GET"));
                }
            }
            else Assert.That(command.Arguments[0], Is.AnyOf("--version", "auth"));
            Count++;
            return await runner.RunAsync(command, cancellationToken);
        }
    }
}


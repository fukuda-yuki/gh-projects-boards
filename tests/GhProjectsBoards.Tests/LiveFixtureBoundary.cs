using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Tests;

// External service substitute for lifecycle tests. The actual runner, connection,
// reader, Apply executor and durable checkpoints remain real.
internal sealed class LiveFixtureBoundary : IGhProcessRunner
{
    private const string Project = LivePerformanceRun.ProjectId, Repository = LivePerformanceRun.RepositoryId;
    internal readonly Dictionary<string, (int Number, string Title, string Body)> Issues = [];
    internal readonly Dictionary<string, string> Membership = [];
    internal bool FailAdditionAfterCommit;
    internal bool LoseRestoreResponseOnce, LoseDeleteResponseOnce;
    internal bool LoseRemoveBeforeCommitOnce, LoseDeleteBeforeCommitOnce;
    private int next = 2;
    public Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Arguments[0] == "--version") return Task.FromResult(new GhProcessResult(ProcessCompletion.Exited, true, 0, "gh version 2.100.0"));
        if (command.Arguments[0] == "auth") return Task.FromResult(GhConnectionTests.Auth());
        var endpoint = command.Arguments[1]; var graphql = endpoint == "graphql";
        GhProcessResult Response(object body, int status = 200, int exit = 0) => new(ProcessCompletion.Exited, true, exit,
            $"HTTP/2.0 {status} Result\r\nX-RateLimit-Resource: {(graphql ? "graphql" : "core")}\r\nX-RateLimit-Remaining: 5000\r\n\r\n" + JsonSerializer.Serialize(body));
        if (endpoint == "user") return Task.FromResult(Response(new { id = 42, login = "fukuda-yuki" }));
        if (endpoint == "rate_limit") return Task.FromResult(Response(new { resources = new { core = new { remaining = 5000 }, graphql = new { remaining = 5000 } } }));
        if (!graphql)
        {
            if (command.StandardInput is not null)
            {
                using var body = JsonDocument.Parse(command.StandardInput); var number = next++; var id = "I" + number;
                Issues[id] = (number, body.RootElement.GetProperty("title").GetString()!, body.RootElement.GetProperty("body").GetString()!);
                return Task.FromResult(Response(IssueRest(id)));
            }
            if (endpoint.Contains('?')) return Task.FromResult(Response(Issues.Keys.Select(IssueRest).ToArray()));
            var found = Issues.SingleOrDefault(i => i.Value.Number == int.Parse(endpoint.Split('/').Last()));
            return Task.FromResult(found.Key is null ? Response(new { message = "Gone" }, 410, 1) : Response(IssueRest(found.Key)));
        }
        using var payload = JsonDocument.Parse(command.StandardInput!); var query = payload.RootElement.GetProperty("query").GetString()!;
        var variables = payload.RootElement.TryGetProperty("variables", out var vars) ? vars : default;
        if (query.StartsWith("mutation"))
        {
            if (query.Contains("PerformanceAdd"))
            {
                var issue = variables.GetProperty("issue").GetString()!; Membership["T" + issue] = issue;
                if (FailAdditionAfterCommit) return Task.FromResult(Response(new { errors = new[] { new { type = "UNPROCESSABLE", message = "Item already exists" } } }, exit: 1));
                return Task.FromResult(Response(new { data = new { addProjectV2ItemById = new { item = new { id = "T" + issue } } } }));
            }
            if (query.Contains("PerformanceRemove")) {
                if (LoseRemoveBeforeCommitOnce) { LoseRemoveBeforeCommitOnce = false; return Task.FromResult(new GhProcessResult(ProcessCompletion.Exited, true, 0, "HTTP/2.0 200 OK\r\n\r\n{")); }
                Membership.Remove(variables.GetProperty("item").GetString()!); return Task.FromResult(Response(new { data = new { } })); }
            if (query.Contains("PerformanceDelete")) {
                if (LoseDeleteBeforeCommitOnce) { LoseDeleteBeforeCommitOnce = false; return Task.FromResult(new GhProcessResult(ProcessCompletion.Exited, true, 0, "HTTP/2.0 200 OK\r\n\r\n{")); }
                Issues.Remove(variables.GetProperty("issue").GetString()!);
                if (LoseDeleteResponseOnce) { LoseDeleteResponseOnce = false; return Task.FromResult(new GhProcessResult(ProcessCompletion.Exited, true, 0, "HTTP/2.0 200 OK\r\n\r\n{")); }
                return Task.FromResult(Response(new { data = new { } })); }
            var input = variables.GetProperty("input"); var id = input.GetProperty("id").GetString()!; var prior = Issues[id];
            Issues[id] = prior with { Title = input.GetProperty("title").GetString()! };
            if (LoseRestoreResponseOnce && Issues[id].Title.EndsWith("baseline"))
            { LoseRestoreResponseOnce = false; return Task.FromResult(new GhProcessResult(ProcessCompletion.Exited, true, 0, "HTTP/2.0 200 OK\r\n\r\n{")); }
            return Task.FromResult(Response(new { data = new { updateIssue = new { issue = new { id, title = Issues[id].Title } } } }));
        }
        if (query.Contains("PerformanceScope")) return Task.FromResult(Response(new { data = new {
            viewer = new { databaseId = 42 }, repository = new { id = Repository, viewerPermission = "ADMIN", issue = new { id = "scope", number = 1, title = "scope", body = "scope", state = "CLOSED" } },
            user = new { projectV2 = new { id = Project, number = 3, viewerCanUpdate = true, fields = Page([]), items = Page(Membership.Keys.Select(Item).ToArray()) } } } }));
        if (query.Contains("RegistrationResolve")) return Task.FromResult(Response(new { data = new { user = new { projectV2 = Metadata() } } }));
        if (query.Contains("RegistrationLinks")) return Task.FromResult(Response(new { data = new { node = new { repositories = Page([]) } } }));
        var data = new JsonObject();
        if (query.Contains("ApplyObservation"))
        {
            data["project"] = JsonSerializer.SerializeToNode(Fields()); data["item"] = JsonSerializer.SerializeToNode(Item(variables.GetProperty("item").GetString()!));
        }
        else if (query.Contains("ProjectFields")) data["node"] = JsonSerializer.SerializeToNode(Fields());
        else if (query.Contains("ProjectItems")) data["node"] = JsonSerializer.SerializeToNode(new { __typename = "ProjectV2", id = Project, items = Page(Membership.Keys.Select(Item).ToArray()) });
        else if (query.Contains("ApplyItem")) data["node"] = JsonSerializer.SerializeToNode(Item(variables.GetProperty("id").GetString()!));
        else throw new InvalidOperationException("Unsupported synthetic lifecycle query");
        if (query.Contains("viewer { databaseId }")) data["viewer"] = JsonSerializer.SerializeToNode(new { databaseId = 42 });
        return Task.FromResult(Response(new { data }));
    }
    private object IssueRest(string id) => new { node_id = id, number = Issues[id].Number, title = Issues[id].Title, body = Issues[id].Body, repository_url = "https://api.github.com/repos/fukuda-yuki/codex-sandbox" };
    private object Metadata() => new { __typename = "ProjectV2", id = Project, number = 3, url = "https://github.com/users/fukuda-yuki/projects/3", title = "fixture project", viewerCanUpdate = true, owner = new { __typename = "User", id = "O1", login = "fukuda-yuki" } };
    private object Fields() { var data = JsonSerializer.SerializeToNode(Metadata())!; data["fields"] = JsonSerializer.SerializeToNode(Page([])); return data; }
    private object Item(string item)
    {
        var id = Membership[item]; var issue = Issues[id];
        return new { __typename = "ProjectV2Item", id = item, type = "ISSUE", isArchived = false, project = new { id = Project }, fieldValues = Page([]),
            content = new { __typename = "Issue", id, number = issue.Number, title = issue.Title, state = "OPEN", viewerCanUpdate = true,
                assignees = Page([]), blockedBy = Page([]), parent = (object?)null,
                url = "https://github.com/fukuda-yuki/codex-sandbox/issues/" + issue.Number,
                repository = new { id = Repository, nameWithOwner = "fukuda-yuki/codex-sandbox", owner = new { id = "O1" } } } };
    }
    private static object Page(object[] nodes) => new { nodes, totalCount = nodes.Length, pageInfo = new { hasNextPage = false, endCursor = (string?)null } };
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("LiveGitHub")]
[NonParallelizable]
internal sealed class LiveGitHubTests
{
    // These are the only resources authorized by the repository's sandbox record.
    private const string Owner = "fukuda-yuki";
    private const string Repository = "codex-sandbox";
    private const string IssueEndpoint = "repos/fukuda-yuki/codex-sandbox/issues";
    private const string ProjectId = "PVT_kwHOBGPKL84BjFYc";
    private const string RepositoryId = "R_kgDOUVKgAw";

    [Test]
    public async Task RoundTripsIssueAndProjectChangesThroughTheProductionAdapterAndCleansUp()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_LIVE_GITHUB") != "1")
            Assert.Ignore("Run scripts/Test-LiveGitHub.ps1 for the explicitly authorized sandbox.");
        var executable = Environment.GetEnvironmentVariable("GHPB_LIVE_GH_PATH")!;
        var directory = Environment.GetEnvironmentVariable("GHPB_LIVE_ARTIFACTS")!;
        Assert.That(File.Exists(executable), Is.True, "The real GitHub CLI is required.");
        Directory.CreateDirectory(directory);
        var evidencePath = Path.Combine(directory, "adapter-evidence.json");
        var runId = $"ghpb-connection-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var startedUtc = DateTimeOffset.UtcNow;
        var runner = new MeasuredRunner();
        var service = new GhConnectionService(executable, "github.com", runner);
        var stages = new List<object>();
        var cleanupFailures = new List<string>();
        ConnectionReport? connection = null;
        string? issueId = null;
        int? issueNumber = null;
        string? itemId = null;
        string[]? baselineItems = null;
        string[]? baselineFields = null;
        string? failureType = null;
        var scenarioPassed = false;
        var cleanupPassed = false;
        Save();

        try
        {
            connection = await service.ConnectAsync();
            Record("connect", connection.Result);
            Assert.That(connection.IsConnected, Is.True, connection.Result.ToString());
            Assert.That(connection.Context!.Login, Is.EqualTo(Owner));
            Assert.That(connection.Authentication!.Store, Is.EqualTo(CredentialStore.Keyring));
            Assert.That(connection.Authentication.HasScope("repo"), Is.True);
            Assert.That(connection.Authentication.HasScope("project"), Is.True);

            var baseline = await Snapshot("baseline");
            var repository = baseline.GetProperty("repository");
            Assert.That(repository.GetProperty("id").GetString(), Is.EqualTo(RepositoryId));
            Assert.That(repository.GetProperty("issue").GetProperty("number").GetInt32(), Is.EqualTo(1));
            var project = baseline.GetProperty("user").GetProperty("projectV2");
            Assert.That(project.GetProperty("id").GetString(), Is.EqualTo(ProjectId));
            Assert.That(project.GetProperty("viewerCanUpdate").GetBoolean(), Is.True);
            baselineItems = Ids(project, "items");
            baselineFields = Ids(project, "fields");
            var status = project.GetProperty("fields").GetProperty("nodes").EnumerateArray()
                .Single(field => field.TryGetProperty("name", out var name) && name.GetString() == "Status");
            var fieldId = status.GetProperty("id").GetString()!;
            var optionId = status.GetProperty("options")[0].GetProperty("id").GetString()!;
            Save();

            var title = $"[{runId}] 日本語の\"接続試験\"";
            const string body = "実装アダプターの試験です。\n二行目に\"引用符\"と\\記号。\n試験後にこのIssueを削除します。";
            var created = await Send("create-issue", ApiRequest.Rest("POST", IssueEndpoint, new { title, body }));
            issueId = created.GetProperty("node_id").GetString();
            issueNumber = created.GetProperty("number").GetInt32();
            Save(); // Preserve returned identities before the next remote operation.
            Assert.That(issueNumber, Is.GreaterThan(1), "Never modify the retained scope record.");
            Assert.That(issueId, Is.Not.Null.And.Not.Empty);
            var issue = await Send("read-created-issue", ApiRequest.Rest("GET", $"{IssueEndpoint}/{issueNumber}"));
            AssertContent(issue, title, body);

            var updatedTitle = $"[{runId}] 更新済みの\"題名\"";
            var updatedBody = body + "\n更新後の行：\"確認\"。";
            await Send("update-issue", ApiRequest.Rest("PATCH", $"{IssueEndpoint}/{issueNumber}", new { title = updatedTitle, body = updatedBody }));
            issue = await Send("read-updated-issue", ApiRequest.Rest("GET", $"{IssueEndpoint}/{issueNumber}"));
            AssertContent(issue, updatedTitle, updatedBody);

            var added = await Send("add-project-item", ApiRequest.GraphQl("""
                mutation Add($input: AddProjectV2ItemByIdInput!) {
                  addProjectV2ItemById(input: $input) { item { id } }
                }
                """, new { input = new { projectId = ProjectId, contentId = issueId } }));
            itemId = added.GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString();
            Save();
            Assert.That(itemId, Is.Not.Null.And.Not.Empty);
            Assert.That(baselineItems, Does.Not.Contain(itemId), "Only a newly created item may be changed or deleted.");
            await Send("update-project-status", ApiRequest.GraphQl("""
                mutation SetStatus($input: UpdateProjectV2ItemFieldValueInput!) {
                  updateProjectV2ItemFieldValue(input: $input) { projectV2Item { id } }
                }
                """, new { input = new { projectId = ProjectId, itemId, fieldId, value = new { singleSelectOptionId = optionId } } }));
            var readback = await Send("read-project-item", ApiRequest.GraphQl("""
                query ReadItem($id: ID!) {
                  node(id: $id) { ... on ProjectV2Item {
                    id project { id } content { ... on Issue { id } }
                    fieldValueByName(name: "Status") { ... on ProjectV2ItemFieldSingleSelectValue { optionId } }
                  } }
                }
                """, new { id = itemId }));
            var item = readback.GetProperty("data").GetProperty("node");
            Assert.That(item.GetProperty("project").GetProperty("id").GetString(), Is.EqualTo(ProjectId));
            Assert.That(item.GetProperty("content").GetProperty("id").GetString(), Is.EqualTo(issueId));
            Assert.That(item.GetProperty("fieldValueByName").GetProperty("optionId").GetString(), Is.EqualTo(optionId));
            scenarioPassed = true;
            Save();
        }
        catch (Exception exception)
        {
            failureType = exception.GetType().Name;
            Save();
            // Do not print exceptions that could contain process output or payloads.
            TestContext.Progress.WriteLine($"Live scenario failed ({failureType}); inspect safe stage evidence.");
        }
        finally
        {
            if (connection?.Context is not null)
            {
                if (itemId is not null && baselineItems is not null && !baselineItems.Contains(itemId))
                    await Cleanup("delete-project-item", async () => await Send("delete-project-item", ApiRequest.GraphQl("""
                        mutation Remove($input: DeleteProjectV2ItemInput!) {
                          deleteProjectV2Item(input: $input) { deletedItemId }
                        }
                        """, new { input = new { projectId = ProjectId, itemId } })));
                if (issueId is not null && issueNumber is > 1)
                    await Cleanup("delete-issue", async () => await Send("delete-issue", ApiRequest.GraphQl("""
                        mutation Delete($input: DeleteIssueInput!) {
                          deleteIssue(input: $input) { repository { id } }
                        }
                        """, new { input = new { issueId } })));
                if (issueNumber is > 1)
                    await Cleanup("verify-issue-deleted", async () =>
                    {
                        var result = await service.SendAsync(connection.Context, ApiRequest.Rest("GET", $"{IssueEndpoint}/{issueNumber}"));
                        Record("verify-issue-deleted", result);
                        Assert.That(result.HttpStatus, Is.EqualTo(404).Or.EqualTo(410));
                        Assert.That(result.Failure, Is.EqualTo(FailureKind.NotFoundOrInaccessible));
                    });
                if (baselineItems is not null && baselineFields is not null)
                    await Cleanup("verify-project-restored", async () =>
                    {
                        var final = (await Snapshot("verify-project-restored")).GetProperty("user").GetProperty("projectV2");
                        Assert.That(Ids(final, "items"), Is.EqualTo(baselineItems));
                        Assert.That(Ids(final, "fields"), Is.EqualTo(baselineFields));
                    });
            }
            cleanupPassed = cleanupFailures.Count == 0 && issueNumber is > 1 && itemId is not null;
            Save();
            TestContext.AddTestAttachment(evidencePath, "Safe live adapter stages, identities, timings and cleanup results");
        }
        Assert.That(scenarioPassed, Is.True, $"Live scenario failed ({failureType}); see adapter-evidence.json.");
        Assert.That(cleanupPassed, Is.True, $"Cleanup is incomplete: {string.Join(", ", cleanupFailures)}. Do not recreate uncertain results.");

        async Task<JsonElement> Send(string stage, ApiRequest request)
        {
            var result = await service.SendAsync(connection!.Context!, request);
            Record(stage, result);
            Assert.That(result.IsSuccess, Is.True, $"{stage}: {result}");
            Assert.That(result.Data.HasValue, Is.True, $"{stage}: missing structured data.");
            return result.Data!.Value;
        }
        async Task<JsonElement> Snapshot(string stage)
        {
            var result = await Send(stage, ApiRequest.GraphQl("""
                query Sandbox($owner: String!, $repository: String!, $number: Int!) {
                  repository(owner: $owner, name: $repository) { id issue(number: 1) { number } }
                  user(login: $owner) { projectV2(number: $number) {
                    id viewerCanUpdate
                    items(first: 100) { nodes { id } pageInfo { hasNextPage } }
                    fields(first: 100) { nodes {
                      ... on ProjectV2FieldCommon { id name }
                      ... on ProjectV2SingleSelectField { options { id name } }
                    } pageInfo { hasNextPage } }
                  } }
                }
                """, new { owner = Owner, repository = Repository, number = 3 }));
            return result.GetProperty("data");
        }
        async Task Cleanup(string stage, Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) { cleanupFailures.Add($"{stage}: {exception.GetType().Name}"); Save(); }
        }
        void Record(string stage, ApiResult result)
        {
            stages.Add(new { stage, utc = DateTimeOffset.UtcNow, outcome = result.Outcome.ToString(), failure = result.Failure.ToString(),
                result.ExitCode, result.HttpStatus, result.GraphQlErrors, elapsedMs = result.Elapsed.TotalMilliseconds });
            Save();
            TestContext.Progress.WriteLine($"{stage}: {result}");
        }
        void Save() => File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
        {
            runId, startedUtc, host = "github.com", repository = $"{Owner}/{Repository}", projectNumber = 3,
            projectId = ProjectId, viewerId = connection?.Context?.ViewerId, ghVersion = connection?.Version,
            credentialStore = connection?.Authentication?.Store.ToString(), issueNumber, issueId, itemId,
            baselineItems, baselineFields, scenarioPassed, failureType, cleanupPassed, cleanupFailures,
            ghProcessCount = runner.Count, ghElapsedMs = runner.Elapsed.TotalMilliseconds, stages
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string[] Ids(JsonElement project, string collection)
    {
        var connection = project.GetProperty(collection);
        Assert.That(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False,
            "This bounded sandbox test requires at most 100 items and fields; do not validate a partial baseline.");
        return connection.GetProperty("nodes").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).Order().ToArray();
    }

    private static void AssertContent(JsonElement issue, string title, string body)
    {
        Assert.That(issue.GetProperty("title").GetString() == title, Is.True, "Independent Issue title readback must match exactly.");
        Assert.That(issue.GetProperty("body").GetString() == body, Is.True, "Independent Issue body readback must preserve Japanese, newlines and quotes.");
    }

    private sealed class MeasuredRunner : IGhProcessRunner
    {
        private readonly GhProcessRunner runner = new();
        public int Count { get; private set; }
        public TimeSpan Elapsed { get; private set; }
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            var timer = Stopwatch.StartNew();
            try { return await runner.RunAsync(command, cancellationToken); }
            finally { Count++; Elapsed += timer.Elapsed; }
        }
    }
}

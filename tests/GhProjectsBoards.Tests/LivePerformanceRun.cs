using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Tests;

// Opt-in adapter measurements, never part of the application or ordinary test execution.
internal sealed partial class LivePerformanceRun
{
    private readonly string root;
    private readonly string marker;
    private LivePerformanceRun(string root, string? retainedMarker = null)
    { this.root = root; fixtureRoot = root; marker = retainedMarker ?? "ghpb-perf51-" + Guid.NewGuid().ToString("N"); }
    internal const string ProjectId = "PVT_kwHOBGPKL84BjFYc";
    internal const string RepositoryId = "R_kgDOUVKgAw";
    internal const string Endpoint = "repos/fukuda-yuki/codex-sandbox/issues";
    internal const string Gh = @"C:\Program Files\GitHub CLI\gh.exe";
    private readonly List<Fixture> fixtures = [];
    private readonly List<string> stages = [];
    private LivePerformanceRunner runner = null!;
    private GhConnectionService service = null!;
    private ConnectionContext context = null!;
    private JsonElement baseline;
    private bool complete, cleanup;
    private int createIntents;
    private sealed record Fixture(string IssueId, int Number, string Baseline, string Changed, string? ItemId = null);

    private string fixtureRoot;
    private bool prepared;
    private int measuredSamples, restoredSamples;
    private bool samplePending;
    private void Save()
    {
        var value = new { version = 2, marker, repository = RepositoryId, project = ProjectId, fixtures, createIntents,
            stages, prepared, measuredSamples, restoredSamples, samplePending, complete, cleanup,
            stoppedByLimit = runner?.StopReason == "RateLimited", stopReason = runner?.StopReason,
            mutations = runner?.Mutations, at = DateTimeOffset.UtcNow };
        Write("fixture.json", value);
        if (root != fixtureRoot) WriteFile(Path.Combine(fixtureRoot, "fixture.json"), value);
    }
    private void Write(string name, object value)
    {
        using var file = new FileStream(Path.Combine(root, name), FileMode.Create, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true);
    }
    private async Task<JsonElement> Send(string stage, ApiRequest request)
    {
        stages.Add(stage + ":intent"); Save();
        var response = await service.SendAsync(context, request, requiredScope: request.IsMutation ? request.IsGraphQl && request.Payload!.Contains("ProjectV2") ? "project" : "repo" : null);
        if (!response.IsSuccess) throw new InvalidOperationException(stage + ": " + response);
        stages.Add(stage + ":confirmed"); Save(); return response.Data!.Value;
    }
    private async Task<JsonElement> Snapshot(string name)
    {
        var value = (await Send(name, ApiRequest.GraphQl("""
            query PerformanceScope {
              viewer { databaseId }
              repository(owner:"fukuda-yuki",name:"codex-sandbox") { id viewerPermission issue(number:1) { id number title body state } }
              user(login:"fukuda-yuki") { projectV2(number:3) { id number viewerCanUpdate
                fields(first:100) { totalCount pageInfo { hasNextPage } nodes {
                  ... on ProjectV2FieldCommon { id name dataType }
                  ... on ProjectV2SingleSelectField { options { id name } }
                } }
                items(first:100) { totalCount pageInfo { hasNextPage } nodes { id isArchived
                  content { ... on Issue { id title state repository { id nameWithOwner } } }
                  fieldValues(first:100) { pageInfo { hasNextPage } nodes {
                    __typename ... on ProjectV2ItemFieldSingleSelectValue { optionId field { ... on ProjectV2FieldCommon { id } } }
                  } }
                } }
              } }
            }
            """))).GetProperty("data");
        var project = value.GetProperty("user").GetProperty("projectV2");
        Require(value.GetProperty("repository").GetProperty("id").GetString() == RepositoryId && project.GetProperty("id").GetString() == ProjectId
            && project.GetProperty("number").GetInt32() == 3 && value.GetProperty("viewer").GetProperty("databaseId").GetInt64() == context.ViewerId
            && value.GetProperty("repository").GetProperty("viewerPermission").GetString() == "ADMIN" && project.GetProperty("viewerCanUpdate").ValueKind == JsonValueKind.True, "Exact sandbox resource or capability mismatch");
        foreach (var field in new[] { "fields", "items" }) Require(!project.GetProperty(field).GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), "Incomplete sandbox snapshot");
        foreach (var item in project.GetProperty("items").GetProperty("nodes").EnumerateArray())
            Require(!item.GetProperty("fieldValues").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), "Incomplete sandbox value snapshot");
        Write(name + ".json", value); return value;
    }
    private async Task Measure(RegistrationWorkspace workspace, string sampleRoot, Fixture[] selected, string mode, string initialSha256)
    {
        var processStart = runner.Records.Count; var mutationStart = runner.Mutations;
        var preparationStart = Stopwatch.GetTimestamp();
        using var trace = new PerformanceTrace(); var timer = Stopwatch.StartNew();
        using (PerformanceTrace.Span("prepare")) await workspace.PrepareApplyAsync(selected.Select(f => f.ItemId!).ToHashSet());
        var prepare = timer.Elapsed.TotalMilliseconds;
        var review = workspace.ApplyReview; Require(review?.Batch.Operations.Length == selected.Length, "Measurement review mismatch");
        var executionStart = Stopwatch.GetTimestamp(); timer.Restart();
        using (PerformanceTrace.Span("execute")) await workspace.ConfirmApplyAsync(review!);
        var execute = timer.Elapsed.TotalMilliseconds;
        var settlementEnd = Stopwatch.GetTimestamp(); trace.Dispose();
        var durable = await new DraftStore(sampleRoot).LoadAsync(ConnectionScope.From(context));
        var batch = durable!.Journal!.SingleOrDefault(b => b.Id == review!.Batch.Id);
        var settled = batch is not null && batch.Operations.Length == selected.Length && batch.Operations.All(o => o.State == ApplyState.Succeeded);
        var firstSuccess = trace.Samples.FirstOrDefault(s => s.Kind == "durable-success");
        Write(Path.GetFileName(sampleRoot) + ".json", new { mode, initialSha256, changedRows = selected.Length, changedFields = selected.Length,
            prepareMs = prepare, executeMs = execute, endToEndMs = prepare + execute,
            firstDurableSuccessMs = firstSuccess is null ? (double?)null : (firstSuccess.Start - executionStart) * 1000d / Stopwatch.Frequency,
            success = settled && runner.Mutations - mutationStart == selected.Length, mutations = runner.Mutations - mutationStart,
            errors = batch?.Operations.Where(o => o.State != ApplyState.Succeeded).Select(o => new { o.State, o.Reason }),
            preparationStart, executionStart, settlementEnd, processRecords = runner.Records.Skip(processStart).ToArray(), spans = trace.Samples });
        Require(settled && runner.Mutations - mutationStart == selected.Length, "Live measurement failed or uncertain; never retry");
        stages.Add($"measured-{selected.Length}-{mode}"); Save();
        Console.WriteLine($"size={selected.Length} variant={mode} prepareMs={prepare:F1} executeMs={execute:F1} settled=True");
    }
    private async Task<bool> VerifyIssue(Fixture fixture, string? expectedTitle, bool allowAbsent = false)
    {
        var response = await service.SendAsync(context, ApiRequest.Rest("GET", Endpoint + "/" + fixture.Number));
        if (allowAbsent && response.HttpStatus is 404 or 410) return false;
        Require(response.IsSuccess && response.Data is not null, "Owned Issue unavailable");
        var owned = response.Data!.Value;
        Require(owned.GetProperty("node_id").GetString() == fixture.IssueId && owned.GetProperty("number").GetInt32() == fixture.Number
            && owned.GetProperty("repository_url").GetString() == "https://api.github.com/repos/fukuda-yuki/codex-sandbox"
            && owned.GetProperty("title").GetString()!.StartsWith(marker + " ")
            && (expectedTitle is null || owned.GetProperty("title").GetString() == expectedTitle), "Owned Issue identity/value mismatch");
        return true;
    }
    private async Task Cleanup()
    {
        // Reconcile an interrupted create by exact run marker; never repeat a create/add/update.
        if (createIntents > fixtures.Count)
        {
            var issues = await Send("reconcile-create", ApiRequest.Rest("GET", Endpoint + "?state=all&per_page=100&sort=created&direction=desc"));
            var missing = issues.EnumerateArray().Where(i => i.GetProperty("title").GetString()!.StartsWith(marker + " ")
                && !fixtures.Any(f => f.IssueId == i.GetProperty("node_id").GetString())).ToArray();
            Require(missing.Length == createIntents - fixtures.Count, "Interrupted create unresolved; preserve manifest for reconciliation");
            foreach (var issue in missing)
            {
                var title = issue.GetProperty("title").GetString()!;
                var f = new Fixture(issue.GetProperty("node_id").GetString()!, issue.GetProperty("number").GetInt32(), title, title.Replace(" baseline", " changed"));
                Require(f.Number > 1 && issue.GetProperty("body").GetString()!.StartsWith(marker + " "), "Create ownership unverified");
                fixtures.Add(f); runner.AllowIssue(f.IssueId, f.Baseline, f.Changed); Save();
            }
        }
        var current = await Snapshot("before-cleanup");
        var items = current.GetProperty("user").GetProperty("projectV2").GetProperty("items").GetProperty("nodes").EnumerateArray().ToArray();
        foreach (var fixture in fixtures)
        {
            if (!await VerifyIssue(fixture, null, stages.Contains("delete-" + fixture.Number + ":intent")))
            {
                Require(!items.Any(i => i.GetProperty("content").ValueKind == JsonValueKind.Object && i.GetProperty("content").TryGetProperty("id", out var id) && id.GetString() == fixture.IssueId), "Deleted Issue still has membership");
                continue;
            }
            foreach (var item in items.Where(i => i.GetProperty("content").ValueKind == JsonValueKind.Object
                && i.GetProperty("content").TryGetProperty("id", out var id) && id.GetString() == fixture.IssueId))
            {
                var itemId = item.GetProperty("id").GetString()!;
                Require(!baseline.GetProperty("user").GetProperty("projectV2").GetProperty("items").GetProperty("nodes").EnumerateArray()
                    .Any(i => i.GetProperty("id").GetString() == itemId), "Refusing existing Project item");
                runner.AllowItem(itemId, fixture.IssueId);
                Require(!stages.Contains("remove-" + fixture.Number + ":intent"), "Prior removal is unresolved while target remains present; do not replay");
                await Send("remove-" + fixture.Number, ApiRequest.GraphQl("""
                    mutation PerformanceRemove($project:ID!,$item:ID!) { deleteProjectV2Item(input:{projectId:$project,itemId:$item}) { deletedItemId } }
                    """, new { project = ProjectId, item = itemId }));
            }
            Require(!stages.Contains("delete-" + fixture.Number + ":intent"), "Prior deletion is unresolved while target remains present; do not replay");
            await Send("delete-" + fixture.Number, ApiRequest.GraphQl("mutation PerformanceDelete($issue:ID!) { deleteIssue(input:{issueId:$issue}) { clientMutationId } }", new { issue = fixture.IssueId }));
            var absent = await service.SendAsync(context, ApiRequest.Rest("GET", Endpoint + "/" + fixture.Number));
            Require(absent.HttpStatus is 404 or 410, "Deleted Issue absence not verified");
        }
        var after = await Snapshot("after-cleanup");
        Require(SameSnapshot(after, baseline), "Existing sandbox snapshot changed");
        cleanup = true; Save();
    }
    internal static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    internal static bool SameSnapshot(JsonElement left, JsonElement right) => JsonElement.DeepEquals(left, right);
}

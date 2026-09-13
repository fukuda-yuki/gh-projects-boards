using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("LiveGitHub"), NonParallelizable]
internal sealed class RefreshLiveTests
{
    private const string ProjectId = "PVT_kwHOBGPKL84BjFYc";
    private const string RepositoryId = "R_kgDOUVKgAw";
    private const string Endpoint = "repos/fukuda-yuki/codex-sandbox/issues";
    [Test]
    public async Task ProductionRefreshResolvesLocallyWhileFixtureRemoteValueRemainsUnchanged()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_REFRESH_LIVE") != "1") Assert.Ignore("Opt-in disposable sandbox fixture only.");
        var root = Environment.GetEnvironmentVariable("GHPB_REFRESH_LIVE_ARTIFACTS")!;
        var gh = Environment.GetEnvironmentVariable("GHPB_REFRESH_LIVE_GH")!;
        Directory.CreateDirectory(root);
        var marker = "ghpb-refresh-" + Guid.NewGuid().ToString("N");
        var fixture = new GhConnectionService(gh, "github.com"); var connection = await fixture.ConnectAsync();
        Assert.That(connection.IsConnected && connection.Context!.Login == "fukuda-yuki", Is.True);
        Assert.That(connection.Authentication!.Store, Is.EqualTo(CredentialStore.Keyring));
        var context = connection.Context!;
        string? issueId = null, itemId = null; int? number = null;
        bool scenarioPassed = false, cleanupPassed = false, createAttempted = false;
        var stages = new List<string>(); var productRunner = new ProjectReadLiveTests.ReadOnlyRunner();
        void Save() => File.WriteAllText(Path.Combine(root, "fixture-evidence.json"), JsonSerializer.Serialize(new {
            marker, repository = "fukuda-yuki/codex-sandbox", repositoryId = RepositoryId, project = ProjectId,
            issueId, itemId, number, createAttempted, scenarioPassed, cleanupPassed, stages,
            productQueries = productRunner.Queries, productProcesses = productRunner.Count, productMutations = 0,
            fixtureWritesAreSeparate = true, at = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
        async Task<JsonElement> Send(string stage, ApiRequest request)
        {
            stages.Add(stage + ":attempt"); Save();
            var result = await fixture.SendAsync(context, request);
            Assert.That(result.IsSuccess, Is.True, stage + ": " + result);
            stages.Add(stage + ":confirmed"); Save(); return result.Data!.Value;
        }
        async Task<JsonElement> Snapshot() => (await Send("independent-sandbox-read", ApiRequest.GraphQl("""
            query RefreshFixtureScope {
              repository(owner: "fukuda-yuki", name: "codex-sandbox") { id issue(number: 1) { id number title body state } }
              user(login: "fukuda-yuki") { id projectV2(number: 3) { id number
                fields(first:100) { totalCount pageInfo { hasNextPage } nodes {
                  ... on ProjectV2FieldCommon { id name dataType }
                  ... on ProjectV2SingleSelectField { options { id name } }
                } }
                items(first:100) { totalCount pageInfo { hasNextPage } nodes { id isArchived
                  content { ... on Issue { id title state } }
                  fieldValues(first:100) { pageInfo { hasNextPage } nodes {
                    ... on ProjectV2ItemFieldSingleSelectValue { optionId field { ... on ProjectV2FieldCommon { id } } }
                  } }
                } }
              } }
            }
            """))).GetProperty("data");
        var before = await Snapshot(); var project = before.GetProperty("user").GetProperty("projectV2");
        Assert.That(before.GetProperty("repository").GetProperty("id").GetString(), Is.EqualTo(RepositoryId));
        Assert.That(before.GetProperty("repository").GetProperty("issue").GetProperty("number").GetInt32(), Is.EqualTo(1));
        Assert.That(project.GetProperty("id").GetString(), Is.EqualTo(ProjectId));
        foreach (var name in new[] { "items", "fields" }) Assert.That(project.GetProperty(name).GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
        foreach (var item in project.GetProperty("items").GetProperty("nodes").EnumerateArray())
            Assert.That(item.GetProperty("fieldValues").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
        Save();
        try
        {
            createAttempted = true; Save();
            var issue = await Send("fixture-create", ApiRequest.Rest("POST", Endpoint, new { title = marker + " A", body = "Disposable #9 reconciliation fixture; automated owned cleanup." }));
            issueId = issue.GetProperty("node_id").GetString(); number = issue.GetProperty("number").GetInt32(); Save();
            Assert.That(number, Is.GreaterThan(1)); Assert.That(issueId, Is.Not.Null.And.Not.Empty);
            var added = await Send("fixture-add", ApiRequest.GraphQl("""
                mutation AddRefreshFixture($project: ID!, $issue: ID!) { addProjectV2ItemById(input:{projectId:$project, contentId:$issue}) { item { id } } }
                """, new { project = ProjectId, issue = issueId }));
            itemId = added.GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString(); Save();
            Assert.That(project.GetProperty("items").GetProperty("nodes").EnumerateArray().Any(i => i.GetProperty("id").GetString() == itemId), Is.False);

            var service = new GhConnectionService(gh, "github.com", productRunner); var bound = (await service.ConnectAsync()).Context!;
            var workspace = new RegistrationWorkspace(new(Path.Combine(root, "live-data"))); await workspace.BindAsync(bound, service);
            var choice = await new ProjectDiscovery(service).ResolveAsync(bound, "https://github.com/users/fukuda-yuki/projects/3", default);
            Assert.That(choice.Id.NodeId, Is.EqualTo(ProjectId)); await workspace.RegisterAsync(choice, null);
            Assert.That(workspace.LatestAttempt, Is.EqualTo(RegistrationAttempt.Complete));
            var cell = workspace.Drafts!.Workspace.Open(workspace.Selected!).Single(r => r.ItemId == itemId).Cells[0];
            Assert.That(workspace.Drafts.Workspace.Value(cell), Is.EqualTo(marker + " A"));
            workspace.Drafts.Workspace.Commit(ProjectId, cell, marker + " B"); Assert.That(await workspace.FlushDraftsAsync(), Is.True);
            await Send("fixture-external-change", ApiRequest.Rest("PATCH", $"{Endpoint}/{number}", new { title = marker + " C" }));
            var external = await Send("independent-before-refresh", ApiRequest.Rest("GET", $"{Endpoint}/{number}"));
            Assert.That(external.GetProperty("title").GetString(), Is.EqualTo(marker + " C"));
            await workspace.RegisterAsync(choice, null, true);
            Assert.That(workspace.LatestAttempt, Is.EqualTo(RegistrationAttempt.Complete));
            var field = workspace.Drafts.Workspace.Field(cell)!;
            Assert.That(field.Conflict, Is.True); Assert.That(field.Baseline, Is.EqualTo(marker + " A"));
            Assert.That(field.Change!.Value, Is.EqualTo(marker + " B")); Assert.That(field.Observation!.Value, Is.EqualTo(marker + " C"));
            var decision = workspace.Drafts.Workspace.Decision(cell.Key!);
            Assert.That(await workspace.Drafts.CommitAsync(candidate => { candidate.Resolve(ProjectId, decision, new(marker + " B")); return candidate; }, () => true), Is.True);
            var verified = await Send("independent-after-local-resolution", ApiRequest.Rest("GET", $"{Endpoint}/{number}"));
            Assert.That(verified.GetProperty("title").GetString(), Is.EqualTo(marker + " C"));
            var restart = new RegistrationWorkspace(new(Path.Combine(root, "live-data"))); await restart.RestoreAsync(); await restart.SelectProfileAsync(choice.Id.Scope); await restart.SelectAsync(choice.Id);
            Assert.That(restart.Drafts!.Workspace.Field(cell)!.Baseline, Is.EqualTo(marker + " C")); Assert.That(restart.Drafts.Workspace.Value(cell), Is.EqualTo(marker + " B"));
            scenarioPassed = true; Save();
        }
        finally
        {
            try
            {
                if (itemId is not null)
                {
                    Assert.That(number > 1 && issueId is not null, Is.True, "Only a newly created disposable Issue may own cleanup targets.");
                    Assert.That(project.GetProperty("items").GetProperty("nodes").EnumerateArray().Any(i => i.GetProperty("id").GetString() == itemId), Is.False);
                    var item = (await Send("verify-owned-item-before-delete", ApiRequest.GraphQl("""
                        query OwnedRefreshItem($id:ID!) { node(id:$id) { ... on ProjectV2Item { id project { id } content { ... on Issue { id } } } } }
                        """, new { id = itemId }))).GetProperty("data").GetProperty("node");
                    Assert.That(item.GetProperty("project").GetProperty("id").GetString(), Is.EqualTo(ProjectId));
                    Assert.That(item.GetProperty("content").GetProperty("id").GetString(), Is.EqualTo(issueId));
                    await Send("fixture-remove-item", ApiRequest.GraphQl("""
                        mutation RemoveRefreshFixture($project:ID!, $item:ID!) { deleteProjectV2Item(input:{projectId:$project,itemId:$item}) { deletedItemId } }
                        """, new { project = ProjectId, item = itemId }));
                }
                if (issueId is not null && number > 1)
                {
                    var owned = await Send("verify-owned-fixture-before-delete", ApiRequest.Rest("GET", $"{Endpoint}/{number}"));
                    Assert.That(owned.GetProperty("node_id").GetString() == issueId && owned.GetProperty("title").GetString()!.StartsWith(marker), Is.True);
                    await Send("fixture-delete-issue", ApiRequest.GraphQl("""
                        mutation DeleteRefreshFixture($issue:ID!) { deleteIssue(input:{issueId:$issue}) { clientMutationId } }
                        """, new { issue = issueId }));
                    var absent = await fixture.SendAsync(context, ApiRequest.Rest("GET", $"{Endpoint}/{number}"));
                    Assert.That(absent.HttpStatus, Is.AnyOf(404, 410)); stages.Add("fixture-absence-confirmed");
                }
                var after = await Snapshot(); Assert.That(after.GetRawText(), Is.EqualTo(before.GetRawText()), "Existing Project items/fields and scope Issue must be preserved.");
                cleanupPassed = !createAttempted || issueId is not null; Save();
            }
            finally { Save(); }
        }
    }
}

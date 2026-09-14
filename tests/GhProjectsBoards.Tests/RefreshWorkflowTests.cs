using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class RefreshWorkflowTests
{
    [TestCase("complete"), TestCase("partial"), TestCase("failed"), TestCase("cancel"), TestCase("concurrent"), TestCase("options")]
    public async Task LocalRowsSurviveEveryRefreshOutcomeWithoutRemoteIdentity(string mode)
    {
        var boundary = new ProjectReaderTests.ProjectBoundary(); var refreshing = false; RegistrationWorkspace? workspace = null;
        var addedDuringRead = false;
        boundary.Override = (q, v) => {
            if (refreshing && q.Contains("ProjectItems"))
            {
                if (mode == "failed" || mode == "partial" && v.TryGetProperty("after", out var cursor) && cursor.ValueKind == JsonValueKind.String)
                    return ScriptedRunner.Http("{}", 503);
                if (mode == "cancel") workspace!.Cancel();
                if (mode == "concurrent" && !addedDuringRead)
                {
                    addedDuringRead = true; var local = workspace!.Drafts!.Workspace;
                    var id = local.AddRow(workspace.Selected!); local.SetBuffer(local.Open(workspace.Selected!).Single(r => r.ItemId == id).Cells[0], "During retrieval");
                }
            }
            var response = RegistrationResponses.Query(q, v); if (response is null) return null;
            var data = JsonNode.Parse(JsonSerializer.Serialize(response))!;
            if (refreshing && mode == "options" && q.Contains("ProjectFields"))
            {
                var options = data["data"]!["node"]!["fields"]!["nodes"]![0]!["options"]!.AsArray();
                options.Remove(options.Single(o => o!["id"]!.ToString() == "done"));
            }
            return ScriptedRunner.Http(data.ToJsonString());
        };
        var service = new GhConnectionService("gh.exe", "github.com", boundary.Runner); var context = (await service.ConnectAsync()).Context!;
        var store = new RegistrationStore(Path.Combine(Path.GetTempPath(), "ghpb-local-refresh-" + Guid.NewGuid()));
        workspace = new(store); await workspace.BindAsync(context, service);
        var choice = await new ProjectDiscovery(service).ResolveAsync(context, "https://github.com/users/sample-user/projects/1", default);
        await workspace.RegisterAsync(choice, null); Assert.That(await workspace.PrepareLocalRowsAsync(), Is.True);
        var session = workspace.Drafts!; var id = session.Workspace.AppendRows(workspace.Selected!, "Issue 1\tDone").Single();
        session.Workspace.SetBuffer(session.Workspace.Open(workspace.Selected!).Single(r => r.ItemId == id).Cells[0], "pending日本語");
        var expected = JsonSerializer.Serialize(session.Workspace.LocalRows[0]); var initial = workspace.Selected;
        refreshing = true; await workspace.RegisterAsync(choice, null, true);
        Assert.That(JsonSerializer.Serialize(session.Workspace.LocalRows[0]), Is.EqualTo(expected));
        if (mode is "partial" or "failed" or "cancel") Assert.That(workspace.Selected, Is.SameAs(initial));
        if (mode == "options") Assert.That(session.Workspace.LocalProblems(workspace.Selected!, id).Any(e => e.StartsWith("選択肢")), Is.True,
            workspace.Status + " / " + JsonSerializer.Serialize(workspace.Selected!.Snapshot.Fields));
        if (mode == "concurrent") Assert.That(session.Workspace.LocalRows[1].TitleBuffer, Is.EqualTo("During retrieval"));
        Assert.That(await session.FlushAsync(), Is.True);
        var restart = new RegistrationWorkspace(store); await restart.RestoreAsync(); await restart.SelectProfileAsync(ConnectionScope.From(context)); await restart.SelectAsync(choice.Id);
        Assert.That(JsonSerializer.Serialize(restart.Drafts!.Workspace.LocalRows), Is.EqualTo(JsonSerializer.Serialize(session.Workspace.LocalRows)));
        Assert.That(restart.Selected!.Snapshot.Items.Any(i => i.Id.NodeId == id), Is.False); boundary.AssertQueriesOnly();
    }
    [TestCase("partial"), TestCase("complete"), TestCase("composition"), TestCase("concurrent"), TestCase("legacy-writer")]
    public async Task GuardedRefreshPreservesCurrentWorkAndRejectsUnverifiedOrStaleCommits(string mode)
    {
        var boundary = new ProjectReaderTests.ProjectBoundary(); var remote = false; RegistrationWorkspace? workspace = null;
        boundary.Override = (q, v) => {
            if (remote && mode == "partial" && q.Contains("ProjectItems") && v.TryGetProperty("after", out var cursor) && cursor.ValueKind == JsonValueKind.String)
                return ScriptedRunner.Http("{}", 403);
            var response = RegistrationResponses.Query(q, v);
            if (response is null) return null;
            var data = JsonNode.Parse(JsonSerializer.Serialize(response))!;
            if (remote && q.Contains("ProjectItems"))
            {
                foreach (var item in data["data"]!["node"]!["items"]!["nodes"]!.AsArray().Where(i => i!["content"]!["id"]!.ToString() == "I1")) item!["content"]!["title"] = "External C";
                if (mode == "concurrent") { var session = workspace!.Drafts!; session.Workspace.SetBuffer(session.Workspace.Open(workspace.Selected!)[1].Cells[0], "During I/O"); }
            }
            return ScriptedRunner.Http(data.ToJsonString());
        };
        var service = new GhConnectionService("gh.exe", "github.com", boundary.Runner); var context = (await service.ConnectAsync()).Context!;
        var store = new RegistrationStore(Path.Combine(Path.GetTempPath(), "ghpb-refresh-workflow-" + Guid.NewGuid()));
        workspace = new(store); await workspace.BindAsync(context, service);
        var choice = await new ProjectDiscovery(service).ResolveAsync(context, "https://github.com/users/sample-user/projects/1", default);
        await workspace.RegisterAsync(choice, null); var initial = workspace.Selected!;
        var w = workspace.Drafts!.Workspace; var rows = w.Open(initial); w.Commit("P1", rows[0].Cells[0], "Local B"); await workspace.FlushDraftsAsync();
        var calls = boundary.Runner.Commands.Count;
        if (mode == "legacy-writer") await store.SaveAsync(initial with { DefaultRepository = "sample-user/newer" });
        if (mode == "composition") workspace.CanRefresh = () => false;
        remote = true; await workspace.RegisterAsync(choice, null, true);
        Assert.That(workspace.Drafts.Workspace.Value(rows[0].Cells[0]), Is.EqualTo("Local B"));
        if (mode is "partial" or "composition" or "legacy-writer")
        {
            Assert.That(workspace.Selected, Is.SameAs(initial)); Assert.That(workspace.Drafts.Workspace.Field(rows[0].Cells[0])!.Conflict, Is.False);
            if (mode == "partial") Assert.That(workspace.Incomplete, Is.Not.Null);
            if (mode == "composition") Assert.That(boundary.Runner.Commands, Has.Count.EqualTo(calls));
            if (mode == "legacy-writer") Assert.That((await store.LoadAsync()).Registrations.Single().DefaultRepository, Is.EqualTo("sample-user/newer"));
        }
        else
        {
            Assert.That(workspace.Drafts.Workspace.Field(rows[0].Cells[0])!.Conflict, Is.True);
            var restart = new RegistrationWorkspace(store); await restart.RestoreAsync(); await restart.SelectProfileAsync(context is null ? null : ConnectionScope.From(context)); await restart.SelectAsync(choice.Id);
            Assert.That(restart.Drafts!.Workspace.Field(rows[0].Cells[0])!.Conflict, Is.True);
            if (mode == "concurrent") Assert.That(restart.Drafts.Workspace.Buffer(rows[1].Cells[0]), Is.EqualTo("During I/O"));
        }
        boundary.AssertQueriesOnly();
    }
}

using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("LiveGitHub"), NonParallelizable]
internal sealed class PlanningApplicationLiveTests
{
    private const string Project = "PVT_kwHOBGPKL84BjFYc", Repository = "R_kgDOUVKgAw";
    [Test]
    public async Task ProductionPlanningRoundTripsOwnedNumberDateAndDependencyThenReconcilesExternalDate()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_PLANNING_APP_PROOF") != "1") Assert.Ignore("Opt-in bounded production planning adapter proof.");
        var root = Environment.GetEnvironmentVariable("GHPB_LIVE_ARTIFACTS")!;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "planning-application.json");
        Assert.That(File.Exists(path), Is.False, "Never replay a previous live run.");
        var service = new GhConnectionService(Environment.GetEnvironmentVariable("GHPB_LIVE_GH_PATH")!, "github.com");
        var connection = await service.ConnectAsync();
        Assert.That(connection.IsConnected, Is.True);
        Assert.That(connection.Context!.Login, Is.EqualTo("fukuda-yuki"));
        Assert.That(connection.Authentication!.Store, Is.EqualTo(CredentialStore.Keyring));
        var marker = "ghpb-plan-app-" + Guid.NewGuid().ToString("N");
        var owned = new List<(string Id, int Number)>(); var items = new List<string>(); var stages = new List<object>();
        JsonElement? before = null; bool success = false, cleanup = false; string? failure = null;
        Save();
        try
        {
            before = await Snapshot(); Save();
            for (var i = 0; i < 2; i++)
            {
                var created = await Send("create-" + i, ApiRequest.Rest("POST", "repos/fukuda-yuki/codex-sandbox/issues", new { title = marker + "-" + i, body = "Owned disposable #61 production adapter fixture." }));
                owned.Add((created.GetProperty("node_id").GetString()!, created.GetProperty("number").GetInt32())); Save();
                Assert.That(owned[^1].Number, Is.GreaterThan(1));
                var current = await Snapshot();
                var matching = current.GetProperty("items").GetProperty("nodes").EnumerateArray()
                    .Where(n => n.GetProperty("content").ValueKind == JsonValueKind.Object && n.GetProperty("content").TryGetProperty("id", out var id) && id.GetString() == owned[^1].Id).ToArray();
                Assert.That(matching.Length, Is.LessThanOrEqualTo(1));
                var item = matching.Length == 1 ? matching[0].GetProperty("id").GetString()! :
                    (await Send("add-" + i, ApiRequest.GraphQl("mutation($input:AddProjectV2ItemByIdInput!){addProjectV2ItemById(input:$input){item{id}}}",
                        new { input = new { projectId = Project, contentId = owned[^1].Id } }))).GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString()!;
                Assert.That(before.Value.GetProperty("items").GetProperty("nodes").EnumerateArray().Any(n => n.GetProperty("id").GetString() == item), Is.False);
                items.Add(item); Save();
            }
            var workspace = new RegistrationWorkspace(new(Path.Combine(root, "session")));
            await workspace.BindAsync(connection.Context, service);
            var choice = await new ProjectDiscovery(service).ResolveAsync(connection.Context, "https://github.com/users/fukuda-yuki/projects/3", default);
            Assert.That(choice.Id.NodeId, Is.EqualTo(Project)); await workspace.RegisterAsync(choice, null);
            Assert.That(workspace.LatestAttempt, Is.EqualTo(RegistrationAttempt.Complete), workspace.Status);
            Assert.That(await workspace.PrepareLocalRowsAsync(), Is.True);
            var p = workspace.Selected!; var w = workspace.Drafts!.Workspace;
            var number = p.Snapshot.Fields.First(f => f.DataType == "NUMBER");
            var date = p.Snapshot.Fields.First(f => f.DataType == "DATE");
            var manual = new PlanningTask(owned[0].Id, PlanningMode.Manual, ManualStart: PlanningContractTests.At("2026-10-05 12:07"), ManualFinish: PlanningContractTests.At("2026-10-06 16:19"));
            var config = PlanningPathTests.Plan() with { ProjectId = Project, People = [], Fields = [new("Estimate", number.Id.NodeId, "NUMBER"), new("Finish", date.Id.NodeId, "DATE")],
                Tasks = [manual, new(owned[1].Id, PlanningMode.Auto)] };
            w.CommitPlanning(p, config, w.Revision, [new(items[0], "Estimate", "16.125"), new(items[1], "Estimate", "4")], [new(owned[1].Id, [owned[0].Id])]);
            Assert.That(w.PlanFor(p).Tasks.Single(t => t.Id == owned[1].Id).Finish, Is.EqualTo(PlanningContractTests.At("2026-10-07 11:19")));
            Assert.That(await workspace.FlushDraftsAsync(), Is.True);
            await Publish(5);
            var fresh = await new ProjectReader(service).ReadAsync(connection.Context, choice.Id);
            Assert.That(fresh.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
            Assert.That(fresh.Project!.Issues[new(choice.Id.Scope, owned[1].Id)].Native!.Predecessors.Select(i => i.NodeId), Does.Contain(owned[0].Id));
            Assert.That(fresh.Project.Items.Single(i => i.Id.NodeId == items[0]).Values.Single(v => v.FieldId == number.Id).Scalar, Is.EqualTo("16.125"));
            Assert.That(fresh.Project.Items.Single(i => i.Id.NodeId == items[1]).Values.Single(v => v.FieldId == date.Id).Scalar, Is.EqualTo("2026-10-07"));
            stages.Add(new { stage = "production-readback", numbers = true, dates = true, nativeDependency = true }); Save();
            var restart = new RegistrationWorkspace(new(Path.Combine(root, "session")));
            await restart.RestoreAsync(); await restart.BindAsync(connection.Context, service); await restart.SelectAsync(choice.Id);
            workspace = restart; w = workspace.Drafts!.Workspace; p = workspace.Selected!;
            Assert.That(w.Planning(Project)!.Tasks.Single(t => t.Id == owned[0].Id), Is.EqualTo(manual));
            await Send("external-date", ApiRequest.GraphQl("mutation($input:UpdateProjectV2ItemFieldValueInput!){updateProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}",
                new { input = new { projectId = Project, itemId = items[0], fieldId = date.Id.NodeId, value = new { date = "2026-10-07" } } }));
            await workspace.RegisterAsync(choice, null, true);
            Assert.That(workspace.LatestAttempt, Is.EqualTo(RegistrationAttempt.Complete), workspace.Status);
            w = workspace.Drafts.Workspace; p = workspace.Selected!;
            var decision = w.PlanningDecisions(Project, items[0]).Single();
            Assert.That(w.Planning(Project)!.Tasks.Single(t => t.Id == owned[0].Id).ManualFinish, Is.EqualTo(manual.ManualFinish));
            var adopted = manual with { ManualFinish = PlanningContractTests.At("2026-10-07 10:23") };
            w.CommitPlanning(p, w.Planning(Project)! with { Tasks = [adopted, new(owned[1].Id, PlanningMode.Auto)] }, w.Revision,
                decisions: [new(decision.Key, decision.Observation!.Id, true)]);
            Assert.That(await workspace.FlushDraftsAsync(), Is.True);
            var restored = EditingWorkspace.Restore((await new DraftStore(Path.Combine(root, "session")).LoadAsync(choice.Id.Scope))!);
            Assert.That(restored.Planning(Project)!.Tasks.Single(t => t.Id == owned[0].Id), Is.EqualTo(adopted));
            Assert.That(restored.PlanFor(p).Tasks.Single(t => t.Id == owned[1].Id).Finish, Is.EqualTo(PlanningContractTests.At("2026-10-07 15:23")));
            stages.Add(new { stage = "external-date-decision-restart", retainedBeforeDecision = manual.ManualFinish, adopted.ManualFinish }); Save();
            // Explicit NUMBER clear and FS removal reuse the same reviewed queue.
            w.CommitPlanning(p, w.Planning(Project)!, w.Revision, [new(items[0], "Estimate", null)], [new(owned[1].Id, [])]);
            await Publish(3); // Clear, remove edge, and the successor's newly projected finish day.
            fresh = await new ProjectReader(service).ReadAsync(connection.Context, choice.Id);
            Assert.That(fresh.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
            Assert.That(fresh.Project!.Issues[new(choice.Id.Scope, owned[1].Id)].Native!.Predecessors, Is.Empty);
            Assert.That(fresh.Project.Items.Single(i => i.Id.NodeId == items[0]).Values.Single(v => v.FieldId == number.Id).Availability, Is.EqualTo(ValueAvailability.Empty));
            success = true; Save();
            async Task Publish(int count)
            {
                await workspace.PrepareApplyAsync(items.ToHashSet());
                var review = workspace.ApplyReview!;
                Assert.That(review.Blocked, Is.Empty, workspace.Status);
                Assert.That(review.Batch.Operations.Length, Is.EqualTo(count));
                Assert.That(review.Batch.Operations.All(o => items.Contains(o.ItemId) && owned.Any(i => i.Id == o.IssueId)
                    && (o.Key.Kind == "Dependency" ? o.Key.FieldId == owned[0].Id : o.Key.FieldId == number.Id.NodeId || o.Key.FieldId == date.Id.NodeId)), Is.True);
                await workspace.ConfirmApplyAsync(review);
                w = workspace.Drafts!.Workspace; p = workspace.Selected!;
                var batch = w.Journal.Single(b => b.Id == review.Batch.Id);
                stages.Add(new { stage = "production-apply", batch.Id, operations = batch.Operations.Select(o => new { o.Key, o.State, o.Verification }) }); Save();
                Assert.That(batch.Operations.All(o => o.State == ApplyState.Succeeded), Is.True, workspace.Status);
            }
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; Save(); }
        finally
        {
            // Read back uncertain creates by the unique run marker; never resend them.
            var inventory = await Send("owned-inventory", ApiRequest.Rest("GET", "repos/fukuda-yuki/codex-sandbox/issues?state=all&per_page=100"));
            Assert.That(inventory.GetArrayLength(), Is.LessThan(100));
            foreach (var issue in inventory.EnumerateArray().Where(i => i.GetProperty("title").GetString()!.StartsWith(marker, StringComparison.Ordinal)))
            {
                var id = issue.GetProperty("node_id").GetString()!;
                if (owned.All(i => i.Id != id)) owned.Add((id, issue.GetProperty("number").GetInt32())); Save();
            }
            foreach (var issue in owned)
            {
                Assert.That(issue.Number, Is.GreaterThan(1));
                var verify = await Send("verify-owned", ApiRequest.Rest("GET", "repos/fukuda-yuki/codex-sandbox/issues/" + issue.Number));
                Assert.That(verify.GetProperty("node_id").GetString(), Is.EqualTo(issue.Id));
                Assert.That(verify.GetProperty("title").GetString(), Does.StartWith(marker));
                await Send("delete-owned", ApiRequest.GraphQl("mutation($input:DeleteIssueInput!){deleteIssue(input:$input){repository{id}}}", new { input = new { issueId = issue.Id } }));
                var absent = await service.SendAsync(connection.Context, ApiRequest.Rest("GET", "repos/fukuda-yuki/codex-sandbox/issues/" + issue.Number));
                Assert.That(absent.HttpStatus, Is.AnyOf(404, 410));
            }
            var after = await Snapshot();
            if (before is { } original) Assert.That(after.GetRawText(), Is.EqualTo(original.GetRawText()), "Existing items, field values, definitions and scope Issue must be unchanged.");
            cleanup = true; Save(); TestContext.AddTestAttachment(path);
        }
        Assert.That(success, Is.True, failure); Assert.That(cleanup, Is.True);
        async Task<JsonElement> Snapshot()
        {
            var result = await Send("scope", ApiRequest.GraphQl("query{repository(owner:\"fukuda-yuki\",name:\"codex-sandbox\"){id issue(number:1){number}} user(login:\"fukuda-yuki\"){projectV2(number:3){id viewerCanUpdate fields(first:100){nodes{... on ProjectV2FieldCommon{id name dataType}} pageInfo{hasNextPage}} items(first:100){nodes{id content{... on Issue{id}} fieldValues(first:100){nodes{... on ProjectV2ItemFieldNumberValue{number field{... on ProjectV2FieldCommon{id}}} ... on ProjectV2ItemFieldDateValue{date field{... on ProjectV2FieldCommon{id}}} ... on ProjectV2ItemFieldSingleSelectValue{optionId field{... on ProjectV2FieldCommon{id}}}} pageInfo{hasNextPage}}} pageInfo{hasNextPage}}}}}"));
            var data = result.GetProperty("data"); Assert.That(data.GetProperty("repository").GetProperty("id").GetString(), Is.EqualTo(Repository));
            Assert.That(data.GetProperty("repository").GetProperty("issue").GetProperty("number").GetInt32(), Is.EqualTo(1));
            var project = data.GetProperty("user").GetProperty("projectV2");
            Assert.That(project.GetProperty("id").GetString(), Is.EqualTo(Project)); Assert.That(project.GetProperty("viewerCanUpdate").GetBoolean(), Is.True);
            foreach (var property in new[] { "fields", "items" }) Assert.That(project.GetProperty(property).GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
            foreach (var item in project.GetProperty("items").GetProperty("nodes").EnumerateArray()) Assert.That(item.GetProperty("fieldValues").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
            return project;
        }
        async Task<JsonElement> Send(string stage, ApiRequest request)
        {
            stages.Add(new { stage, state = "intent", at = DateTimeOffset.UtcNow }); Save();
            await Task.Delay(1100);
            var result = await service.SendAsync(connection.Context, request);
            stages.Add(new { stage, state = result.Outcome.ToString(), failure = result.Failure.ToString(), result.HttpStatus }); Save();
            Assert.That(result.IsSuccess, Is.True, stage + ": " + result);
            return result.Data!.Value;
        }
        void Save() => File.WriteAllText(path, JsonSerializer.Serialize(new { marker, source = Environment.GetEnvironmentVariable("GHPB_PROOF_SOURCE"),
            host = "github.com", repository = Repository, project = Project, connection.Version,
            owned = owned.Select(i => new { i.Id, i.Number }), items, stages, success, cleanup, failure }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

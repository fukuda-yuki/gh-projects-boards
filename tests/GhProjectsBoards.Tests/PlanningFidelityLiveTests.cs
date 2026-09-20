using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// Bounded investigation, not a second publication implementation. Only run-owned
// Issues/items are modified; production guarded transport is the external boundary.
[TestFixture, Category("LiveGitHub"), NonParallelizable]
internal sealed class PlanningFidelityLiveTests
{
    private const string Project = "PVT_kwHOBGPKL84BjFYc", Repository = "R_kgDOUVKgAw";
    [Test]
    public async Task NumberDateAndNativePredecessorRoundtrip()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_PLANNING_PROOF") != "1") Assert.Ignore("Opt-in bounded planning proof.");
        var root = Environment.GetEnvironmentVariable("GHPB_LIVE_ARTIFACTS")!;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "planning-fidelity.json");
        Assert.That(File.Exists(path), Is.False, "Never overwrite/replay a prior run.");
        var service = new GhConnectionService(Environment.GetEnvironmentVariable("GHPB_LIVE_GH_PATH")!, "github.com");
        var connection = await service.ConnectAsync();
        Assert.That(connection.IsConnected, Is.True);
        Assert.That(connection.Context!.Login, Is.EqualTo("fukuda-yuki"));
        Assert.That(connection.Authentication!.Store, Is.EqualTo(CredentialStore.Keyring));
        var marker = "ghpb-planning-" + Guid.NewGuid().ToString("N");
        var owned = new List<(string Id, int Number)>();
        var items = new List<string>(); var stages = new List<object>(); var samples = new List<object>();
        string[] beforeItems = [], beforeFields = [];
        var cleanup = false; var succeeded = false; string? failure = null;
        Save();
        try
        {
            var baseline = await Snapshot();
            beforeItems = Ids(baseline, "items"); beforeFields = Ids(baseline, "fields"); Save();
            var numberField = baseline.GetProperty("fields").GetProperty("nodes").EnumerateArray().First(f => f.GetProperty("dataType").GetString() == "NUMBER").GetProperty("id").GetString()!;
            var dateField = baseline.GetProperty("fields").GetProperty("nodes").EnumerateArray().First(f => f.GetProperty("dataType").GetString() == "DATE").GetProperty("id").GetString()!;
            for (var i = 0; i < 2; i++)
            {
                var created = await Send("create-" + i, ApiRequest.Rest("POST", "repos/fukuda-yuki/codex-sandbox/issues", new { title = marker + "-" + i, body = "" }));
                owned.Add((created.GetProperty("node_id").GetString()!, created.GetProperty("number").GetInt32())); Save();
                Assert.That(owned[^1].Number, Is.GreaterThan(1));
            }
            // Project workflows may already have added an Issue. Observe before add.
            var membership = await Send("membership", ApiRequest.GraphQl("query($id:ID!){node(id:$id){... on ProjectV2{items(first:100){nodes{id content{... on Issue{id}}} pageInfo{hasNextPage}}}}}", new { id = Project }));
            var page = membership.GetProperty("data").GetProperty("node").GetProperty("items");
            Assert.That(page.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
            var match = page.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("content").ValueKind == JsonValueKind.Object && n.GetProperty("content").TryGetProperty("id", out var id) && id.GetString() == owned[0].Id).ToArray();
            string item;
            if (match.Length == 1) item = match[0].GetProperty("id").GetString()!;
            else
            {
                Assert.That(match, Is.Empty);
                var added = await Send("add", ApiRequest.GraphQl("mutation($input:AddProjectV2ItemByIdInput!){addProjectV2ItemById(input:$input){item{id}}}", new { input = new { projectId = Project, contentId = owned[0].Id } }));
                item = added.GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString()!;
            }
            Assert.That(beforeItems, Does.Not.Contain(item)); items.Add(item); Save();
            foreach (var value in new[] { 0m, 16m, 0.125m, 1.23456789m, 999999.12345678m, 999999999.12345678m })
            {
                await Set(numberField, new { number = value });
                var read = await ReadItem(item);
                var actual = read.EnumerateArray().Single(v => v.GetProperty("field").GetProperty("id").GetString() == numberField).GetProperty("number").GetDecimal();
                samples.Add(new { kind = "number", sent = value, received = actual, exact = value == actual }); Save();
                if (PlanningContract.CanPublishHours(value))
                    Assert.That(actual, Is.EqualTo(value), "Publishable canonical numeric work must survive the external roundtrip.");
                else Assert.That(value, Is.EqualTo(999999999.12345678m), "Only the named unsupported Float precision probe may lose fidelity.");
            }
            await Set(dateField, new { date = "2026-10-05" });
            var dates = await ReadItem(item);
            var date = dates.EnumerateArray().Single(v => v.GetProperty("field").GetProperty("id").GetString() == dateField).GetProperty("date").GetString();
            Assert.That(date, Is.EqualTo("2026-10-05"));
            samples.Add(new { kind = "date", sent = "2026-10-05", received = date, intraday = "not represented by DATE" }); Save();
            var work = new EditingWorkspace(ConnectionScope.From(connection.Context));
            var manual = PlanningContractTests.Plan().Tasks[0] with { Id = owned[0].Id };
            work.SetPlanning(PlanningContractTests.Plan() with { ProjectId = Project, Fields = [new("Start", dateField, "DATE")], Tasks = [manual] }, 0);
            var store = new DraftStore(Path.Combine(root, "manual-checkpoint"));
            await store.SaveAsync(work.Snapshot(), 0);
            await Set(dateField, new { date = "2026-10-06" });
            var remote = (await ReadItem(item)).EnumerateArray().Single(v => v.GetProperty("field").GetProperty("id").GetString() == dateField).GetProperty("date").GetString();
            Assert.That(remote, Is.EqualTo("2026-10-06"));
            var restored = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
            var retained = restored.Planning(Project)!.Tasks.Single();
            Assert.That(PlanningContract.RequiresDateDecision(date, retained.ManualStart, remote), Is.True);
            Assert.That(retained.Mode, Is.EqualTo(PlanningMode.Manual));
            Assert.That(retained.ManualStart, Is.EqualTo(manual.ManualStart));
            samples.Add(new { kind = "external-date-conflict", remote, retained.ManualStart, mode = retained.Mode.ToString(), needsExplicitEndpoint = true }); Save();
            var confirmed = PlanningContract.ConfirmRemoteEndpoint(retained, true, remote, PlanningContractTests.At("2026-10-06 09:23"));
            restored.SetPlanning(restored.Planning(Project)! with { Tasks = [confirmed] }, restored.Planning(Project)!.Stamp);
            await store.SaveAsync(restored.Snapshot(), work.Revision);
            var afterDecision = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!).Planning(Project)!.Tasks.Single();
            Assert.That(afterDecision.ManualStart, Is.EqualTo(PlanningContractTests.At("2026-10-06 09:23")));
            Assert.That(afterDecision.ManualFinish, Is.EqualTo(manual.ManualFinish));
            Assert.That(afterDecision.Mode, Is.EqualTo(PlanningMode.Manual));
            samples.Add(new { kind = "confirmed-date-restored", afterDecision.ManualStart, afterDecision.ManualFinish, mode = afterDecision.Mode.ToString() }); Save();
            // The blocked Issue is the successor. The blocking Issue is the predecessor.
            await Send("add-predecessor", ApiRequest.GraphQl("mutation($input:AddBlockedByInput!){addBlockedBy(input:$input){issue{id}}}", new { input = new { issueId = owned[1].Id, blockingIssueId = owned[0].Id } }));
            var dependencies = await Send("read-predecessor", ApiRequest.GraphQl("query($id:ID!){node(id:$id){... on Issue{id blockedBy(first:100){nodes{id} pageInfo{hasNextPage}}}}}", new { id = owned[1].Id }));
            Assert.That(Ids(dependencies.GetProperty("data").GetProperty("node"), "blockedBy"), Is.EqualTo(new[] { owned[0].Id }));
            await Send("remove-predecessor", ApiRequest.GraphQl("mutation($input:RemoveBlockedByInput!){removeBlockedBy(input:$input){issue{id}}}", new { input = new { issueId = owned[1].Id, blockingIssueId = owned[0].Id } }));
            dependencies = await Send("read-cleared-predecessor", ApiRequest.GraphQl("query($id:ID!){node(id:$id){... on Issue{blockedBy(first:100){nodes{id} pageInfo{hasNextPage}}}}}", new { id = owned[1].Id }));
            Assert.That(Ids(dependencies.GetProperty("data").GetProperty("node"), "blockedBy"), Is.Empty);
            await Send("clear-number", ApiRequest.GraphQl("mutation($input:ClearProjectV2ItemFieldValueInput!){clearProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}", new { input = new { projectId = Project, itemId = item, fieldId = numberField } }));
            Assert.That((await ReadItem(item)).EnumerateArray().Any(v => v.GetProperty("field").GetProperty("id").GetString() == numberField), Is.False);
            succeeded = true; Save();
            async Task Set(string field, object value) => await Send("set-" + field, ApiRequest.GraphQl("mutation($input:UpdateProjectV2ItemFieldValueInput!){updateProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}", new { input = new { projectId = Project, itemId = item, fieldId = field, value } }));
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; Save(); }
        finally
        {
            // Reconcile run-owned identities once; never repeat an uncertain create.
            var all = await Send("reconcile-owned", ApiRequest.Rest("GET", "repos/fukuda-yuki/codex-sandbox/issues?state=all&per_page=100"));
            Assert.That(all.GetArrayLength(), Is.LessThan(100), "Need complete bounded ownership inventory.");
            foreach (var issue in all.EnumerateArray().Where(x => x.GetProperty("title").GetString()!.StartsWith(marker, StringComparison.Ordinal)))
            {
                var id = issue.GetProperty("node_id").GetString()!; var number = issue.GetProperty("number").GetInt32();
                if (owned.All(x => x.Id != id)) owned.Add((id, number)); Save();
            }
            foreach (var issue in owned)
            {
                Assert.That(issue.Number, Is.GreaterThan(1));
                var check = await Send("owned-readback", ApiRequest.Rest("GET", "repos/fukuda-yuki/codex-sandbox/issues/" + issue.Number));
                Assert.That(check.GetProperty("title").GetString(), Does.StartWith(marker));
                await Send("delete-owned", ApiRequest.GraphQl("mutation($input:DeleteIssueInput!){deleteIssue(input:$input){repository{id}}}", new { input = new { issueId = issue.Id } }));
                var absent = await service.SendAsync(connection.Context, ApiRequest.Rest("GET", "repos/fukuda-yuki/codex-sandbox/issues/" + issue.Number));
                stages.Add(new { stage = "absence", issue.Number, absent.HttpStatus }); Save();
                Assert.That(absent.HttpStatus, Is.EqualTo(404).Or.EqualTo(410));
            }
            var after = await Snapshot();
            Assert.That(Ids(after, "items"), Is.EqualTo(beforeItems));
            Assert.That(Ids(after, "fields"), Is.EqualTo(beforeFields));
            cleanup = true; Save();
            TestContext.AddTestAttachment(path);
        }
        Assert.That(succeeded, Is.True, failure);
        Assert.That(cleanup, Is.True);

        async Task<JsonElement> ReadItem(string id)
        {
            var read = await Send("read-item", ApiRequest.GraphQl("query($id:ID!){node(id:$id){... on ProjectV2Item{id project{id} fieldValues(first:100){nodes{... on ProjectV2ItemFieldNumberValue{number field{... on ProjectV2FieldCommon{id}}} ... on ProjectV2ItemFieldDateValue{date field{... on ProjectV2FieldCommon{id}}}} pageInfo{hasNextPage}}}}}", new { id }));
            var node = read.GetProperty("data").GetProperty("node");
            Assert.That(node.GetProperty("project").GetProperty("id").GetString(), Is.EqualTo(Project));
            Assert.That(node.GetProperty("fieldValues").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
            return JsonSerializer.SerializeToElement(node.GetProperty("fieldValues").GetProperty("nodes").EnumerateArray().Where(v => v.TryGetProperty("field", out _)));
        }
        async Task<JsonElement> Snapshot()
        {
            var r = await Send("scope-snapshot", ApiRequest.GraphQl("query{repository(owner:\"fukuda-yuki\",name:\"codex-sandbox\"){id issue(number:1){number}} user(login:\"fukuda-yuki\"){projectV2(number:3){id viewerCanUpdate fields(first:100){nodes{... on ProjectV2FieldCommon{id dataType}} pageInfo{hasNextPage}} items(first:100){nodes{id} pageInfo{hasNextPage}}}}}"));
            var data = r.GetProperty("data");
            Assert.That(data.GetProperty("repository").GetProperty("id").GetString(), Is.EqualTo(Repository));
            Assert.That(data.GetProperty("repository").GetProperty("issue").GetProperty("number").GetInt32(), Is.EqualTo(1));
            var p = data.GetProperty("user").GetProperty("projectV2");
            Assert.That(p.GetProperty("id").GetString(), Is.EqualTo(Project));
            Assert.That(p.GetProperty("viewerCanUpdate").GetBoolean(), Is.True);
            return p;
        }
        async Task<JsonElement> Send(string stage, ApiRequest request)
        {
            stages.Add(new { stage, state = "intent", at = DateTimeOffset.UtcNow }); Save();
            await Task.Delay(1100); // Bounded probe also respects mutation pacing.
            var r = await service.SendAsync(connection.Context, request);
            stages.Add(new { stage, state = r.Outcome.ToString(), failure = r.Failure.ToString(), r.HttpStatus, r.ExitCode }); Save();
            Assert.That(r.IsSuccess, Is.True, stage + ": " + r);
            return r.Data!.Value;
        }
        void Save() => File.WriteAllText(path, JsonSerializer.Serialize(new { marker, source = Environment.GetEnvironmentVariable("GHPB_PROOF_SOURCE"), host = "github.com", repository = Repository, project = Project, connection.Version, owned = owned.Select(o => new { o.Id, o.Number }), items, beforeItems, beforeFields, samples, stages, succeeded, cleanup, failure }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string[] Ids(JsonElement node, string property)
    {
        var page = node.GetProperty(property);
        Assert.That(page.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
        return page.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).Order().ToArray();
    }
}

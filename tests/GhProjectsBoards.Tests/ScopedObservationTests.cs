using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ScopedObservationTests
{
    [TestCase(100), TestCase(101), TestCase(1000)]
    public async Task TenSelectedFieldsNeverTraverseUnrelatedItems(int count)
    {
        var h = await ApplyTests.Harness.Create(count);
        var w = h.Workspace.Drafts!.Workspace; var rows = w.Open(h.Workspace.Selected!);
        for (var i = 0; i < 10; i++) w.Commit("P1", rows[i].Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(rows.Take(10).Select(r => r.ItemId).ToHashSet());
        using var trace = new PerformanceTrace();
        // Logical work-count check calls real observations, separately from paced elapsed runs.
        var batch = h.Workspace.ApplyReview!.Batch;
        var remote = new ApplyRemote(h.Service, h.Context);
        foreach (var operation in batch.Operations)
            Assert.That((await remote.ObserveAsync(batch, operation, default)).Observation?.Value, Is.EqualTo("todo"));
        Assert.That(trace.Samples.Where(s => s.Kind == "full-project-traversal"), Is.Empty);
        Assert.That(trace.Samples.Where(s => s.Kind == "returned-items").Sum(s => s.Count), Is.EqualTo(10));
        Assert.That(h.Writes, Is.Empty);
    }

    [TestCase("item-id"), TestCase("project"), TestCase("content"), TestCase("archived"), TestCase("field-id"), TestCase("field-type"),
     TestCase("option"), TestCase("missing-values"), TestCase("unknown-field"), TestCase("short-page")]
    public async Task InvalidScopedEvidenceNeverDispatchesAndRecoversDraft(string defect)
    {
        var h = await ApplyTests.Harness.Create();
        var w = h.Workspace.Drafts!.Workspace; var row = w.Open(h.Workspace.Selected!)[0];
        w.Commit("P1", row.Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { row.ItemId });
        h.ChangeResponse = (q, data) =>
        {
            var node = data["data"]!["node"]!;
            if (q.Contains("ProjectFields"))
            {
                var field = node["fields"]!["nodes"]![0]!;
                if (defect == "field-id") field["id"] = "wrong-id-same-name";
                if (defect == "field-type") field["dataType"] = "TEXT";
                if (defect == "option") field["options"] = new JsonArray();
            }
            if (!q.Contains("ApplyItem")) return;
            switch (defect)
            {
                case "item-id": node["id"] = "replacement"; break;
                case "project": node["project"]!["id"] = "P2"; break;
                case "content": node["content"]!["id"] = "I2"; break;
                case "archived": node["isArchived"] = true; break;
                case "missing-values": node.AsObject().Remove("fieldValues"); break;
                case "unknown-field": node["fieldValues"]!["nodes"]![0]!["field"] = null; break;
                case "short-page": node["fieldValues"]!["totalCount"] = 2; break;
            }
        };
        if (defect == "missing-values")
        {
            var prior = h.Boundary.Override!;
            h.Boundary.Override = (q, v) => q.Contains("ProjectItemValues") ? ScriptedRunner.Http("{}", 503) : prior(q, v);
        }
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes, Is.Empty);
        var recovered = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(recovered.Journal!.Single().Operations.Single().State, Is.EqualTo(ApplyState.Waiting));
        Assert.That(recovered.Fields.Single(f => f.Key == row.Cells[1].Key).Change!.Value, Is.EqualTo("done"));
    }

    [Test]
    public async Task DuplicateNamesUseExactFieldAndOptionIds()
    {
        var h = await ApplyTests.Harness.Create(); await h.Workspace.PrepareLocalRowsAsync(); var w = h.Workspace.Drafts!.Workspace;
        var row = w.Open(h.Workspace.Selected!)[0]; w.Commit("P1", row.Cells[1], "done", true);
        var local = w.AddRow(h.Workspace.Selected!);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { row.ItemId });
        h.ChangeResponse = (q, data) =>
        {
            if (!q.Contains("ProjectFields")) return;
            var fields = data["data"]!["node"]!["fields"]!;
            fields["nodes"]!.AsArray().Add(JsonSerializer.SerializeToNode(ProjectReaderTests.Field("P1", "other-status")));
            fields["totalCount"] = 3;
            fields["nodes"]![0]!["options"]![1]!["name"] = "Todo";
        };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Single().GetProperty("fieldId").GetString(), Is.EqualTo("P1-status"));
        Assert.That(h.Writes.Single().GetProperty("value").GetProperty("singleSelectOptionId").GetString(), Is.EqualTo("done"));
        var recovered = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(recovered.LocalRows!.Single().Id, Is.EqualTo(local));
        Assert.That(recovered.Journal!.Single().Operations.Single().State, Is.EqualTo(ApplyState.Succeeded));
    }

    [TestCase("mismatch"), TestCase("partial"), TestCase("cancel")]
    public async Task SuccessfulMutationWithUntrustedReadbackRemainsUnknown(string defect)
    {
        var h = await ApplyTests.Harness.Create(); var w = h.Workspace.Drafts!.Workspace; var row = w.Open(h.Workspace.Selected!)[0];
        w.Commit("P1", row.Cells[0], "B"); await h.Workspace.PrepareApplyAsync(new HashSet<string> { row.ItemId });
        var prior = h.Boundary.Override!;
        h.Boundary.Override = (q, v) =>
        {
            if (q.Contains("ApplyItem") && h.Writes.Count == 1)
            {
                if (defect == "partial") return ScriptedRunner.Http("{\"data\":{\"node\":null},\"errors\":[{\"type\":\"INTERNAL\"}]}");
                if (defect == "cancel") h.Workspace.Cancel();
                if (defect == "mismatch") h.Titles["I1"] = "C";
            }
            return prior(q, v);
        };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        var recovered = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        var operation = recovered.Journal!.Single().Operations.Single();
        Assert.That(h.Writes, Has.Count.EqualTo(1));
        Assert.That(operation.State, Is.EqualTo(ApplyState.Unknown));
        Assert.That(operation.Attempts.Single().State, Is.EqualTo(ApplyState.Unknown));
        Assert.That(recovered.Fields.Single(f => f.Key == row.Cells[0].Key).Change!.Value, Is.EqualTo("B"));
    }

    [TestCase("option"), TestCase("capability"), TestCase("membership")]
    public async Task WaitRequiresFreshSafetyPredicates(string defect)
    {
        var h = await ApplyTests.Harness.Create(); var w = h.Workspace.Drafts!.Workspace; var row = w.Open(h.Workspace.Selected!)[0];
        w.Commit("P1", row.Cells[1], "done", true); await h.Workspace.PrepareApplyAsync(new HashSet<string> { row.ItemId });
        h.MutationResult = (_, _) => ScriptedRunner.Http("{}", 429, 1, "Retry-After: 0\r\n");
        h.ChangeResponse = (q, data) =>
        {
            if (h.Writes.Count == 0) return;
            var node = data["data"]!["node"]!;
            if (q.Contains("ProjectFields") && defect == "option") node["fields"]!["nodes"]![0]!["options"] = new JsonArray();
            if (q.Contains("ProjectFields") && defect == "capability") node["viewerCanUpdate"] = false;
            if (q.Contains("ApplyItem") && defect == "membership") node["isArchived"] = true;
        };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes, Has.Count.EqualTo(1));
        var recovered = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(recovered.Journal!.Single().Operations.Single().Attempts.Single().State, Is.EqualTo(ApplyState.Failed));
        Assert.That(recovered.Fields.Single(f => f.Key == row.Cells[1].Key).Change!.Value, Is.EqualTo("done"));
    }

    [Test]
    public async Task ScopedCreationSetupCannotPromoteWithoutCompleteSnapshot()
    {
        var h = await CreationHarness.Create(); var local = h.Add();
        var row = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == local);
        h.Session.Workspace.Commit("P1", row.Cells[1], "done", true);
        var prior = h.Existing.Boundary.Override!;
        var fullReadUnavailable = true;
        h.Existing.Boundary.Override = (q, v) => q.Contains("ProjectItems") && fullReadUnavailable
            && h.Writes.Any(x => x.Query.Contains("ApplySelect")) ? ScriptedRunner.Http("{}", 503) : prior(q, v);
        await h.Apply(local);
        Assert.That(h.Session.Workspace.Creations.Single().Fields!.Single().State, Is.EqualTo(ApplyState.Succeeded));
        Assert.That(h.Session.Workspace.LocalRows.Single().Id, Is.EqualTo(local));
        var writes = h.Writes.Count; await h.Restart();
        Assert.That(h.Session.Workspace.LocalRows.Single().Id, Is.EqualTo(local));
        fullReadUnavailable = false;
        var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context, "https://github.com/users/sample-user/projects/1", default);
        await h.Workspace.RegisterAsync(choice, null, true);
        Assert.That(h.Session.Workspace.LocalRows, Is.Empty);
        Assert.That(h.Writes.Count, Is.EqualTo(writes));
    }

    [TestCase(false), TestCase(true)]
    public async Task LaterTargetValueRequiresCompletePages(bool failLater)
    {
        var h = await ApplyTests.Harness.Create(); var w = h.Workspace.Drafts!.Workspace;
        var row = w.Open(h.Workspace.Selected!)[0]; w.Commit("P1", row.Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { row.ItemId });
        h.ChangeResponse = (q, data) =>
        {
            if (!q.Contains("ApplyItem")) return;
            data["data"]!["node"]!["fieldValues"] = JsonSerializer.SerializeToNode(ProjectReaderTests.Page(
                [new { __typename = "ProjectV2ItemFieldTextValue", id = "other-value", field = new { id = "P1-text", project = new { id = "P1" } } }], 2, true, "later"));
        };
        var prior = h.Boundary.Override!;
        h.Boundary.Override = (q, v) => !q.Contains("ProjectItemValues") ? prior(q, v) : failLater
            ? ScriptedRunner.Http("{}", 503)
            : ProjectReaderTests.Response(new { __typename = "ProjectV2Item", id = row.ItemId, project = new { id = "P1" },
                fieldValues = ProjectReaderTests.Page([ProjectReaderTests.Value("P1", "P1-status", h.Selects.GetValueOrDefault(row.ItemId, "todo"))], 2) });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Count, Is.EqualTo(failLater ? 0 : 1));
        var recovered = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(recovered.Journal!.Single().Operations.Single().State, Is.EqualTo(failLater ? ApplyState.Waiting : ApplyState.Succeeded));
    }
}

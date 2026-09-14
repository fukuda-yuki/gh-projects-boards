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
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes, Is.Empty);
        var recovered = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(recovered.Journal!.Single().Operations.Single().State, Is.EqualTo(ApplyState.Waiting));
        Assert.That(recovered.Fields.Single(f => f.Key == row.Cells[1].Key).Change!.Value, Is.EqualTo("done"));
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

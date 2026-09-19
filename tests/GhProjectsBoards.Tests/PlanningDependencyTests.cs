using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningDependencyTests
{
    [Test]
    public void SharedDependencyAcknowledgementRevokesOtherProjectsStalePlanningUndo()
    {
        var a = PlanningPathTests.Registration();
        var b = a with { Snapshot = a.Snapshot with { Id = new(a.Snapshot.Id.Scope, "P2"),
            Fields = a.Snapshot.Fields.Select(f => f with { ProjectId = new(a.Snapshot.Id.Scope, "P2") }).ToArray(),
            Items = a.Snapshot.Items.Select(i => i with { Id = new(a.Snapshot.Id.Scope, i.Id.NodeId.Replace("P1", "P2")) }).ToArray() } };
        var w = new EditingWorkspace(a.Snapshot.Id.Scope); w.SetRegistrations([a, b]);
        w.CommitPlanning(a, PlanningPathTests.Plan() with { Fields = [] }, w.Revision);
        w.CommitPlanning(b, PlanningPathTests.Plan() with { ProjectId = "P2", Tasks = [new("I1", PlanningMode.Auto, "U1"),
            new("I2", PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 09:00"), PlanningContractTests.At("2026-10-05 12:00"))] }, w.Revision);
        var effort = w.Open(b)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        w.Commit("P2", effort, "6"); w.Commit("P2", effort, "12");
        w.CommitPlanning(a, w.Planning("P1")!, w.Revision, dependencies: [new("I1", ["I2"])]);
        var review = w.ReviewApply(a, new HashSet<string> { "P1T1" }); w.ConfirmApply(review);
        var operation = review.Batch.Operations.Single();
        w.RecordApply(review.Batch.Id, operation with { State = ApplyState.Succeeded,
            Verification = new("verified", a.Snapshot.Id, DateTimeOffset.UtcNow, "present", ValueAvailability.Present, null, []) }, acknowledge: true);
        var current = w.CheckpointRegistrations.Single(p => p.Snapshot.Id.NodeId == "P2");
        Assert.That(w.PlanFor(current).Tasks.Single(t => t.Id == "I1").Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 17:00")));
        w.Undo("P2");
        Assert.That(w.Value(effort), Is.EqualTo("12"), "A shared native acknowledgement cannot restore a plan from the old graph.");
        Assert.That(w.Value(w.Open(current)[0].Cells.Single(c => c.Key?.FieldId == "F-Finish")), Is.EqualTo("2026-10-06"));
        DraftStore.Validate(w.Snapshot());
    }
    [Test]
    public void ReSavingUnchangedPredecessorsDoesNotInvalidateEarlierUndo()
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.CommitPlanning(p, PlanningPathTests.Plan(), w.Revision);
        w.CommitPlanning(p, w.Planning("P1")!, w.Revision, dependencies: [new("I2", ["I1"])]);
        w.CommitPlanning(p, w.Planning("P1")! with { Cutoff = PlanningContractTests.At("2026-10-06 09:00") }, w.Revision, dependencies: [new("I2", ["I1"])]);
        w.Undo("P1"); w.Undo("P1");
        Assert.That(w.PlanFor(p).Inputs!.Single(i => i.Task.Id == "I2").Predecessors, Is.Empty);
    }
    [Test]
    public void VerifiedNativeEdgeChangeReconcilesOtherProjectDraftsAndDerivedPlan()
    {
        ProjectRegistration Registered(string project)
        {
            var p = EditingTests.Registration(project, count: 2);
            return p with { Snapshot = p.Snapshot with { Issues = p.Snapshot.Issues.ToDictionary(i => i.Key,
                i => i.Value with { Native = new([], i.Key.NodeId == "I2" ? [new(p.Snapshot.Id.Scope, "I1")] : [], new(ValueAvailability.Empty), true) }) } };
        }
        var a = Registered("P1"); var b = Registered("P2"); var w = new EditingWorkspace(a.Snapshot.Id.Scope); w.SetRegistrations([a, b]);
        w.CommitPlanning(a, PlanningPathTests.Plan() with { Fields = [] }, w.Revision);
        w.CommitPlanning(b, PlanningPathTests.Plan() with { ProjectId = "P2", Fields = [] }, w.Revision);
        Assert.That(w.PlanFor(b).Inputs!.Single(i => i.Task.Id == "I2").Predecessors, Has.Length.EqualTo(1));
        w.CommitPlanning(a, w.Planning("P1")!, w.Revision, dependencies: [new("I2", [])]);
        var review = w.ReviewApply(a, new HashSet<string> { "P1T2" }); w.ConfirmApply(review);
        var operation = review.Batch.Operations.Single();
        w.RecordApply(review.Batch.Id, operation with { State = ApplyState.Succeeded, Verification = new("verified", a.Snapshot.Id, DateTimeOffset.UtcNow,
            null, ValueAvailability.Empty, null, []) }, acknowledge: true);
        var current = w.CheckpointRegistrations.Single(p => p.Snapshot.Id.NodeId == "P2");
        Assert.That(w.PlanFor(current).Inputs!.Single(i => i.Task.Id == "I2").Predecessors, Is.Empty);
        Assert.That(w.Fields.Single(f => f.Key.Kind == "Dependency" && f.Key.ProjectId == "P2").Baseline, Is.Null);
        DraftStore.Validate(w.Snapshot());
    }
    [Test]
    public void DependencyAndWorkEditRecalculatesWholeGraphAndUndoRestoresOneOperation()
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.CommitPlanning(p, PlanningPathTests.Plan(), w.Revision);
        var rows = w.Open(p);
        var config = w.Planning("P1")! with { Tasks = [new("I1", PlanningMode.Auto, "U1"), new("I2", PlanningMode.Auto, "U1")] };
        w.CommitPlanning(p, config, w.Revision, [new(rows[0].ItemId, "Estimate", "4"), new(rows[1].ItemId, "Estimate", "4")], [new("I2", ["I1"])]);
        Assert.That(w.PlanFor(p).Tasks[1].Start, Is.EqualTo(PlanningContractTests.At("2026-10-05 14:00")));
        w.SaveRowView(w.PrepareRowView(p) with { Definition = new(Title: "Issue 2") });
        Assert.That(w.EvaluateRows(p).Select(r => r.ItemId), Is.EqualTo(new[] { "P1T2" }));
        Assert.That(w.PlanFor(p).Tasks[1].Start, Is.EqualTo(PlanningContractTests.At("2026-10-05 14:00")), "Filtering out the predecessor cannot remove it from the calculation graph.");
        var review = w.ReviewApply(p, new HashSet<string> { rows[1].ItemId });
        Assert.That(review.Blocked, Is.Empty);
        Assert.That(review.Batch.Operations.Single(o => o.Key.Kind == "Dependency").Key.FieldId, Is.EqualTo("I1"));
        var restored = EditingWorkspace.Restore(w.Snapshot()); restored.Undo("P1");
        Assert.That(restored.Planning("P1")!.Tasks, Is.Empty);
        Assert.That(restored.Fields.Where(f => f.Key.Kind == "Dependency").All(f => f.Change is null), Is.True);
        Assert.That(restored.Value(rows[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate")), Is.Null);
    }

    [Test]
    public async Task NativeLinkUsesReviewedSerialPublicationAndIndependentReadbackThenExplicitRemoval()
    {
        var h = await ApplyTests.Harness.Create(2, planning: true); var links = new HashSet<string>();
        h.ChangeResponse = (_, data) =>
        {
            var node = data["data"]!["node"]!;
            var items = node["items"] is { } all ? all["nodes"]!.AsArray().ToArray() : node["fieldValues"] is not null ? [node] : [];
            foreach (var item in items.Where(i => i!["content"]!["id"]!.ToString() == "I2"))
                item!["content"]!["blockedBy"] = JsonSerializer.SerializeToNode(ProjectReaderTests.Page(links.Select(id => (object)new { id }).ToArray(), links.Count));
        };
        h.MutationResult = (q, input) =>
        {
            if (!q.Contains("ApplyDependency")) return null;
            var id = input.GetProperty("blockingIssueId").GetString()!;
            if (q.Contains("addBlockedBy")) links.Add(id); else links.Remove(id);
            var name = q.Contains("addBlockedBy") ? "addBlockedBy" : "removeBlockedBy";
            return ScriptedRunner.Http(new JsonObject { ["data"] = new JsonObject { [name] = new JsonObject { ["issue"] = new JsonObject { ["id"] = "I2" } } } }.ToJsonString());
        };
        await h.Workspace.PrepareLocalRowsAsync();
        var w = h.Workspace.Drafts!.Workspace; var p = h.Workspace.Selected!;
        w.CommitPlanning(p, PlanningPathTests.Plan(), w.Revision, dependencies: [new("I2", ["I1"])]);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(await h.Workspace.Drafts.FlushAsync(), Is.True);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T2" });
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Empty);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);
        w = h.Workspace.Drafts.Workspace; p = h.Workspace.Selected!;
        Assert.That(w.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Succeeded), h.Workspace.Status);
        Assert.That(links, Is.EquivalentTo(new[] { "I1" }));
        w.CommitPlanning(p, w.Planning("P1")!, w.Revision, dependencies: [new("I2", [])]);
        await h.Workspace.Drafts.FlushAsync();
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T2" });
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Empty);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);
        Assert.That(links, Is.Empty);
        Assert.That(h.Workspace.Drafts.Workspace.Journal.SelectMany(b => b.Operations).All(o => o.State == ApplyState.Succeeded), Is.True);
    }
}

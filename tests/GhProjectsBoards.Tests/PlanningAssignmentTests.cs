using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningAssignmentTests
{
    internal static ProjectRegistration Assigned(params string[] people)
    {
        var p = PlanningPathTests.Registration();
        return p with { Snapshot = p.Snapshot with { Issues = p.Snapshot.Issues.ToDictionary(i => i.Key, i => i.Value with
        { Native = i.Value.Native! with { Assignees = people.Select(id => new NativePerson(new(p.Snapshot.Id.Scope, id), id)).ToArray() } }) } };
    }

    [Test]
    public void FirstEstimateUsesKnownAssigneeAndProjectWeightWithoutAnotherOwnerSelectionAndUndoRestoresUnplanned()
    {
        var p = Assigned("U1"); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.SetPlanning(PlanningPathTests.Plan() with { Version = 3, People = [new("U1", "Alice", 50)] }, 0);
        var rows = w.Open(p); var estimate = rows[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        var initial = JsonSerializer.Serialize(w.Snapshot());
        Assert.That(w.PlanFor(p).Tasks[0].Mode, Is.EqualTo(PlanningMode.Unplanned));
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(initial), "Viewing does not choose a method.");
        w.Commit("P1", estimate, "8");
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
        Assert.That(w.Planning("P1")!.Tasks.Single().OwnerId, Is.EqualTo("U1"));
        Assert.That(w.Value(estimate), Is.EqualTo("8"));
        var restored = EditingWorkspace.Restore(w.Snapshot()); restored.Undo("P1");
        Assert.That(restored.PlanFor(p).Tasks[0].Mode, Is.EqualTo(PlanningMode.Unplanned));
        Assert.That(restored.Value(estimate), Is.Null);
    }

    [TestCase("missing"), TestCase("multiple"), TestCase("incomplete"), TestCase("weight")]
    public void UnresolvedAssignmentNeverFabricatesCapacity(string condition)
    {
        var p = Assigned(condition == "missing" ? [] : condition == "multiple" ? ["U1", "U2"] : ["U1"]);
        if (condition == "incomplete") p = p with { Snapshot = p.Snapshot with
        { Issues = p.Snapshot.Issues.ToDictionary(i => i.Key, i => i.Value with { Native = null }) } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.SetPlanning(PlanningPathTests.Plan() with { Version = 3, People = condition == "weight" ? [] : [new("U1", "Alice", 50)] }, 0);
        var estimate = w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate"); w.Commit("P1", estimate, "8");
        Assert.That(w.PlanFor(p).Tasks[0].Resolved, Is.False);
        Assert.That(w.PlanFor(p).Tasks[0].Problem, Is.Not.Empty);
        Assert.That(w.Value(estimate), Is.EqualTo("8"));
    }

    [Test]
    public async Task LegacyOwnerMovesOnlyAfterExplicitComparisonAndUndoRestoresDatesReportsAndVersion()
    {
        var p = Assigned("U2"); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        var plan = PlanningPathTests.Plan() with { People = [new("U1", "Old", 100), new("U2", "New", 50)],
            Tasks = [new("I1", PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 10:17"),
                PlanningContractTests.At("2026-10-06 16:43"), Actuals: [new("U1", 5, new(2026, 10, 4))])] };
        w.SetPlanning(plan, 0); var row = w.Open(p)[0];
        w.Commit("P1", row.Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "8");
        var before = JsonSerializer.Serialize(w.Snapshot());
        Assert.That(w.PlanFor(p).Tasks[0].Start, Is.EqualTo(plan.Tasks[0].ManualStart));
        var candidate = w.SchedulingCandidate(p, row.ItemId, PlanningMode.Auto, null, null, true);
        var staged = EditingWorkspace.Restore(w.Snapshot()); staged.CommitPlanning(p, candidate, staged.Revision);
        Assert.That(staged.PlanFor(p).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before), "Comparison is read-only.");
        w.CommitPlanning(p, candidate, w.Revision);
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-assignment-" + Guid.NewGuid()));
        Assert.That(await new DraftSession(store, w, 0).FlushAsync(), Is.True);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!); restored.Undo("P1");
        Assert.That(restored.Planning("P1")!.Version, Is.EqualTo(1));
        Assert.That(restored.Planning("P1")!.Tasks[0].OwnerId, Is.EqualTo("U1"));
        Assert.That(restored.Planning("P1")!.Tasks[0].Actuals, Is.EqualTo(plan.Tasks[0].Actuals));
        Assert.That(restored.PlanFor(p).Tasks[0].Finish, Is.EqualTo(plan.Tasks[0].ManualFinish));
    }

    [TestCase("Start"), TestCase("Finish")]
    public void DirectDateInputRetainsTheOtherExactEndpointAndUndoRestoresPendingText(string role)
    {
        var p = Assigned("U1"); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.SetPlanning(PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual, "U1",
            PlanningContractTests.At("2026-10-05 10:17"), PlanningContractTests.At("2026-10-06 16:43"))] }, 0);
        var row = w.Open(p)[0]; var cell = row.Cells.Single(c => c.Key?.FieldId == "F-" + role);
        const string text = "2026-10-06 12:31"; w.SetPlanningBuffer(cell, text);
        w.CommitDateInput(p, row.ItemId, role, text, w.Revision);
        var result = w.PlanFor(p).Tasks[0]; Assert.That(result.Mode, Is.EqualTo(PlanningMode.Manual));
        Assert.That(role == "Start" ? result.Finish : result.Start, Is.EqualTo(PlanningContractTests.At(role == "Start" ? "2026-10-06 16:43" : "2026-10-05 10:17")));
        Assert.That(w.Value(cell), Is.EqualTo("2026-10-06")); Assert.That(w.Buffer(cell), Is.Null);
        var restored = EditingWorkspace.Restore(w.Snapshot()); restored.Undo("P1");
        Assert.That(restored.Buffer(cell), Is.EqualTo(text));
        Assert.That(restored.PlanFor(p).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 16:43")));
    }

    [Test]
    public void NativeReassignmentDoesNotMoveAnAdoptedAutomaticPlanUntilExplicitReplan()
    {
        var p = Assigned("U1"); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.SetPlanning(PlanningPathTests.Plan() with { Version = 3, People = [new("U1", "A", 100), new("U2", "B", 50)] }, 0);
        var row = w.Open(p)[0]; w.Commit("P1", row.Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "8");
        var later = Assigned("U2"); w.Reconcile(p, later); w.SetRegistrations([later]);
        var saved = JsonSerializer.Serialize(w.Snapshot());
        Assert.That(w.PlanFor(later).Tasks[0].Resolved, Is.False);
        Assert.That(w.Planning("P1")!.Tasks[0].OwnerId, Is.EqualTo("U1"));
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(saved));
        w.CommitPlanning(later, w.SchedulingCandidate(later, row.ItemId, PlanningMode.Auto, null, null, true), w.Revision);
        Assert.That(w.Planning("P1")!.Tasks[0].OwnerId, Is.EqualTo("U2"));
        Assert.That(w.PlanFor(later).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
    }

    [TestCase("EndDate"), TestCase("Completion")]
    public void ObservedFieldNameChangesKeepTheMappedIdAndCannotSelectAnotherSameNameField(string name)
    {
        var p = Assigned("U1");
        var mapped = p.Snapshot.Fields.Single(f => f.Id.NodeId == "F-Finish") with { Name = name };
        var other = mapped with { Id = new(p.Snapshot.Id.Scope, "F-other") };
        p = p with { Snapshot = p.Snapshot with {
            Fields = p.Snapshot.Fields.Select(f => f.Id == mapped.Id ? mapped : f).Append(other).ToArray(),
            Items = p.Snapshot.Items.Select(i => i with { Values = i.Values.Append(new(other.Id, null, "Absent", ValueAvailability.Empty)).ToArray() }).ToArray() } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.SetPlanning(PlanningPathTests.Plan() with { Version = 3 }, 0);
        var row = w.Open(p)[0]; var cell = row.Cells.Single(c => c.Key?.FieldId == "F-Finish");
        Assert.That(w.Columns(p).Visible.Single(c => c.Id.FieldId == "F-Finish").Name, Is.EqualTo(name));
        Assert.That(row.Cells.Any(c => c.Key?.FieldId == "F-other"), Is.False, "Unmapped same-name DATE is not adopted.");
        w.Commit("P1", row.Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "8");
        Assert.That(w.Value(cell), Is.EqualTo("2026-10-05"));
        Assert.That(w.ReviewApply(p, new HashSet<string> { row.ItemId }).Batch.Operations.Where(o => o.Key.FieldId == "F-Finish").Single().Intended.Value, Is.EqualTo("2026-10-05"));
    }
}

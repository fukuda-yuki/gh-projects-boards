using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningLocalRowTests
{
    private static (ProjectRegistration P, EditingWorkspace W) Workspace()
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        w.SetRegistrations([p]); w.CommitPlanning(p, PlanningPathTests.Plan(), w.Revision); w.Open(p);
        return (p, w);
    }
    private static EditCell Cell(EditingWorkspace w, ProjectRegistration p, string id, string role)
        => w.Open(p).Single(r => r.ItemId == id).Cells.Single(c => c.Key?.FieldId == "F-" + role);
    [TestCase(false), TestCase(true)]
    public void EarlierRowAdditionUndoCannotOrphanLaterTypedBufferOrInvalidatedManualWork(bool refresh)
    {
        var (p, w) = Workspace(); var id = w.AddRow(p); var cell = Cell(w, p, id, "Estimate");
        if (!refresh) w.SetBuffer(cell, "16未確定");
        else
        {
            w.CommitPlanning(p, w.Planning("P1")! with { Tasks = [new(id, PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 12:07"))] },
                w.Revision, [new(id, "Estimate", "16")]);
            var current = p with { RetrievedAt = p.RetrievedAt.AddMinutes(1), Snapshot = p.Snapshot with {
                Issues = p.Snapshot.Issues.ToDictionary(i => i.Key, i => i.Key.NodeId == "I2"
                    ? i.Value with { Native = i.Value.Native! with { Predecessors = [new(w.Scope, "I1")] } } : i.Value) } };
            w.Reconcile(p, current); w.SetRegistrations([current]); p = current;
        }
        Assert.Throws<InvalidOperationException>(() => w.Undo("P1"));
        Assert.That(w.LocalRows.Single().Id, Is.EqualTo(id));
        Assert.That(w.ApplyCandidates(p).Any(c => c.Missing), Is.False);
        if (refresh) { Assert.That(w.Value(cell), Is.EqualTo("16")); Assert.That(w.Planning("P1")!.Tasks.Single().Mode, Is.EqualTo(PlanningMode.Manual)); }
        else Assert.That(w.Buffer(cell), Is.EqualTo("16未確定"));
        w = EditingWorkspace.Restore(w.Snapshot()); Assert.That(w.LocalRows.Single().Id, Is.EqualTo(id));
    }
    [TestCase(false), TestCase(true)]
    public void RemoteGraphChangeCannotSplitLocalRowUndoFromItsTypedWorkAndManualPlan(bool remove)
    {
        var (p, w) = Workspace(); var id = w.AppendRows(p, "Prepared\tDone\t16\t8\t\t\t").Single();
        if (remove)
        {
            w.CommitPlanning(p, w.Planning("P1")! with { Tasks = [new(id, PlanningMode.Manual, "U1",
                PlanningContractTests.At("2026-10-05 12:07"), PlanningContractTests.At("2026-10-06 16:19"))] }, w.Revision);
            w.RemoveRows("P1", [w.Open(p).Single(r => r.ItemId == id)]);
        }
        var current = p with { RetrievedAt = p.RetrievedAt.AddMinutes(1), Snapshot = p.Snapshot with {
            Issues = p.Snapshot.Issues.ToDictionary(i => i.Key, i => i.Key.NodeId == "I2"
                ? i.Value with { Native = i.Value.Native! with { Predecessors = [new(w.Scope, "I1")] } } : i.Value) } };
        w.Reconcile(p, current); w.SetRegistrations([current]); w.Undo("P1");
        Assert.That(w.LocalRows.Any(r => r.Id == id), Is.EqualTo(!remove), "An indivisible stale operation must remain unchanged.");
        Assert.That(w.ApplyCandidates(current).Any(c => c.Missing), Is.False);
        if (!remove) Assert.That(w.Value(Cell(w, current, id, "Estimate")), Is.EqualTo("16"));
        w = EditingWorkspace.Restore(w.Snapshot());
        Assert.That(w.LocalRows.Any(r => r.Id == id), Is.EqualTo(!remove));
    }
    [Test]
    public void AppendAndDuplicateCopyExactCommittedEffortWithOneUndoAndNoInventedActuals()
    {
        var (p, w) = Workspace();
        var id = w.AppendRows(p, "Prepared\tDone\t16.125\t8\t\t\t").Single();
        Assert.That(w.Value(Cell(w, p, id, "Estimate")), Is.EqualTo("16.125"));
        Assert.That(w.Value(Cell(w, p, id, "Remaining")), Is.EqualTo("8"));
        var copy = w.DuplicateRows(p, [w.Open(p).Single(r => r.ItemId == id)]).Single();
        Assert.That(w.Value(Cell(w, p, copy, "Estimate")), Is.EqualTo("16.125"));
        Assert.That(w.PlanFor(p).Tasks.Single(t => t.Id == copy).Mode, Is.EqualTo(PlanningMode.Unplanned));
        Assert.That(w.CreationPlanningFor(p, copy).Select(i => i.FieldId), Is.EquivalentTo(new[] { "F-Estimate", "F-Remaining" }));
        w.Undo("P1"); Assert.That(w.LocalRows.Select(r => r.Id), Is.EqualTo(new[] { id }));
        w.Undo("P1"); Assert.That(w.LocalRows, Is.Empty); Assert.That(w.ApplyCandidates(p), Is.Empty);
        DraftStore.Validate(w.Snapshot());
    }
    [Test]
    public void LocalTypedInputRequiresCurrentRowAndDatedProjectPermission()
    {
        var (p, w) = Workspace(); var id = w.AddRow(p); var cell = Cell(w, p, id, "Estimate");
        var denied = p with { Snapshot = p.Snapshot with { Capability = new(false, DateTimeOffset.UtcNow) } };
        w.SetRegistrations([denied]);
        Assert.That(Cell(w, denied, id, "Estimate").Editable, Is.False);
        Assert.Throws<InvalidOperationException>(() => w.Commit("P1", cell, "16"));
        w.SetRegistrations([p]); w.RemoveRows("P1", [w.Open(p).Single(r => r.ItemId == id)]);
        Assert.Throws<InvalidOperationException>(() => w.Commit("P1", cell, "16"));
        Assert.That(w.ApplyCandidates(p), Is.Empty);
    }
    [Test]
    public void ExplicitClearOfNewTaskDiffersFromUnspecifiedAndRestoresThroughUndo()
    {
        var (p, w) = Workspace(); var id = w.AddRow(p); var cell = Cell(w, p, id, "Remaining");
        Assert.That(w.CreationPlanningFor(p, id), Is.Empty);
        w.Clear("P1", [cell]);
        Assert.That(w.CreationPlanningFor(p, id).Single().Value.Clear, Is.True);
        w = EditingWorkspace.Restore(w.Snapshot()); w.Undo("P1");
        Assert.That(w.CreationPlanningFor(p, id), Is.Empty);
    }
    [Test]
    public void DeleteAndUndoRetainLocalManualMetadataAndPendingWorkWithoutGhostPublication()
    {
        var (p, w) = Workspace(); var id = w.AddRow(p);
        var task = new PlanningTask(id, PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 12:07"), PlanningContractTests.At("2026-10-06 16:19"));
        w.CommitPlanning(p, w.Planning("P1")! with { Tasks = [task] }, w.Revision, [new(id, "Estimate", "16")]);
        var cell = Cell(w, p, id, "Remaining"); w.SetBuffer(cell, "未確定");
        w.RemoveRows("P1", [w.Open(p).Single(r => r.ItemId == id)]);
        Assert.That(w.ApplyCandidates(p), Is.Empty); Assert.That(w.Planning("P1")!.Tasks, Is.Empty);
        w = EditingWorkspace.Restore(w.Snapshot()); w.Undo("P1");
        Assert.That(w.Planning("P1")!.Tasks.Single(), Is.EqualTo(task));
        Assert.That(w.Buffer(Cell(w, p, id, "Remaining")), Is.EqualTo("未確定"));
        Assert.That(w.Value(Cell(w, p, id, "Estimate")), Is.EqualTo("16"));
    }
}

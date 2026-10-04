using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class GanttDependencyEditTests
{
    private static (ProjectRegistration Project, EditingWorkspace Work) Fixture()
    {
        var project = PlanningPathTests.Registration(3);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = Enumerable.Range(1, 3)
            .Select(n => new PlanningTask("I" + n, PlanningMode.Auto, "U1")).ToArray() }, work.Revision,
            Enumerable.Range(1, 3).Select(n => new PlanningValueEdit("P1T" + n, "Estimate", "8")).ToArray());
        return (project, work);
    }
    private static GanttDependencyEdit Edit(EditingWorkspace work, string from = "1", string to = "2", bool remove = false)
        => new("P1T" + from, "I" + from, "P1T" + to, "I" + to, work.Revision, remove);

    [Test]
    public void AddedLinkReplansSuccessorRetainsOtherWorkAndRestoredUndoRemovesOnlyThatOperation()
    {
        var (project, work) = Fixture();
        var title = work.Open(project)[2].Cells[0]; work.SetBuffer(title, "unfinished title");
        var before = work.PlanFor(project); var history = work.Snapshot().History.Length;

        work.CommitGanttDependency(project, Edit(work));

        var after = work.PlanFor(project);
        Assert.That(after.Inputs!.Single(i => i.Task.Id == "I2").Predecessors, Is.EqualTo(new[] { new PlanningLink("I1") }));
        Assert.That(after.Tasks.Single(t => t.Id == "I2").Start, Is.GreaterThanOrEqualTo(after.Tasks.Single(t => t.Id == "I1").Finish!.Value));
        Assert.That(work.Snapshot().History, Has.Length.EqualTo(history + 1));
        Assert.That(work.Buffer(title), Is.EqualTo("unfinished title"));
        Assert.That(work.Journal, Is.Empty);
        var restored = EditingWorkspace.Restore(work.Snapshot()); restored.Undo("P1");
        Assert.That(restored.PlanFor(project).Inputs!.Single(i => i.Task.Id == "I2").Predecessors, Is.Empty);
        Assert.That(restored.PlanFor(project).Tasks.Select(t => t.Start), Is.EqualTo(before.Tasks.Select(t => t.Start)));
        Assert.That(restored.Buffer(title), Is.EqualTo("unfinished title"));
    }

    [Test]
    public void ExplicitRemovalPreservesOtherEdgesAndUndoRestoresTheSelectedEdge()
    {
        var (project, work) = Fixture();
        work.CommitPlanning(project, work.Planning("P1")!, work.Revision, dependencies: [new("I3", ["I1", "I2"])]);
        var history = work.Snapshot().History.Length;

        work.CommitGanttDependency(project, Edit(work, "1", "3", remove: true));

        Assert.That(work.PlanFor(project).Inputs!.Single(i => i.Task.Id == "I3").Predecessors.Select(p => p.PredecessorId), Is.EqualTo(new[] { "I2" }));
        Assert.That(work.Snapshot().History, Has.Length.EqualTo(history + 1));
        work.Undo("P1");
        Assert.That(work.PlanFor(project).Inputs!.Single(i => i.Task.Id == "I3").Predecessors.Select(p => p.PredecessorId), Is.EquivalentTo(new[] { "I1", "I2" }));
        Assert.That(work.Journal, Is.Empty);
    }

    [TestCase(false), TestCase(true)]
    public void LocalEndpointLinkSurvivesReloadAndExplicitRemovalCanBeUndone(bool localSuccessor)
    {
        var (project, work) = Fixture(); var localId = work.AddRow(project);
        var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with { Tasks = plan.Tasks.Append(new(localId, PlanningMode.Auto, "U1")).ToArray() },
            work.Revision, [new(localId, "Estimate", "8")]);
        var fromRow = localSuccessor ? "P1T1" : localId; var fromTask = localSuccessor ? "I1" : localId;
        var toRow = localSuccessor ? localId : "P1T1"; var toTask = localSuccessor ? localId : "I1";
        work.CommitGanttDependency(project, new(fromRow, fromTask, toRow, toTask, work.Revision));
        var restored = EditingWorkspace.Restore(work.Snapshot());
        Assert.That(restored.PlanFor(project).Inputs!.Single(i => i.Task.Id == toTask).Predecessors.Select(l => l.PredecessorId), Does.Contain(fromTask));

        restored.CommitGanttDependency(project, new(fromRow, fromTask, toRow, toTask, restored.Revision, Remove: true));

        Assert.That(restored.PlanFor(project).Inputs!.Single(i => i.Task.Id == toTask).Predecessors, Is.Empty);
        restored.Undo("P1");
        Assert.That(restored.PlanFor(project).Inputs!.Single(i => i.Task.Id == toTask).Predecessors.Select(l => l.PredecessorId), Does.Contain(fromTask));
        Assert.That(restored.Journal, Is.Empty);
    }

    [TestCase("self"), TestCase("duplicate"), TestCase("cycle"), TestCase("stale"), TestCase("wrong-task"), TestCase("missing-row"), TestCase("missing-edge")]
    public void InvalidLinkRequestLeavesTheEntireCheckpointUnchanged(string reason)
    {
        var (project, work) = Fixture();
        if (reason == "duplicate") work.CommitGanttDependency(project, Edit(work));
        if (reason == "cycle") work.CommitPlanning(project, work.Planning("P1")!, work.Revision,
            dependencies: [new("I1", ["I3"]), new("I3", ["I2"])]);
        var request = Edit(work, to: reason == "self" ? "1" : "2", remove: reason == "missing-edge");
        request = reason switch { "stale" => request with { Revision = request.Revision - 1 },
            "wrong-task" => request with { PredecessorTaskId = "I3" },
            "missing-row" => request with { SuccessorRowId = "absent" }, _ => request };
        var before = JsonSerializer.Serialize(work.Snapshot());

        Assert.Throws<InvalidOperationException>(() => work.CommitGanttDependency(project, request));

        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
    }
}

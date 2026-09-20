using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class GanttProjectionTests
{
    [Test]
    public void ExactAdoptedTimesAndHiddenPredecessorsSurviveProjectionWithoutUsingRemoteDays()
    {
        var p = PlanningPathTests.Registration(4); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.CommitPlanning(p, PlanningPathTests.Plan() with { People = [new("U1", "Owner", 80)], Tasks = [
            new("I1", PlanningMode.Auto, "U1"), new("I2", PlanningMode.Manual, ManualStart: At("2026-10-05 12:07"), ManualFinish: At("2026-10-05 13:00")),
            new("I3", PlanningMode.Auto, LocalLinks: [new("I2")])] }, w.Revision,
            [new("P1T1", "Estimate", "16"), new("P1T3", "Estimate", "4")]);
        var projection = GanttProjection.Create(w, p, ["P1T1", "P1T3"]);
        Assert.That(projection.Rows, Has.Length.EqualTo(4));
        var weighted = projection.Rows.Single(r => r.TaskId == "I1");
        Assert.That(weighted.Plan!.Finish, Is.EqualTo(At("2026-10-07 13:00")));
        var manual = projection.Rows.Single(r => r.TaskId == "I2");
        Assert.That(manual.Plan!.Start, Is.EqualTo(At("2026-10-05 12:07")));
        Assert.That(manual.HiddenOnBoards, Is.True);
        Assert.That(manual.HasBar, Is.True, "Advisory unresolved suggestion does not replace a valid Manual pair.");
        var successor = projection.Rows.Single(r => r.TaskId == "I3");
        Assert.That(successor.Plan!.Start, Is.EqualTo(At("2026-10-05 14:00")));
        Assert.That(successor.Plan.Finish, Is.EqualTo(At("2026-10-05 18:00")));
        Assert.That(successor.Input!.Predecessors.Single().PredecessorId, Is.EqualTo("I2"));
        Assert.That(projection.Rows.Single(r => r.TaskId == "I4").State, Is.EqualTo(GanttState.Unplanned));
        Assert.That(w.Journal, Is.Empty);
    }

    [Test]
    public void MinuteGeometryDoesNotStretchShortTasksIntoDays()
    {
        var axis = new GanttAxis(At("2026-10-05 00:00"), 14, 96);
        Assert.That(axis.Position(At("2026-10-05 09:00")), Is.EqualTo(36));
        Assert.That(axis.Position(At("2026-10-05 13:00")), Is.EqualTo(52));
        Assert.That(axis.Position(At("2026-10-05 14:00")), Is.EqualTo(56));
        Assert.That(axis.Position(At("2026-10-05 18:00")), Is.EqualTo(72));
        Assert.That(axis.Position(At("2026-10-05 12:08")) - axis.Position(At("2026-10-05 12:07")), Is.EqualTo(1d / 15).Within(.00001));
    }

    internal static DateTime At(string value) => PlanningContractTests.At(value);

    [TestCase(PlanningMode.Unplanned, false, false, false, GanttState.Unplanned)]
    [TestCase(PlanningMode.Manual, true, false, false, GanttState.Partial)]
    [TestCase(PlanningMode.Auto, false, false, false, GanttState.Unresolved)]
    [TestCase(PlanningMode.Auto, true, true, true, GanttState.Stale)]
    [TestCase(PlanningMode.Manual, true, true, false, GanttState.Scheduled)]
    public void UnknownPartialAndStaleWorkCannotBecomeValidBars(PlanningMode mode, bool start, bool finish, bool stale, GanttState expected)
    {
        var plan = new TaskPlan("task", mode, stale ? 1 : 2, null, null,
            start ? At("2026-10-10 12:07") : null, finish ? At("2026-10-10 12:08") : null,
            null, null, "Missing effort", ["Manual warning"], null);
        Assert.That(GanttProjection.StateFor(plan, 2), Is.EqualTo(expected));
    }

    [Test]
    public void EmptyLegacyAndLocalTasksRemainVisibleWithoutInventingTimesOrChangingDrafts()
    {
        var p = PlanningPathTests.Registration(2); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        var local = w.AddRow(p); var pending = w.Open(p)[0].Cells[0]; w.SetBuffer(pending, "unfinished");
        var before = System.Text.Json.JsonSerializer.Serialize(w.Snapshot());
        var projection = GanttProjection.Create(w, p, []);
        Assert.That(projection.Rows, Has.Length.EqualTo(3));
        Assert.That(projection.Rows.Single(r => r.RowId == local).State, Is.EqualTo(GanttState.Unplanned));
        Assert.That(projection.Rows.All(r => !r.HasBar && r.Plan is null), Is.True);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before));
        var empty = PlanningPathTests.Registration(0); var emptyWork = new EditingWorkspace(empty.Snapshot.Id.Scope); emptyWork.SetRegistrations([empty]);
        Assert.That(GanttProjection.Create(emptyWork, empty, []).Rows, Is.Empty);
    }

    [Test]
    public void NormalFixtureHasDistinctIdentityAndExplicitLongHorizonAndDoesNotDropUnresolvedWork()
    {
        var (p, w) = GanttWorkload.Create(); var view = GanttProjection.Create(w, p, ["P1T1"]);
        Assert.That(view.Rows, Has.Length.EqualTo(1000));
        Assert.That(view.Rows[0].Title, Is.EqualTo(view.Rows[1].Title));
        Assert.That(view.Rows[0].Identity, Is.Not.EqualTo(view.Rows[1].Identity));
        Assert.That(view.Rows[996].State, Is.EqualTo(GanttState.Unplanned));
        Assert.That(view.Rows[997].State, Is.EqualTo(GanttState.Partial));
        Assert.That(view.Rows[998].State, Is.EqualTo(GanttState.Unresolved));
        Assert.That(view.Rows[999].Plan!.Finish, Is.EqualTo(At("2027-03-15 13:00")));
        var axis = GanttAxis.For(view, false);
        Assert.That(axis.Origin, Is.EqualTo(At("2026-10-05 00:00")));
        Assert.That(axis.Days, Is.GreaterThanOrEqualTo(169));
        Assert.That(view.Rows[989].Input!.Predecessors, Has.Length.EqualTo(12));
    }

    [Test]
    public void MaximumDateHasOneRenderableDayAndKeepsExactMinutePositions()
    {
        var plan = new TaskPlan("last", PlanningMode.Manual, 1, null, null, At("9999-12-31 12:07"), At("9999-12-31 13:00"), null, null, null, [], null);
        var view = new GanttProjection([new("row", "last", "last", "#1", plan, null, GanttState.Scheduled, false)], new("P1", 1, [plan]));
        var axis = GanttAxis.For(view, false);
        Assert.That(axis.Days, Is.EqualTo(1));
        Assert.That(axis.Origin.AddDays(axis.Days - 1), Is.EqualTo(At("9999-12-31 00:00")));
        Assert.That(axis.Position(plan.Finish!.Value), Is.EqualTo(52));
    }
}

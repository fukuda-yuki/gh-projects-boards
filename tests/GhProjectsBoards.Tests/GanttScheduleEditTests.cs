using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class GanttScheduleEditTests
{
    private static DateTime At(string value) => PlanningContractTests.At(value);
    private static (ProjectRegistration Project, EditingWorkspace Work) Fixture()
    {
        var project = PlanningPathTests.Registration();
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [
            new("I1", PlanningMode.Manual, "U1", At("2026-10-05 12:07"), At("2026-10-08 16:19"),
                Progress: PlanningProgress.InProgress, ActualStart: At("2026-10-05 09:00"), Actuals: [new("U1", 3, new(2026, 10, 5))]),
            new("I2", PlanningMode.Auto, "U1", LocalLinks: [new("I1")])] }, work.Revision,
            [new("P1T1", "Estimate", "16"), new("P1T1", "Remaining", "4"), new("P1T2", "Estimate", "1")]);
        return (project, work);
    }

    [TestCase(GanttDragPart.Move, "2026-10-07 12:07", "2026-10-10 16:19")]
    [TestCase(GanttDragPart.Start, "2026-10-07 12:07", "2026-10-08 16:19")]
    [TestCase(GanttDragPart.Finish, "2026-10-05 12:07", "2026-10-10 16:19")]
    public void CalendarDayDragRetainsExactMinutesAndOtherWorkAndOneUndoRestoresThePlan(GanttDragPart part, string start, string finish)
    {
        var (project, work) = Fixture();
        var before = work.Planning("P1")!; var history = work.Snapshot().History.Length;
        var unrelated = work.Open(project)[1].Cells[0]; work.SetBuffer(unrelated, "unfinished title");

        work.CommitGanttSchedule(project, new("P1T1", "I1", work.Revision, part, 2));

        var task = work.Planning("P1")!.Tasks[0];
        Assert.That(task, Is.EqualTo(before.Tasks[0] with { ManualStart = At(start), ManualFinish = At(finish) }));
        Assert.That(work.Snapshot().History.Length, Is.EqualTo(history + 1));
        Assert.That(work.Buffer(unrelated), Is.EqualTo("unfinished title"));
        Assert.That(work.Journal, Is.Empty);
        var restored = EditingWorkspace.Restore(work.Snapshot());
        restored.Undo("P1");
        Assert.That(PlanningContract.SameAdoptedPlan(restored.Planning("P1")!, before), Is.True);
        Assert.That(restored.Buffer(unrelated), Is.EqualTo("unfinished title"));
    }

    [TestCase("stale"), TestCase("wrong-task"), TestCase("missing-row"), TestCase("auto"), TestCase("completed"), TestCase("partial"), TestCase("reversed"), TestCase("date-buffer")]
    public void InvalidOrChangedDragCannotMutateWork(string reason)
    {
        var (project, work) = Fixture();
        if (reason is "completed" or "partial")
        {
            var plan = work.Planning("P1")!;
            work.CommitPlanning(project, plan with { Tasks = plan.Tasks.Select(task => task.Id != "I1" ? task : reason == "partial"
                ? task with { ManualFinish = null }
                : task with { Progress = PlanningProgress.Completed, ActualFinish = At("2026-10-08 16:19") }).ToArray() }, work.Revision,
                reason == "completed" ? [new("P1T1", "Remaining", "0")] : []);
        }
        if (reason == "date-buffer") work.SetPlanningBuffer(work.Open(project)[0].Cells.Single(cell => cell.Key?.FieldId == "F-Start"), "2026-11-05 12:07");
        var request = new GanttScheduleEdit(reason == "auto" ? "P1T2" : reason == "missing-row" ? "absent" : "P1T1",
            reason is "auto" or "wrong-task" ? "I2" : "I1", work.Revision - (reason == "stale" ? 1 : 0),
            reason == "reversed" ? GanttDragPart.Start : GanttDragPart.Move, reason == "reversed" ? 5 : 1);
        var before = System.Text.Json.JsonSerializer.Serialize(work.Snapshot());

        Assert.Throws<InvalidOperationException>(() => work.CommitGanttSchedule(project, request));

        Assert.That(System.Text.Json.JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
    }

    [Test]
    public void ZeroDayDragDoesNotCreateHistoryOrChangeRevision()
    {
        var (project, work) = Fixture(); var before = System.Text.Json.JsonSerializer.Serialize(work.Snapshot());
        work.CommitGanttSchedule(project, new("P1T1", "I1", work.Revision, GanttDragPart.Move, 0));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
    }
}

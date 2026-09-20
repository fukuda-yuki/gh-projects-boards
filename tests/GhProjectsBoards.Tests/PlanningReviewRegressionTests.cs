using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningReviewRegressionTests
{
    internal static ProjectRegistration Observed(string role, string value)
    {
        var p = PlanningAssignmentTests.Assigned("U1");
        return p with { Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Select(i => i with
        { Values = i.Values.Select(v => v.FieldId?.NodeId == "F-" + role
            ? v with { Scalar = value, Availability = ValueAvailability.Present } : v).ToArray() }).ToArray() } };
    }
    private static EditingWorkspace Work(ProjectRegistration p, ProjectPlanning? plan = null)
    {
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.SetPlanning(plan ?? PlanningPathTests.Plan(), 0); w.Open(p); return w;
    }
    [Test]
    public void EmptyBreakdownUpdateCannotTurnAnUnattributedObservedTotalIntoDeletion()
    {
        var p = Observed("Actual", "5"); var w = Work(p); var before = JsonSerializer.Serialize(w.Snapshot());
        Assert.That(() => w.CommitActualReports(p, "P1T1", [], w.Revision), Throws.InvalidOperationException);
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before));
        w.RemoveActualInput(p, "P1T1", w.Revision);
        Assert.That(w.Fields.Single(f => f.Key.NodeId == "P1T1" && f.Key.FieldId == "F-Actual").Change?.Clear, Is.True);
    }
    [TestCase("-1"), TestCase("1000000001"), TestCase("0.000000001")]
    public void RemoteNumberOutsideHoursDomainRemainsReadableAndRequiresExplicitWorker(string number)
    {
        var p = Observed("Actual", number); var w = Work(p); var before = JsonSerializer.Serialize(w.Snapshot());
        var context = w.ActualInput(p, "P1T1");
        Assert.That(context.ObservedTotal, Is.Null); Assert.That(context.HasPerson, Is.False);
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before));
    }
    [TestCase("Start", PlanningMode.Unplanned), TestCase("Finish", PlanningMode.Unplanned)]
    [TestCase("Start", PlanningMode.Auto), TestCase("Finish", PlanningMode.Auto)]
    public void EditingOneEndpointCannotClearTheOtherObservedDayWhoseExactTimeIsUnknown(string role, PlanningMode mode)
    {
        var other = role == "Start" ? "Finish" : "Start";
        var p = Observed(other, "2026-10-06");
        var w = Work(p, PlanningPathTests.Plan() with { Tasks = [new("I1", mode, "U1")] });
        var cell = w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-" + role);
        w.SetPlanningBuffer(cell, "2026-10-06 12:31"); var before = JsonSerializer.Serialize(w.Snapshot());
        Assert.That(() => w.CommitDateInput(p, "P1T1", role, "2026-10-06 12:31", w.Revision), Throws.InvalidOperationException);
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before));
    }
    [TestCase(false), TestCase(true)]
    public void OrdinaryManualOrEstimateInputDoesNotAdoptTheAssigneeOfALegacyUnplannedTask(bool estimate)
    {
        var p = PlanningAssignmentTests.Assigned("U1");
        var plan = EditingWorkspace.UpgradeAssignmentContract(PlanningPathTests.Plan() with
        { Tasks = [new("I1", OwnerId: "U-old")] });
        var w = Work(p, plan);
        if (estimate) w.Commit("P1", w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "8");
        else w.CommitDateInput(p, "P1T1", "Start", "2026-10-05 10:17", w.Revision);
        var task = w.Planning("P1")!.Tasks.Single();
        Assert.That(task.OwnerId, Is.EqualTo("U-old")); Assert.That(task.Assignment!.Legacy, Is.True);
        if (estimate) Assert.That(task.Mode, Is.EqualTo(PlanningMode.Unplanned));
    }
    [Test]
    public void DetachedPreviewAcceptsAnUnchangedRetainedTaskButRejectsItsModification()
    {
        var p = PlanningAssignmentTests.Assigned("U1");
        var retained = new PlanningTask("I-absent", PlanningMode.Manual, "U-old", Actuals: [new("U-old", 5, new(2026, 10, 5))],
            Contributions: [new("U-old", null, null)], LocalLinks: [], Assignment: new([], false, true));
        var w = Work(p, PlanningPathTests.Plan() with { Version = 3, Tasks = [retained] });
        var candidate = w.SchedulingCandidate(p, "P1T1", PlanningMode.Manual, PlanningContractTests.At("2026-10-05 10:17"), null, false);
        var staged = EditingWorkspace.Restore(w.Snapshot());
        Assert.That(() => staged.CommitPlanning(p, candidate, staged.Revision), Throws.Nothing);
        Assert.That(JsonSerializer.Serialize(staged.Planning("P1")!.Tasks.Single(t => t.Id == retained.Id)), Is.EqualTo(JsonSerializer.Serialize(retained)));
        var changed = candidate with { Tasks = candidate.Tasks.Select(t => t.Id == retained.Id ? t with { OwnerId = "U1" } : t).ToArray() };
        Assert.That(() => w.CommitPlanning(p, changed, w.Revision), Throws.InvalidOperationException);
    }
    [TestCase(false), TestCase(true)]
    public void UnrelatedWorkOnAPartialManualTaskKeepsTheObservedDayWithUnknownTime(bool actual)
    {
        var p = Observed("Finish", "2026-10-06");
        var w = Work(p, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual,
            ManualStart: PlanningContractTests.At("2026-10-05 10:17"))] });
        if (actual) w.CommitActualInput(p, "P1T1", "7", new(2026, 10, 6), "U1", w.Revision);
        else w.Commit("P1", w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "8");
        var field = w.Fields.Single(f => f.Key.NodeId == "P1T1" && f.Key.FieldId == "F-Finish");
        Assert.That(field.Baseline, Is.EqualTo("2026-10-06")); Assert.That(field.Change, Is.Null);
        Assert.That(w.Planning("P1")!.Tasks.Single().ManualFinish, Is.Null, "A day does not fabricate an exact time.");
    }
}

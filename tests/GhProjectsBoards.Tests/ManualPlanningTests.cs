using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ManualPlanningTests
{
    private static DateTime At(string s) => PlanningContractTests.At(s);
    [Test]
    public void MAN01_02_03_06_ManualPairAndPendingTextSurviveMetadataWorkRefreshAndRestart()
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        var plan = PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual, "U1", At("2026-10-05 12:07"), At("2026-10-06 16:19")), new("I2", PlanningMode.Auto, "U1")] };
        w.CommitPlanning(p, plan, w.Revision, [new("P1T1", "Estimate", "16"), new("P1T2", "Estimate", "1")], [new("I2", ["I1"])]);
        var cell = w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        w.SetBuffer(cell, "24"); var before = w.PlanFor(p).Tasks[0];
        w.CommitPlanning(p, plan with { People = [new("U1", "Owner", 50)] }, w.Revision);
        w.Reconcile(p, p with { RetrievedAt = p.RetrievedAt.AddMinutes(1) });
        var restored = EditingWorkspace.Restore(w.Snapshot()); var results = restored.PlanFor(p).Tasks;
        Assert.That(results[0].Start, Is.EqualTo(before.Start)); Assert.That(results[0].Finish, Is.EqualTo(before.Finish));
        Assert.That(results[0].Mode, Is.EqualTo(PlanningMode.Manual));
        Assert.That(restored.Buffer(cell), Is.EqualTo("24")); Assert.That(restored.Value(cell), Is.EqualTo("16"));
        Assert.That(results[1].Start, Is.EqualTo(before.Finish));
        Assert.That(results[1].Warnings, Is.Not.Empty);
        Assert.That(restored.Journal, Is.Empty);
    }
    [Test]
    public void MAN04_05_PartialManualAndExplicitReturnToAutoAreOneUndoableDecision()
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        var manual = new PlanningTask("I1", PlanningMode.Manual, "U1", At("2026-10-04 12:07"));
        var plan = PlanningPathTests.Plan() with { Tasks = [manual] };
        w.CommitPlanning(p, plan, w.Revision, [new("P1T1", "Estimate", "16")]);
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.Null);
        w.CommitPlanning(p, plan with { Tasks = [manual with { Mode = PlanningMode.Auto, ManualStart = null }] }, w.Revision);
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.EqualTo(At("2026-10-06 18:00")));
        w.Undo("P1"); Assert.That(w.PlanFor(p).Tasks[0].Start, Is.EqualTo(manual.ManualStart));
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.Null);
        Assert.That(w.PlanFor(p).Tasks[0].Warnings, Has.Some.Contains("区間外"));
    }
    [TestCase("Start", "2026-10-07", "2026-10-07 09:17")]
    [TestCase("Finish", null, null)]
    public void OutsideDateRequiresExactDecisionAndUndoRetainsFreshRemoteBaseline(string role, string? day, string? exact)
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        var task = new PlanningTask("I1", PlanningMode.Manual, "U1", At("2026-10-05 12:07"), At("2026-10-09 16:19"));
        var plan = PlanningPathTests.Plan() with { Tasks = [task] };
        w.CommitPlanning(p, plan, w.Revision);
        // Publish-compatible initial baseline, then a later independent outside edit.
        var current = WithScalar(p, "P1T1", role, role == "Start" ? "2026-10-05" : "2026-10-09");
        w.Reconcile(p, current); w.SetRegistrations([current]);
        var remote = WithScalar(current, "P1T1", role, day); w.Reconcile(current, remote); w.SetRegistrations([remote]);
        Assert.That(w.Planning("P1")!.Tasks[0], Is.EqualTo(task));
        var pending = w.PlanningDecisions("P1", "P1T1").Single();
        Assert.That(w.ReviewApply(remote, new HashSet<string> { "P1T1" }).Blocked, Is.Not.Empty);
        var adopted = role == "Start" ? task with { ManualStart = exact is null ? null : At(exact) } : task with { ManualFinish = exact is null ? null : At(exact) };
        w.CommitPlanning(remote, plan with { Tasks = [adopted] }, w.Revision, decisions: [new(pending.Key, pending.Observation!.Id, true)]);
        Assert.That(w.PlanningDecisions("P1", "P1T1"), Is.Empty);
        Assert.That(w.Planning("P1")!.Tasks[0], Is.EqualTo(adopted));
        w.Undo("P1"); Assert.That(w.Planning("P1")!.Tasks[0], Is.EqualTo(task));
        Assert.That(w.Fields.Single(f => f.Key == pending.Key).Baseline, Is.EqualTo(day));
        DraftStore.Validate(w.Snapshot());
    }
    [Test]
    public void KeepingOutsideActualTotalRequiresExplicitReportsAndReportedThroughDate()
    {
        var p = WithScalar(PlanningPathTests.Registration(), "P1T1", "Actual", "10");
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.CommitPlanning(p, PlanningPathTests.Plan(), w.Revision);
        var remote = WithScalar(p, "P1T1", "Actual", "12"); w.Reconcile(p, remote); w.SetRegistrations([remote]);
        var field = w.PlanningDecisions("P1", "P1T1").Single(); var decision = new PlanningProjectionDecision(field.Key, field.Observation!.Id, false);
        Assert.Throws<InvalidOperationException>(() => w.CommitPlanning(remote, w.Planning("P1")!, w.Revision, decisions: [decision]));
        w.CommitPlanning(remote, w.Planning("P1")! with { Tasks = [new("I1", Actuals: [new(null, 10, new(2026, 10, 5))])] }, w.Revision, decisions: [decision]);
        Assert.That(w.Fields.Single(f => f.Key == field.Key).Change?.Value, Is.EqualTo("10"));
        Assert.That(w.PlanningDecisions("P1", "P1T1"), Is.Empty);
        DraftStore.Validate(w.Snapshot());
        w.Undo("P1");
        var review = w.ReviewApply(remote, new HashSet<string> { "P1T1" });
        Assert.That(review.Blocked, Has.Some.Contains("報告"));
        Assert.That(review.Batch.Operations.Any(o => o.Key.FieldId == "F-Actual"), Is.False);
    }
    internal static ProjectRegistration WithScalar(ProjectRegistration p, string row, string role, string? value)
        => p with { RetrievedAt = p.RetrievedAt.AddMinutes(1), Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Select(i => i.Id.NodeId != row ? i : i with {
            Values = i.Values.Select(v => v.FieldId?.NodeId != "F-" + role ? v : v with { Scalar = value, Availability = value is null ? ValueAvailability.Empty : ValueAvailability.Present }).ToArray() }).ToArray() } };
}

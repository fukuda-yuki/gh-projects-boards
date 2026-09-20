using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningTransitionTests
{
    [Test]
    public async Task PublishedWorkRevokesEarlierSettingsUndoThatWouldDivergeFromAdoptedDateFields()
    {
        var h = await ApplyTests.Harness.Create(2, planning: true); await h.Workspace.PrepareLocalRowsAsync();
        var w = h.Workspace.Drafts!.Workspace; var p = h.Workspace.Selected!;
        w.CommitPlanning(p, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Auto, "U1")] }, w.Revision);
        w.CommitPlanning(p, w.Planning("P1")! with { People = [new("U1", "Owner", 50)] }, w.Revision);
        w.Commit("P1", w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "16");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }); await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        w = h.Workspace.Drafts.Workspace; p = h.Workspace.Selected!;
        Assert.That(w.Journal.Single().Operations.All(o => o.State == ApplyState.Succeeded), Is.True);
        w.Undo("P1");
        Assert.That(w.Planning("P1")!.People.Single().WeightPercent, Is.EqualTo(50));
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-08 18:00")));
        Assert.That(w.Value(w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Finish")), Is.EqualTo("2026-10-08"));
    }
    [TestCase(null, true), TestCase("16", true), TestCase("8", false)]
    public void PendingTextRefreshUsesCompatibleCommittedWorkAndHoldsRealConflict(string? remoteHours, bool compatible)
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.CommitPlanning(p, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Auto, "U1")] }, w.Revision,
            [new("P1T1", "Estimate", "16")]);
        var cell = w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate"); w.SetBuffer(cell, "24未確定");
        var current = p with { RetrievedAt = p.RetrievedAt.AddMinutes(1), Snapshot = p.Snapshot with {
            Items = p.Snapshot.Items.Select(i => i.Id.NodeId != "P1T1" ? i : i with { Values = i.Values.Select(v => v.FieldId?.NodeId != "F-Estimate" ? v
                : v with { Scalar = remoteHours, Availability = remoteHours is null ? ValueAvailability.Empty : ValueAvailability.Present }).ToArray() }).ToArray() } };
        w.Reconcile(p, current); w.SetRegistrations([current]); w = EditingWorkspace.Restore(w.Snapshot());
        Assert.That(w.PlanFor(current).Tasks[0].Resolved, Is.EqualTo(compatible));
        if (compatible) Assert.That(w.PlanFor(current).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
        Assert.That(w.Buffer(cell), Is.EqualTo("24未確定")); Assert.That(w.Value(cell), Is.EqualTo("16"));
        w.SetBuffer(cell, null); w.Reconcile(current, current with { RetrievedAt = current.RetrievedAt.AddMinutes(1) });
        Assert.That(w.PlanFor(current).Tasks[0].Resolved, Is.EqualTo(compatible));
        Assert.That(w.Buffer(cell), Is.Null); Assert.That(w.Journal, Is.Empty);
    }
    private static (ProjectRegistration P, EditingWorkspace W) Setup(PlanningTask task)
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.CommitPlanning(p, PlanningPathTests.Plan() with { Tasks = [task] }, w.Revision, [new("P1T1", "Estimate", "16"), new("P1T1", "Remaining", "0")]);
        return (p, w);
    }
    [Test]
    public void UnresolvedAutoCannotPublishPreviouslyCalculatedDateDraft()
    {
        var (p, w) = Setup(new("I1", PlanningMode.Auto, "U1"));
        w.CommitPlanning(p, w.Planning("P1")! with { People = [new("U1", "Owner", 0)] }, w.Revision);
        Assert.That(w.PlanFor(p).Tasks[0].Resolved, Is.False);
        var review = w.ReviewApply(p, new HashSet<string> { "P1T1" });
        Assert.That(review.Blocked, Has.Some.Contains("未解決"));
        Assert.That(review.Batch.Operations.Any(o => o.Key.Kind == "Date"), Is.False);
        w.Undo("P1");
        Assert.That(w.ReviewApply(p, new HashSet<string> { "P1T1" }).Blocked, Is.Empty);
    }
    [Test]
    public void ReopeningRequiresExplicitRemainingAndPreservesActualsAndManualUntilReleased()
    {
        var completed = new PlanningTask("I1", PlanningMode.Manual, "U1",
            PlanningContractTests.At("2026-10-05 12:07"), PlanningContractTests.At("2026-10-06 16:19"), PlanningProgress.Completed,
            PlanningContractTests.At("2026-10-05 09:00"), PlanningContractTests.At("2026-10-06 18:00"),
            Actuals: [new("U1", 15, new(2026, 10, 6))]);
        var (p, w) = Setup(completed);
        var reopened = completed with { Progress = PlanningProgress.Reopened };
        w.CommitPlanning(p, w.Planning("P1")! with { Tasks = [reopened] }, w.Revision);
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.EqualTo(completed.ManualFinish));
        var auto = w.Planning("P1")! with { Tasks = [reopened with { Mode = PlanningMode.Auto, ManualStart = null, ManualFinish = null }] };
        Assert.Throws<InvalidOperationException>(() => w.CommitPlanning(p, auto, w.Revision));
        w.CommitPlanning(p, auto, w.Revision, [new("P1T1", "Remaining", "4")]);
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-05 13:00")));
        Assert.That(w.Planning("P1")!.Tasks[0].Actuals, Is.EqualTo(completed.Actuals));
        w.Undo("P1"); Assert.That(w.Planning("P1")!.Tasks[0].Mode, Is.EqualTo(PlanningMode.Manual));
    }
    [Test]
    public void NativeGraphChangeRevokesPriorPlanningUndoEvenWhenPublishedDayDoesNotChange()
    {
        var (p, w) = Setup(new("I1", PlanningMode.Auto, "U1"));
        w.CommitPlanning(p, w.Planning("P1")! with { Tasks = [new("I1", PlanningMode.Auto, "U1"),
            new("I2", PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 09:00"), PlanningContractTests.At("2026-10-05 12:00"))] }, w.Revision);
        var cell = w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        w.Commit("P1", cell, "6"); w.Commit("P1", cell, "12");
        var issue = p.Snapshot.Issues[new(w.Scope, "I1")];
        var current = p with { RetrievedAt = p.RetrievedAt.AddMinutes(1), Snapshot = p.Snapshot with { Issues = p.Snapshot.Issues.ToDictionary(k => k.Key,
            k => k.Key == issue.Id ? issue with { Native = issue.Native! with { Predecessors = [new(w.Scope, "I2")] } } : k.Value) } };
        w.Reconcile(p, current); w.SetRegistrations([current]);
        var finish = w.PlanFor(current).Tasks.Single(t => t.Id == "I1").Finish;
        Assert.That(finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 17:00")));
        w.Undo("P1");
        Assert.That(w.Value(cell), Is.EqualTo("12"), "An old calculation may not be restored across a new native graph.");
        Assert.That(w.PlanFor(current).Tasks.Single(t => t.Id == "I1").Finish, Is.EqualTo(finish));
        Assert.That(w.UndoWarnings, Is.Not.Empty);
    }
    [Test]
    public void MAN05_UnplannedCannotEraseAnAdoptedManualOverride()
    {
        var (p, w) = Setup(new("I1", PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 12:07")));
        var before = w.Planning("P1")!;
        Assert.Throws<InvalidOperationException>(() => w.CommitPlanning(p, before with { Tasks = [before.Tasks[0] with { Mode = PlanningMode.Unplanned, ManualStart = null }] }, w.Revision));
        Assert.That(w.Planning("P1"), Is.SameAs(before));
    }
}

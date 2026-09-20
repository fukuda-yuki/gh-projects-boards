using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("Integration")]
internal sealed class PlanningCreationTests
{
    [TestCase(false), TestCase(true)]
    public async Task CreationLineageCannotSplitTypedBatchUndoAndOrphanUnpublishedWork(bool incomplete)
    {
        var h = await CreationHarness.Create(2, planning: true); var p = h.Workspace.Selected!; var w = h.Session.Workspace;
        w.CommitPlanning(p, PlanningPathTests.Plan(), w.Revision);
        var ids = w.AppendRows(p with { DefaultRepository = "sample-user/first" }, "First\tTodo\t16\t\t\t\t\nSecond\tTodo\t8\t\t\t\t");
        if (incomplete) h.AfterCreate = () => h.Incomplete = true;
        await h.Apply(ids[0]);
        w = h.Session.Workspace; p = h.Workspace.Selected!;
        Assert.That(w.Creations.Single().Completed, Is.EqualTo(!incomplete));
        if (incomplete) Assert.Throws<InvalidOperationException>(() => w.Undo("P1"));
        else w.Undo("P1");
        var retained = w.Open(p).Single(r => r.ItemId == ids[1]);
        Assert.That(w.Value(retained.Cells.Single(c => c.Key?.FieldId == "F-Estimate")), Is.EqualTo("8"));
        Assert.That(w.ApplyCandidates(p).Any(c => c.Missing), Is.False);
        DraftStore.Validate(w.Snapshot());
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task LocalPlanningPromotesThroughKnownIdentitiesAndResumesDependentSetupWithoutCreatingAgain(bool removeAfterApproval)
    {
        var h = await CreationHarness.Create(2, planning: true); var b = h.Add("Auto successor"); var a = h.Add("Manual predecessor");
        var p = h.Workspace.Selected!; var w = h.Session.Workspace;
        var plan = PlanningPathTests.Plan() with { Tasks = [
            new(a, PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 12:07"), PlanningContractTests.At("2026-10-06 16:19")),
            new(b, PlanningMode.Auto, "U1")] };
        w.CommitPlanning(p, plan, w.Revision, [new(a, "Estimate", "4"), new(b, "Estimate", "8")], [new(b, [a])]);
        Assert.That(w.PlanFor(p).Tasks.Single(t => t.Id == b).Start, Is.EqualTo(plan.Tasks[0].ManualFinish));
        await h.Apply(b, a);
        Assert.That(h.Session.Workspace.Creations.Count(), Is.EqualTo(2), h.Workspace.Status);
        Assert.That(h.Session.Workspace.Creations.Single(c => c.LocalId == b).Completed, Is.False);
        Assert.That(h.Session.Workspace.Creations.Single(c => c.LocalId == b).Reason, Does.Contain("先行"));
        var batch = h.Session.Workspace.Journal.Single();
        if (removeAfterApproval)
        {
            w = h.Session.Workspace; p = h.Workspace.Selected!;
            w.CommitPlanning(p, w.Planning("P1")!, w.Revision, dependencies: [new(b, [])]);
            await h.Session.FlushAsync();
        }
        await h.Restart(); await h.Workspace.ResumeApplyAsync(batch.Id);
        Assert.That(h.Issues, Has.Count.EqualTo(2));
        Assert.That(h.Session.Workspace.Creations.All(c => c.Completed), Is.True, string.Join(" / ", h.Session.Workspace.Creations.Select(c => c.Reason)));
        var previous = h.Session.Workspace.Creations.Single(c => c.LocalId == a); var successor = h.Session.Workspace.Creations.Single(c => c.LocalId == b);
        Assert.That(h.Dependencies[successor.Verified!.Id], Does.Contain(previous.Verified!.Id));
        Assert.That(h.Session.Workspace.LocalRows, Is.Empty, h.Workspace.Status + " / " + h.Session.Status);
        var result = h.Session.Workspace.PlanFor(h.Workspace.Selected!);
        Assert.That(result.Tasks.Single(t => t.Id == previous.Verified.Id).Finish, Is.EqualTo(plan.Tasks[0].ManualFinish));
        Assert.That(result.Tasks.Single(t => t.Id == successor.Verified.Id).Start, Is.EqualTo(removeAfterApproval ? plan.Start : plan.Tasks[0].ManualFinish));
        if (removeAfterApproval) Assert.That(h.Session.Workspace.Fields.Single(f => f.Key.Kind == "Dependency" && f.Key.NodeId == successor.Verified.Id).Change?.Clear, Is.True);
        Assert.That(h.Existing.Scalars[successor.ItemId + "/F-Estimate"]!.GetValue<decimal>(), Is.EqualTo(8));
        DraftStore.Validate(h.Session.Workspace.Snapshot());
    }
    [Test]
    public async Task UnspecifiedLocalNumberAdoptsValueObservedAfterCreationInsteadOfClearingIt()
    {
        var h = await CreationHarness.Create(2, planning: true); var id = h.Add();
        var w = h.Session.Workspace; w.CommitPlanning(h.Workspace.Selected!, PlanningPathTests.Plan(), w.Revision);
        h.AfterAdd = () => h.Existing.Scalars["item-created1/F-Remaining"] = 5m;
        await h.Apply(id);
        Assert.That(h.Session.Workspace.LocalRows, Is.Empty, h.Workspace.Status);
        var field = h.Session.Workspace.Fields.Single(f => f.Key.NodeId == "item-created1" && f.Key.FieldId == "F-Remaining");
        Assert.That(field.Baseline, Is.EqualTo("5")); Assert.That(field.Change, Is.Null);
    }
    [Test]
    public async Task KnownSetupCannotNewlyApproveAnUnresolvedAutoDateProjection()
    {
        var h = await CreationHarness.Create(2, planning: true); var id = h.Add();
        var w = h.Session.Workspace; w.CommitPlanning(h.Workspace.Selected!, PlanningPathTests.Plan() with { Tasks = [new(id, PlanningMode.Auto, "U1")] }, w.Revision, [new(id, "Estimate", "16")]);
        h.AfterCreate = () => h.Incomplete = true;
        await h.Apply(id); var batch = h.Session.Workspace.Journal.Single(); var creation = batch.Creations!.Single();
        h.Incomplete = false; w = h.Session.Workspace;
        w.CommitPlanning(h.Workspace.Selected!, w.Planning("P1")! with { People = [new("U1", "Owner", 0)] }, w.Revision);
        await h.Workspace.PrepareCreationSetupAsync(batch.Id, creation.Id);
        Assert.That(h.Workspace.CreationSetupReview, Is.Null); Assert.That(h.Workspace.Status, Does.Contain("未解決"));
        Assert.That(h.Issues, Has.Count.EqualTo(1));
    }
    [Test]
    public async Task MissingUnusedFieldDoesNotTrapAnOtherwiseCompletedNewRow()
    {
        var h = await CreationHarness.Create(2, planning: true); var id = h.Add();
        var w = h.Session.Workspace; w.CommitPlanning(h.Workspace.Selected!, PlanningPathTests.Plan(), w.Revision); w.Open(h.Workspace.Selected!);
        h.AfterCreate = () => h.MissingPlanningField = "F-Remaining";
        await h.Apply(id);
        Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True, h.Workspace.Status);
        Assert.That(h.Session.Workspace.LocalRows, Is.Empty);
        DraftStore.Validate(h.Session.Workspace.Snapshot());
    }
    [Test]
    public async Task WithdrawnMissingFieldRetainsPendingTextWhenKnownCreationPromotes()
    {
        var h = await CreationHarness.Create(2, planning: true); var id = h.Add();
        var w = h.Session.Workspace; w.CommitPlanning(h.Workspace.Selected!, PlanningPathTests.Plan(), w.Revision, [new(id, "Estimate", "16")]);
        w.SetBuffer(w.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "未確定");
        h.AfterCreate = () => h.MissingPlanningField = "F-Estimate";
        await h.Apply(id);
        var batch = h.Session.Workspace.Journal.Single(); var creation = batch.Creations!.Single();
        Assert.That(creation.Completed, Is.False);
        await h.Workspace.PrepareCreationSetupAsync(batch.Id, creation.Id);
        var review = h.Workspace.CreationSetupReview!;
        Assert.That(review, Is.Not.Null, h.Workspace.Status);
        Assert.That(review.WithdrawnPlanning!.Single().FieldId, Is.EqualTo("F-Estimate"));
        await h.Workspace.ConfirmCreationSetupAsync(review);
        Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True, h.Workspace.Status);
        Assert.That(h.Session.Workspace.LocalRows, Is.Empty);
        var retained = h.Session.Workspace.Fields.Single(f => f.Key.NodeId == "item-created1" && f.Key.FieldId == "F-Estimate");
        Assert.That(retained.Buffer, Is.EqualTo("未確定")); Assert.That(retained.Change, Is.Null);
        Assert.That(retained.Observation?.Reason, Is.Not.Null);
        await h.Restart();
        Assert.That(h.Session.Workspace.Fields.Single(f => f.Key == retained.Key).Buffer, Is.EqualTo("未確定"));
        w = h.Session.Workspace; w.CancelUnavailablePlanningDraft(retained.Key, w.Revision);
        Assert.That(w.Fields.Single(f => f.Key == retained.Key).Buffer, Is.Null);
        w.CommitPlanning(h.Workspace.Selected!, w.Planning("P1")! with { Fields = w.Planning("P1")!.Fields.Where(b => b.FieldId != "F-Estimate").ToArray() }, w.Revision);
        w.Undo("P1"); w.Undo("P1");
        Assert.That(w.Fields.Single(f => f.Key == retained.Key).Buffer, Is.EqualTo("未確定"));
    }
    [Test]
    public async Task BindingCannotReplacePlanningWorkAlreadyOwnedByExistingIssue()
    {
        var h = await CreationHarness.Create(2, planning: true); var id = h.Add();
        var w = h.Session.Workspace; w.CommitPlanning(h.Workspace.Selected!, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual)] }, w.Revision);
        h.LoseCreate = true; await h.Apply(id);
        w = h.Session.Workspace; var c = w.Creations.Single(); var batch = w.Journal.Single();
        var issue = new CreatedIssue("I1", c.Repository.Id, 1, "https://github.com/sample-user/first/issues/1", "Issue 1", DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => w.BindCreation(batch.Id, c.Id, issue, w.Revision));
        Assert.That(w.Planning("P1")!.Tasks.Single().Id, Is.EqualTo("I1"));
    }
}

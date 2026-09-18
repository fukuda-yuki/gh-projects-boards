using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyConfirmationTests
{
    [Test]
    public async Task RecheckingSameIdentityRetainsProjectAndPendingWorkButInvalidatesApproval()
    {
        var h = await CreationHarness.Create(3);
        var p = h.Workspace.Selected!;
        var cell = h.Session.Workspace.Open(p)[0].Cells[0];
        h.Session.Workspace.Commit("P1", cell, "Outgoing");
        h.Session.Workspace.SetBuffer(cell, "送らない入力");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        var review = h.Workspace.ApplyReview!;

        await h.Workspace.BindAsync(h.Existing.Context, h.Existing.Service);

        Assert.That(h.Workspace.Selected?.Snapshot.Id, Is.EqualTo(p.Snapshot.Id));
        Assert.That(h.Session.Workspace.Buffer(cell), Is.EqualTo("送らない入力"));
        Assert.That(h.Workspace.ApplyReview, Is.Null);
        await h.Workspace.ConfirmApplyAsync(review);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(h.Session.Workspace.Journal, Is.Empty);
    }

    [Test]
    public async Task LeavingAndReturningToTheSameProjectCannotReuseAnEarlierReview()
    {
        var h = await CreationHarness.Create(1);
        var project = h.Workspace.Selected!.Snapshot.Id;
        h.Session.Workspace.Commit("P1", h.Session.Workspace.Open(h.Workspace.Selected!)[0].Cells[0], "Retained outgoing");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        var review = h.Workspace.ApplyReview!;
        await h.Workspace.SelectProfileAsync(project.Scope);
        await h.Workspace.SelectAsync(project);

        await h.Workspace.ConfirmApplyAsync(review);

        Assert.That(h.Workspace.ApplyReview, Is.Null);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(h.Session.Workspace.Journal, Is.Empty);
        Assert.That(h.Session.Workspace.DifferenceCount, Is.EqualTo(1));
    }

    [Test]
    public async Task NoEffectiveChangesCannotCreateAnApprovedExecution()
    {
        var h = await CreationHarness.Create(1);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Session.Workspace.Journal, Is.Empty);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(h.Workspace.Status, Does.Contain("変更"));
    }

    [Test]
    public async Task ThreeChangesAmongOneHundredExposeOnlyChangedWorkAndSendTwoExplicitRows()
    {
        var h = await CreationHarness.Create(100);
        var p = h.Workspace.Selected!;
        var w = h.Session.Workspace; var rows = w.Open(p);
        for (var i = 0; i < 3; i++) w.Commit("P1", rows[i].Cells[0], "Changed " + (i + 1));
        w.Commit("P1", rows[0].Cells[1], "done", true);
        w.SetBuffer(rows[0].Cells[0], "今回送らない日本語");
        var incomplete = w.AddRow(p);

        Assert.That(w.ApplyCandidates(p).Select(c => c.Id), Is.EqualTo(new[] { "P1-T1", "P1-T2", "P1-T3", incomplete }));
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1", "P1-T2" });
        var review = h.Workspace.ApplyReview!;
        Assert.That(review.UpdatedIssues, Is.EqualTo(2));
        Assert.That(review.Batch.Operations.Length, Is.EqualTo(3));
        Assert.That(review.PendingBuffers, Is.EqualTo(1));
        Assert.That(review.Blocked, Is.Empty);
        Assert.That(h.Writes, Is.Empty);
        await h.Workspace.ConfirmApplyAsync(review);

        Assert.That(h.Existing.Titles.Keys, Is.EquivalentTo(new[] { "I1", "I2" }));
        Assert.That(h.Existing.Titles["I1"], Is.EqualTo("Changed 1"));
        Assert.That(h.Session.Workspace.Field(rows[2].Cells[0])!.Change!.Value, Is.EqualTo("Changed 3"));
        Assert.That(h.Session.Workspace.Buffer(rows[0].Cells[0]), Is.EqualTo("今回送らない日本語"));
        Assert.That(h.Session.Workspace.LocalRows.Single().Id, Is.EqualTo(incomplete));
    }

    [Test]
    public void RemovedFieldCannotSilentlyDropFromASelectedRowsOtherChanges()
    {
        var previous = EditingTests.Registration(count: 1);
        var w = new EditingWorkspace(previous.Snapshot.Id.Scope);
        w.SetRegistrations([previous]); var row = w.Open(previous)[0];
        w.Commit("P1", row.Cells[0], "Title to send");
        w.Commit("P1", row.Cells[1], "done", true);
        var current = previous with { Snapshot = previous.Snapshot with { Fields = previous.Snapshot.Fields.Where(f => f.DataType != "SINGLE_SELECT").ToArray() } };
        w.Reconcile(previous, current); w.SetRegistrations([current]);

        var review = w.ReviewApply(current, new HashSet<string> { row.ItemId });

        Assert.That(review.Blocked, Has.Some.Contains("フィールドを確認できません"));
        Assert.That(w.ApplyCandidates(current).Single().Changes, Is.EqualTo(2));
        Assert.Throws<InvalidOperationException>(() => w.ConfirmApply(review));
        Assert.That(w.Journal, Is.Empty);
    }

    [Test]
    public async Task AConflictingSelectedRowBlocksAllWritesUntilExplicitlyExcluded()
    {
        var h = await CreationHarness.Create(2); var w = h.Session.Workspace;
        var rows = w.Open(h.Workspace.Selected!);
        w.Commit("P1", rows[0].Cells[0], "Local conflict");
        w.Commit("P1", rows[1].Cells[0], "Send second");
        h.Existing.Titles["I1"] = "Remote conflict";
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1", "P1-T2" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Not.Empty);

        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T2" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);

        Assert.That(h.Writes.Single().Input.GetProperty("id").GetString(), Is.EqualTo("I2"));
        Assert.That(h.Session.Workspace.Field(rows[0].Cells[0])!.Change!.Value, Is.EqualTo("Local conflict"));
    }
}

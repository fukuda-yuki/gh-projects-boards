using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class CreationTests
{
    [Test]
    public async Task MixedSelectionCreatesDistinctIdenticalTitlesAndPreservesUnselectedIncompleteRow()
    {
        var h = await CreationHarness.Create(); var a = h.Add(); var b = h.Add(repository: "sample-user/second");
        var incomplete = h.Session.Workspace.AddRow(h.Workspace.Selected!);
        var row = h.Session.Workspace.Open(h.Workspace.Selected!)[0]; h.Session.Workspace.Commit("P1", row.Cells[0], "Updated existing");
        await h.Apply(a, b, row.ItemId);
        Assert.That(h.Issues, Has.Count.EqualTo(2), h.Workspace.Status);
        Assert.That(h.Members, Has.Count.EqualTo(2), string.Join(" / ", h.Session.Workspace.Creations.Select(c => c.Reason)));
        Assert.That(h.Session.Workspace.Creations.All(c => c.Completed), Is.True);
        Assert.That(h.Session.Workspace.LocalRows.Select(r => r.Id), Is.EqualTo(new[] { incomplete }));
        Assert.That(h.Existing.Titles, Has.Count.EqualTo(1));
        foreach (var write in h.Writes.Where(w => w.Query.Contains("CreateWorkspaceIssue")))
            Assert.That(write.Input.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "repositoryId", "title" }));
        var count = h.Writes.Count; await h.Restart(); await h.Workspace.ResumeApplyAsync(h.Session.Workspace.Journal.Last().Id);
        Assert.That(h.Writes, Has.Count.EqualTo(count));
    }
    [TestCase(false), TestCase(true)]
    public async Task LostResponseSurvivesSupersedeReloadAndExplicitUrlBinding(bool retry)
    {
        var h = await CreationHarness.Create(); var id = h.Add(); h.LoseCreate = true; await h.Apply(id);
        var batch = h.Session.Workspace.Journal.Single(); var c = h.Session.Workspace.Creations.Single();
        Assert.That(c.Dispatched, Is.True); Assert.That(c.Verified, Is.Null);
        await h.Workspace.SupersedeApplyAsync(batch.Id); await h.Restart();
        Assert.That(h.Session.Workspace.CreationLocked(id), Is.True);
        Assert.Throws<InvalidOperationException>(() => h.Session.Workspace.RemoveRows("P1", [h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == id)]));
        Assert.Throws<InvalidOperationException>(() => h.Session.Workspace.Undo("P1"));
        await h.Workspace.ResumeApplyAsync(batch.Id); Assert.That(h.Issues, Has.Count.EqualTo(1));
        h.LoseCreate = false;
        if (retry)
        {
            await h.Workspace.PrepareCreationRetryAsync(batch.Id, c.Id);
            await h.Workspace.ConfirmCreationRetryAsync(h.Workspace.ApplyReview!);
            Assert.That(h.Issues, Has.Count.EqualTo(2));
            Assert.That(h.Session.Workspace.Creations.Count(), Is.EqualTo(2));
            Assert.That(h.Session.Workspace.Creations.Last().EarlierUncertain, Is.True);
        }
        else
        {
            await h.Workspace.InspectCreationBindingAsync(batch.Id, c.Id, "https://wrong.example/sample-user/first/issues/1001");
            Assert.That(h.Workspace.CreationBindingPreview, Is.Null);
            await h.Workspace.InspectCreationBindingAsync(batch.Id, c.Id, "https://github.com/sample-user/first/issues/1001");
            Assert.That(h.Workspace.CreationBindingPreview, Is.Not.Null, h.Workspace.Status);
            await h.Workspace.ConfirmCreationBindingAsync(batch.Id, c.Id, h.Workspace.CreationBindingPreview!, h.Workspace.CreationBindingRevision);
            await h.Workspace.ResumeApplyAsync(batch.Id);
            Assert.That(h.Issues, Has.Count.EqualTo(1));
        }
        Assert.That(h.Session.Workspace.Creations.Last().Completed, Is.True, h.Workspace.Status);
    }
    [TestCase(false), TestCase(true)]
    public async Task AutoMembershipPartialResponseAndExplicitInitialIntentAreVerified(bool clear)
    {
        var h = await CreationHarness.Create(); h.AutoAdd = true; h.PartialCreate = true; var id = h.Add();
        var cell = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells[1];
        if (clear) h.Session.Workspace.Clear("P1", [cell]); else h.Session.Workspace.Commit("P1", cell, "done", true);
        await h.Apply(id);
        Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True, string.Join(" / ", h.Session.Workspace.Creations.Select(c => c.Reason)));
        Assert.That(h.Writes.Any(w => w.Query.Contains("AddWorkspaceIssue")), Is.False);
        Assert.That(h.Existing.Selects["item-created1"], Is.EqualTo(clear ? null : "done"));
        Assert.That(h.Session.Workspace.Creations.Single().Fields!.Single().Expected, Is.EqualTo("todo"));
        Assert.That(h.Session.Workspace.Creations.Single().Fields!.Single().Attempts.Single().State, Is.EqualTo(ApplyState.Succeeded));
    }
    [Test]
    public async Task LaterCommittedTitleAndPendingTextSurviveAtomicPromotion()
    {
        var h = await CreationHarness.Create(); var id = h.Add();
        h.AfterCreate = () => {
            var w = h.Session.Workspace; var cell = w.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells[0];
            w.Commit("P1", cell, "B"); w.SetBuffer(cell, "C");
        };
        await h.Apply(id);
        var field = h.Session.Workspace.Fields.SingleOrDefault(f => f.Key == new FieldKey("Title", "created1"));
        Assert.That(field, Is.Not.Null, h.Workspace.Status);
        Assert.That(field!.Baseline, Is.EqualTo("A")); Assert.That(field.Change?.Value, Is.EqualTo("B")); Assert.That(field.Buffer, Is.EqualTo("C"));
        await h.Restart(); field = h.Session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "created1"));
        Assert.That(field.Change?.Value, Is.EqualTo("B")); Assert.That(field.Buffer, Is.EqualTo("C"));
    }
    [Test]
    public async Task IndependentCreationPermissionAndExecutionLeasePreventDispatch()
    {
        var h = await CreationHarness.Create(); var id = h.Add(); h.DenyCreate = true;
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { id });
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Not.Empty);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview); Assert.That(h.Writes, Is.Empty);
        h.DenyCreate = false; await h.Workspace.PrepareApplyAsync(new HashSet<string> { id });
        using var lease = new DraftStore(h.Existing.Root).AcquireExecution(h.Session.Workspace.Scope);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!); Assert.That(h.Writes, Is.Empty);
    }
    [Test]
    public async Task IncompleteMembershipObservationNeverAuthorizesRecreationOrAddition()
    {
        var h = await CreationHarness.Create(); var id = h.Add(); h.AfterCreate = () => h.Incomplete = true;
        await h.Apply(id); Assert.That(h.Issues, Has.Count.EqualTo(1)); Assert.That(h.Members, Is.Empty);
        Assert.That(h.Session.Workspace.Creations.Single().Verified, Is.Not.Null);
        await h.Restart(); await h.Workspace.ResumeApplyAsync(h.Session.Workspace.Journal.Single().Id);
        Assert.That(h.Issues, Has.Count.EqualTo(1)); Assert.That(h.Members, Is.Empty);
        h.Incomplete = false; h.LoseAdd = true; await h.Workspace.ResumeApplyAsync(h.Session.Workspace.Journal.Single().Id);
        Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True);
        Assert.That(h.Issues, Has.Count.EqualTo(1)); Assert.That(h.Members, Has.Count.EqualTo(1));
    }
    [Test]
    public async Task DurableDispatchIntentWithoutResponseCannotBecomeFreshAfterReload()
    {
        var h = await CreationHarness.Create(); var id = h.Add();
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { id }); var review = h.Workspace.ApplyReview!;
        Assert.That(await h.Session.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => true), Is.True);
        var c = h.Session.Workspace.Creations.Single();
        Assert.That(await h.Session.CommitAsync(w => { w.RecordCreation(review.Batch.Id, c with { Dispatched = true }); return w; }, () => true), Is.True);
        await h.Restart(); await h.Workspace.ResumeApplyAsync(review.Batch.Id); Assert.That(h.Writes, Is.Empty);
        Assert.That(h.Session.Workspace.Creations.Single().Dispatched, Is.True);
    }
    [Test]
    public async Task RealisticVersionFourMigratesRowsUndoPendingAndSelectIntent()
    {
        var h = await CreationHarness.Create(); var id = h.Add(); var w = h.Session.Workspace;
        var cells = w.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells;
        w.Commit("P1", cells[1], "done", true); w.SetBuffer(cells[0], "pending日本語");
        var v4 = w.Snapshot() with { Version = 4 };
        var root = Path.Combine(Path.GetTempPath(), "ghpb-v4-" + Guid.NewGuid()); var store = new DraftStore(root);
        await store.SaveAsync(v4, 0); var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        Assert.That(restored.LocalRows.Single().TitleBuffer, Is.EqualTo("pending日本語"));
        Assert.That(restored.LocalRows.Single().Selects.Single().Intent, Is.EqualTo("Set"));
        await store.SaveAsync(restored.Snapshot(), v4.Revision);
        Assert.That((await store.LoadAsync(w.Scope))!.Version, Is.EqualTo(7)); Assert.That(File.Exists(store.FileFor(w.Scope) + ".bak"), Is.True);
    }
    [Test]
    public async Task IdOnlyResponseIsDurableAndVerifiedWithoutRecreation()
    {
        var h = await CreationHarness.Create(); h.IdOnlyResponse = true; var id = h.Add(); await h.Apply(id);
        Assert.That(h.Session.Workspace.Creations.Single().ReceivedId, Is.EqualTo("created1"));
        Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True);
        Assert.That(h.Issues, Has.Count.EqualTo(1));
    }
    [Test]
    public async Task BindingRejectsOtherRepositoryAndAlreadyClaimedIdentityWithoutWrites()
    {
        var h = await CreationHarness.Create(); var first = h.Add(); await h.Apply(first);
        var other = h.Add(repository: "sample-user/second"); await h.Apply(other);
        var pending = h.Add(); h.LoseCreate = true; await h.Apply(pending);
        var b = h.Session.Workspace.Journal.Last(); var c = b.Creations!.Single(); var writes = h.Writes.Count;
        foreach (var url in new[] { "https://github.com/sample-user/second/issues/1002", "https://github.com/sample-user/first/issues/1001" })
        {
            await h.Workspace.InspectCreationBindingAsync(b.Id, c.Id, url);
            Assert.That(h.Workspace.CreationBindingPreview, Is.Null); Assert.That(h.Writes.Count, Is.EqualTo(writes));
        }
        await h.Restart(); Assert.That(h.Session.Workspace.Creations.Last().Verified, Is.Null);
        Assert.That(h.Session.Workspace.CreationLocked(pending), Is.True);
    }
    [Test]
    public async Task RetainedEarlierUncertaintyDoesNotBlockUnrelatedApply()
    {
        var h = await CreationHarness.Create(); var id = h.Add(); h.LoseCreate = true; await h.Apply(id);
        var old = h.Session.Workspace.Journal.Single(); var c = old.Creations!.Single();
        h.LoseCreate = false; await h.Workspace.PrepareCreationRetryAsync(old.Id, c.Id);
        await h.Workspace.ConfirmCreationRetryAsync(h.Workspace.ApplyReview!);
        var independent = h.Add("Independent"); await h.Apply(independent);
        Assert.That(h.Issues, Has.Count.EqualTo(3));
        Assert.That(h.Session.Workspace.Creations.Any(x => x.EarlierUncertain), Is.True);
    }
    [Test]
    public async Task KnownIssueSetupCanBeReauthorizedAfterSupersedeWithoutCreatingAgain()
    {
        var h = await CreationHarness.Create(); var id = h.Add(); h.AfterCreate = () => h.Incomplete = true;
        await h.Apply(id); var b = h.Session.Workspace.Journal.Single(); var c = b.Creations!.Single();
        await h.Workspace.SupersedeApplyAsync(b.Id); await h.Restart(); h.Incomplete = false;
        await h.Workspace.PrepareCreationSetupAsync(b.Id, c.Id);
        Assert.That(h.Workspace.CreationSetupReview, Is.Not.Null, h.Workspace.Status);
        await h.Workspace.ConfirmCreationSetupAsync(h.Workspace.CreationSetupReview!);
        Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True, h.Workspace.Status);
        Assert.That(h.Issues, Has.Count.EqualTo(1));
    }
    [Test]
    public async Task CheckpointRejectsSetupTargetOutsideCreationIdentity()
    {
        var h = await CreationHarness.Create(); var id = h.Add();
        h.Session.Workspace.Commit("P1", h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells[1], "done", true);
        await h.Apply(id); var record = h.Session.Workspace.Snapshot(); var b = record.Journal!.Single(); var c = b.Creations!.Single();
        var field = c.Fields!.Single() with { IssueId = "unrelated" };
        Assert.Throws<InvalidDataException>(() => EditingWorkspace.Restore(record with { Journal = [b with { Creations = [c with { Fields = [field] }] }] }));
    }
    [Test]
    public async Task DeletedOptionCanBeReplacedByExplicitFreshSetupReview()
    {
        var h = await CreationHarness.Create(); var id = h.Add();
        h.Session.Workspace.Commit("P1", h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells[1], "done", true);
        h.AfterCreate = () => h.Existing.ChangeResponse = (q, data) => {
            if (q.Contains("ProjectFields"))
            {
                var options = data["data"]!["node"]!["fields"]!["nodes"]![0]!["options"]!.AsArray();
                foreach (var option in options.Where(o => o!["id"]!.ToString() == "done").ToArray()) options.Remove(option);
            }
        };
        await h.Apply(id); var b = h.Session.Workspace.Journal.Single(); var c = b.Creations!.Single();
        Assert.That(c.Completed, Is.False); Assert.That(c.Verified, Is.Not.Null);
        h.Session.Workspace.Commit("P1", h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells[1], "todo", true);
        await h.Workspace.PrepareCreationSetupAsync(b.Id, c.Id); Assert.That(h.Workspace.CreationSetupReview, Is.Not.Null);
        await h.Workspace.ConfirmCreationSetupAsync(h.Workspace.CreationSetupReview!);
        var final = h.Session.Workspace.Creations.Single();
        Assert.That(final.Completed, Is.True, h.Workspace.Status); Assert.That(final.Selects.Single().OptionId, Is.EqualTo("done"));
        Assert.That(final.SetupIntents!.Single().OptionId, Is.EqualTo("todo")); Assert.That(h.Issues, Has.Count.EqualTo(1));
    }
    [Test]
    public async Task ResponseBeforePersistenceKeepsDispatchVetoAndUnrelatedMixedUndo()
    {
        var h = await CreationHarness.Create(); var a = h.Add("A"); var b = h.Add("B");
        h.Session.Workspace.Paste("P1", h.Session.Workspace.Open(h.Workspace.Selected!), 100, 0, "A2\nB2");
        FileStream? gate = null;
        h.AfterCreate = () => gate = new FileStream(Path.Combine(h.Existing.Root, "Drafts", ".writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { await h.Apply(a); } finally { gate?.Dispose(); }
        await h.Restart(); var c = h.Session.Workspace.Creations.Single();
        Assert.That(c.Dispatched, Is.True); Assert.That(c.ReceivedId, Is.Null); Assert.That(h.Issues, Has.Count.EqualTo(1));
        h.Session.Workspace.Undo("P1");
        Assert.That(h.Session.Workspace.LocalRows.Single(r => r.Id == a).Title, Is.EqualTo("A2"));
        Assert.That(h.Session.Workspace.LocalRows.Single(r => r.Id == b).Title, Is.EqualTo("B"));
        await h.Workspace.ResumeApplyAsync(h.Session.Workspace.Journal.Single().Id); Assert.That(h.Issues, Has.Count.EqualTo(1));
    }
    [Test]
    public void CheckpointUsesNextExplicitVersion()
        => Assert.That(new EditingWorkspace(new("github.com", 42)).Snapshot().Version, Is.EqualTo(7));

    [TestCase(false), TestCase(true)]
    public async Task RemoteMembershipOrFieldBeforePersistenceReconcilesAfterReloadWithoutReplay(bool fieldStage)
    {
        var h = await CreationHarness.Create(); var id = h.Add();
        h.Session.Workspace.Commit("P1", h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells[1], "done", true);
        FileStream? gate = null;
        void FailSave() => gate = new FileStream(Path.Combine(h.Existing.Root, "Drafts", ".writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (fieldStage) h.Existing.OnMutation = FailSave; else h.AfterAdd = FailSave;
        try { await h.Apply(id); } finally { gate?.Dispose(); }
        h.AfterAdd = null; h.Existing.OnMutation = null;
        await h.Restart(); var c = h.Session.Workspace.Creations.Single();
        Assert.That(c.Verified, Is.Not.Null); Assert.That(c.Completed, Is.False); Assert.That(h.Members, Has.Count.EqualTo(1));
        if (fieldStage) Assert.That(c.Fields!.Single().State, Is.EqualTo(ApplyState.Running));
        else Assert.That(c.ItemId, Is.Null);
        var creates = h.Writes.Count(x => x.Query.Contains("CreateWorkspaceIssue"));
        var adds = h.Writes.Count(x => x.Query.Contains("AddWorkspaceIssue"));
        await h.Workspace.ResumeApplyAsync(h.Session.Workspace.Journal.Single().Id);
        Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True, h.Workspace.Status);
        Assert.That(h.Writes.Count(x => x.Query.Contains("CreateWorkspaceIssue")), Is.EqualTo(creates));
        Assert.That(h.Writes.Count(x => x.Query.Contains("AddWorkspaceIssue")), Is.EqualTo(adds));
        Assert.That(h.Writes.Count(x => x.Query.Contains("ApplySelect")), Is.EqualTo(1));
    }

    [Test]
    public void SelectedIncompleteLocalRowIsReportedInsteadOfSilentlyExcluded()
    {
        var p = EditingTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        w.SetRegistrations([p]); var id = w.AddRow(p);
        var review = w.ReviewApply(p, new HashSet<string> { id });
        Assert.That(review.SelectedRows, Is.EqualTo(1));
        Assert.That(review.Blocked, Has.Some.Contains("タイトル"));
        Assert.That(w.ReviewApply(p, new HashSet<string>()).Blocked, Is.Empty);
    }
}

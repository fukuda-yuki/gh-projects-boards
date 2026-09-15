using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class LocalRowTests
{
    private static EditingWorkspace Workspace(ProjectRegistration p)
    { var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.Open(p); return w; }
    private static EditRow Row(EditingWorkspace w, ProjectRegistration p, string id) => w.Open(p).Single(r => r.ItemId == id);
    private static string Json(object? o) => System.Text.Json.JsonSerializer.Serialize(o);
    [Test]
    public void RemovalUndoPlacementSurvivesOtherProjectRemoval()
    {
        var p = EditingTests.Registration(); var other = EditingTests.Registration("P2"); var w = Workspace(p);
        var otherId = w.AddRow(other); var a = w.AddRow(p); var b = w.AddRow(p);
        w.RemoveRows("P1", [Row(w, p, a)]); w.RemoveRows("P2", [Row(w, other, otherId)]);
        w = EditingWorkspace.Restore(w.Snapshot()); w.Undo("P1");
        Assert.That(w.Open(p).Where(r => r.IsLocal).Select(r => r.ItemId), Is.EqualTo(new[] { a, b }));
    }
    [Test]
    public async Task MixedPasteApplyKeepsLocalUndoAndDiscardKeepsSharedTitle()
    {
        var h = await ApplyTests.Harness.Create(); var s = h.Workspace.Drafts!; var p = h.Workspace.Selected!;
        Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True); var id = s.Workspace.AddRow(p);
        var rows = s.Workspace.Open(p); s.Workspace.Paste("P1", rows, 99, 0, "Remote\nLocal");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { rows[99].ItemId }); await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes, Has.Count.EqualTo(1)); s.Workspace.Undo("P1");
        Assert.That(s.Workspace.LocalRows.Single().Title, Is.Empty); Assert.That(s.Workspace.Value(rows[99].Cells[0]), Is.EqualTo("Remote"));
        var first = EditingTests.Registration(); var second = EditingTests.Registration("P2"); var w = Workspace(first);
        w.Open(second); w.AddRow(first); w.Paste("P1", w.Open(first), 100, 0, "Shared\nNew");
        w.Discard(first.Snapshot, [second.Snapshot]); Assert.DoesNotThrow(() => EditingWorkspace.Restore(w.Snapshot()));
        Assert.That(w.Value(w.Open(second)[100].Cells[0]), Is.EqualTo("Shared")); Assert.That(w.LocalRows, Is.Empty);
    }
    [Test]
    public void RepositorySyntaxAndMissingV4PayloadAreDiagnosed()
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var id = w.AddRow(p);
        w.Commit("P1", Row(w, p, id).Cells[^1], "owner/repo\n");
        Assert.That(w.LocalProblems(p, id).Any(e => e.StartsWith("宛先")), Is.True);
        Assert.DoesNotThrow(() => EditingWorkspace.Restore(w.Snapshot()));
        Assert.Throws<InvalidDataException>(() => EditingWorkspace.Restore(w.Snapshot() with { LocalRows = null }));
        w.AddRow(p); var snapshot = w.Snapshot();
        Assert.Throws<InvalidDataException>(() => EditingWorkspace.Restore(snapshot with { LocalRows = snapshot.LocalRows!.Reverse().ToArray() }));
        Assert.Throws<InvalidDataException>(() => EditingWorkspace.Restore(snapshot with { LocalRows = [snapshot.LocalRows![0], snapshot.LocalRows[1] with { CreatedRevision = snapshot.LocalRows[0].CreatedRevision }] }));
    }
    [Test]
    public void UnknownFieldCannotBeDuplicatedAsEmpty()
    {
        var p = EditingTests.Registration(); var field = p.Snapshot.Fields[0] with { Availability = ValueAvailability.Unavailable };
        p = p with { Snapshot = p.Snapshot with { Fields = [field], Items = p.Snapshot.Items.Select(i => i with {
            Values = i.Values.Select(v => v with { Availability = ValueAvailability.Empty, OptionId = null }).ToArray() }).ToArray() } };
        var w = Workspace(p); Assert.Throws<InvalidOperationException>(() => w.DuplicateRows(p, [w.Open(p)[0]]));
        Assert.That(w.LocalRows, Is.Empty);
    }
    [TestCase("success"), TestCase("unknown"), TestCase("ack-failure"), TestCase("cancel")]
    public async Task ExistingApplyPreservesLocalRowsAndOnlySendsRemoteTargets(string mode)
    {
        var h = await ApplyTests.Harness.Create(); var session = h.Workspace.Drafts!; var p = h.Workspace.Selected!;
        Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True);
        var w = session.Workspace; var id = w.AppendRows(p, "Prepared\tDone").Single();
        w.SetBuffer(Row(w, p, id).Cells[0], "pending日本語"); var expected = Json(w.LocalRows);
        var remote = w.Open(p)[0]; w.Commit("P1", remote.Cells[0], "Apply existing");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { remote.ItemId });
        Assert.That(Json(session.Workspace.LocalRows), Is.EqualTo(expected));
        FileStream? locked = null;
        if (mode == "unknown") h.LoseResponse = true;
        h.OnMutation = () => {
            session.Workspace.AddRow(p with { DefaultRepository = "later/repo" });
            expected = Json(session.Workspace.LocalRows);
            if (mode == "ack-failure") locked = new FileStream(Path.Combine(h.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (mode == "cancel") h.Workspace.Cancel();
        };
        try { await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!); } finally { locked?.Dispose(); }
        Assert.That(h.Writes, Has.Count.EqualTo(1));
        Assert.That(h.Writes[0].GetProperty("id").GetString(), Is.EqualTo("I1"));
        Assert.That(h.Writes[0].EnumerateObject().Select(x => x.Name), Is.EquivalentTo(new[] { "id", "title" }));
        Assert.That(Json(session.Workspace.LocalRows), Is.EqualTo(expected));
        Assert.That(await session.FlushAsync(), Is.True);
        var restored = EditingWorkspace.Restore((await new DraftStore(h.Root).LoadAsync(w.Scope))!);
        Assert.That(Json(restored.LocalRows), Is.EqualTo(expected));
        restored.Undo("P1"); Assert.That(restored.LocalRows, Has.Count.EqualTo(1)); Assert.That(restored.LocalRows[0].Id, Is.EqualTo(id));
    }
    [Test]
    public async Task LocalAdditionAfterReviewRejectsStaleApprovalWithoutConsumingUndo()
    {
        var h = await ApplyTests.Harness.Create(); var s = h.Workspace.Drafts!; var p = h.Workspace.Selected!;
        s.Workspace.Commit("P1", s.Workspace.Open(p)[0].Cells[0], "Existing");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }); var review = h.Workspace.ApplyReview!;
        var id = s.Workspace.AddRow(p); await h.Workspace.ConfirmApplyAsync(review);
        Assert.That(h.Writes, Is.Empty); Assert.That(s.Workspace.LocalRows.Single().Id, Is.EqualTo(id));
        s.Workspace.Undo("P1"); Assert.That(s.Workspace.LocalRows, Is.Empty);
    }
    [Test]
    public async Task VersionThreeApplyHistoryConflictsAttemptsAndPendingWorkMigrateTogether()
    {
        var h = await ApplyTests.Harness.Create(); var s = h.Workspace.Drafts!; var p = h.Workspace.Selected!;
        var remote = s.Workspace.Open(p);
        s.Workspace.Commit("P1", remote[0].Cells[0], "Succeeded");
        s.Workspace.Commit("P1", remote[1].Cells[0], "Unresolved");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1", "P1-T2" });
        h.OnMutation = () => { if (h.Writes.Count == 2) h.LoseResponse = true; };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        s.Workspace.Commit("P1", remote[2].Cells[0], "Local conflict");
        s.Workspace.SetBuffer(remote[3].Cells[0], "Pending");
        h.Titles["I3"] = "External conflict";
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T3" });
        var v3 = s.Workspace.Snapshot() with { Version = 3, LocalRows = null };
        Assert.That(v3.Fields.Any(f => f.Conflict), Is.True);
        Assert.That(v3.Journal![0].Operations.Select(o => o.State), Is.EqualTo(new[] { ApplyState.Succeeded, ApplyState.Unknown }));
        var root = Path.Combine(Path.GetTempPath(), "ghpb-local-v3-" + Guid.NewGuid()); var store = new DraftStore(root);
        await store.SaveAsync(v3, 0); var restored = EditingWorkspace.Restore((await store.LoadAsync(v3.Scope))!);
        restored.AddRow(h.Workspace.Selected!); await store.SaveAsync(restored.Snapshot(), v3.Revision);
        var final = (await store.LoadAsync(v3.Scope))!;
        Assert.That(Json(final.Journal), Is.EqualTo(Json(v3.Journal))); Assert.That(Json(final.Fields), Is.EqualTo(Json(v3.Fields)));
        Assert.That(Json(final.History[..v3.History.Length]), Is.EqualTo(Json(v3.History)));
        Assert.That(final.Version, Is.EqualTo(6)); Assert.That(EditingWorkspace.Restore(final).HasUnresolvedApply, Is.True);
    }
    [Test]
    public async Task LastUnregistrationRetainRestartAndReregisterRecoversLocalRows()
    {
        var h = await ApplyTests.Harness.Create(); var s = h.Workspace.Drafts!; var p = h.Workspace.Selected!;
        Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True); s.Workspace.AddRow(p);
        var expected = Json(s.Workspace.LocalRows); await h.Workspace.UnregisterAsync(retainDrafts: true);
        var reopened = new RegistrationWorkspace(new(h.Root)); await reopened.RestoreAsync(); await reopened.BindAsync(h.Context, h.Service);
        var choice = await new ProjectDiscovery(h.Service).ResolveAsync(h.Context, p.Snapshot.Url, default);
        await reopened.RegisterAsync(choice, null);
        Assert.That(Json(reopened.Drafts!.Workspace.LocalRows), Is.EqualTo(expected)); Assert.That(h.Writes, Is.Empty);
    }
    [Test]
    public async Task IncompleteRowsDestinationsRemovalAndPendingTextRestoreExactly()
    {
        var p = EditingTests.Registration() with { DefaultRepository = "one/repo" }; var w = Workspace(p);
        var id = w.AddRow(p); var row = Row(w, p, id);
        Assert.That(w.LocalProblems(p, id), Does.Contain("タイトルが必要です"));
        w.Commit("P1", row.Cells[0], "Prepared"); w.Commit("P1", row.Cells[^1], "chosen/repo");
        w.Commit("P1", row.Cells[1], "Done"); w.SetBuffer(row.Cells[0], "pending日本語");
        var copy = w.DuplicateRows(p with { DefaultRepository = "later/repo" }, [row]).Single();
        Assert.That(w.LocalRows.Single(r => r.Id == copy).Repository, Is.EqualTo("chosen/repo"));
        Assert.That(w.LocalRows.Single(r => r.Id == copy).Title, Is.EqualTo("Prepared"));
        Assert.That(w.LocalRows.Single(r => r.Id == copy).TitleBuffer, Is.Null);
        var added = w.AddRow(p with { DefaultRepository = "later/repo" });
        Assert.That(w.LocalRows.Single(r => r.Id == added).Repository, Is.EqualTo("later/repo"));
        var before = Json(w.LocalRows); w.RemoveRows("P1", [row]);
        var root = Path.Combine(Path.GetTempPath(), "ghpb-local-" + Guid.NewGuid()); var store = new DraftStore(root);
        await store.SaveAsync(w.Snapshot(), 0); w = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        w.Undo("P1"); Assert.That(Json(w.LocalRows), Is.EqualTo(before));
        Assert.That(w.Open(p), Has.Length.EqualTo(104)); Assert.That(w.DifferenceCount, Is.Zero);
        w.Clear("P1", Row(w, p, added).Cells);
        Assert.That(w.LocalRows, Has.Count.EqualTo(3)); Assert.That(w.LocalProblems(p, added), Has.Length.EqualTo(2));
        Assert.That(w.Open(EditingTests.Registration("P2")), Has.Length.EqualTo(101));
    }
    [TestCase("First\tDone\n\tTodo")]
    [TestCase("First\tDone\nSecond\tUnknown")]
    [TestCase("First\tDone\nSecond")]
    [TestCase("First\tDone\textra")]
    public void InvalidAppendIsAtomic(string tsv)
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var remote = w.Open(p)[0];
        w.Commit("P1", remote.Cells[0], "Earlier"); w.SetBuffer(remote.Cells[0], "Unfinished");
        var before = Json(w.Snapshot()); Assert.Throws<InvalidOperationException>(() => w.AppendRows(p, tsv));
        Assert.That(Json(w.Snapshot()), Is.EqualTo(before));
    }
    [Test]
    public void ValidAppendAndMixedEditHaveOneUndoAndNeverOverflow()
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var remote = w.Open(p)[0];
        w.Commit("P1", remote.Cells[0], "Earlier"); w.SetBuffer(remote.Cells[0], "Pending");
        var before = Json(w.Fields); var ids = w.AppendRows(p, "First\tDone\r\nSecond\t\r\n");
        Assert.That(w.LocalRows.Select(r => r.Title), Is.EqualTo(new[] { "First", "Second" }));
        Assert.That(w.LocalRows[0].Selects[0].OptionId, Is.EqualTo("done"));
        Assert.That(w.LocalRows[1].Selects[0].OptionId, Is.Null);
        Assert.That(w.LocalProblems(p, ids[0]), Has.Length.EqualTo(1));
        w = EditingWorkspace.Restore(w.Snapshot()); w.Undo("P1");
        Assert.That(w.LocalRows, Is.Empty); Assert.That(Json(w.Fields), Is.EqualTo(before));
        var id = w.AddRow(p); var rows = w.Open(p);
        Assert.Throws<InvalidOperationException>(() => w.Paste("P1", rows, 101, 0, "One\nTwo"));
        w.Paste("P1", rows, 100, 0, "Remote\tDone\nLocal\tDone");
        Assert.That(w.Value(rows[100].Cells[0]), Is.EqualTo("Remote"));
        Assert.That(w.LocalRows.Single().Title, Is.EqualTo("Local"));
        w.Undo("P1"); Assert.That(w.Value(rows[100].Cells[0]), Is.EqualTo("Issue 101")); Assert.That(w.LocalRows.Single().Title, Is.Empty);
    }
    [Test]
    public void DuplicationRejectsConflictAndUnknownButUsesCommittedValues()
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[0], "Committed"); w.SetBuffer(rows[0].Cells[0], "Pending");
        var id = w.DuplicateRows(p, [rows[0]]).Single();
        Assert.That(w.LocalRows.Single().Title, Is.EqualTo("Committed"));
        w.SetBuffer(rows[0].Cells[0], null);
        var fresh = EditingTests.Registration(titlePrefix: "Remote "); w.Reconcile(p, fresh);
        var before = Json(w.Snapshot());
        Assert.Throws<InvalidOperationException>(() => w.DuplicateRows(fresh, [rows[1], rows[0]]));
        Assert.That(Json(w.Snapshot()), Is.EqualTo(before));
        Assert.That(w.LocalRows.Single().Id, Is.EqualTo(id));
    }
    [Test]
    public void RefreshRetainsIdsMissingDefinitionsAndSameTitleIssueWithoutBaseline()
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var id = w.AppendRows(p, "Issue 1\tDone").Single();
        w.SetBuffer(Row(w, p, id).Cells[0], "日本語未確定"); var before = Json(w.LocalRows);
        var fresh = p with { RetrievedAt = p.RetrievedAt.AddMinutes(1), Snapshot = p.Snapshot with { Fields = [] } };
        w.Reconcile(p, fresh); w.SetRegistrations([fresh]);
        Assert.That(Json(w.LocalRows), Is.EqualTo(before));
        Assert.That(w.LocalProblems(fresh, id).Any(e => e.Contains("P1-status")), Is.True);
        Assert.That(w.Open(fresh), Has.Length.EqualTo(102));
        Assert.That(w.Fields.All(f => !f.Key.NodeId.StartsWith("local-")), Is.True);
        w = EditingWorkspace.Restore(w.Snapshot());
        Assert.That(Json(w.LocalRows), Is.EqualTo(before));
        w.RemoveRows("P1", [Row(w, fresh, id)]); w.Undo("P1"); Assert.That(Json(w.LocalRows), Is.EqualTo(before));
    }
    [Test]
    public void MixedRemovalAndStaleUndoPreserveNewerWork()
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var id = w.AddRow(p); var row = Row(w, p, id);
        var before = Json(w.Snapshot()); Assert.Throws<InvalidOperationException>(() => w.RemoveRows("P1", [w.Open(p)[0], row]));
        Assert.That(Json(w.Snapshot()), Is.EqualTo(before));
        w.SetBuffer(row.Cells[0], "newer"); before = Json(w.Snapshot());
        Assert.Throws<InvalidOperationException>(() => w.Undo("P1")); Assert.That(Json(w.Snapshot()), Is.EqualTo(before));
        w.Discard(p.Snapshot, []); Assert.That(w.LocalRows, Is.Empty); Assert.DoesNotThrow(() => EditingWorkspace.Restore(w.Snapshot()));
    }
    [TestCase(1), TestCase(2), TestCase(3)]
    public async Task MigrationPreservesDraftsAndBackup(int version)
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[0], "Committed"); w.SetBuffer(rows[1].Cells[0], "Pending");
        var legacy = w.Snapshot() with { Version = version, LocalRows = null };
        var root = Path.Combine(Path.GetTempPath(), "ghpb-local-migrate-" + Guid.NewGuid()); var store = new DraftStore(root);
        await store.SaveAsync(legacy, 0); w = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        w.AddRow(p); await store.SaveAsync(w.Snapshot(), legacy.Revision);
        Assert.That(Json((await store.LoadAsync(w.Scope))!.Fields), Is.EqualTo(Json(legacy.Fields)));
        Assert.That(System.Text.Json.JsonDocument.Parse(File.ReadAllText(store.FileFor(w.Scope) + ".bak")).RootElement.GetProperty("Version").GetInt32(), Is.EqualTo(version));
        Assert.Throws<InvalidDataException>(() => EditingWorkspace.Restore(w.Snapshot() with { Version = 3 }));
    }
    [Test]
    public async Task FailedCommitAndStaleCandidateKeepNewerRows()
    {
        var p = EditingTests.Registration(); var w = Workspace(p); var root = Path.Combine(Path.GetTempPath(), "ghpb-local-race-" + Guid.NewGuid());
        var store = new DraftStore(root); var session = new DraftSession(store, w, 0); Assert.That(await session.FlushAsync(), Is.True);
        var result = await session.CommitAsync(candidate => { candidate.AddRow(p); return candidate; }, () => { w.AddRow(p); return true; });
        Assert.That(result, Is.False); Assert.That(session.Workspace.LocalRows, Has.Count.EqualTo(1));
        Assert.That((await store.LoadAsync(w.Scope))!.LocalRows, Is.Empty);
        Assert.That(await session.FlushAsync(), Is.True); Assert.That((await store.LoadAsync(w.Scope))!.LocalRows![0].Id, Is.EqualTo(w.LocalRows[0].Id));
    }
    [Test]
    public void CheckpointUsesCurrentVersionWithoutChangingRemoteRows()
    {
        var p = EditingTests.Registration();
        var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        Assert.That(w.Open(p), Has.Length.EqualTo(101));
        Assert.That(w.Snapshot().Version, Is.EqualTo(6));
    }
}

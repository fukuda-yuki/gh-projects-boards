using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class RowViewTests
{
    private static EditingWorkspace Work(params ProjectRegistration[] projects)
    { var w = new EditingWorkspace(projects[0].Snapshot.Id.Scope); w.SetRegistrations(projects); foreach (var p in projects) w.Open(p); return w; }
    private static void View(EditingWorkspace w, ProjectRegistration p, RowViewDefinition d) => w.SaveRowView(w.PrepareRowView(p) with { Definition = d });
    [Test]
    public void CheckpointExplicitlyVersionsRowDefinitions()
    {
        var w = new EditingWorkspace(EditingTests.Registration().Snapshot.Id.Scope);
        Assert.That(w.Snapshot().Version, Is.EqualTo(10));
    }
    [TestCase(false), TestCase(true)]
    public void OptionOrderAndDistinctMissingStatesSortAfterValues(bool descending)
    {
        var p = EditingTests.Registration(count: 5); var w = Work(p); var rows = w.Open(p);
        w.Commit("P1", rows[1].Cells[1], "done", true); w.Clear("P1", [rows[2].Cells[1]]);
        var local = w.AddRow(p); var cleared = w.AddRow(p); w.Clear("P1", [w.Open(p).Single(r => r.ItemId == cleared).Cells[1]]);
        var unknown = p with { Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Select((i, n) => n == 3 ? i with { Values = [i.Values[0] with { Availability = ValueAvailability.Unavailable }] } : i).ToArray() } };
        w.Reconcile(p, unknown); w.SetRegistrations([unknown]);
        View(w, unknown, new("Field", descending, "P1-status"));
        var sorted = w.EvaluateRows(unknown); var states = sorted.Select(r => w.EffectiveRowValue(r.Cells[1]).State).ToArray();
        Assert.That(states, Is.EqualTo(new[] { "Present", "Present", "Present", "Empty", "Empty", "Unspecified", "Unknown" }));
        Assert.That(w.Value(sorted[descending ? 0 : 2].Cells[1]), Is.EqualTo("done"));
        View(w, unknown, new(Filters: [new("P1-status", [], ["Empty"])]));
        Assert.That(w.EvaluateRows(unknown).Select(r => r.ItemId), Is.EquivalentTo(new[] { rows[2].ItemId, cleared }));
        Assert.That(w.EvaluateRows(unknown).Any(r => r.ItemId == local), Is.False);
    }
    [Test]
    public void LiteralTitleTiesCommittedValuesConflictAndIdBasedOrAnd()
    {
        var p = ColumnTests.Project(); var w = Work(p); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[0], "Same [x]"); w.Commit("P1", rows[1].Cells[0], "same [X]");
        w.SetBuffer(rows[0].Cells[0], "does not match");
        View(w, p, new("Title", true, Title: "[X]", Filters: [new("P1A", ["A0", "A1"], []), new("P1B", ["B0"], [])]));
        Assert.That(w.EvaluateRows(p).Select(r => r.ItemId), Is.EqualTo(new[] { "P1T1", "P1T2" }));
        var f = w.Field(rows[0].Cells[0])!;
        var snapshot = w.Snapshot(); w = EditingWorkspace.Restore(snapshot with { Fields = snapshot.Fields.Select(x => x.Key == f.Key ? x with { Conflict = true, Observation = new("ob", p.Snapshot.Id, DateTimeOffset.UtcNow, "Remote", ValueAvailability.Present, null, []) } : x).ToArray() });
        Assert.That(w.EffectiveRowValue(rows[0].Cells[0]), Is.EqualTo(new RowValue("Present", "Same [x]", true, ValueAvailability.Present)));
        Assert.That(w.EvaluateRows(p), Has.Length.EqualTo(2));
        View(w, p, new(Filters: [new("P1A", ["A0"], []), new("P1B", ["B1"], [])]));
        Assert.That(w.EvaluateRows(p), Is.Empty);
    }
    [Test]
    public void ReorderedFilteredRectangleAndUndoRetainOriginalKeys()
    {
        var p = ColumnTests.Project();
        var extra = EditingTests.Registration(count: 3);
        p = p with { Snapshot = p.Snapshot with { Issues = extra.Snapshot.Issues, Items = p.Snapshot.Items.Append(p.Snapshot.Items[0] with { Id = new(p.Snapshot.Id.Scope, "P1T3"), ContentId = new(p.Snapshot.Id.Scope, "I3") }).ToArray() } };
        var w = Work(p); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[0], "keep Z"); w.Commit("P1", rows[2].Cells[0], "keep A");
        w.SaveColumns(ColumnTests.Reordered(w, p)); View(w, p, new("Title", Title: "keep"));
        var projection = new RowProjection(p.Snapshot.Id); projection.Reapply(w, p);
        var generation = projection.Generation;
        var displayed = w.Columns(p).Resolve(projection.Resolve(rows));
        Assert.That(displayed.Select(r => r.ItemId), Is.EqualTo(new[] { "P1T3", "P1T1" }));
        w.Paste("P1", displayed, 0, 1, "Done\tDone\nDone\tDone");
        Assert.That(w.Snapshot().History.Last().Changes.Select(c => c.Key), Is.EquivalentTo(new[] {
            rows[2].Cells[3].Key, rows[2].Cells[1].Key, rows[0].Cells[3].Key, rows[0].Cells[1].Key }));
        Assert.That(w.Value(rows[1].Cells[1]), Is.EqualTo("A0")); Assert.That(w.Value(rows[0].Cells[2]), Is.EqualTo("B0"));
        View(w, p, new()); projection.Reapply(w, p); Assert.That(projection.Generation, Is.Not.EqualTo(generation));
        w.Undo("P1"); foreach (var row in rows) { Assert.That(w.Value(row.Cells[1]), Is.EqualTo("A0")); Assert.That(w.Value(row.Cells[3]), Is.EqualTo("C0")); }
    }
    [Test]
    public void CommittedAndPendingEditsDoNotReorderNewRowsStayUntilReapply()
    {
        var p = EditingTests.Registration(count: 2); var w = Work(p); View(w, p, new("Title", Title: "Issue"));
        var view = new RowProjection(p.Snapshot.Id); view.Reapply(w, p); var rows = w.Open(p);
        w.SetBuffer(rows[0].Cells[0], "pending"); Assert.That(view.NeedsReapply(w, p), Is.False);
        w.Commit("P1", rows[1].Cells[0], "hidden"); Assert.That(view.NeedsReapply(w, p), Is.True);
        Assert.That(view.Resolve(w.Open(p)).Select(r => r.ItemId), Is.EqualTo(new[] { "P1T1", "P1T2" }));
        var id = w.AddRow(p); view.IncludeNew(w.Open(p), [id]); Assert.That(view.Ids.Last(), Is.EqualTo(id)); Assert.That(view.Temporary, Does.Contain(id));
        view.Reapply(w, p); Assert.That(view.Ids, Is.EqualTo(new[] { "P1T1" })); Assert.That(view.Temporary, Is.Empty);
        Assert.That(w.Buffer(rows[0].Cells[0]), Is.EqualTo("pending")); Assert.That(w.LocalRows.Single().Id, Is.EqualTo(id));
    }
    [Test]
    public async Task CoherentSaveReloadTwoProjectsFailureAndStaleCandidate()
    {
        var p = ColumnTests.Project(); var p2 = ColumnTests.Project("P2"); var w = Work(p, p2);
        var root = Path.Combine(Path.GetTempPath(), "row-view-" + Guid.NewGuid().ToString("N")); var store = new DraftStore(root);
        var s = new DraftSession(store, w, 0); Assert.That(await s.FlushAsync(), Is.True);
        var candidate = w.PrepareRowView(p) with { Definition = new("Title", true, Title: "Issue 1") };
        w.SaveColumns(ColumnTests.Reordered(w, p)); w.SetBuffer(w.Open(p)[0].Cells[0], "late buffer"); w.AddRow(p2);
        Assert.That(await s.CommitAsync(c => { c.SaveRowView(candidate); return c; }, () => false), Is.False);
        Assert.That(s.Workspace.RowView(p).Title, Is.Empty);
        using (var locked = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.That(await s.CommitAsync(c => { c.SaveRowView(candidate); return c; }, () => true), Is.False);
        Assert.That(s.Workspace.RowView(p).Title, Is.Empty);
        Assert.That(await s.CommitAsync(c => { c.SaveRowView(candidate); return c; }, () => true), Is.True);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        Assert.That(restored.RowView(p).Title, Is.EqualTo("Issue 1")); Assert.That(restored.RowView(p2).Title, Is.Empty);
        Assert.That(restored.LocalRows.Single().ProjectId, Is.EqualTo("P2")); Assert.That(restored.Buffer(restored.Open(p)[0].Cells[0]), Is.EqualTo("late buffer"));
        Assert.That(restored.Columns(p).Visible[1].Id.FieldId, Is.EqualTo("P1C"));
        Assert.Throws<InvalidOperationException>(() => restored.SaveRowView(candidate));
    }
    [TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5), TestCase(6)]
    public async Task EarlierCheckpointMigratesWithDefaultView(int version)
    {
        var p = ColumnTests.Project(); var w = Work(p); if (version == 6) w.SaveColumns(ColumnTests.Reordered(w, p));
        var record = w.Snapshot() with { Version = version, RowPreferences = null, ColumnPreferences = version == 6 ? w.Snapshot().ColumnPreferences : null, LocalRows = version >= 4 ? [] : null };
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "row-migrate-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(record, 0); var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        Assert.That(restored.RowView(p), Is.EqualTo(new RowViewDefinition()));
        await store.SaveAsync(restored.Snapshot(), record.Revision);
        Assert.That((await store.LoadAsync(w.Scope))!.Version, Is.EqualTo(10));
        Assert.That(restored.Columns(p).Visible[1].Id.FieldId, Is.EqualTo(version == 6 ? "P1C" : "P1A"));
    }
    [Test]
    public void RemovedOptionFailsClosedRetainsCriteriaAndCanReset()
    {
        var p = ColumnTests.Project(); var w = Work(p); View(w, p, new(Filters: [new("P1A", ["A0"], [])]));
        var stale = w.PrepareRowView(p);
        var changed = p with { Snapshot = p.Snapshot with { Fields = p.Snapshot.Fields.Select(f => f.Id.NodeId == "P1A" ? f with { Options = [new("replacement", "Todo")] } : f).ToArray() } };
        w.SetRegistrations([changed]); var view = new RowProjection(p.Snapshot.Id); view.Reapply(w, changed);
        Assert.That(view.Ids, Is.Empty); Assert.That(view.Problem, Does.Contain("A0")); Assert.That(w.RowView(changed).Filters![0].OptionIds, Is.EqualTo(new[] { "A0" }));
        Assert.Throws<InvalidOperationException>(() => w.SaveRowView(stale));
        View(w, changed, new()); view.Reapply(w, changed); Assert.That(view.Ids, Has.Length.EqualTo(2)); Assert.That(w.Open(changed), Has.Length.EqualTo(2));
    }
    [Test]
    public void FreshMembershipRequiresNewTargetConfirmation()
    {
        var p = EditingTests.Registration(count: 3);
        var selection = new RowTargetSelection(p.Snapshot.Id, ["R3", "R1"], ["R1"], false);
        Assert.That(selection.NeedsConfirmation(["R1", "R3"]), Is.False);
        Assert.That(selection.NeedsConfirmation(["R1"]), Is.True);
        Assert.That(selection.NeedsConfirmation(["R1", "R2", "R3"]), Is.True);
        Assert.That(selection.Selected, Is.EqualTo(new[] { "R1" }));
    }
    [TestCase(false), TestCase(true)]
    public async Task VisibleOnlyOrExplicitHiddenSelectionDispatchesExactFrozenPayloads(bool hidden)
    {
        var h = await ApplyTests.Harness.Create(3); Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True); var s = h.Workspace.Drafts!; var p = h.Workspace.Selected!; var rows = s.Workspace.Open(p);
        s.Workspace.Commit("P1", rows[0].Cells[0], "keep"); s.Workspace.Commit("P1", rows[1].Cells[0], "hidden");
        View(s.Workspace, p, new(Title: "keep"));
        var visible = s.Workspace.EvaluateRows(p).Select(r => r.ItemId).ToArray(); var chosen = hidden ? rows.Take(2).Select(r => r.ItemId).ToArray() : visible;
        await h.Workspace.PrepareApplyAsync(chosen.ToHashSet(), new(p.Snapshot.Id, visible, chosen, hidden));
        Assert.That(h.Workspace.ApplyReview, Is.Not.Null, h.Workspace.Status);
        // The executor has already received approval before this callback changes only the view.
        h.OnMutation = () => View(s.Workspace, h.Workspace.Selected!, new(Title: "matches nothing"));
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Select(x => x.GetProperty("id").GetString()), Is.EquivalentTo(hidden ? new[] { "I1", "I2" } : new[] { "I1" }));
        Assert.That(h.Writes.Select(x => x.GetProperty("title").GetString()), Is.EquivalentTo(hidden ? new[] { "keep", "hidden" } : new[] { "keep" }));
        Assert.That(s.Workspace.Journal.Single().Operations.Length, Is.EqualTo(hidden ? 2 : 1));
    }
    [Test]
    public async Task RefreshChangingMembershipDoesNotAddTargetsOrKeepHiddenDefaults()
    {
        var h = await ApplyTests.Harness.Create(3); Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True); var s = h.Workspace.Drafts!; var p = h.Workspace.Selected!;
        View(s.Workspace, p, new(Title: "Issue 1")); var visible = s.Workspace.EvaluateRows(p).Select(r => r.ItemId).ToArray();
        h.Titles["I1"] = "gone"; h.Titles["I2"] = "Issue 1";
        await h.Workspace.PrepareApplyAsync(visible.ToHashSet(), new(p.Snapshot.Id, visible, visible, false));
        Assert.That(h.Workspace.ApplyReview, Is.Null); Assert.That(h.Workspace.Status, Does.Contain("選び直して")); Assert.That(h.Writes, Is.Empty);
        Assert.That(s.Workspace.EvaluateRows(h.Workspace.Selected!).Select(r => r.ItemId), Is.EqualTo(new[] { "P1-T2" }));
    }
    [TestCase(false), TestCase(true)]
    public async Task CreationPromotionRetainsVisiblePositionOrHiddenHistoryAndBuffers(bool hidden)
    {
        var h = await CreationHarness.Create(2); var p = h.Workspace.Selected!; var id = h.Add(); var w = h.Session.Workspace;
        View(w, p, new(Title: hidden ? "Issue" : "A")); var view = new RowProjection(p.Snapshot.Id); view.Reapply(w, p);
        var before = view.Ids.ToArray();
        h.AfterCreate = () => { var current = h.Session.Workspace; var cell = current.Open(h.Workspace.Selected!).Single(r => r.ItemId == id).Cells[0]; current.Commit("P1", cell, "later"); current.SetBuffer(cell, "unfinished"); };
        await h.Apply(id); w = h.Session.Workspace; view.Promote(w);
        var operation = w.Creations.Single(); Assert.That(operation.Completed, Is.True, h.Workspace.Status);
        Assert.That(view.Ids, Is.EqualTo(before.Select(row => row == id ? operation.ItemId : row)));
        Assert.That(view.Resolve(w.Open(h.Workspace.Selected!)).Length, Is.EqualTo(before.Length));
        Assert.That(w.Fields.Single(f => f.Key == new FieldKey("Title", "created1")).Buffer, Is.EqualTo("unfinished"));
        await h.Restart(); Assert.That(h.Session.Workspace.Creations.Single().Completed, Is.True); Assert.That(h.Session.Workspace.LocalRows, Is.Empty);
        Assert.That(h.Issues, Has.Count.EqualTo(1));
    }
    [Test]
    public async Task CompletedCreationWithRetainedLocalWorkDoesNotPrematurelyRetargetProjection()
    {
        var h = await CreationHarness.Create(2); var p = h.Workspace.Selected!; var id = h.Add();
        var local = h.Session.Workspace.LocalRows.Single(); var view = new RowProjection(p.Snapshot.Id); view.Reapply(h.Session.Workspace, p);
        await h.Apply(id); var checkpoint = h.Session.Workspace.Snapshot();
        // Completed execution can coexist with local work that a complete promotion could not transfer.
        var retained = EditingWorkspace.Restore(checkpoint with { LocalRows = [local] });
        Assert.That(retained.Creations.Single().Completed, Is.True);
        view.Promote(retained); Assert.That(view.Ids.Last(), Is.EqualTo(id));
        Assert.That(view.Resolve(retained.Open(h.Workspace.Selected!)).Last().IsLocal, Is.True);
        view.Promote(h.Session.Workspace); Assert.That(view.Ids.Last(), Is.EqualTo("item-created1"));
    }
    [Test]
    public void DuplicateOptionLabelsNeverRetargetCriteria()
    {
        var p = EditingTests.Registration(count: 2); var w = Work(p); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[1], "dup1", true); w.Commit("P1", rows[1].Cells[1], "dup2", true);
        View(w, p, new(Filters: [new("P1-status", ["dup2"], [])]));
        Assert.That(w.EvaluateRows(p).Select(r => r.ItemId), Is.EqualTo(new[] { "P1T2" }));
        View(w, p, new(Filters: [new("P1-status", ["dup1", "dup2"], [])]));
        Assert.That(w.EvaluateRows(p), Has.Length.EqualTo(2));
    }
    [Test]
    public async Task CorruptRowContractDoesNotOverwriteOrResetRecoverableCheckpoint()
    {
        var p = ColumnTests.Project(); var w = Work(p); w.SetBuffer(w.Open(p)[0].Cells[0], "recover me");
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "row-corruption-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(w.Snapshot(), 0); var path = store.FileFor(w.Scope); var original = await File.ReadAllTextAsync(path);
        foreach (var record in new[] { w.Snapshot() with { RowPreferences = null },
            w.Snapshot() with { RowPreferences = [new("P1", new("UnknownSort"))] },
            w.Snapshot() with { RowPreferences = [new("P1", new(Filters: [new("P1A", [], ["missing-state"])]))] } })
        {
            var damaged = System.Text.Json.JsonSerializer.Serialize(record); await File.WriteAllTextAsync(path, damaged);
            Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(w.Scope)); Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(damaged));
        }
        await File.WriteAllTextAsync(path, original);
        Assert.That(EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!).Buffer(w.Open(p)[0].Cells[0]), Is.EqualTo("recover me"));
    }
}
